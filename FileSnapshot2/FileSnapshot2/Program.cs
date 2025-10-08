using System.Collections.Immutable;
using System.Security.Cryptography;

// 圧縮ファイル1件の情報
record FileEntryInfo(
    string FilePath,         // 圧縮ファイルのパス（キーでもある）
    long Size,               // ファイルサイズ
    DateTimeOffset Modified, // 最終更新日時
    byte[] Hash              // SHA256 ハッシュ（内容の比較用）
);

class Program
{
    // ===== 設定 =====
    static readonly string TargetFolder = @"D:\data";          // 監視フォルダ
    static readonly TimeSpan ScanPeriod = TimeSpan.FromSeconds(30); // 周期

    // 現在のスナップショット（キー＝圧縮ファイルパス）
    static ImmutableDictionary<string, FileEntryInfo> _snapshot =
        ImmutableDictionary<string, FileEntryInfo>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    static async Task Main()
    {
        Console.WriteLine($"[Start] Archive integrity scanner: {TargetFolder}");
        using var timer = new PeriodicTimer(ScanPeriod);

        // 初回スキャン
        await UpdateSnapshotAsync();

        // 周期的にループ
        while (await timer.WaitForNextTickAsync())
        {
            await UpdateSnapshotAsync();
        }
    }

    static async Task UpdateSnapshotAsync()
    {
        try
        {
            var builder = ImmutableDictionary.CreateBuilder<string, FileEntryInfo>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(TargetFolder, "*.*", SearchOption.TopDirectoryOnly);

            foreach (var path in files)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) continue;

                    // 圧縮ファイル全体のハッシュを計算
                    byte[] hash;
                    using (var fs = fi.OpenRead())
                    using (var sha = SHA256.Create())
                        hash = sha.ComputeHash(fs);

                    builder[path] = new FileEntryInfo(
                        FilePath: path,
                        Size: fi.Length,
                        Modified: fi.LastWriteTimeUtc,
                        Hash: hash
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WARN] Failed to read {path}: {ex.Message}");
                }
            }

            var next = builder.ToImmutable();

            // 差分出力
            PrintDiff(_snapshot, next);

            // 原子的にスナップショット更新
            _snapshot = next;

            Console.WriteLine($"[INFO] Scan done: {_snapshot.Count} files ({DateTime.Now:T})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Update failed: {ex}");
        }

        await Task.CompletedTask;
    }

    static void PrintDiff(
        ImmutableDictionary<string, FileEntryInfo> prev,
        ImmutableDictionary<string, FileEntryInfo> next)
    {
        foreach (var (path, info) in next)
        {
            if (!prev.TryGetValue(path, out var old))
            {
                Console.WriteLine($"[ADD] {path}");
            }
            else if (!info.Hash.SequenceEqual(old.Hash))
            {
                Console.WriteLine($"[MOD] {path} (content changed)");
            }
        }

        foreach (var (path, _) in prev)
        {
            if (!next.ContainsKey(path))
            {
                Console.WriteLine($"[DEL] {path}");
            }
        }
    }
}
