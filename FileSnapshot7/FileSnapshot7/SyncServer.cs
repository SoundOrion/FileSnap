using FileSnapshot7;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

public sealed class SyncServer : BackgroundService
{
    private readonly ILogger<SyncServer> _log; private readonly IFileIndex _index;
    private readonly int _port = 5001; private readonly int _maxClients = Math.Max(64, Environment.ProcessorCount * 128);
    public SyncServer(ILogger<SyncServer> log, IFileIndex index) { _log = log; _index = index; }

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
        using var ns = client.GetStream(); var sjis = Encoding.GetEncoding("shift_jis");
        var lenBuf = new byte[4];

        while (true)
        {
            // === 圧縮コマンド (SYNCGET): [len(LE)][payload]... EOF(len=0) を1本の Deflate ストリームとして受信 ===
            var ch = Channel.CreateBounded<byte[]>(
                new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

            var producer = Task.Run(async () =>
            {
                while (true)
                {
                    if (!await ReadExactAsync(ns, lenBuf, 4, ct)) { ch.Writer.TryComplete(); return; }
                    int sz = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                    if (sz == 0) { ch.Writer.TryComplete(); return; } // EOF
                    if (sz < 0 || sz > 100_000_000) { ch.Writer.TryComplete(new InvalidDataException("bad length")); return; }

                    var buf = new byte[sz];
                    if (!await ReadExactAsync(ns, buf, sz, ct)) { ch.Writer.TryComplete(new EndOfStreamException()); return; }
                    await ch.Writer.WriteAsync(buf, ct);
                }
            }, ct);

            await using var src = new ChannelReaderStream(ch.Reader);
            using var def = new DeflateStream(src, CompressionMode.Decompress, leaveOpen: false);
            using var ms = new MemoryStream();
            await def.CopyToAsync(ms, ct);
            await producer;

            var cmd = sjis.GetString(ms.ToArray());
            _log.LogInformation("CMD: {Cmd}", cmd);

            if (!cmd.StartsWith("SYNCGET|", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = cmd.Split('|', 4);
            if (parts.Length != 4 || !long.TryParse(parts[2], out var clientUnixMs) || !long.TryParse(parts[3], out var clientSize))
                continue;

            await SendFileIfUpdatedAsync(parts[1], clientUnixMs, clientSize, ns, ct);
        }
    }

    private async Task SendFileIfUpdatedAsync(string filePath, long clientUnixMs, long clientSize, NetworkStream ns, CancellationToken ct)
    {
        if (!_index.Snapshot.TryGetValue(filePath, out var info) || !File.Exists(info.FilePath))
        { await SendCtrlAsync(ns, "NOTFOUND", ct); return; }

        var fi = new FileInfo(info.FilePath);
        bool modified = fi.Length != clientSize || new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() > clientUnixMs;
        if (!modified) { await SendCtrlAsync(ns, "NOTMODIFIED", ct); return; }

        await SendCtrlAsync(ns, $"FILE|{Path.GetFileName(info.FilePath)}|{fi.Length}", ct);

        // 圧縮 + Channel 送信（[len(LE)][payload]... EOF=0）
        var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

        var sender = Task.Run(async () =>
        {
            await foreach (var chunk in ch.Reader.ReadAllAsync(ct))
            {
                await WriteInt32LEAsync(ns, chunk.Length, ct);
                await ns.WriteAsync(chunk, ct);
                await ns.FlushAsync(ct);
            }
            await WriteInt32LEAsync(ns, 0, ct); // EOF
            await ns.FlushAsync(ct);
        }, ct);

        await using (var sink = new ChannelWriterStream(ch.Writer, 128 * 1024))
        await using (var def = new DeflateStream(sink, CompressionLevel.Fastest, leaveOpen: true))
        await using (var fs = new FileStream(info.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan))
        {
            await fs.CopyToAsync(def, ct);
        }
        await sender;
    }

    private static async Task SendCtrlAsync(NetworkStream ns, string msg, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(msg);
        await WriteInt32LEAsync(ns, data.Length, ct);
        await ns.WriteAsync(data, ct);
        await ns.FlushAsync(ct);
    }

    private static async Task WriteInt32LEAsync(NetworkStream ns, int value, CancellationToken ct)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        await ns.WriteAsync(buf, ct);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream ns, byte[] buf, int count, CancellationToken ct)
    {
        int off = 0;
        while (off < count)
        {
            int n = await ns.ReadAsync(buf.AsMemory(off, count - off), ct);
            if (n == 0) return false; off += n;
        }
        return true;
    }
}