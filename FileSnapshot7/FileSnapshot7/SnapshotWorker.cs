// ====== Worker #1: スナップショット更新 ======
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;

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
        var option = _recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(_folder, "*.*", option);

        var bag = new ConcurrentBag<ArchiveInfo>();
        var po = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        await Parallel.ForEachAsync(files, po, async (path, token) =>
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return;

                // 大きなフォルダ向け最適化：まずは mtime/size だけ
                byte[] hash = Array.Empty<byte>();

                //await using var fs = new FileStream(
                //    path,
                //    FileMode.Open,
                //    FileAccess.Read,
                //    FileShare.ReadWrite | FileShare.Delete,
                //    bufferSize: 1024 * 64,
                //    options: FileOptions.Asynchronous | FileOptions.SequentialScan);

                //// .NET 8: 非同期で SHA256
                //byte[] hash = await SHA256.HashDataAsync(fs, token);

                bag.Add(new ArchiveInfo(
                    FilePath: path,
                    Size: fi.Length,
                    ModifiedUtc: fi.LastWriteTimeUtc,
                    Hash: hash
                ));
            }
            catch (OperationCanceledException) { throw; } // そのまま上へ
            catch
            {
                // 読めない/ロック中などはスキップ（必要ならログ）
            }
        });

        var builder = ImmutableDictionary.CreateBuilder<string, ArchiveInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in bag) builder[a.FilePath] = a;

        _index.Replace(builder.ToImmutable());
    }
}