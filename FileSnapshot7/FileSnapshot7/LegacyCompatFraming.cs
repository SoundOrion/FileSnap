using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FileSnapshot7;

/// <summary>
/// 旧方式との互換性を持つメッセージフレーミング処理を提供します。
/// <para>
/// - 内側ヘッダ形式: <c>Int32(±payloadSize)</c>。正の値は非圧縮、負の値は Deflate 圧縮を示します。<br/>
/// - 送信時、<b>payload 長が OuterChunkMax 以下のときは外側チャンクを用いず</b>、内側メッセージ (<c>[±size(LE)][payload]</c>) をそのまま送出します。<br/>
/// - 上記以外（= 大きい場合）は、全体のバイト列 (<c>senddata</c>) を 65532 バイト単位に分割し、各チャンクを <c>[len(LE)][payload]</c> 形式で送信します。<br/>
/// - EOF フレームは存在しません（元仕様に準拠）。<br/>
/// - エンディアンは <see cref="BinaryPrimitives"/> による Little Endian 固定です。<br/>
/// <br/>
/// 理論上の最大通信サイズは約 2.1GB（<see cref="int.MaxValue"/>）、
/// 実用上の安全な通信サイズは数十MB程度です。
/// </para>
//// </summary>
public static class LegacyCompatFraming
{
    public const int OuterChunkMax = 65532; // 元コード互換
    public static Encoding TextEncoding { get; set; } = Encoding.UTF8;

