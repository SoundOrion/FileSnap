using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;

namespace FileSnapshot7;

/// <summary>
/// 旧方式との互換性を持つメッセージフレーミング処理を提供します。
/// <para>
/// - 内側ヘッダ形式: <c>Int32(±payloadSize)</c>。正の値は非圧縮、負の値は Deflate 圧縮を示します。<br/>
/// - 全体のバイト列 (<c>senddata</c>) を 65532 バイト単位で分割し、各チャンクを <c>[len(LE)][payload]</c> 形式で送信します。<br/>
/// - EOF フレームは存在しません（元仕様に準拠）。<br/>
/// - エンディアンは <see cref="BinaryPrimitives"/> による Little Endian 固定です。<br/>
/// <br/>
/// 理論上の最大通信サイズは約 2.1GB（<see cref="int.MaxValue"/>）、<br/>
/// 実用上の安全な通信サイズは数十MB程度です。
/// </para>
/// </summary>
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

    /// <summary>
    /// 指定された文字列をネットワークストリームに送信します。
    /// <para>
    /// 内部的には文字列を <see cref="TextEncoding"/>（既定は UTF-8）でバイト配列に変換し、
    /// <see cref="SendMessageAsync"/> を使用して送信します。<br/>
    /// 圧縮や分割送信などの詳細なフレーミング処理は <see cref="SendMessageAsync"/> に委譲されます。
    /// </para>
    /// </summary>
    /// <param name="ns">送信先の <see cref="NetworkStream"/>。</param>
    /// <param name="text">送信する文字列。null の場合は空文字列として扱われます。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    /// <returns>非同期操作を表す <see cref="Task"/>。</returns>
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

    /// <summary>
    /// ネットワークストリームから1つのメッセージを受信し、対応する文字列を返します。
    /// <para>
    /// 内部的には <see cref="ReceiveMessageAsync"/> によりバイト列を受信し、
    /// <see cref="TextEncoding"/>（既定は UTF-8）で文字列にデコードして返します。<br/>
    /// 圧縮データの場合は自動的に解凍されます。
    /// </para>
    /// </summary>
    /// <param name="ns">受信元の <see cref="NetworkStream"/>。</param>
    /// <param name="ct">キャンセル操作を制御する <see cref="CancellationToken"/>。</param>
    /// <returns>受信したメッセージを文字列として返す非同期タスク。</returns>
    /// <exception cref="EndOfStreamException">ストリームが途中で切断された場合にスローされます。</exception>
    /// <exception cref="InvalidDataException">データ形式が不正な場合にスローされます。</exception>
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


// LegacyCompatFraming.cs に追記
using System.Buffers.Binary;
using System.IO.Compression;

public static partial class LegacyCompatFraming
{
    public static void EnqueueFrames(Queue<byte[]> q, byte[] data)
    {
        if (q is null) throw new ArgumentNullException(nameof(q));
        if (data is null) throw new ArgumentNullException(nameof(data));

        // 1) Deflate 圧縮（短い方を選ぶ）
        byte[] comp;
        using (var ms = new MemoryStream())
        {
            using (var def = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                def.Write(data, 0, data.Length);
            comp = ms.ToArray();
        }
        bool useRaw = data.Length <= comp.Length;
        var payload = useRaw ? data : comp;
        int inner = useRaw ? payload.Length : -payload.Length;

        // 2) senddata = [4B: inner(LE)] + payload
        var send = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(send.AsSpan(0, 4), inner);
        Buffer.BlockCopy(payload, 0, send, 4, payload.Length);

        // 3) 65532 で分割 → 各チャンクを [4B:len(LE)][chunk] でキューへ
        int off = 0;
        while (off < send.Length)
        {
            int len = Math.Min(OuterChunkMax, send.Length - off); // OuterChunkMax=65532
            var frame = new byte[4 + len];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), len);
            Buffer.BlockCopy(send, off, frame, 4, len);
            q.Enqueue(frame);
            off += len;
        }
    }
}

// using 追加:
// using System.Buffers.Binary;
// using System.IO.Compression;

