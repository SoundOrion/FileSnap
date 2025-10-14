// ====== Consumer: ChannelReader から dequeue → 重い処理 → TCS.SetResult ======
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

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
                        JsonSerializerOptions JsonOpt() => new()
                        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

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
        if (string.IsNullOrWhiteSpace(clientHex) || clientHex.Length != 64)
            return false;

        Span<byte> buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++)
        {
            string hexByte = clientHex.Substring(i * 2, 2);
            buf[i] = Convert.ToByte(hexByte, 16);
        }

        return serverHash.AsSpan().SequenceEqual(buf);
    }

}