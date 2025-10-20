using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileSnapshot7;

// LegacyCompatFraming.cs
// - 元方式と互換: 内側ヘッダ = Int32(±payloadSize). 正=非圧縮, 負=圧縮
// - その後、全体バイト列(senddata)を 65532 バイトで分割し、各チャンクを [len(LE)][payload] で送出
// - EOF フレームはありません（元方式どおり）
// - エンディアンは Little Endian 固定（BinaryPrimitives）

using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;

public static class LegacyCompatFraming
{
    public const int OuterChunkMax = 65532; // 元コード互換
    public static Encoding TextEncoding { get; set; } = Encoding.UTF8;

    /// <summary>
    /// 指定されたバイト配列をネットワークストリームに送信します。
    /// データは Deflate 圧縮を行い、元のサイズと圧縮後のサイズを比較して
    /// より短い方を送信します。非圧縮データの場合は正の長さ、圧縮データの場合は負の長さがヘッダに設定されます。
    /// その後、65532 バイト単位に分割し、各チャンクを [長さ(LE)][データ] 形式で送出します。
    /// </summary>
    /// <param name="ns">送信対象の <see cref="NetworkStream"/>。</param>
    /// <param name="data">送信するデータ（バイト配列）。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    /// <exception cref="ArgumentNullException">data が null の場合にスローされます。</exception>
    /// <exception cref="IOException">ネットワーク送信中にエラーが発生した場合にスローされます。</exception>
    public static async Task SendMessageAsync(NetworkStream ns, byte[] data, CancellationToken ct = default)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        // 1) Deflate 圧縮（メモリ上）
        byte[] compData;
        using (var ms = new MemoryStream())
        {
            using (var def = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                await def.WriteAsync(data, ct);
            compData = ms.ToArray();
        }

        // 2) 元方式どおり「短い方」を選ぶ（非圧縮=正、圧縮=負）
        bool useRaw = data.Length <= compData.Length;
        int innerSize = useRaw ? data.Length : -compData.Length;

        // 3) senddata = [innerSize(LE)] + payload
        var payload = useRaw ? data : compData;
        var senddata = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(senddata.AsSpan(0, 4), innerSize);
        Buffer.BlockCopy(payload, 0, senddata, 4, payload.Length);

        // 4) 65532 で分割し、各チャンクを [len(LE)][chunk] で送出（EOF はなし）
        int offset = 0;
        while (offset < senddata.Length)
        {
            int len = Math.Min(OuterChunkMax, senddata.Length - offset);
            await WriteInt32LEAsync(ns, len, ct);
            await ns.WriteAsync(senddata.AsMemory(offset, len), ct);
            offset += len;
        }
        await ns.FlushAsync(ct);
    }

    // ---- 送信（文字列）----
    public static Task SendStringAsync(NetworkStream ns, string text, CancellationToken ct = default)
        => SendMessageAsync(ns, TextEncoding.GetBytes(text ?? string.Empty), ct);

    /// <summary>
    /// ネットワークストリームから1つのメッセージを受信し、対応するバイト配列を返します。
    /// 内部形式は、先頭4バイトが「データサイズ（符号付き）」を示し、負の場合は Deflate 圧縮データとして解凍されます。
    /// 複数のチャンクに分割されている場合も、全てを再構築して返します。
    /// </summary>
    /// <param name="ns">受信元の <see cref="NetworkStream"/>。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    /// <returns>受信した1メッセージ分のバイト配列。</returns>
    /// <exception cref="EndOfStreamException">ストリームの途中で切断された場合にスローされます。</exception>
    /// <exception cref="InvalidDataException">フレーム長や内部サイズが不正な場合にスローされます。</exception>
    public static async Task<byte[]> ReceiveMessageAsync(NetworkStream ns, CancellationToken ct = default)
    {
        // 外側フレームを読み足していき、まずは内側サイズ（先頭4B）を確定、その後は必要バイト満了まで集める
        var buffer = new MemoryStream(capacity: 4 + 64 * 1024); // 内側ヘッダ＋ある程度の余裕
        int? innerNeeded = null;       // abs(±size)
        bool innerCompressed = false;  // size < 0

        while (true)
        {
            // 1) 外側フレーム長を読む（LE）
            int? maybeLen = await ReadInt32LEAsync(ns, ct);
            if (maybeLen is null) throw new EndOfStreamException("stream closed while reading frame length");
            int len = maybeLen.Value;
            if (len <= 0 || len > OuterChunkMax) throw new InvalidDataException($"bad outer frame length {len}");

            // 2) 指定バイトを取り込み
            var tmp = new byte[len];
            await ReadExactAsync(ns, tmp, len, ct);
            buffer.Write(tmp, 0, len);

            // 3) 先頭4バイト（内側ヘッダ）がまだ取れていなければ読む
            if (innerNeeded == null && buffer.Length >= 4)
            {
                var all = buffer.GetBuffer();
                int inner = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(all, 0, 4));
                innerCompressed = inner < 0;
                innerNeeded = Math.Abs(inner);

                // ありえないサイズは拒否
                if (innerNeeded.Value > int.MaxValue) // 任意の上限
                    throw new InvalidDataException($"inner size too large: {innerNeeded.Value}");
            }

            // 4) 必要バイト数が満たされたら終了（= 4 + innerNeeded）
            if (innerNeeded != null && buffer.Length >= 4L + innerNeeded.Value)
                break;
        }

        // 5) 復元
        buffer.Position = 4; // 内側ヘッダの後ろへ
        if (innerCompressed)
        {
            using var def = new DeflateStream(buffer, CompressionMode.Decompress, leaveOpen: true);
            using var outMs = new MemoryStream();
            await def.CopyToAsync(outMs, ct);
            return outMs.ToArray();
        }
        else
        {
            var result = new byte[innerNeeded!.Value];
            int read = await buffer.ReadAsync(result, ct);
            if (read != result.Length) throw new InvalidDataException("unexpected end while reading raw payload");
            return result;
        }
    }

    // ---- 受信（文字列）----
    public static async Task<string> ReceiveStringAsync(NetworkStream ns, CancellationToken ct = default)
        => TextEncoding.GetString(await ReceiveMessageAsync(ns, ct));

    /// <summary>
    /// 32ビット整数値をリトルエンディアン形式でネットワークストリームに非同期で書き込みます。
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
    /// ネットワークストリームから 4 バイトを読み込み、リトルエンディアンの 32 ビット整数として返します。
    /// ストリームが終了した場合は null を返します。
    /// </summary>
    /// <param name="ns">読み込み対象の <see cref="NetworkStream"/>。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    /// <returns>読み取られた整数値。ストリーム終了時は null。</returns>
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
    /// 指定されたバイト数をネットワークストリームから完全に読み込み、
    /// バッファを埋めるまでブロッキング動作を行います。
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
