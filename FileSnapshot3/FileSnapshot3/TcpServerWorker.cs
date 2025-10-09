using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FileSnapshot3;

// ========== Worker #2: TCPサーバ ==========
public sealed class TcpServerWorker : BackgroundService
{
    private readonly ILogger<TcpServerWorker> _logger;
    private readonly IFileIndex _index;
    private readonly int _port = 5001;

    public TcpServerWorker(ILogger<TcpServerWorker> logger, IFileIndex index)
    {
        _logger = logger;
        _index = index;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _logger.LogInformation("TCP server listening on {Port}", _port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken); // 投げっぱなしタスク
            }
        }
        catch (OperationCanceledException) { /* graceful stop */ }
        finally { listener.Stop(); }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        //await using var _ = client;
        client.NoDelay = true;

        var ep = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogInformation("Client connected: {EP}", ep);

        try
        {
            using var ns = client.GetStream();
            using var reader = new StreamReader(ns, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true };

            // 1) 1行 JSON を受信（クライアント側のスナップショット）
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line))
            {
                await writer.WriteLineAsync("{\"error\":\"empty payload\"}");
                return;
            }

            var clientSnap = JsonSerializer.Deserialize<ClientSnapshot>(line!, JsonOptions()) ?? new ClientSnapshot();

            // 2) サーバ側スナップショットと差分計算
            var server = _index.Snapshot;

            // 辞書化
            var clientMap = clientSnap.Files?.ToDictionary(
                f => f.FilePath, f => f, StringComparer.OrdinalIgnoreCase
            ) ?? new Dictionary<string, ClientFileInfo>(StringComparer.OrdinalIgnoreCase);

            var toDownload = new List<string>(); // サーバが新しい or クライアントに無い → クライアントはダウンロード
            var toDelete = new List<string>(); // サーバに無い → クライアントは削除
            var upToDate = new List<string>(); // 同一

            // サーバ視点
            foreach (var (path, s) in server)
            {
                if (!clientMap.TryGetValue(path, out var c))
                {
                    toDownload.Add(path);
                }
                else
                {
                    if (HashesEqual(s.Hash, c.Sha256Hex))
                        upToDate.Add(path);
                    else
                        toDownload.Add(path); // サーバ版で更新
                }
            }

            // クライアントにあってサーバにない
            foreach (var (path, _) in clientMap)
            {
                if (!server.ContainsKey(path))
                    toDelete.Add(path);
            }

            var resp = new ServerDiffResponse
            {
                ToDownload = toDownload,
                ToDelete = toDelete,
                UpToDate = upToDate
            };

            // 3) 応答
            var json = JsonSerializer.Serialize(resp, JsonOptions());
            await writer.WriteLineAsync(json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Client error: {EP}", ep);
        }
        finally
        {
            _logger.LogInformation("Client disconnected: {EP}", ep);
        }
    }

    private static bool HashesEqual(byte[] serverHash, string? clientHex)
    {
        if (string.IsNullOrWhiteSpace(clientHex)) return false;
        try
        {
            var span = clientHex.AsSpan();
            if (span.Length != 64) return false; // SHA256 = 32 bytes = 64 hex chars
            Span<byte> buf = stackalloc byte[32];
            for (int i = 0; i < 32; i++)
            {
                buf[i] = Convert.ToByte(new string(new[] { (char)span[i * 2], (char)span[i * 2 + 1] }), 16);
            }
            return serverHash.AsSpan().SequenceEqual(buf);
        }
        catch { return false; }
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}