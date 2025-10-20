using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;


//// 送信側
//await LegacyCompatFramingStreamedBrotli.SendStreamAsync(
//    ns,
//    File.OpenRead("input.bin"),
//    compress: true,
//    level: CompressionLevel.Optimal);

//// 受信側
//await LegacyCompatFramingStreamedBrotli.ReceiveStreamAsync(
//    ns,
//    File.Create("output.bin"));


/// <summary>
/// Legacy互換のメッセージフレーミング実装です。  
/// Brotli圧縮および64bitサイズヘッダ（<see cref="long"/>）に対応したストリーム転送版です。
/// </summary>
/// <remarks>
/// <para>
/// 本クラスは旧仕様（65532バイト単位のチャンク分割）との互換を保ちながら、  
/// 以下の機能強化を行っています。
/// </para>
/// <list type="bullet">
///   <item><description>サイズヘッダを <see cref="long"/> 化し、理論上TB級のデータ転送に対応。</description></item>
///   <item><description>Brotli圧縮を採用（負のサイズ値が圧縮フラグとして機能）。</description></item>
///   <item><description>データをメモリに展開せず、ストリーム単位で逐次転送。</description></item>
///   <item><description>エンディアンは Little Endian 固定（<see cref="BinaryPrimitives"/> を使用）。</description></item>
/// </list>
/// <para>
/// Brotli による高圧縮と、メモリ効率の良いストリーム転送により、  
/// 実用的かつ高パフォーマンスな大容量通信を実現します。
/// </para>
/// </remarks>
public static class LegacyCompatFramingStreamedBrotli
{
    /// <summary>
    /// 外側フレーム（チャンク）の最大長。旧仕様と互換。
    /// </summary>
    public const int OuterChunkMax = 65532;

    /// <summary>
    /// ストリームをネットワークストリームに分割して送信します。
    /// Brotli圧縮を行い、64bitサイズヘッダで送信します。
    /// </summary>
    /// <param name="ns">送信先の <see cref="NetworkStream"/>。</param>
    /// <param name="source">送信元の <see cref="Stream"/>。</param>
    /// <param name="compress">Brotli圧縮を行う場合は true。</param>
    /// <param name="level">圧縮レベル（Fastest / Optimal / NoCompression）。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    public static async Task SendStreamAsync(
        NetworkStream ns,
        Stream source,
        bool compress = true,
        CompressionLevel level = CompressionLevel.Optimal,
        CancellationToken ct = default)
    {
        if (ns is null) throw new ArgumentNullException(nameof(ns));
        if (source is null) throw new ArgumentNullException(nameof(source));

        Stream inputStream = source;
        MemoryStream? compressed = null;
        bool usedCompression = false;

        if (compress)
        {
            compressed = new MemoryStream();
            using (var brotli = new BrotliStream(compressed, level, leaveOpen: true))
                await source.CopyToAsync(brotli, 64 * 1024, ct);

            compressed.Position = 0;
            usedCompression = compressed.Length < source.Length;
            inputStream = usedCompression ? compressed : source;
        }

        // 内側ヘッダ：負の値は圧縮
        long innerSize = usedCompression ? -inputStream.Length : inputStream.Length;
        await WriteInt64LEAsync(ns, innerSize, ct);

        byte[] buffer = new byte[OuterChunkMax];
        int bytesRead;
        while ((bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await WriteInt32LEAsync(ns, bytesRead, ct);
            await ns.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
        }

        await ns.FlushAsync(ct);
    }

    /// <summary>
    /// ネットワークストリームから受信したデータを復元してストリームに書き込みます。
    /// Brotli圧縮データの場合は自動で解凍されます。
    /// </summary>
    /// <param name="ns">受信元の <see cref="NetworkStream"/>。</param>
    /// <param name="destination">出力先の <see cref="Stream"/>。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    public static async Task ReceiveStreamAsync(NetworkStream ns, Stream destination, CancellationToken ct = default)
    {
        if (ns is null) throw new ArgumentNullException(nameof(ns));
        if (destination is null) throw new ArgumentNullException(nameof(destination));

        long? header = await ReadInt64LEAsync(ns, ct);
        if (header is null) throw new EndOfStreamException("stream closed before inner header");

        bool compressed = header < 0;
        long totalSize = Math.Abs(header.Value);

        Stream targetStream = compressed ? new MemoryStream() : destination;

        byte[] buffer = new byte[OuterChunkMax];
        while (true)
        {
            int? chunkLen = await ReadInt32LEAsync(ns, ct);
            if (chunkLen is null)
                throw new EndOfStreamException("unexpected end of stream while reading chunk length");

            int len = chunkLen.Value;
            if (len <= 0) break;

            await ReadExactAsync(ns, buffer, len, ct);
            await targetStream.WriteAsync(buffer.AsMemory(0, len), ct);

            if (targetStream.Length >= totalSize) break;
        }

        if (compressed)
        {
            targetStream.Position = 0;
            using var brotli = new BrotliStream(targetStream, CompressionMode.Decompress);
            await brotli.CopyToAsync(destination, 64 * 1024, ct);
        }

        await destination.FlushAsync(ct);
    }

    // ===== 内部ユーティリティ =====

    private static async Task WriteInt32LEAsync(NetworkStream ns, int value, CancellationToken ct)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        await ns.WriteAsync(buf, ct);
    }

    private static async Task WriteInt64LEAsync(NetworkStream ns, long value, CancellationToken ct)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, value);
        await ns.WriteAsync(buf, ct);
    }

    private static async Task<int?> ReadInt32LEAsync(NetworkStream ns, CancellationToken ct)
    {
        var buf = new byte[4];
        int off = 0;
        while (off < 4)
        {
            int n = await ns.ReadAsync(buf.AsMemory(off, 4 - off), ct);
            if (n == 0) return null;
            off += n;
        }
        return BinaryPrimitives.ReadInt32LittleEndian(buf);
    }

    private static async Task<long?> ReadInt64LEAsync(NetworkStream ns, CancellationToken ct)
    {
        var buf = new byte[8];
        int off = 0;
        while (off < 8)
        {
            int n = await ns.ReadAsync(buf.AsMemory(off, 8 - off), ct);
            if (n == 0) return null;
            off += n;
        }
        return BinaryPrimitives.ReadInt64LittleEndian(buf);
    }

    private static async Task ReadExactAsync(NetworkStream ns, byte[] buf, int count, CancellationToken ct)
    {
        int off = 0;
        while (off < count)
        {
            int n = await ns.ReadAsync(buf.AsMemory(off, count - off), ct);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }
}
