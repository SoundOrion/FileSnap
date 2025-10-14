using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

//いいですね、その分割は相性バツグンです。
//**「受信（write側）」と「処理（read側）」を別 `BackgroundService` にして、`Channel` をシングルトン共有**にしましょう。
//下はあなたのコードを最小変更で再構成した完全サンプルです（.NET 8）。

//---

//# 役割分担

//* **SnapshotWorker** … サーバのスナップショット更新（そのまま）
//* **AcceptService (Producer)** … TCPで受信→`ChannelWriter<SyncJob>` に投入→`TCS` 完了待ち→応答を同じ接続に返す
//* **ProcessService (Consumer)** … `ChannelReader<SyncJob>` からジョブを取得→差分/ZIPなど重い処理→`TCS.SetResult`

//> こうすると、ネットワークI/O（軽い）と重い処理（CPU/IOバウンド）がきれいに分離され、停止/例外ハンドリングもスッキリします。

//---

//## Program.cs（サーバ・分割版）

//```csharp
using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ====== 共有モデル ======
public sealed record ArchiveInfo(string FilePath, long Size, DateTimeOffset ModifiedUtc, byte[] Hash);
public interface IFileIndex
{
    ImmutableDictionary<string, ArchiveInfo> Snapshot { get; }
    void Replace(ImmutableDictionary<string, ArchiveInfo> next);
}
public sealed class InMemoryFileIndex : IFileIndex
{
    private ImmutableDictionary<string, ArchiveInfo> _snap =
        ImmutableDictionary<string, ArchiveInfo>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    public ImmutableDictionary<string, ArchiveInfo> Snapshot => System.Threading.Volatile.Read(ref _snap);
    public void Replace(ImmutableDictionary<string, ArchiveInfo> next)
        => System.Threading.Interlocked.Exchange(ref _snap, next);
}

// ====== DTO ======
public sealed class ClientSyncRequest
{
    public List<ClientFileInfo>? Files { get; set; }
    public bool BundleMissingAsZip { get; set; } = true;
}
public sealed class ClientFileInfo
{
    public string FilePath { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }
    public string? Sha256Hex { get; set; }
}
public sealed class ServerSyncResult
{
    public List<string> ToDownload { get; set; } = new();
    public List<string> ToDelete { get; set; } = new();
    public List<string> UpToDate { get; set; } = new();
    public string? ZipPath { get; set; }
    public string? Error { get; set; }
}

// ジョブ（結果は TCS で返す：受信側が await）
public readonly record struct SyncJob(string Json, TaskCompletionSource<ServerSyncResult> Tcs);

// ====== Worker #1: スナップショット更新 ======
public sealed class SnapshotWorker : BackgroundService
{
    private readonly ILogger<SnapshotWorker> _log;
    private readonly IFileIndex _index;
    private readonly string _folder = @"D:\data";
    private readonly TimeSpan _period = TimeSpan.FromSeconds(30);
    private readonly bool _recurse = false;
    public SnapshotWorker(ILogger<SnapshotWorker> log, IFileIndex index) { _log = log; _index = index; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await BuildAndSwapAsync(stoppingToken);
        using var timer = new PeriodicTimer(_period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            try { await BuildAndSwapAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Snapshot update failed"); }
    }

    private async Task BuildAndSwapAsync(CancellationToken ct)
    {
        var opt = _recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(_folder, "*.*", opt);
        var bag = new System.Collections.Concurrent.ConcurrentBag<ArchiveInfo>();
        var po = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount };

        await Task.Run(() =>
        {
            Parallel.ForEach(files, po, path =>
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) return;
                    using var fs = fi.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sha = SHA256.Create();
                    var hash = sha.ComputeHash(fs);
                    bag.Add(new ArchiveInfo(path, fi.Length, fi.LastWriteTimeUtc, hash));
                }
                catch (OperationCanceledException) { throw; }
                catch { /* skip */ }
            });
        }, ct);

        var b = ImmutableDictionary.CreateBuilder<string, ArchiveInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in bag) b[a.FilePath] = a;
        _index.Replace(b.ToImmutable());
    }
}

// ====== Producer: TCP 受信→ChannelWriter へ enqueue → TCS await して応答 ======
public sealed class AcceptService : BackgroundService
{
    private readonly ILogger<AcceptService> _log;
    private readonly ChannelWriter<SyncJob> _writer;
    private readonly int _port = 5001;

    public AcceptService(ILogger<AcceptService> log, ChannelWriter<SyncJob> writer)
    { _log = log; _writer = writer; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _log.LogInformation("Listening on {Port}", _port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken); // 軽いI/Oなので fire-and-track でもOK
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        finally
        {
            listener.Stop();
            _writer.TryComplete(); // 受け付け終了
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        await using var _ = client;
        using var ns = client.GetStream();
        using var reader = new StreamReader(ns, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true };

        try
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
            {
                await writer.WriteLineAsync("{\"error\":\"empty\"}");
                return;
            }

            // enqueue → 完了待ち
            var tcs = new TaskCompletionSource<ServerSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _writer.WriteAsync(new SyncJob(line!, tcs), ct);
            var result = await tcs.Task;

            var json = JsonSerializer.Serialize(result, JsonOpt());
            await writer.WriteLineAsync(json);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { await writer.WriteLineAsync("{\"error\":\"internal\"}"); } catch { }
            _log.LogWarning(ex, "Client error");
        }
    }

