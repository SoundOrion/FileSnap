// ====== DTO ======
public sealed class ClientSyncRequest
{
    public List<ClientFileInfo>? Files { get; set; }
    public bool BundleMissingAsZip { get; set; } = true;
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
    public string? ZipPath { get; set; }
    public string? Error { get; set; }
}

// ジョブ（結果は TCS で返す：受信側が await）
public readonly record struct SyncJob(string Json, TaskCompletionSource<ServerSyncResult> Tcs);