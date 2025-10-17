// Client.cs (.NET 8, C# 12 OK)
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

public static class SyncClient
{
    public static async Task Main(string[] args)
    {
        var sjis = Encoding.GetEncoding("shift_jis");
        using var client = new TcpClient();
        client.NoDelay = true;
        client.ReceiveTimeout = 60_000;
        client.SendTimeout = 60_000;

        await client.ConnectAsync("127.0.0.1", 5001);
        using var ns = client.GetStream();

        // 1) SYNCGET コマンドを Deflate + Channel で送信（[len(LE)][payload]... EOF=0）
        string cmd = "SYNCGET|D:\\data\\sample.txt|1734000000000|512"; // 適宜変更（mtime は UTC の Unix ms）
        await SendCompressedCommandAsync(ns, sjis.GetBytes(cmd));

        // 2) サーバーのレスポンス受信（CTRL フレーム → FILE 本体は Deflate + Channel で復元）
        await ReceiveLoopAsync(ns);
    }

    private static async Task SendCompressedCommandAsync(NetworkStream ns, byte[] commandBytes, CancellationToken ct = default)
    {
        var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

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

        await using (var sink = new ChannelWriterStream(ch.Writer, 64 * 1024))
        await using (var def = new DeflateStream(sink, CompressionLevel.Fastest, leaveOpen: true))
        {
            await def.WriteAsync(commandBytes, ct);
            await def.FlushAsync(ct);
        }
        await sender;
        Console.WriteLine("Client: SYNCGET sent");
    }

    private static async Task ReceiveLoopAsync(NetworkStream ns, CancellationToken ct = default)
    {
        var lenBuf = new byte[4];

        while (true)
        {
            // CTRL フレーム（UTF-8）: NOTFOUND / NOTMODIFIED / FILE|name|rawSize
            if (!await ReadExactAsync(ns, lenBuf, 4, ct)) break;
            int ctrlLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
            if (ctrlLen <= 0 || ctrlLen > 1_000_000) break;

            var ctrl = new byte[ctrlLen];
            if (!await ReadExactAsync(ns, ctrl, ctrlLen, ct)) break;
            var msg = Encoding.UTF8.GetString(ctrl);
            Console.WriteLine($"CTRL: {msg}");

            if (msg.StartsWith("NOTFOUND") || msg.StartsWith("NOTMODIFIED"))
                continue;

            if (msg.StartsWith("FILE|"))
            {
                var parts = msg.Split('|', 3);
                string name = parts[1];
                string outPath = Path.Combine(Path.GetTempPath(), $"recv_{name}");

                // 圧縮データ受信用 Channel
                var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

                // Producer: Network → Channel （[len(LE)][payload] ... EOF=0）
                var producer = Task.Run(async () =>
                {
                    while (true)
                    {
                        if (!await ReadExactAsync(ns, lenBuf, 4, ct)) { ch.Writer.TryComplete(); return; }
                        int sz = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                        if (sz == 0) { ch.Writer.TryComplete(); return; }
                        if (sz < 0 || sz > 100_000_000) { ch.Writer.TryComplete(new InvalidDataException("bad data length")); return; }

                        var buf = new byte[sz];
                        if (!await ReadExactAsync(ns, buf, sz, ct)) { ch.Writer.TryComplete(new EndOfStreamException()); return; }
                        await ch.Writer.WriteAsync(buf, ct);
                    }
                }, ct);

                // Consumer: Channel → Deflate → File
                await using var src = new ChannelReaderStream(ch.Reader);
                using var def = new DeflateStream(src, CompressionMode.Decompress, leaveOpen: false);
                await using var fout = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
                await def.CopyToAsync(fout, ct);
                await fout.FlushAsync(ct);

                await producer;
                Console.WriteLine($"Client: received file -> {outPath}");
            }
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream ns, byte[] buf, int count, CancellationToken ct = default)
    {
        int off = 0;
        while (off < count)
        {
            int n = await ns.ReadAsync(buf.AsMemory(off, count - off), ct);
            if (n == 0) return false; off += n;
        }
        return true;
    }

    private static async Task WriteInt32LEAsync(NetworkStream ns, int value, CancellationToken ct)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        await ns.WriteAsync(buf, ct);
    }
}

// ===== ChannelWriterStream / ChannelReaderStream =====
internal sealed class ChannelWriterStream : Stream
{
    private readonly ChannelWriter<byte[]> _writer; private readonly int _chunk; private byte[] _buf; private int _fill; private bool _completed;
    public ChannelWriterStream(ChannelWriter<byte[]> writer, int chunkSize = 64 * 1024) { _writer = writer; _chunk = chunkSize; _buf = new byte[_chunk]; }
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> src, CancellationToken ct = default)
    {
        var mem = src;
        while (!mem.IsEmpty)
        {
            int space = _chunk - _fill; int n = Math.Min(space, mem.Length);
            mem.Span[..n].CopyTo(_buf.AsSpan(_fill)); _fill += n; mem = mem[n..];
            if (_fill == _chunk) { var full = _buf; _buf = new byte[_chunk]; _fill = 0; await _writer.WriteAsync(full, ct); }
        }
    }
    public override async ValueTask DisposeAsync()
    {
        if (_completed) return; _completed = true;
        if (_fill > 0) { var last = new byte[_fill]; Buffer.BlockCopy(_buf, 0, last, 0, _fill); await _writer.WriteAsync(last); _fill = 0; }
        _writer.TryComplete();
    }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class ChannelReaderStream : Stream
{
    private readonly ChannelReader<byte[]> _reader; private ReadOnlyMemory<byte> _current; private bool _completed;
    public ChannelReaderStream(ChannelReader<byte[]> reader) { _reader = reader; }
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> dest, CancellationToken ct = default)
    {
        if (_completed) return 0;
        if (_current.IsEmpty)
        {
            if (!await _reader.WaitToReadAsync(ct) || !_reader.TryRead(out var next)) { _completed = true; return 0; }
            _current = next;
        }
        int n = Math.Min(dest.Length, _current.Length);
        _current.Span[..n].CopyTo(dest.Span); _current = _current[n..]; return n;
    }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