    private static JsonSerializerOptions JsonOpt() => new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

// ====== Consumer: ChannelReader から dequeue → 重い処理 → TCS.SetResult ======
public sealed class ProcessService : BackgroundService
{
    private readonly ILogger<ProcessService> _log;
    private readonly ChannelReader<SyncJob> _reader;
    private readonly IFileIndex _index;
    private readonly string _bundleDir = Path.Combine(Path.GetTempPath(), "sync-bundles");
    private readonly int _workers = Math.Max(1, Environment.ProcessorCount / 2);

    public ProcessService(ILogger<ProcessService> log, ChannelReader<SyncJob> reader, IFileIndex index)
    { _log = log; _reader = reader; _index = index; Directory.CreateDirectory(_bundleDir); }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = Enumerable.Range(0, _workers)
            .Select(i => WorkerLoopAsync(i, stoppingToken)).ToArray();

        await Task.WhenAll(tasks);
    }

    private async Task WorkerLoopAsync(int id, CancellationToken ct)
    {
        _log.LogInformation("Worker {Id} started", id);

        try
        {
            while (await _reader.WaitToReadAsync(ct))
            {
                while (_reader.TryRead(out var job))
                {
                    try
                    {
                        var req = JsonSerializer.Deserialize<ClientSyncRequest>(job.Json, JsonOpt()) ?? new();

                        var diff = ComputeDiff(_index.Snapshot, req);

                        string? zipPath = null;
                        if (req.BundleMissingAsZip && diff.ToDownload.Count > 0)
                            zipPath = await CreateBundleAsync(diff.ToDownload, ct);

                        job.Tcs.TrySetResult(new ServerSyncResult
                        {
                            ToDownload = diff.ToDownload,
                            ToDelete = diff.ToDelete,
                            UpToDate = diff.UpToDate,
                            ZipPath = zipPath
                        });
                    }
                    catch (Exception ex)
                    {
                        job.Tcs.TrySetResult(new ServerSyncResult { Error = "processing_failed" });
                        _log.LogWarning(ex, "Worker {Id} failed", id);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        _log.LogInformation("Worker {Id} stopped", id);
    }

    private static (List<string> ToDownload, List<string> ToDelete, List<string> UpToDate)
        ComputeDiff(ImmutableDictionary<string, ArchiveInfo> server, ClientSyncRequest req)
    {
        var cli = (req.Files ?? new()).ToDictionary(x => x.FilePath, x => x, StringComparer.OrdinalIgnoreCase);
        var toDownload = new List<string>(); var toDelete = new List<string>(); var upToDate = new List<string>();

        foreach (var (path, s) in server)
        {
            if (!cli.TryGetValue(path, out var c)) toDownload.Add(path);
            else if (!HashesEqual(s.Hash, c.Sha256Hex)) toDownload.Add(path);
            else upToDate.Add(path);
        }
        foreach (var (path, _) in cli) if (!server.ContainsKey(path)) toDelete.Add(path);
        return (toDownload, toDelete, upToDate);
    }

    private async Task<string> CreateBundleAsync(List<string> serverPaths, CancellationToken ct)
    {
        var name = $"bundle_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmssfff}.zip";
        var zip = Path.Combine(_bundleDir, name);

        using var fs = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        foreach (var p in serverPaths)
        {
            ct.ThrowIfCancellationRequested();
            var e = za.CreateEntry(Path.GetFileName(p), CompressionLevel.Optimal);
            using var es = e.Open();
            using var src = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await src.CopyToAsync(es, ct);
        }
        return zip;
    }

    private static bool HashesEqual(byte[] serverHash, string? clientHex)
    {
        if (string.IsNullOrWhiteSpace(clientHex) || clientHex.Length != 64) return false;
        Span<byte> buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++) buf[i] = Convert.ToByte(clientHex.AsSpan(i * 2, 2), 16);
        return serverHash.AsSpan().SequenceEqual(buf);
    }

    private static JsonSerializerOptions JsonOpt() => new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

// ====== Host 起動（Channelをシングルトン共有：Writer/Readerを個別DI） ======
public static class Program
{
    public static async Task Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(s =>
            {
                // 共有スナップショット
                s.AddSingleton<IFileIndex, InMemoryFileIndex>();

                // Channel をシングルトン化し、Writer/Reader を個別登録
                s.AddSingleton(sp => Channel.CreateBounded<SyncJob>(new BoundedChannelOptions(1024)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = false
                }));
                s.AddSingleton<ChannelWriter<SyncJob>>(sp => sp.GetRequiredService<Channel<SyncJob>>().Writer);
                s.AddSingleton<ChannelReader<SyncJob>>(sp => sp.GetRequiredService<Channel<SyncJob>>().Reader);

                // Hosted services
                s.AddHostedService<SnapshotWorker>();
                s.AddHostedService<AcceptService>();   // producer
                s.AddHostedService<ProcessService>();  // consumer(s)
            })
            .Build();

        await host.RunAsync();
    }
}
//```

//---

//## 補足メモ

//***構造化非同期 * *：ワーカーは `ProcessService.ExecuteAsync` で `Task.WhenAll` 管理。`Task.Run` は不要。
//* **バックプレッシャー**：`BoundedChannel(1024)` で受信過多を抑止（必要に応じてサイズ調整）。
//* **応答の整合**：`TCS` で「ジョブ単位の完了」を受信側が待ち、**同じTCP接続に最終結果を返します**。
//* **耐障害性**：処理側で例外→`Error` を返し、受信側は接続クローズ（必要ならエラーコード追加）。
//* **Zip の出し方**：ここではサーバ内パスを返しています。実運用は HTTP/SMB の URL を返してクライアントが取得する方が扱いやすいです。

//この分割で「受信」と「重処理」が疎結合になり、テストしやすくスケールしやすい構成になります。
