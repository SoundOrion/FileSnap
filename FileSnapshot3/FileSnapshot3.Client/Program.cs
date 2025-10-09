using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// ====== 設定 ======
string targetFolder = args.FirstOrDefault() ?? @"D:\data"; // 監視対象（クライアント側）
string serverHost = "127.0.0.1";
int serverPort = 5001;

// 並列ハッシュ計算の並列度
int maxDegreeOfParallelism = Environment.ProcessorCount;

Console.WriteLine($"[Client] Building snapshot of '{targetFolder}' ...");

// 1) クライアント側スナップショット構築
var files = Directory.EnumerateFiles(targetFolder, "*.*", SearchOption.TopDirectoryOnly);
var bag = new System.Collections.Concurrent.ConcurrentBag<ClientFileInfo>();

Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism }, path =>
{
    try
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return;

        string sha256Hex;
        using (var fs = fi.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(fs);
            sha256Hex = Convert.ToHexString(hash).ToLowerInvariant();
        }

        bag.Add(new ClientFileInfo
        {
            FilePath = path,
            Size = fi.Length,
            ModifiedUtc = fi.LastWriteTimeUtc,
            Sha256Hex = sha256Hex
        });
    }
    catch
    {
        // 読めない・ロック中などはスキップ（必要ならログ）
    }
});

JsonSerializerOptions JsonOptions() => new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

var payload = new ClientSnapshot { Files = bag.ToList() };
var json = JsonSerializer.Serialize(payload, JsonOptions());
Console.WriteLine($"[Client] Snapshot files: {payload.Files?.Count ?? 0}");

// 2) サーバへ送信（1行JSON）→ 応答（1行JSON）を受信
Console.WriteLine($"[Client] Connecting {serverHost}:{serverPort} ...");
using var client = new TcpClient();
await client.ConnectAsync(serverHost, serverPort);
using var ns = client.GetStream();
using var writer = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true };
using var reader = new StreamReader(ns, Encoding.UTF8);

await writer.WriteLineAsync(json);

var line = await reader.ReadLineAsync();
if (string.IsNullOrWhiteSpace(line))
{
    Console.WriteLine("[Client] Empty response.");
    return;
}

// 3) 差分結果の表示
var diff = JsonSerializer.Deserialize<ServerDiffResponse>(line, JsonOptions()) ?? new ServerDiffResponse();
PrintList("ToDownload (server is newer / missing locally)", diff.ToDownload);
PrintList("ToDelete   (not present on server)", diff.ToDelete);
PrintList("UpToDate   (same content)", diff.UpToDate);

static void PrintList(string title, List<string>? list)
{
    list ??= new();
    Console.WriteLine($"\n[{title}] ({list.Count})");
    foreach (var p in list) Console.WriteLine(" - " + p);
}

// ====== DTO ======
public sealed class ClientSnapshot
{
    public List<ClientFileInfo>? Files { get; set; }
}
public sealed class ClientFileInfo
{
    public string FilePath { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }
    public string? Sha256Hex { get; set; }
}
public sealed class ServerDiffResponse
{
    public List<string> ToDownload { get; set; } = new();
    public List<string> ToDelete { get; set; } = new();
    public List<string> UpToDate { get; set; } = new();
}