public static partial class LegacyCompatFraming
{
    /// <summary>
    /// 送信側：data を旧実装互換の「外側フレーム列」にして Queue に積む。
    /// 仕様：内側ヘッダ = Int32(±payloadSize)、65532 分割、各チャンクは [4B:len(LE)][payload]。
    /// </summary>
    public static void EnqueueFrames(Queue<byte[]> q, byte[] data)
    {
        if (q is null) throw new ArgumentNullException(nameof(q));
        if (data is null) throw new ArgumentNullException(nameof(data));

        // 1) Deflate圧縮（短い方採用）
        byte[] comp;
        using (var ms = new MemoryStream())
        {
            using (var def = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                def.Write(data, 0, data.Length);
            comp = ms.ToArray();
        }
        bool useRaw = data.Length <= comp.Length;
        var payload = useRaw ? data : comp;
        int inner = useRaw ? payload.Length : -payload.Length;

        // 2) senddata = [4B: inner(LE)] + payload
        var send = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(send.AsSpan(0, 4), inner);
        Buffer.BlockCopy(payload, 0, send, 4, payload.Length);

        // 3) 65532 で分割し、各チャンクを [4B:len(LE)][chunk] で Enqueue
        int off = 0;
        while (off < send.Length)
        {
            int len = Math.Min(OuterChunkMax, send.Length - off);
            var frame = new byte[4 + len];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), len);
            Buffer.BlockCopy(send, off, frame, 4, len);
            q.Enqueue(frame);
            off += len;
        }
    }

    /// <summary>
    /// 受信側：外側フレーム（[4B:len][chunk]）の Queue からちょうど1メッセージ分を復元。
    /// 復元できたら true。足りなければ false（フレームは消費しない）。
    /// </summary>
    public static bool TryDequeueOneMessage(Queue<byte[]> q, out byte[] message)
    {
        if (q is null) throw new ArgumentNullException(nameof(q));

        // ローカルバッファに積みながら様子を見る（不足時はロールバック）
        var stash = new List<byte[]>();
        using var buffer = new MemoryStream(capacity: 4 + 64 * 1024);

        int? innerNeeded = null;
        bool innerCompressed = false;

        while (true)
        {
            if (q.Count == 0)
            {
                // フレーム不足：ロールバック（何も消費しない）
                for (int i = stash.Count - 1; i >= 0; i--) q.Enqueue(stash[i]); // 逆順復帰は不要：今回は消費前に取り出していない
                message = Array.Empty<byte>();
                return false;
            }

            var frame = q.Peek(); // まず覗く（不足時に戻す必要がないように）
            if (frame.Length < 4) throw new InvalidDataException("bad outer frame (length < 4)");
            int len = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4));
            if (len <= 0 || len > OuterChunkMax) throw new InvalidDataException($"bad outer frame length {len}");
            if (frame.Length != 4 + len) throw new InvalidDataException("outer frame size mismatch");

            // ここで消費確定
            q.Dequeue();
            stash.Add(frame);

            // chunk を buffer に追記
            buffer.Write(frame, 4, len);

            // 内側ヘッダ（±size）未確定なら確定
            if (innerNeeded == null && buffer.Length >= 4)
            {
                var all = buffer.GetBuffer();
                int inner = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(all, 0, 4));
                innerCompressed = inner < 0;
                innerNeeded = Math.Abs(inner);
                if (innerNeeded.Value < 0 || innerNeeded.Value > int.MaxValue)
                    throw new InvalidDataException("invalid inner size");
            }

            // 必要量に到達（= 4 + innerNeeded）したら復元
            if (innerNeeded != null && buffer.Length >= 4L + innerNeeded.Value)
            {
                buffer.Position = 4;
                if (innerCompressed)
                {
                    using var def = new DeflateStream(buffer, CompressionMode.Decompress, leaveOpen: true);
                    using var outMs = new MemoryStream();
                    def.CopyTo(outMs);
                    message = outMs.ToArray();
                    return true;
                }
                else
                {
                    message = new byte[innerNeeded.Value];
                    int read = buffer.Read(message, 0, message.Length);
                    if (read != message.Length) throw new InvalidDataException("unexpected end of raw payload");
                    return true;
                }
            }
        }
    }
}

public enum LegacyInnerHeaderMode
{
    Standard,                 // これまで通り: 常に [±size][payload] を付ける
    OmitWhenRawSingleChunk    // 非圧縮 かつ 1チャンクで収まる時は内側ヘッダを省略
}

