// ====== Producer: TCP 受信→ChannelWriter へ enqueue → TCS await して応答 ======
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
                _ = HandleClientAsync(client, stoppingToken); // 軽いI/Oなので fire-and-track でもOK
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
        finally
        {
            listener.Stop();
            _writer.TryComplete(); // 受け付け終了
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        //await using var _ = client;
        using var ns = client.GetStream();
        using var reader = new StreamReader(ns, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true };

        try
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
            {
                await writer.WriteLineAsync("{\"error\":\"empty\"}");
                return;
            }

            // enqueue → 完了待ち
            var tcs = new TaskCompletionSource<ServerSyncResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _writer.WriteAsync(new SyncJob(line!, tcs), ct);
            var result = await tcs.Task;

            var json = JsonSerializer.Serialize(result, JsonOpt());
            await writer.WriteLineAsync(json);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { await writer.WriteLineAsync("{\"error\":\"internal\"}"); } catch { }
            _log.LogWarning(ex, "Client error");
        }
    }

    private static JsonSerializerOptions JsonOpt() => new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}