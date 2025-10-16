using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

public sealed class AcceptService : BackgroundService
{
    private readonly ILogger<AcceptService> _log;
    private readonly ChannelWriter<SyncJob> _writer;
    private readonly int _port = 5001;

    public AcceptService(ILogger<AcceptService> log, ChannelWriter<SyncJob> writer)
    { _log = log; _writer = writer; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _log.LogInformation("Listening on {Port}", _port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
            _writer.TryComplete();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var ns = client.GetStream();
        var bs = new BufferedStream(ns, 64 * 1024);
        var reader = new StreamReader(bs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var writer = new StreamWriter(bs, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

        try
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
            {
                await writer.WriteLineAsync("ERROR|empty");
                await writer.FlushAsync();
                return;
            }

            // 形式: SYNCGET|<filePath>|<clientUnixMs>|<clientSize>
            if (!line.StartsWith("SYNCGET|", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("ERROR|unknown_command");
                await writer.FlushAsync();
                return;
            }

            var parts = line.Split('|', 4, StringSplitOptions.None);
            if (parts.Length != 4 ||
                !long.TryParse(parts[2], out var clientUnixMs) ||
                !long.TryParse(parts[3], out var clientSize))
            {
                await writer.WriteLineAsync("ERROR|bad_request");
                await writer.FlushAsync();
                return;
            }

            var filePath = parts[1];

            // enqueue → 完了待ち（この間は writer/bs/ns を開いたまま保持）
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _writer.WriteAsync(new SyncJob(
                FilePath: filePath,
                ClientUnixMs: clientUnixMs,
                ClientSize: clientSize,
                Stream: bs,
                Writer: writer,
                Completion: tcs
            ), ct);
            await tcs.Task; // 送信完了まで待つ
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { await writer.WriteLineAsync("ERROR|internal"); await writer.FlushAsync(); } catch { }
            _log.LogWarning(ex, "Client error");
        }
        finally
        {
            try { await writer.FlushAsync(); } catch { }
            reader.Dispose();
            writer.Dispose();
            bs.Dispose();
            ns.Dispose();
        }
    }
}
