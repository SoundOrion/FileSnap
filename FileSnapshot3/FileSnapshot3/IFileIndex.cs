using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileSnapshot3;

// ========== モデル & 共有インデックス ==========
public sealed record ArchiveInfo(
    string FilePath,
    long Size,
    DateTimeOffset ModifiedUtc,
    byte[] Hash // SHA256 (binary)
);

public interface IFileIndex
{
    ImmutableDictionary<string, ArchiveInfo> Snapshot { get; }
    void Replace(ImmutableDictionary<string, ArchiveInfo> next);
}

public sealed class InMemoryFileIndex : IFileIndex
{
    private ImmutableDictionary<string, ArchiveInfo> _snap
        = ImmutableDictionary<string, ArchiveInfo>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    public ImmutableDictionary<string, ArchiveInfo> Snapshot
        => System.Threading.Volatile.Read(ref _snap);

    public void Replace(ImmutableDictionary<string, ArchiveInfo> next)
        => System.Threading.Interlocked.Exchange(ref _snap, next);
}