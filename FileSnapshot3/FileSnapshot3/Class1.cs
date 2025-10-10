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

// ====== Worker #1: スナップショット更新 ======
public sealed class SnapshotWorker : BackgroundService
{
    private readonly ILogger<SnapshotWorker> _log;
    private readonly IFileIndex _index;
    private readonly string _folder = @"D:\data";                 // 監視フォルダ（変更可）
    private readonly TimeSpan _period = TimeSpan.FromSeconds(30); // 周期
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
                catch { /* skip locked/unreadable */ }
            });
        }, ct);

        var b = ImmutableDictionary.CreateBuilder<string, ArchiveInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in bag) b[a.FilePath] = a;
        _index.Replace(b.ToImmutable());
    }
}

// ====== Worker #2: TCP + Channelで重い処理をワーカーへ ======
public sealed class SyncServerWorker : BackgroundService
{
    private readonly ILogger<SyncServerWorker> _log;
    private readonly IFileIndex _index;

    private readonly int _port = 5001;
    private readonly Channel<SyncJob> _ch;
    private readonly int _workerCount = Math.Max(1, Environment.ProcessorCount / 2);
    private readonly string _bundleDir = Path.Combine(Path.GetTempPath(), "sync-bundles");

    public SyncServerWorker(ILogger<SyncServerWorker> log, IFileIndex index)
    {
        _log = log;
        _index = index;
        Directory.CreateDirectory(_bundleDir);
        _ch = Channel.CreateBounded<SyncJob>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        //// コンシューマ（重い処理担当）
        //var consumers = Enumerable.Range(0, _workerCount)
        //    .Select(i => Task.Run(() => WorkerLoopAsync(i, stoppingToken), stoppingToken))
        //    .ToArray();
        // ワーカーを作成して並列実行を待つ
        var consumerTasks = new List<Task>();
        for (int i = 0; i < _workerCount; i++)
            consumerTasks.Add(WorkerLoopAsync(i, stoppingToken));

        // プロデューサ（受信）
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _log.LogInformation("Sync server listening on {Port}", _port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            //listener.Stop();
            //_ch.Writer.TryComplete();
            //await Task.WhenAll(consumers);

            listener.Stop();
            _ch.Writer.TryComplete();
            // ★ ここでawaitして確実にワーカー終了を待つ
            await Task.WhenAll(consumerTasks);
            //_logger.LogInformation("Sync server stopped.");
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

            // 受け取った要求をジョブ化し、完了を待って同じコネクションへ返す
            var tcs = new TaskCompletionSource<ServerSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _ch.Writer.WriteAsync(new SyncJob(line!, tcs), ct);
            var result = await tcs.Task; // ← 時間がかかるが処理はChannelのワーカーで実行

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

    private async Task WorkerLoopAsync(int id, CancellationToken ct)
    {
        while (await _ch.Reader.WaitToReadAsync(ct))
        {
            while (_ch.Reader.TryRead(out var job))
            {
                try
                {
                    var req = JsonSerializer.Deserialize<ClientSyncRequest>(job.Json, JsonOpt()) ?? new();
                    // 1) 差分計算（重いならここで実行）
                    var diff = ComputeDiff(_index.Snapshot, req);

                    // 2) （オプション）ZIPバンドル作成（さらに重い）
                    string? zipPath = null;
                    if (req.BundleMissingAsZip && diff.ToDownload.Count > 0)
                    {
                        zipPath = await CreateBundleAsync(diff.ToDownload, ct); // Temp に ZIP
                    }

                    job.Complete(new ServerSyncResult
                    {
                        ToDownload = diff.ToDownload,
                        ToDelete = diff.ToDelete,
                        UpToDate = diff.UpToDate,
                        ZipPath = zipPath
                    });
                }
                catch (Exception ex)
                {
                    job.Complete(new ServerSyncResult { Error = "processing_failed" });
                    // ログだけ落として継続
                    Console.WriteLine($"[Worker {id}] {ex}");
                }
            }
        }
    }

    private static DiffResult ComputeDiff(ImmutableDictionary<string, ArchiveInfo> server, ClientSyncRequest req)
    {
        var cli = (req.Files ?? new()).ToDictionary(x => x.FilePath, x => x, StringComparer.OrdinalIgnoreCase);
        var toDownload = new List<string>();
        var toDelete = new List<string>();
        var upToDate = new List<string>();

        foreach (var (path, s) in server)
        {
            if (!cli.TryGetValue(path, out var c))
                toDownload.Add(path);
            else if (!HashesEqual(s.Hash, c.Sha256Hex))
                toDownload.Add(path);
            else
                upToDate.Add(path);
        }
        foreach (var (path, _) in cli)
            if (!server.ContainsKey(path)) toDelete.Add(path);

        return new DiffResult(toDownload, toDelete, upToDate);
    }

    private async Task<string> CreateBundleAsync(List<string> serverPaths, CancellationToken ct)
    {
        Directory.CreateDirectory(_bundleDir);
        var name = $"bundle_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmssfff}.zip";
        var zip = Path.Combine(_bundleDir, name);

        using var fs = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None);
        using var za = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false);

        foreach (var p in serverPaths)
        {
            ct.ThrowIfCancellationRequested();
            var entryName = Path.GetFileName(p);
            var e = za.CreateEntry(entryName, CompressionLevel.Optimal);
            using var es = e.Open();
            using var src = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await src.CopyToAsync(es, ct);
        }
        return zip; // サーバ上の一時パス（SMB/HTTPで配れるならURLに変換してもOK）
    }

    private static bool HashesEqual(byte[] serverHash, string? clientHex)
    {
        if (string.IsNullOrWhiteSpace(clientHex) || clientHex.Length != 64) return false;
        Span<byte> buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++)
            buf[i] = Convert.ToByte(clientHex.AsSpan(i * 2, 2), 16);
        return serverHash.AsSpan().SequenceEqual(buf);
    }

    private static JsonSerializerOptions JsonOpt() => new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // 内部型
    private readonly record struct SyncJob(string Json, TaskCompletionSource<ServerSyncResult> Tcs)
    { public void Complete(ServerSyncResult res) => Tcs.TrySetResult(res); }

    private readonly record struct DiffResult(List<string> ToDownload, List<string> ToDelete, List<string> UpToDate);
}

// ====== DTO ======
public sealed class ClientSyncRequest
{
    public List<ClientFileInfo>? Files { get; set; }
    public bool BundleMissingAsZip { get; set; } = true; // 失われたファイルをZIP化して返すか
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
    public string? ZipPath { get; set; }    // サーバ側に作ったZIPのパス（任意）
    public string? Error { get; set; }
}

// ====== Host起動 ======
public static class Program
{
    public static async Task Main(string[] args)
    {
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(s =>
            {
                s.AddSingleton<IFileIndex, InMemoryFileIndex>();
                s.AddHostedService<SnapshotWorker>();    // スナップショット
                s.AddHostedService<SyncServerWorker>();  // TCP + Channel
            })
            .Build();
        await host.RunAsync();
    }
}
