using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;

namespace FileSnapshot7;

/// <summary>
/// 旧方式との互換性を持つメッセージフレーミング処理。
/// <para>
/// <b>内側メッセージ（直送：A）</b><br/>
/// 形式: <c>[±payloadSize (Int32, LE)][payload]</c>。正=非圧縮, 負=Deflate 圧縮。<br/>
/// <br/>
/// <b>送信ルール</b><br/>
/// <list type="bullet">
///   <item><description><b>(A) 直送</b>: <c>4 + payload.Length ≤ 65536</c> の場合は外側チャンクを使わず、そのまま送出。</description></item>
/// <item><description><b>(B) 分割</b>: 上記を超える場合は、内側メッセージ全体を<see cref = "OuterChunkMax" /> バイト（=65532）毎に分割し、
///     各チャンクを <c>[len(Int32, LE)][chunkBytes]</c> で送出（EOF フレームなし）。</description></item>
/// </list>
/// 受信側は (A)/(B) の両方を自動判別して復元する。LE は <see cref="BinaryPrimitives"/> による Little Endian 固定。
/// </para>
/// <para>
/// <b>受信時の先頭 4B（= <c>first</c>）の解釈</b><br/>
/// ・<c>first &lt; 0</c> … 直送(A)（圧縮）<br/>
/// ・<c>first = 0</c> … 直送(A)（空）<br/>
/// ・<c>1 ≤ first ≤ 65531</c> … 直送(A)（非圧縮）<br/>
/// ・<c>first = 65532</c> … 境界値（直送: payload=65532 か、分割: len=65532）。判別には次の 65532B ブロック先頭 4B を参照。<br/>
/// ・<c>first &gt; 65532</c> … 不正値（直送ヘッダにもチャンク長にもなり得ない）。<br/>
/// </para>
/// </summary>
public static class LegacyCompatFraming
{
    /// <summary>外側チャンクの最大サイズ（旧仕様互換の分割単位）。</summary>
    public const int OuterChunkMax = 65532;

    /// <summary>文字列の既定エンコーディング。</summary>
    public static Encoding TextEncoding { get; set; } = Encoding.UTF8;

