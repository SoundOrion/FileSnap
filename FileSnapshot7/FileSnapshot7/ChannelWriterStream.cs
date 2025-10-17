using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FileSnapshot7;

// ===== ChannelWriterStream: Deflate 出力 → Channel<byte[]> へチャンク化 =====
internal sealed class ChannelWriterStream : Stream
{
    private readonly ChannelWriter<byte[]> _writer; private readonly int _chunk; private byte[] _buf; private int _fill; private bool _completed;
    public ChannelWriterStream(ChannelWriter<byte[]> writer, int chunkSize = 128 * 1024) { _writer = writer; _chunk = chunkSize; _buf = new byte[_chunk]; }
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> src, CancellationToken ct = default)
    {
        var mem = src;
        while (!mem.IsEmpty)
        {
            int space = _chunk - _fill; int toCopy = Math.Min(space, mem.Length);
            mem.Span[..toCopy].CopyTo(_buf.AsSpan(_fill)); _fill += toCopy; mem = mem[toCopy..];
            if (_fill == _chunk)
            {
                var full = _buf; _buf = new byte[_chunk]; _fill = 0;
                await _writer.WriteAsync(full, ct);
            }
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

// ===== ChannelReaderStream: Channel<byte[]> → Stream 変換 =====
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

//internal sealed class ChannelWriterStream : Stream
//{
//    private readonly ChannelWriter<byte[]> _writer;
//    private readonly int _chunkSize = 128 * 1024;
//    private byte[] _buf;
//    private int _fill;
//    private bool _done;

//    public ChannelWriterStream(ChannelWriter<byte[]> writer)
//    { _writer = writer; _buf = new byte[_chunkSize]; }

//    public override bool CanRead => false;
//    public override bool CanSeek => false;
//    public override bool CanWrite => true;
//    public override long Length => throw new NotSupportedException();
//    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

//    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> src, CancellationToken ct = default)
//    {
//        var mem = src;
//        while (!mem.IsEmpty)
//        {
//            int space = _chunkSize - _fill;
//            int n = Math.Min(space, mem.Length);
//            mem.Span[..n].CopyTo(_buf.AsSpan(_fill));
//            _fill += n;
//            mem = mem[n..];

//            if (_fill == _chunkSize)
//            {
//                var full = _buf;
//                _buf = new byte[_chunkSize];
//                _fill = 0;
//                await _writer.WriteAsync(full, ct);
//            }
//        }
//    }

//    public override async ValueTask DisposeAsync()
//    {
//        if (_done) return;
//        _done = true;
//        if (_fill > 0)
//        {
//            var last = new byte[_fill];
//            Buffer.BlockCopy(_buf, 0, last, 0, _fill);
//            await _writer.WriteAsync(last);
//        }
//        _writer.TryComplete();
//    }

//    public override void Flush() { }
//    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
//    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
//    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
//    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
//    public override void SetLength(long value) => throw new NotSupportedException();
//}
