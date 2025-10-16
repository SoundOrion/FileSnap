using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;

public sealed class SyncServer : BackgroundService
{
    private readonly ILogger<SyncServer> _log;
    private readonly IFileIndex _index;
    private readonly int _port = 5001;
    private readonly int _maxClients;

    public SyncServer(ILogger<SyncServer> log, IFileIndex index)
    {
        _log = log;
        _index = index;
        _maxClients = ServerTuning.EstimateMaxClients(); // 自動推定
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _log.LogInformation("Listening on {Port}, max concurrent clients = {Max}", _port, _maxClients);

        using var gate = new SemaphoreSlim(_maxClients);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);

                // 5秒以内に空かない場合は拒否
                if (!await gate.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken))
                {
                    await RejectAsync(client);
                    continue;
                }

                // Task.Runで非同期ハンドル（簡潔・安全）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleClientAsync(client, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Error in client handling");
                    }
                    finally
                    {
                        gate.Release();
                        client.Close();
                    }
                }, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task RejectAsync(TcpClient client)
    {
        try
        {
            using var ns = client.GetStream();
            using var w = new StreamWriter(ns, Encoding.UTF8);
            await w.WriteLineAsync("ERROR|server_busy");
            await w.FlushAsync();
        }
        catch { }
        finally
        {
            client.Close();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var ns = client.GetStream();
        using var bs = new BufferedStream(ns, 64 * 1024);
        using var reader = new StreamReader(bs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        using var writer = new StreamWriter(bs, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        _log.LogInformation("Accepted {Client}", client.Client.RemoteEndPoint);

        try
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) return;

            if (!line.StartsWith("SYNCGET|", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("ERROR|unknown_command");
                return;
            }

            var parts = line.Split('|', 4);
            if (parts.Length != 4 ||
                !long.TryParse(parts[2], out var clientUnixMs) ||
                !long.TryParse(parts[3], out var clientSize))
            {
                await writer.WriteLineAsync("ERROR|bad_request");
                return;
            }

            await SendFileIfUpdatedAsync(parts[1], clientUnixMs, clientSize, writer, bs, ct);
        }
        finally
        {
            _log.LogInformation("Closed {Client}", client.Client.RemoteEndPoint);
        }
    }

    private async Task SendFileIfUpdatedAsync(string filePath, long clientUnixMs, long clientSize,
        StreamWriter writer, Stream stream, CancellationToken ct)
    {
        var snap = _index.Snapshot;
        if (!snap.TryGetValue(filePath, out var info) || !File.Exists(info.FilePath))
        {
            await writer.WriteLineAsync("NOTFOUND");
            return;
        }

        var fi = new FileInfo(info.FilePath);
        var serverSize = fi.Length;
        var serverUnixMs = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var modified = serverSize != clientSize || serverUnixMs > clientUnixMs;

        if (!modified)
        {
            await writer.WriteLineAsync("NOTMODIFIED");
            return;
        }

        await writer.WriteLineAsync($"FILEB64|{Path.GetFileName(info.FilePath)}|{serverSize}");

        using var fs = new FileStream(info.FilePath, FileMode.Open, FileAccess.Read,
                                      FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
        using var transform = new System.Security.Cryptography.ToBase64Transform();

        byte[] inBuf = new byte[57 * 1024];
        byte[] outBuf = new byte[inBuf.Length * 2];

        while (true)
        {
            int n = await fs.ReadAsync(inBuf.AsMemory(0, inBuf.Length), ct);
            if (n <= 0) break;

            int whole = n - (n % transform.InputBlockSize);
            if (whole > 0)
            {
                int outN = transform.TransformBlock(inBuf, 0, whole, outBuf, 0);
                await stream.WriteAsync(outBuf.AsMemory(0, outN), ct);
            }

            if (whole != n)
            {
                var final = transform.TransformFinalBlock(inBuf, whole, n - whole);
                await stream.WriteAsync(final, ct);
                break;
            }
        }

        await stream.FlushAsync(ct);
    }
}
