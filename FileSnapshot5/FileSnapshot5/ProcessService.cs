using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text;
using System.Threading.Channels;

public sealed class ProcessService : BackgroundService
{
    private readonly ILogger<ProcessService> _log;
    private readonly ChannelReader<SyncJob> _reader;
    private readonly IFileIndex _index;
    private readonly int _workers = Math.Max(1, Environment.ProcessorCount / 2);

    public ProcessService(ILogger<ProcessService> log, ChannelReader<SyncJob> reader, IFileIndex index)
    { _log = log; _reader = reader; _index = index; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = Enumerable.Range(0, _workers).Select(i => WorkerLoopAsync(i, stoppingToken)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task WorkerLoopAsync(int id, CancellationToken ct)
    {
        _log.LogInformation("Worker {Id} started", id);
        try
        {
            await foreach (var job in _reader.ReadAllAsync(ct))
            {
                try
                {
                    await HandleOneAsync(job, ct);
                    job.Completion.TrySetResult(true);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Worker {Id} failed", id);
                    try { await job.Writer.WriteLineAsync("ERROR|processing_failed"); await job.Writer.FlushAsync(); } catch { }
                    job.Completion.TrySetResult(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        _log.LogInformation("Worker {Id} stopped", id);
    }

    private async Task HandleOneAsync(SyncJob job, CancellationToken ct)
    {
        var snap = _index.Snapshot;
        if (!snap.TryGetValue(job.FilePath, out var info) || !File.Exists(info.FilePath))
        {
            await job.Writer.WriteLineAsync("NOTFOUND");
            await job.Writer.FlushAsync();
            return;
        }

        var fi = new FileInfo(info.FilePath);
        var serverSize = fi.Length;
        var serverUnixMs = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var modified = serverSize != job.ClientSize || serverUnixMs > job.ClientUnixMs;

        if (!modified)
        {
            await job.Writer.WriteLineAsync("NOTMODIFIED");
            await job.Writer.FlushAsync();
            return;
        }

        // 送信：FILEB64 ヘッダ → Base64 本文（ちょうど b64Len 文字）
        long rawLen = serverSize;
        long b64Len = ((rawLen + 2) / 3) * 4;
        var nameOnly = Path.GetFileName(job.FilePath);

        await job.Writer.WriteLineAsync($"FILEB64|{nameOnly}|{rawLen}|{b64Len}");
        await job.Writer.FlushAsync();

        using var fs = new FileStream(info.FilePath, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
        using var xform = new System.Security.Cryptography.ToBase64Transform();

        // 3 の倍数で読みやすいサイズ（大きめOK）
        byte[] inBuf = new byte[57 * 1024];
        byte[] outBuf = new byte[xform.OutputBlockSize * (inBuf.Length / xform.InputBlockSize) + 8];

        while (true)
        {
            int n = await fs.ReadAsync(inBuf.AsMemory(0, inBuf.Length), ct);
            if (n <= 0) break;

            int whole = n - (n % xform.InputBlockSize);
            if (whole > 0)
            {
                int outN = xform.TransformBlock(inBuf, 0, whole, outBuf, 0);
                await job.Stream.WriteAsync(outBuf.AsMemory(0, outN), ct);
            }
            if (whole != n)
            {
                var final = xform.TransformFinalBlock(inBuf, whole, n - whole);
                await job.Stream.WriteAsync(final, ct);
                break;
            }
        }

        await job.Stream.FlushAsync(ct);
    }
}
