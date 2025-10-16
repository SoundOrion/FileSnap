using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

public static class Program
{
    public static async Task Main(string[] args)
    {
        // --- 設定 ---
        string host = "127.0.0.1";
        int port = 5001;
        string filePath = @"D:\data\test.zip";

        Console.WriteLine($"サーバー {host}:{port} に接続し、{filePath} の更新を確認します…");

        await SyncClient.RunAsync(host, port, filePath);

        Console.WriteLine("完了しました。Enterで終了。");
        Console.ReadLine();
    }
}

public static class SyncClient
{
    public static async Task RunAsync(string host, int port, string filePath)
    {
        var fi = new FileInfo(filePath);
        long clientUnixMs = fi.Exists ? new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() : 0;
        long clientSize = fi.Exists ? fi.Length : 0;

        using var client = new TcpClient();
        await client.ConnectAsync(host, port);
        using var ns = client.GetStream();
        using var bs = new BufferedStream(ns, 64 * 1024);
        using var reader = new StreamReader(bs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(bs, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        // --- 要求送信 ---
        string request = $"SYNCGET|{filePath}|{clientUnixMs}|{clientSize}";
        await writer.WriteLineAsync(request);
        await writer.FlushAsync();

        // --- 応答ヘッダ受信 ---
        string? header = await reader.ReadLineAsync();
        if (header == null)
        {
            Console.WriteLine("サーバーから応答なし");
            return;
        }

        if (header.StartsWith("NOTMODIFIED"))
        {
            Console.WriteLine("変更なし");
            return;
        }
        if (header.StartsWith("NOTFOUND"))
        {
            Console.WriteLine("ファイルがサーバーに存在しません");
            return;
        }
        if (header.StartsWith("ERROR"))
        {
            Console.WriteLine($"サーバーエラー: {header}");
            return;
        }

        if (header.StartsWith("FILEB64|"))
        {
            var parts = header.Split('|');
            if (parts.Length < 3)
            {
                Console.WriteLine("ヘッダ形式エラー");
                return;
            }

            string name = parts[1];
            long expectedSize = long.Parse(parts[2]);
            string tempFile = Path.Combine(Path.GetTempPath(), name);

            Console.WriteLine($"ファイル更新あり: {name}, size={expectedSize}");

            using var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
            using var base64 = new System.Security.Cryptography.FromBase64Transform();

            byte[] inBuf = new byte[64 * 1024];
            byte[] outBuf = new byte[inBuf.Length];

            int read;
            while ((read = await bs.ReadAsync(inBuf, 0, inBuf.Length)) > 0)
            {
                // Base64デコード
                int whole = read - (read % 4);
                if (whole > 0)
                {
                    int outN = base64.TransformBlock(inBuf, 0, whole, outBuf, 0);
                    await fs.WriteAsync(outBuf.AsMemory(0, outN));
                }

                // 最後の断片処理
                if (read < inBuf.Length)
                {
                    var final = base64.TransformFinalBlock(inBuf, whole, read - whole);
                    await fs.WriteAsync(final);
                    break;
                }
            }

            Console.WriteLine($"ダウンロード完了: {tempFile}");
            return;
        }

        Console.WriteLine($"未知の応答: {header}");
    }
}