    /// <summary>
    /// バイト配列を送信。Deflate 圧縮を試行し、短い方（非圧縮 or 圧縮）を選択。
    /// その後、(A) 64KB 以下なら内側メッセージのみ、(B) 超過なら 65532B チャンク化。
    /// </summary>
    /// <param name="ns">送信先 <see cref="NetworkStream"/>。</param>
    /// <param name="data">送信するバイト列。</param>
    /// <param name="ct">キャンセル トークン。</param>
    public static async Task SendMessageAsync(NetworkStream ns, byte[] data, CancellationToken ct = default)
    {
        if (ns is null) throw new ArgumentNullException(nameof(ns));
        if (data is null) throw new ArgumentNullException(nameof(data));

        // 1) Deflate 圧縮
        byte[] compData;
        using (var ms = new MemoryStream())
        {
            using (var def = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                await def.WriteAsync(data, ct).ConfigureAwait(false);
            compData = ms.ToArray();
        }

        // 2) 短い方を選択（非圧縮=正、圧縮=負）
        bool useRaw = data.Length <= compData.Length;
        int innerSize = useRaw ? data.Length : -compData.Length;
        var payload = useRaw ? data : compData;

        // 3) 内側メッセージ: [±size(4B, LE)] + payload
        var senddata = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(senddata.AsSpan(0, 4), innerSize);
        Buffer.BlockCopy(payload, 0, senddata, 4, payload.Length);

        // 4) 64KB 以下は外側チャンク無しでそのまま送信（payload ≤ 65532）
        //if (senddata.Length <= OuterChunkMax + 4)
        if (payload.Length <= OuterChunkMax)
        {
            await ns.WriteAsync(senddata, ct).ConfigureAwait(false);
            await ns.FlushAsync(ct).ConfigureAwait(false);
            return;
        }

        // 5) 64KB 超は 65532B チャンクに分割 + 各チャンクの先頭に [len(LE)]
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
    /// 文字列を送信（<see cref="TextEncoding"/> でエンコード → <see cref="SendMessageAsync(NetworkStream, byte[], CancellationToken)"/>）。
    /// </summary>
    public static Task SendStringAsync(NetworkStream ns, string text, CancellationToken ct = default)
        => SendMessageAsync(ns, TextEncoding.GetBytes(text ?? string.Empty), ct);

    /// <summary>
    /// 1 メッセージを受信して元のバイト配列を返す。直送（A）と分割（B）の両形式を自動判別。
    /// </summary>
    /// <param name="ns">受信元 <see cref="NetworkStream"/>。</param>
    /// <param name="ct">キャンセル トークン。</param>
    public static async Task<byte[]> ReceiveMessageAsync(NetworkStream ns, CancellationToken ct = default)
    {
        if (ns is null) throw new ArgumentNullException(nameof(ns));

        // 先頭 4B を読む
        int? firstMaybe = await ReadInt32LEAsync(ns, ct).ConfigureAwait(false);
        if (firstMaybe is null)
            throw new EndOfStreamException("stream closed while reading first 4 bytes");
        int first = firstMaybe.Value;

        // --- 直送(A) 確定パス（負=圧縮/0=空/1..65531=非圧縮） ---
        if (first < OuterChunkMax)
            return await ReceiveUnchunkedAfterHeaderAsync(ns, first, ct).ConfigureAwait(false);

        // --- 境界：first == 65532 → まず 65532B をブロックとして読む ---
        if (first == OuterChunkMax)
        {
            var block = new byte[OuterChunkMax];
            await ReadExactAsync(ns, block, block.Length, ct).ConfigureAwait(false);

            // このブロックの先頭4Bは：
            //   - 直送(A)：payload先頭（= 任意の4B）
            //   - 分割(B)：内側ヘッダ（±payloadSize）
            if (block.Length >= 4)
            {
                int innerCandidate = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(0, 4));
                long absInner = Math.Abs((long)innerCandidate);

                // 分割(B)になるのは「内側メッセージ全体＞64KB」、すなわち abs(inner) > 65532。
                if (absInner > OuterChunkMax)
                {
                    // ---- 分割(B) 確定：block には [内側ヘッダ(4B)] + 続き が入っている ----
                    bool innerCompressed = innerCandidate < 0;
                    long innerNeeded = absInner;

                    using var buffer = new MemoryStream(capacity: block.Length + 64 * 1024);
                    buffer.Write(block, 0, block.Length);

                    // 内側メッセージ全体の必要長 = 4 + innerNeeded
                    while (buffer.Length < 4L + innerNeeded)
                    {
                        int? lenMaybe = await ReadInt32LEAsync(ns, ct).ConfigureAwait(false);
                        if (lenMaybe is null)
                            throw new EndOfStreamException("stream closed while reading frame length");
                        int len = lenMaybe.Value;
                        if (len <= 0 || len > OuterChunkMax)
                            throw new InvalidDataException($"bad outer frame length {len}");

                        var tmp = new byte[len];
                        await ReadExactAsync(ns, tmp, len, ct).ConfigureAwait(false);
                        buffer.Write(tmp, 0, tmp.Length);
                    }

                    // 復元（圧縮/非圧縮）
                    buffer.Position = 4;
                    if (innerCompressed)
                    {
                        using var def = new DeflateStream(buffer, CompressionMode.Decompress, leaveOpen: true);
                        using var outMs = new MemoryStream();
                        await def.CopyToAsync(outMs, ct).ConfigureAwait(false);
                        return outMs.ToArray();
                    }
                    else
                    {
                        var result = new byte[innerNeeded];
                        int read = await buffer.ReadAsync(result, ct).ConfigureAwait(false);
                        if (read != result.Length) throw new InvalidDataException("unexpected end while reading raw payload");
                        return result;
                    }
                }
            }

            // ---- 直送(A) 確定（payload 長 = 65532）----
            return block;
        }

        // --- > 65532 → 不正（直送ヘッダにもチャンク長にもなり得ない） ---
        if (first > OuterChunkMax)
            throw new InvalidDataException($"invalid first field {first}");

        // normally unreachable
        throw new InvalidDataException("unreachable state");
    }

    /// <summary>
    /// 先頭 4B が「内側ヘッダ（±payloadSize）」であることが確定している場合の受信処理。
    /// <list type="bullet">
    /// <item><description><c>header &lt; 0</c> … Deflate 展開</description></item>
    /// <item><description><c>header = 0</c> … 空配列を返す</description></item>
    /// <item><description><c>header &gt; 0</c> … 非圧縮で <c>header</c> バイト読み取る</description></item>
    /// </list>
    /// </summary>
    private static async Task<byte[]> ReceiveUnchunkedAfterHeaderAsync(NetworkStream ns, int innerHeader, CancellationToken ct)
    {
        if (innerHeader == int.MinValue)
            throw new InvalidDataException("invalid inner header size (int.MinValue)");

        bool innerCompressed = innerHeader < 0;
        int innerNeeded = Math.Abs(innerHeader);
        if (innerNeeded < 0) // 理論上発生しないが安全のため
            throw new InvalidDataException("inner size overflow");

        var payload = new byte[innerNeeded];
        await ReadExactAsync(ns, payload, innerNeeded, ct).ConfigureAwait(false);

        if (!innerCompressed) return payload;

        using var buf = new MemoryStream(payload, writable: false);
        using var def = new DeflateStream(buf, CompressionMode.Decompress, leaveOpen: false);
        using var outMs = new MemoryStream();
        await def.CopyToAsync(outMs, ct).ConfigureAwait(false);
        return outMs.ToArray();
    }

    /// <summary>Int32 (LE) をストリームへ書き込み。</summary>
    private static async Task WriteInt32LEAsync(NetworkStream ns, int value, CancellationToken ct)
    {
        var buf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        await ns.WriteAsync(buf, ct).ConfigureAwait(false);
    }

    /// <summary>Int32 (LE) をストリームから読み込み。EOF なら <c>null</c>。</summary>
    private static async Task<int?> ReadInt32LEAsync(NetworkStream ns, CancellationToken ct)
    {
        var buf = new byte[4];
        int off = 0;
        while (off < 4)
        {
            int n = await ns.ReadAsync(buf.AsMemory(off, 4 - off), ct).ConfigureAwait(false);
            if (n == 0) return null; // EOF
            off += n;
        }
        return BinaryPrimitives.ReadInt32LittleEndian(buf);
    }

    /// <summary>
    /// 指定バイト数を完全に読み込み（足りなければ <see cref="EndOfStreamException"/>）。
    /// </summary>
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

    /// <summary>
    /// 文字列を受信（<see cref="ReceiveMessageAsync(NetworkStream, CancellationToken)"/> → <see cref="TextEncoding"/> デコード）。
    /// </summary>
    public static async Task<string> ReceiveStringAsync(NetworkStream ns, CancellationToken ct = default)
        => TextEncoding.GetString(await ReceiveMessageAsync(ns, ct).ConfigureAwait(false));
}



//もし送信側の条件を

//```csharp
//if (payload.Length<OuterChunkMax)
//```

//にしていた場合、つまり** 厳密不等号 `<`** を使った場合は以下のような動作になります：

//---

//### 🔹送信側の挙動

//| payload の長さ | 条件判定                                         | 動作                                    |
//| ----------- | -------------------------------------------- | ------------------------------------- |
//| **0〜65531** | `payload.Length<OuterChunkMax` → **true**  | 外側チャンクを使わず「直送」                        |
//| **65532**   | `payload.Length<OuterChunkMax` → **false** | 外側チャンクを使用（最初の `[len = 65532]` フレームから送信） |
//| **65533以上** | false                                        | 同上（チャンク）                              |

//---

//### 🔹その結果

//* **payload = 65532 の場合** → 「チャンクあり送信」になる。
//  つまりこのケースでは `[len(65532)][±size(4B)][payload...]` という形式になる。

//*** payload ≤ 65531 の場合** → 「直送（ヘッダ＋payloadのみ）」になる。

//---

//### 🔹あなたの要件との違い

//あなたの希望はこうですね：

//> len(4B) + data(65532B) = 64KB のときはチャンクしない（直送）

//つまり** payload==65532 も直送扱い** にしたい。
//この場合はやはり**`<=`（以下）** を使う必要があります：

//```csharp
//if (payload.Length <= OuterChunkMax)
//```

//---

//### ✅まとめ

//| 条件式  | 65532 のとき | 意味                                 |
//| ---- | --------- | ---------------------------------- |
//| `<`  | ** チャンク**  | payload=65532 の場合も分割送信             |
//| `<=` | ** 直送**    | payload=65532 の場合はチャンクせず送信（あなたの要件） |

//---

//したがって、
//**「64KB（=65532+4Bヘッダ）のときはチャンクしない」** を実現したいなら、
//**`<=` が正解** です。
