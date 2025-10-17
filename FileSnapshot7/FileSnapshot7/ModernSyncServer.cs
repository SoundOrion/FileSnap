// Server.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

public sealed class ModernSyncServer : BackgroundService
{
    private readonly ILogger<SyncServer> _log;
    private readonly IFileIndex _index;               // 手持ちのインデックスを使用（必要なければ直接 FileInfo でもOK）
    private readonly int _port = 5001;
    private readonly int _maxClients;

    public ModernSyncServer(ILogger<SyncServer> log, IFileIndex index)
    {
        _log = log;
        _index = index;
        _maxClients = ServerTuning.EstimateMaxClients(); // 既存の自動推定
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();
        _log.LogInformation("Listening on {Port}, max clients {Max}", _port, _maxClients);

        using var gate = new SemaphoreSlim(_maxClients);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);

                if (!await gate.WaitAsync(TimeSpan.FromSeconds(5), ct))
                {
                    client.Close();
                    continue;
                }

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
        _log.LogInformation("Accepted {Remote}", client.Client.RemoteEndPoint);

        while (!ct.IsCancellationRequested)
        {
            // 1) 制御フレーム受信（UTF-8 1発）
            var ctrl = await ReadControlAsync(ns, ct);
            if (ctrl == null) break; // 切断

            if (!ctrl.StartsWith("SYNCGET|", StringComparison.OrdinalIgnoreCase))
            {
                await WriteControlAsync(ns, "ERROR|unknown_command", ct);
                continue;
            }

            var parts = ctrl.Split('|', 4);
            if (parts.Length != 4 ||
                !long.TryParse(parts[2], out var clientUnixMs) ||
                !long.TryParse(parts[3], out var clientSize))
            {
                await WriteControlAsync(ns, "ERROR|bad_request", ct);
                continue;
            }

            await SendFileIfUpdatedAsync(parts[1], clientUnixMs, clientSize, ns, ct);
        }

        _log.LogInformation("Closed {Remote}", client.Client.RemoteEndPoint);
    }

    private async Task SendFileIfUpdatedAsync(string filePath, long clientUnixMs, long clientSize,
        NetworkStream ns, CancellationToken ct)
    {
        // ここは手持ちのインデックスを使う例。直に File.Exists/Info でも可。
        if (!_index.Snapshot.TryGetValue(filePath, out var info) || !File.Exists(info.FilePath))
        {
            await WriteControlAsync(ns, "NOTFOUND", ct);
            return;
        }

        var fi = new FileInfo(info.FilePath);
        var serverSize = fi.Length;
        var serverUnixMs = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds();
        var modified = serverSize != clientSize || serverUnixMs > clientUnixMs;

        if (!modified)
        {
            await WriteControlAsync(ns, "NOTMODIFIED", ct);
            return;
        }

        // 2) 制御フレーム（UTF-8 1発）
        await WriteControlAsync(ns, $"SYNCFILE|{Path.GetFileName(info.FilePath)}|{serverSize}", ct);

        // 3) 本体（Deflate → [len][payload]... + EOF=0）を逐次送出
        await using var fw = new FrameWriterStream(ns, chunkSize: 128 * 1024, ct);
        await using var def = new DeflateStream(fw, CompressionLevel.Fastest, leaveOpen: true);

        await using var fs = new FileStream(info.FilePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);

        await fs.CopyToAsync(def, ct); // Channelなしで逐次圧縮→逐次フレーム送出
        // DisposeAsync() が最後に EOF(len=0) を送る
    }

    // ========= 長さ付きフレーム（制御用） =========
    private static async Task WriteControlAsync(NetworkStream ns, string message, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        await WriteFrameAsync(ns, payload, ct);
    }

    private static async Task<string?> ReadControlAsync(NetworkStream ns, CancellationToken ct)
    {
        var payload = await ReadFrameAsync(ns, ct);
        return payload == null ? null : Encoding.UTF8.GetString(payload);
    }

    // ========= フレームI/O（[len(LE)][payload]） =========
    private static async Task WriteFrameAsync(Stream s, byte[] payload, CancellationToken ct)
    {
        var len = new byte[4]; // ← 配列なら await またいでもOK
        BinaryPrimitives.WriteInt32LittleEndian(len, payload.Length);

        await s.WriteAsync(len.AsMemory(0, 4), ct);                 // ← ReadOnlyMemory<byte>
        await s.WriteAsync(payload.AsMemory(0, payload.Length), ct); // ← ReadOnlyMemory<byte>
        await s.FlushAsync(ct);
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        byte[] lenBuf = new byte[4];
        if (!await ReadExactAsync(s, lenBuf, 4, ct)) return null; // 切断
        int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (len < 0 || len > 100_000_000) throw new InvalidDataException($"bad frame length {len}");
        if (len == 0) return Array.Empty<byte>(); // ここは通常 控制では使わない（EOFは本体用）
        var payload = new byte[len];
        if (!await ReadExactAsync(s, payload, len, ct)) return null;
        return payload;
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int off = 0;
        while (off < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(off, count - off), ct);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }
}

// ====== Deflate 出力を [len][payload]… + EOF=0 で直接ネットに流す（Channelなし） ======
internal sealed class FrameWriterStream : Stream
{
    private readonly Stream _s;
    private readonly byte[] _buf;
    private int _fill;
    private readonly CancellationToken _ct;
    private bool _disposed;

    public FrameWriterStream(Stream s, int chunkSize, CancellationToken ct)
    {
        _s = s; _buf = new byte[chunkSize]; _ct = ct;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> src, CancellationToken cancellationToken = default)
    {
        var mem = src;
        while (!mem.IsEmpty)
        {
            int space = _buf.Length - _fill;
            int take = Math.Min(space, mem.Length);
            mem.Span[..take].CopyTo(_buf.AsSpan(_fill));
            _fill += take;
            mem = mem[take..];

            if (_fill == _buf.Length)
                await FlushChunkAsync(_buf, _fill);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        if (_fill > 0) await FlushChunkAsync(_buf, _fill);

        // EOF (len=0)
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, 0);
        await _s.WriteAsync(len.AsMemory(0, 4), _ct);
        await _s.FlushAsync(_ct);
    }

    private async Task FlushChunkAsync(byte[] src, int count)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(len, count);
        await _s.WriteAsync(len.AsMemory(0, 4), _ct);
        await _s.WriteAsync(src.AsMemory(0, count), _ct);
        await _s.FlushAsync(_ct);
        _fill = 0;
    }

    // 使わないAPI
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