public static async Task SendMessageAsync(
    NetworkStream ns,
    byte[] data,
    LegacyInnerHeaderMode headerMode = LegacyInnerHeaderMode.Standard,
    CancellationToken ct = default)
{
    if (data is null) throw new ArgumentNullException(nameof(data));

    // 1) 圧縮を試し、短い方を選択（現行仕様）
    byte[] compData;
    using (var ms = new MemoryStream())
    {
        using (var def = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
            await def.WriteAsync(data, ct);
        compData = ms.ToArray();
    }
    bool useRaw = data.Length <= compData.Length;
    var payload = useRaw ? data : compData;

    // 2) 内側ヘッダを付けるかの判定
    bool isSingleChunk = (4 + payload.Length) <= OuterChunkMax; // 内側ヘッダ有り想定時の総量
    bool omitInnerHeader =
        headerMode == LegacyInnerHeaderMode.OmitWhenRawSingleChunk
        && useRaw
        && isSingleChunk;

    byte[] senddata;
    if (omitInnerHeader)
    {
        // レガシー互換：生+1チャンク時は内側ヘッダを省略し、payload だけを外側フレーム化
        senddata = payload;
    }
    else
    {
        // 標準: [4B:±size(LE)] + payload
        int innerSize = useRaw ? payload.Length : -payload.Length;
        senddata = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(senddata.AsSpan(0, 4), innerSize);
        Buffer.BlockCopy(payload, 0, senddata, 4, payload.Length);
    }

    // 3) 共通: 65532 で分割 → [4B:len(LE)][chunk] で送出（EOFなし）
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

using System;
using System.Diagnostics;

class Program
{
    static void Main()
    {
        string comspec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        // UNC パス（例）： \\server\share\script.vbs
        string uncDir = @"\\server\share";
        string scriptName = "script.vbs";
        // pushd で UNC をドライブに割り当て -> cscript 実行 -> popd で戻す
        string cmd = $"/c pushd \"{uncDir}\" && cscript //nologo \"{scriptName}\" arg1 arg2 && popd";

        var psi = new ProcessStartInfo(comspec, cmd)
        {
            UseShellExecute = false,         // 出力を読み取るなら false
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("起動失敗");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        Console.WriteLine("STDOUT:");
        Console.WriteLine(stdout);
        if (!string.IsNullOrEmpty(stderr))
        {
            Console.WriteLine("STDERR:");
            Console.WriteLine(stderr);
        }
    }
}

// using System.Buffers.Binary;
// using System.IO.Compression;

public static async Task<byte[]> ReceiveMessageAsync(
    NetworkStream ns,
    bool acceptHeaderlessRawSingleChunk = false, // ← 旧挙動を許容するなら true
    CancellationToken ct = default)
    {
        // 外側フレームを読み足していく
        using var buffer = new MemoryStream(capacity: 4 + 64 * 1024);
        int? innerNeeded = null;
        bool innerCompressed = false;

        // まず最初の外側フレームを読む
        int? maybeLen = await ReadInt32LEAsync(ns, ct);
        if (maybeLen is null) throw new EndOfStreamException("stream closed while reading frame length");
        int firstLen = maybeLen.Value;
        if (firstLen <= 0 || firstLen > OuterChunkMax) throw new InvalidDataException($"bad outer frame length {firstLen}");

        var first = new byte[firstLen];
        await ReadExactAsync(ns, first, firstLen, ct);
        buffer.Write(first, 0, firstLen);

        // ★ レガシー互換分岐：1チャンク・非圧縮・ヘッダなし を“推定”できるならそのまま返す
        // 先頭4Bをサイズと解釈したときに「この外側チャンク内に収まらない値（ありえない）」なら、
        // それはヘッダではなく“生データ”の先頭4Bと推定できる。
        if (acceptHeaderlessRawSingleChunk && buffer.Length >= 4)
        {
            int probe = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(buffer.GetBuffer(), 0, 4));
            long room = buffer.Length - 4;             // ヘッダがあるなら payload が入るべき残り
            long need = Math.Abs((long)probe);

            // 条件: probe が 0 以下 or room に収まらない → この4Bはヘッダとして不自然
            // ※ 圧縮(-size)はこの時点ではありえない（旧挙動は“生”のみ省略）
            if (probe <= 0 || need > room)
            {
                // 旧実装の「生+1チャンク＝ヘッダなし」ケースとみなして、このチャンク全体をメッセージ本体として返す
                var raw = buffer.ToArray();
                return raw; // 非圧縮・ヘッダなし
            }
            // ここを通らない＝標準どおりヘッダありとして続行
        }

        // --- 標準挙動：内側ヘッダありとして処理（新旧の“正規仕様”） ---
        // 以降は既存の ReceiveMessageAsync と同じ流れ:contentReference[oaicite:1]{index=1}
        // 1) 先頭4B（内側ヘッダ）を読む
        if (buffer.Length < 4)
            throw new InvalidDataException("inner header missing");
        {
            var all = buffer.GetBuffer();
            int inner = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(all, 0, 4));
            innerCompressed = inner < 0;
            innerNeeded = Math.Abs(inner);
            if (innerNeeded.Value > int.MaxValue)
                throw new InvalidDataException($"inner size too large: {innerNeeded.Value}");
        }

        // 2) 必要バイトが満たされるまで外側フレームを読み足す
        while (buffer.Length < 4L + innerNeeded.Value)
        {
            int? len = await ReadInt32LEAsync(ns, ct);
            if (len is null) throw new EndOfStreamException("stream closed while reading frame length");
            if (len <= 0 || len > OuterChunkMax) throw new InvalidDataException($"bad outer frame length {len}");
            var tmp = new byte[len.Value];
            await ReadExactAsync(ns, tmp, len.Value, ct);
            buffer.Write(tmp, 0, len.Value);
        }

        // 3) 復元（負なら Deflate 展開、正なら生コピー）
        buffer.Position = 4;
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
