using System.Collections.Immutable;
using SharpCompress.Archives;
using SharpCompress.Common;

record FileEntryInfo(
    string EntryName,         // 圧縮内のファイル名
    long Size,
    DateTimeOffset Modified
);

class Program
{
    // ===== 設定 =====
    static readonly string TargetFolder = @"D:\data"; // スキャン対象フォルダ
    static readonly TimeSpan ScanPeriod = TimeSpan.FromSeconds(30); // スキャン周期

    // 現在のスナップショット
    static ImmutableDictionary<string, List<FileEntryInfo>> _snapshot =
        ImmutableDictionary<string, List<FileEntryInfo>>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    static async Task Main()
    {
        Console.WriteLine($"[Start] Archive scanner: {TargetFolder}");
        using var timer = new PeriodicTimer(ScanPeriod);

        // 初回スキャン
        await UpdateSnapshotAsync();

        while (await timer.WaitForNextTickAsync())
        {
            await UpdateSnapshotAsync();
        }
    }

    static async Task UpdateSnapshotAsync()
    {
        try
        {
            var builder = ImmutableDictionary.CreateBuilder<string, List<FileEntryInfo>>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(TargetFolder, "*.*", SearchOption.TopDirectoryOnly);

            foreach (var path in files)
            {
                try
                {
                    using var archive = ArchiveFactory.Open(path); // 拡張子ではなく内容で判別
                    var entries = new List<FileEntryInfo>();

                    foreach (var entry in archive.Entries)
                    {
                        if (entry.IsDirectory) continue;
                        entries.Add(new FileEntryInfo(
                            EntryName: entry.Key,
                            Size: entry.Size,
                            Modified: entry.LastModifiedTime ?? DateTimeOffset.MinValue
                        ));
                    }

                    if (entries.Count > 0)
                        builder[path] = entries;
                }
                catch (InvalidOperationException)
                {
                    // 非対応形式 or 圧縮ファイルでない → スキップ
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            var next = builder.ToImmutable();
            PrintDiff(_snapshot, next);
            _snapshot = next;

            Console.WriteLine($"[INFO] Scan done: {next.Count} archives ({DateTime.Now:T})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Update failed: {ex}");
        }

        await Task.CompletedTask;
    }

    static void PrintDiff(
        ImmutableDictionary<string, List<FileEntryInfo>> prev,
        ImmutableDictionary<string, List<FileEntryInfo>> next)
    {
        // 新しい・更新・削除を検出
        foreach (var (archive, entries) in next)
        {
            if (!prev.TryGetValue(archive, out var oldEntries))
            {
                Console.WriteLine($"[ADD] Archive added: {archive} ({entries.Count} entries)");
                continue;
            }

            // 差分チェック
            var oldSet = oldEntries.ToDictionary(e => e.EntryName, StringComparer.OrdinalIgnoreCase);
            var newSet = entries.ToDictionary(e => e.EntryName, StringComparer.OrdinalIgnoreCase);

            foreach (var e in newSet)
            {
                if (!oldSet.TryGetValue(e.Key, out var old))
                {
                    Console.WriteLine($"  [ADD] {archive} -> {e.Key}");
                }
                else if (old.Size != e.Value.Size || old.Modified != e.Value.Modified)
                {
                    Console.WriteLine($"  [MOD] {archive} -> {e.Key}");
                }
            }

            foreach (var e in oldSet)
            {
                if (!newSet.ContainsKey(e.Key))
                {
                    Console.WriteLine($"  [DEL] {archive} -> {e.Key}");
                }
            }
        }

        foreach (var (archive, entries) in prev)
        {
            if (!next.ContainsKey(archive))
            {
                Console.WriteLine($"[DEL] Archive removed: {archive} ({entries.Count} entries)");
            }
        }
    }
}
