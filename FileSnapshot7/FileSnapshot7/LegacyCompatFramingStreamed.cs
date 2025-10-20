using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Legacy互換のメッセージフレーミング（ストリーム転送対応版）
/// <para>
/// ・旧方式（65532バイト分割、Int32ヘッダ付き）との互換性を維持。<br/>
/// ・データを全てメモリに展開せず、ストリーム経由で逐次転送する。<br/>
/// ・圧縮は DeflateStream を使用（負の長さ値で示す）。
/// </para>
/// </summary>
public static class LegacyCompatFramingStreamed
{
    /// <summary>
    /// 外側フレーム（チャンク）の最大長。
    /// 旧仕様（65532バイト）と互換を保ちます。
    /// </summary>
    public const int OuterChunkMax = 65532;

    /// <summary>
    /// ストリームをネットワークストリームに分割送信します。
    /// メモリに全体を展開せず、入力ストリームから一定サイズごとに読み取り送信します。
    /// </summary>
    /// <param name="ns">送信先の <see cref="NetworkStream"/>。</param>
    /// <param name="source">送信元の <see cref="Stream"/>（例：<see cref="FileStream"/>）。</param>
    /// <param name="compress">Deflate圧縮を行う場合は true。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    /// <exception cref="ArgumentNullException">引数が null の場合にスローされます。</exception>
    /// <exception cref="IOException">ネットワーク書き込み中にエラーが発生した場合にスローされます。</exception>
    public static async Task SendStreamAsync(NetworkStream ns, Stream source, bool compress = true, CancellationToken ct = default)
    {
        if (ns is null) throw new ArgumentNullException(nameof(ns));
        if (source is null) throw new ArgumentNullException(nameof(source));

        // 圧縮を有効にする場合は一時的にメモリ上に圧縮データを生成する
        Stream inputStream = source;
        MemoryStream? compressed = null;
        bool usedCompression = false;

        if (compress)
        {
            compressed = new MemoryStream();
            using (var deflate = new DeflateStream(compressed, CompressionMode.Compress, leaveOpen: true))
                await source.CopyToAsync(deflate, 64 * 1024, ct);

            compressed.Position = 0;
            usedCompression = compressed.Length < source.Length;
            inputStream = usedCompression ? compressed : source;
        }

        // 内側ヘッダ：正=非圧縮、負=圧縮
        int innerSize = usedCompression ? -(int)inputStream.Length : (int)inputStream.Length;
        await WriteInt32LEAsync(ns, innerSize, ct);

        // チャンク単位で順次送信
        byte[] buffer = new byte[OuterChunkMax];
        int bytesRead;
        while ((bytesRead = await inputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await WriteInt32LEAsync(ns, bytesRead, ct);
            await ns.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
        }

        // EOFフレームは送らない（旧仕様互換）
        await ns.FlushAsync(ct);
    }

    /// <summary>
    /// ネットワークストリームから受信したデータをストリームに書き出します。
    /// データは複数のチャンクに分割されている場合があり、順次再構築されます。
    /// 圧縮データの場合は自動的に解凍されます。
    /// </summary>
    /// <param name="ns">受信元の <see cref="NetworkStream"/>。</param>
    /// <param name="destination">出力先の <see cref="Stream"/>（例：<see cref="FileStream"/>）。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    /// <exception cref="ArgumentNullException">引数が null の場合にスローされます。</exception>
    /// <exception cref="EndOfStreamException">ストリームが途中で終了した場合にスローされます。</exception>
    /// <exception cref="InvalidDataException">チャンクサイズやヘッダ値が不正な場合にスローされます。</exception>
    public static async Task ReceiveStreamAsync(NetworkStream ns, Stream destination, CancellationToken ct = default)
    {
        if (ns is null) throw new ArgumentNullException(nameof(ns));
        if (destination is null) throw new ArgumentNullException(nameof(destination));

        // 内側ヘッダ（±サイズ）を読み取る
        int? header = await ReadInt32LEAsync(ns, ct);
        if (header is null) throw new EndOfStreamException("stream closed before inner header");

        bool compressed = header < 0;
        long totalSize = Math.Abs(header.Value);

        // 圧縮データの場合は中間ストリームを使用
        Stream targetStream = compressed ? new MemoryStream() : destination;

        // チャンク受信ループ
        byte[] buffer = new byte[OuterChunkMax];
        while (true)
        {
            int? chunkLen = await ReadInt32LEAsync(ns, ct);
            if (chunkLen is null)
                throw new EndOfStreamException("unexpected end of stream while reading chunk length");

            int len = chunkLen.Value;
            if (len <= 0) break; // EOF（拡張用）

            await ReadExactAsync(ns, buffer, len, ct);
            await targetStream.WriteAsync(buffer.AsMemory(0, len), ct);

            if (targetStream.Length >= totalSize) break; // 受信完了
        }

        // 圧縮データを解凍
        if (compressed)
        {
            targetStream.Position = 0;
            using var def = new DeflateStream(targetStream, CompressionMode.Decompress);
            await def.CopyToAsync(destination, 64 * 1024, ct);
        }

        await destination.FlushAsync(ct);
    }

    // ===== 内部ユーティリティメソッド =====

    /// <summary>
    /// 32ビット整数をリトルエンディアン形式でネットワークストリームに非同期で書き込みます。
    /// </summary>
    /// <param name="ns">書き込み対象の <see cref="NetworkStream"/>。</param>
    /// <param name="value">書き込む整数値。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    private static async Task WriteInt32LEAsync(NetworkStream ns, int value, CancellationToken ct)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        await ns.WriteAsync(buf, ct);
    }

    /// <summary>
    /// ネットワークストリームから 4 バイトを読み取り、リトルエンディアン形式の 32 ビット整数として返します。
    /// ストリームが終了した場合は null を返します。
    /// </summary>
    /// <param name="ns">読み込み対象の <see cref="NetworkStream"/>。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    /// <returns>読み取った整数値。ストリーム終了時は null。</returns>
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

    /// <summary>
    /// 指定したバイト数を完全に読み込むまでストリームを読み続けます。
    /// 必要なデータが取得できない場合は <see cref="EndOfStreamException"/> をスローします。
    /// </summary>
    /// <param name="ns">読み込み対象の <see cref="NetworkStream"/>。</param>
    /// <param name="buf">読み込み結果を格納するバッファ。</param>
    /// <param name="count">読み込むバイト数。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    /// <exception cref="EndOfStreamException">ストリームが途中で終了した場合にスローされます。</exception>
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