    /// <summary>
    /// 指定されたバイト配列をネットワークストリームに送信します。
    /// データは Deflate 圧縮を行い、元のサイズと圧縮後のサイズを比較して
    /// より短い方を送信します。非圧縮データの場合は正の長さ、圧縮データの場合は負の長さがヘッダに設定されます。
    /// その後、<b>payload 長が OuterChunkMax 以下なら外側チャンクを使わず</b>、内側メッセージをそのまま送信します。
    /// 超える場合のみ 65532 バイト単位に分割し、各チャンクを [長さ(LE)][データ] 形式で送出します。
    /// </summary>
    public static async Task SendMessageAsync(NetworkStream ns, byte[] data, CancellationToken ct = default)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));

        // 1) Deflate 圧縮（メモリ上）
        byte[] compData;
        using (var ms = new MemoryStream())
        {
            using (var def = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                await def.WriteAsync(data, ct).ConfigureAwait(false);
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

        // 4) ルールに従い送出：小さいときは内側メッセージのみ、大きいときは外側チャンク
        if (payload.Length <= OuterChunkMax)
        {
            // 外側チャンクを用いず、内側メッセージをそのまま送る
            await ns.WriteAsync(senddata.AsMemory(0, senddata.Length), ct).ConfigureAwait(false);
            await ns.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        // 大きいときは外側チャンクで分割送信（EOF はなし）
        int offset = 0;
        while (offset < senddata.Length)
        {
            int len = Math.Min(OuterChunkMax, senddata.Length - offset);
            await WriteInt32LEAsync(ns, len, ct).ConfigureAwait(false);
            await ns.WriteAsync(senddata.AsMemory(offset, len), ct).ConfigureAwait(false);
            offset += len;
        }
        await ns.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 指定された文字列をネットワークストリームに送信します。
    /// </summary>
    public static Task SendStringAsync(NetworkStream ns, string text, CancellationToken ct = default)
        => SendMessageAsync(ns, TextEncoding.GetBytes(text ?? string.Empty), ct);

    /// <summary>
    /// ネットワークストリームから1つのメッセージを受信し、対応するバイト配列を返します。
    /// 外側チャンクを使わないケース（内側メッセージ直送）と、従来の外側チャンクありの両方に対応します。
    /// 判定は先頭4バイトの値で行います：
    ///   - 1..OuterChunkMax → 外側チャンクあり
    ///   - それ以外（負値を含む） → 外側チャンクなし（= 先頭4Bは内側ヘッダ）
    /// </summary>
    public static async Task<byte[]> ReceiveMessageAsync(NetworkStream ns, CancellationToken ct = default)
    {
        // 先頭 4 バイトを読む（この値でモード判定）
        int? first = await ReadInt32LEAsync(ns, ct).ConfigureAwait(false);
        if (first is null) throw new EndOfStreamException("stream closed while reading first 4 bytes");

        int firstVal = first.Value;

        // 1) 外側チャンクなし（= 先頭4Bは内側ヘッダ）
        if (!(firstVal > 0 && firstVal <= OuterChunkMax))
        {
            // firstVal は ±payloadSize
            if (firstVal == int.MinValue)
                throw new InvalidDataException("invalid inner header size (int.MinValue)");

            bool innerCompressed = firstVal < 0;
            int innerNeeded = Math.Abs(firstVal);

            if (innerNeeded < 0) // 念のためのオーバーフロー防止（理論上は起こらない）
                throw new InvalidDataException("inner size overflow");

            var payload = new byte[innerNeeded];
            await ReadExactAsync(ns, payload, innerNeeded, ct).ConfigureAwait(false);

            if (innerCompressed)
            {
                using var buf = new MemoryStream(payload, writable: false);
                using var def = new DeflateStream(buf, CompressionMode.Decompress, leaveOpen: false);
                using var outMs = new MemoryStream();
                await def.CopyToAsync(outMs, ct).ConfigureAwait(false);
                return outMs.ToArray();
            }
            else
            {
                return payload;
            }
        }

        // 2) 外側チャンクあり（= firstVal は最初の外側フレーム長）
        if (firstVal <= 0 || firstVal > OuterChunkMax)
            throw new InvalidDataException($"bad outer frame length {firstVal}");

        var buffer = new MemoryStream(capacity: 4 + 64 * 1024);
        int? innerNeeded2 = null;
        bool innerCompressed2 = false;

        // 最初の外側フレーム分を取り込み
        {
            var tmp = new byte[firstVal];
            await ReadExactAsync(ns, tmp, firstVal, ct).ConfigureAwait(false);
            buffer.Write(tmp, 0, firstVal);

            if (buffer.Length >= 4)
            {
                var all = buffer.GetBuffer();
                int inner = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(all, 0, 4));
                innerCompressed2 = inner < 0;
                innerNeeded2 = Math.Abs(inner);
                if (innerNeeded2.Value > int.MaxValue)
                    throw new InvalidDataException($"inner size too large: {innerNeeded2.Value}");
            }
        }

        // 後続の外側フレームを読み足し、内側必要長（4 + innerNeeded2）満了まで収集
        while (innerNeeded2 == null || buffer.Length < 4L + innerNeeded2.Value)
        {
            int? maybeLen = await ReadInt32LEAsync(ns, ct).ConfigureAwait(false);
            if (maybeLen is null) throw new EndOfStreamException("stream closed while reading frame length");
            int len = maybeLen.Value;
            if (len <= 0 || len > OuterChunkMax) throw new InvalidDataException($"bad outer frame length {len}");

            var tmp = new byte[len];
            await ReadExactAsync(ns, tmp, len, ct).ConfigureAwait(false);
            buffer.Write(tmp, 0, len);

            if (innerNeeded2 == null && buffer.Length >= 4)
            {
                var all = buffer.GetBuffer();
                int inner = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(all, 0, 4));
                innerCompressed2 = inner < 0;
                innerNeeded2 = Math.Abs(inner);
                if (innerNeeded2.Value > int.MaxValue)
                    throw new InvalidDataException($"inner size too large: {innerNeeded2.Value}");
            }
        }

        // 復元
        buffer.Position = 4;
        if (innerCompressed2)
        {
            using var def = new DeflateStream(buffer, CompressionMode.Decompress, leaveOpen: true);
            using var outMs = new MemoryStream();
            await def.CopyToAsync(outMs, ct).ConfigureAwait(false);
            return outMs.ToArray();
        }
        else
        {
            var result = new byte[innerNeeded2!.Value];
            int read = await buffer.ReadAsync(result, ct).ConfigureAwait(false);
            if (read != result.Length) throw new InvalidDataException("unexpected end while reading raw payload");
            return result;
        }
    }

    /// <summary>
    /// ネットワークストリームから1つのメッセージを受信し、対応する文字列を返します。
    /// </summary>
    public static async Task<string> ReceiveStringAsync(NetworkStream ns, CancellationToken ct = default)
        => TextEncoding.GetString(await ReceiveMessageAsync(ns, ct).ConfigureAwait(false));

    private static async Task WriteInt32LEAsync(NetworkStream ns, int value, CancellationToken ct)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        await ns.WriteAsync(buf, ct).ConfigureAwait(false);
    }

    private static async Task<int?> ReadInt32LEAsync(NetworkStream ns, CancellationToken ct)
    {
        var buf = new byte[4];
        int off = 0;
        while (off < 4)
        {
            int n = await ns.ReadAsync(buf.AsMemory(off, 4 - off), ct).ConfigureAwait(false);
            if (n == 0) return null;
            off += n;
        }
        return BinaryPrimitives.ReadInt32LittleEndian(buf);
    }

    private static async Task ReadExactAsync(NetworkStream ns, byte[] buf, int count, CancellationToken ct)
    {
        int off = 0;
        while (off < count)
        {
            int n = await ns.ReadAsync(buf.AsMemory(off, count - off), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }
}
