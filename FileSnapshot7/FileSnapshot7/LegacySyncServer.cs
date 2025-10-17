using FileSnapshot7;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

public sealed class LegacySyncServer : BackgroundService
{
    private readonly ILogger<SyncServer> _log;
    private readonly IFileIndex _index;
    private readonly int _port = 5001;
    private readonly int _maxClients = Math.Max(64, Environment.ProcessorCount * 128);

    public LegacySyncServer(ILogger<SyncServer> log, IFileIndex index)
    {
        _log = log;
        _index = index;
        _maxClients = ServerTuning.EstimateMaxClients(); // 自動推定（既存ロジック）
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port); listener.Start();
        _log.LogInformation("Server listening on {Port}, max {Max}", _port, _maxClients);
        using var gate = new SemaphoreSlim(_maxClients);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                try
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = 60_000;
                    client.SendTimeout = 60_000;
                }
                catch { /* ignore */ }

                if (!await gate.WaitAsync(TimeSpan.FromSeconds(5), ct))
                { client.Close(); continue; }

                _ = Task.Run(async () =>
                {
                    try { await HandleClientAsync(client, ct); }
                    catch (Exception ex) { _log.LogWarning(ex, "client error"); }
                    finally { client.Close(); gate.Release(); }
                }, ct);
            }
        }
        finally { listener.Stop(); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var ns = client.GetStream();
        var sjis = Encoding.GetEncoding("shift_jis");

        while (!ct.IsCancellationRequested)
        {
            // 旧互換: 1メッセージ受信（内側ヘッダ±サイズ → 65532分割、EOFなし）
            string? cmd;
            try
            {
                cmd = await LegacyCompatFraming.ReceiveStringAsync(ns, ct);
            }
            catch (EndOfStreamException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "recv failed"); break; }

            if (string.IsNullOrEmpty(cmd)) continue;

            // もしクライアントが Shift_JIS で送ってくる場合はこちらを使う:
            // var raw = await LegacyCompatFraming.ReceiveMessageAsync(ns, ct);
            // var cmd = sjis.GetString(raw);

            _log.LogInformation("CMD: {Cmd}", cmd);
            if (!cmd.StartsWith("SYNCGET|", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = cmd.Split('|', 4);
            if (parts.Length != 4 ||
                !long.TryParse(parts[2], out var clientUnixMs) ||
                !long.TryParse(parts[3], out var clientSize))
                continue;

            await SendFileIfUpdatedAsync(parts[1], clientUnixMs, clientSize, ns, ct);
        }
    }

    private async Task SendFileIfUpdatedAsync(string filePath, long clientUnixMs, long clientSize, NetworkStream ns, CancellationToken ct)
    {
        if (!_index.Snapshot.TryGetValue(filePath, out var info) || !File.Exists(info.FilePath))
        {
            // 旧互換：制御も1メッセージとして送信
            await LegacyCompatFraming.SendStringAsync(ns, "NOTFOUND", ct);
            return;
        }

        var fi = new FileInfo(info.FilePath);
        bool modified = fi.Length != clientSize || new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() > clientUnixMs;
        if (!modified)
        {
            await LegacyCompatFraming.SendStringAsync(ns, "NOTMODIFIED", ct);
            return;
        }

        // 1) 制御メッセージ（UTF-8）
        await LegacyCompatFraming.SendStringAsync(ns, $"FILE|{Path.GetFileName(info.FilePath)}|{fi.Length}", ct);

        // 2) 本体（旧互換：内側ヘッダ±サイズ → 65532分割、EOFなし）
        byte[] fileBytes;
        await using (var fs = new FileStream(info.FilePath, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan))
        {
            fileBytes = new byte[fs.Length];
            int read = 0;
            while (read < fileBytes.Length)
            {
                int n = await fs.ReadAsync(fileBytes.AsMemory(read, fileBytes.Length - read), ct);
                if (n == 0) break;
                read += n;
            }
            if (read != fileBytes.Length) throw new IOException("Failed to read full file.");
        }

        await LegacyCompatFraming.SendMessageAsync(ns, fileBytes, ct);
    }
}
