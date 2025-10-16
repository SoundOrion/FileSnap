using System.Collections.Immutable;

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

// 送信ジョブ：このジョブ“だけ”が Writer/Stream に書く
public readonly record struct SyncJob(
    string FilePath,
    long ClientUnixMs,
    long ClientSize,
    Stream Stream,
    StreamWriter Writer,
    TaskCompletionSource<bool> Completion
);
