using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace FileSnapshot3;

// ========== Worker #1: スナップショット更新 ==========
public sealed class SnapshotWorker : BackgroundService
{
    private readonly ILogger<SnapshotWorker> _logger;
    private readonly IFileIndex _index;

    // 設定
    private readonly string _folder = @"D:\data";                 // 監視フォルダ
    private readonly TimeSpan _period = TimeSpan.FromSeconds(30); // 周期
    private readonly bool _recurse = false;                       // サブフォルダ再帰

    public SnapshotWorker(ILogger<SnapshotWorker> logger, IFileIndex index)
    {
        _logger = logger;
        _index = index;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SnapshotWorker started. Folder={Folder}, Period={Period}", _folder, _period);

        // 初回
        await BuildAndSwapAsync(stoppingToken);

        using var timer = new PeriodicTimer(_period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await BuildAndSwapAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Snapshot update failed"); }
        }
    }

    private async Task BuildAndSwapAsync(CancellationToken ct)
    {
        var option = _recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(_folder, "*.*", option);

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

                    bag.Add(new ArchiveInfo(
                        FilePath: path,
                        Size: fi.Length,
                        ModifiedUtc: fi.LastWriteTimeUtc,
                        Hash: hash
                    ));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skip unreadable file: {Path}", path);
                }
            });
        }, ct);

        var builder = ImmutableDictionary.CreateBuilder<string, ArchiveInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var ai in bag) builder[ai.FilePath] = ai;

        _index.Replace(builder.ToImmutable());
        _logger.LogInformation("Snapshot updated: {Count} files", builder.Count);
    }
}