// ====== Worker #1: スナップショット更新 ======
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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