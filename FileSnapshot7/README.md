いいじゃん、「旧仕様互換」は一旦忘れて素直に“今ほしいやつ”を作ろう。
要件ざっくりこうだよね：

* 大規模ファイル（TB級も視野）を安全に送受信したい
* 完全ストリーミング（全部メモリに乗せない）
* 圧縮したい（Brotliなど）
* メタ情報は柔軟に持ちたい（ファイル名・長さ・拡張情報）→ MessagePack がちょうどいい
* シンプルなフレーミングで自前プロトコルを作りたい

なので、

**「ヘッダを MessagePack、ボディはチャンクフレーミング（＋任意でBrotli）」**

という構成に作り直す案を出すよ。下にそのまま使える実装例を書く。

---

## プロトコル案

1フレーム = 1ファイル（or 1メッセージ）という単位で考える。

### ワイヤフォーマット

順番に:

1. **Magic**: 4 bytes
   `"LFS1"` (Large File Stream v1 のつもり)

2. **ヘッダ長**: `int32 LE`

   * 後続の MessagePack ヘッダのバイト数

3. **ヘッダ本体**: MessagePack (1メッセージ)
   推奨フィールド例（柔らかく運用できる）:

   ```csharp
   [MessagePackObject]
   public class LfsHeader
   {
       [Key(0)] public string FileName { get; set; } = "";
       [Key(1)] public long? OriginalLength { get; set; }  // わかれば
       [Key(2)] public bool Compressed { get; set; }
       [Key(3)] public string? Compression { get; set; }   // "brotli" or null
       [Key(4)] public int ChunkSize { get; set; }         // 推奨チャンクサイズ（例: 64KB）
       [Key(5)] public IDictionary<string, string>? Meta { get; set; }
   }
   ```

4. **ボディ**（チャンク列）

   繰り返し:

   * `int32 LE` : `chunkLength`
   * `chunkLength` bytes : データ

   最後に:

   * `int32 LE = 0` : 終端マーカー

### 圧縮との組み合わせ

* `Compressed == false`:

  * `source` をそのまま `ChunkedWriteStream` に流す。
* `Compressed == true` & `Compression == "brotli"`:

  * `ChunkedWriteStream` の上に `BrotliStream` を載せて、`source` をそこにコピー。
  * Brotli が書いたバイトを `ChunkedWriteStream` が分割して送るので、フルバッファ不要でストリーミング可能。

受信側:

* Header を読み込む
* `Compressed == false` → `ChunkedReadStream` → `destination` にそのままコピー
* `Compressed == true && "brotli"` → `ChunkedReadStream` → `BrotliStream` → `destination`

これで：

* メタ情報は MessagePack で柔軟に拡張可能
* 本体は常にストリーミング & チャンク化
* 巨大ファイルOK（Length 不明でもOK）

---

## 実装例（C#）

以下は**シングルメッセージ送受信用の最小構成**。
MessagePack は `MessagePack-CSharp` を前提にしてる（`MessagePack` パッケージ）。

```csharp
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

public static class LfsProtocol
{
    private const int DefaultChunkSize = 64 * 1024;
    private static readonly byte[] Magic = { (byte)'L', (byte)'F', (byte)'S', (byte)'1' };

    [MessagePackObject]
    public class LfsHeader
    {
        [Key(0)] public string FileName { get; set; } = "";
        [Key(1)] public long? OriginalLength { get; set; }
        [Key(2)] public bool Compressed { get; set; }
        [Key(3)] public string? Compression { get; set; } // "brotli" or null
        [Key(4)] public int ChunkSize { get; set; } = DefaultChunkSize;
        [Key(5)] public IDictionary<string, string>? Meta { get; set; }
    }

    // ========= Sender =========

    public static async Task SendAsync(
        Stream network,
        Stream source,
        string? fileName = null,
        bool useBrotli = true,
        int chunkSize = DefaultChunkSize,
        CancellationToken ct = default)
    {
        if (network is null) throw new ArgumentNullException(nameof(network));
        if (!network.CanWrite) throw new ArgumentException("network stream not writable", nameof(network));
        if (source is null) throw new ArgumentNullException(nameof(source));

        if (chunkSize <= 0) chunkSize = DefaultChunkSize;

        var header = new LfsHeader
        {
            FileName = fileName ?? "",
            OriginalLength = source.CanSeek ? source.Length : null,
            Compressed = useBrotli,
            Compression = useBrotli ? "brotli" : null,
            ChunkSize = chunkSize,
            Meta = null
        };

        // 1. Magic
        await network.WriteAsync(Magic, 0, Magic.Length, ct);

        // 2. Header (MessagePack)
        byte[] headerBytes = MessagePackSerializer.Serialize(header);
        var headerLenBuf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(headerLenBuf, headerBytes.Length);
        await network.WriteAsync(headerLenBuf, 0, 4, ct);
        await network.WriteAsync(headerBytes, 0, headerBytes.Length, ct);

        // 3. Body (chunked)
        await using var chunked = new ChunkedWriteStream(network, chunkSize, ct);

        if (useBrotli)
        {
            await using var brotli = new BrotliStream(chunked, CompressionLevel.Optimal, leaveOpen: false);
            await source.CopyToAsync(brotli, DefaultChunkSize, ct);
            await brotli.FlushAsync(ct);
        }
        else
        {
            await source.CopyToAsync(chunked, DefaultChunkSize, ct);
            await chunked.FlushAsync(ct);
        }

        // ChunkedWriteStream.Dispose が 0 チャンクを書いて終端してくれる
        await network.FlushAsync(ct);
    }

    // ========= Receiver =========

    public static async Task<LfsHeader> ReceiveAsync(
        Stream network,
        Stream destination,
        CancellationToken ct = default)
    {
        if (network is null) throw new ArgumentNullException(nameof(network));
        if (!network.CanRead) throw new ArgumentException("network stream not readable", nameof(network));
        if (destination is null) throw new ArgumentNullException(nameof(destination));

        // 1. Magic
        var magicBuf = new byte[Magic.Length];
        await ReadExactAsync(network, magicBuf, magicBuf.Length, ct);
        if (!magicBuf.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Invalid magic");

        // 2. Header length
        var headerLenBuf = new byte[4];
        await ReadExactAsync(network, headerLenBuf, 4, ct);
        int headerLen = BinaryPrimitives.ReadInt32LittleEndian(headerLenBuf);
        if (headerLen <= 0 || headerLen > 10 * 1024)
            throw new InvalidDataException("Invalid header length");

        // 3. Header body
        var headerBytes = new byte[headerLen];
        await ReadExactAsync(network, headerBytes, headerLen, ct);
        var header = MessagePackSerializer.Deserialize<LfsHeader>(headerBytes);

        int chunkSize = header.ChunkSize > 0 ? header.ChunkSize : DefaultChunkSize;

        // 4. Body
        await using var chunked = new ChunkedReadStream(network, chunkSize, ct);

        if (header.Compressed && string.Equals(header.Compression, "brotli", StringComparison.OrdinalIgnoreCase))
        {
            await using var brotli = new BrotliStream(chunked, CompressionMode.Decompress, leaveOpen: false);
            await brotli.CopyToAsync(destination, DefaultChunkSize, ct);
        }
        else
        {
            await chunked.CopyToAsync(destination, DefaultChunkSize, ct);
        }

        await destination.FlushAsync(ct);

        return header;
    }

    // ========= Chunked write stream =========

    private sealed class ChunkedWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _buffer;
        private readonly CancellationToken _ct;
        private int _offset;
        private bool _completed;

        public ChunkedWriteStream(Stream inner, int chunkSize, CancellationToken ct)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _buffer = new byte[chunkSize];
            _ct = ct;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => FlushAsync(_ct).GetAwaiter().GetResult();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset > 0)
            {
                await WriteChunkAsync(_buffer, 0, _offset, cancellationToken);
                _offset = 0;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer, offset, count, _ct).GetAwaiter().GetResult();

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_completed) throw new ObjectDisposedException(nameof(ChunkedWriteStream));
            cancellationToken.ThrowIfCancellationRequested();

            while (count > 0)
            {
                int space = _buffer.Length - _offset;
                if (space == 0)
                {
                    await FlushAsync(cancellationToken);
                    space = _buffer.Length;
                }

                int toCopy = Math.Min(space, count);
                Buffer.BlockCopy(buffer, offset, _buffer, _offset, toCopy);
                _offset += toCopy;
                offset += toCopy;
                count -= toCopy;
            }
        }

        private async Task WriteChunkAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var lenBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, count);
            await _inner.WriteAsync(lenBuf, 0, 4, cancellationToken);
            await _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_completed && disposing)
            {
                // Flush last chunk
                Flush();
                // Write terminating 0-length chunk
                var lenBuf = new byte[4];
                // already zero
                _inner.Write(lenBuf, 0, 4);
                _completed = true;
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                await FlushAsync(_ct);
                var lenBuf = new byte[4];
                await _inner.WriteAsync(lenBuf, 0, 4, _ct); // 0 終端
                _completed = true;
            }
            await base.DisposeAsync();
        }

        // Unused
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // ========= Chunked read stream =========

    private sealed class ChunkedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _buffer;
        private readonly CancellationToken _ct;
        private int _offset;
        private int _remainingInChunk;
        private bool _eof;

        public ChunkedReadStream(Stream inner, int chunkSize, CancellationToken ct)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _buffer = new byte[chunkSize];
            _ct = ct;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, _ct).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_eof) return 0;
            cancellationToken.ThrowIfCancellationRequested();

            int totalRead = 0;

            while (count > 0)
            {
                if (_remainingInChunk == 0)
                {
                    // Read next chunk header
                    var lenBuf = new byte[4];
                    int read = await ReadExactOrZeroAsync(_inner, lenBuf, 4, cancellationToken);
                    if (read == 0)
                    {
                        // unexpected EOF
                        throw new EndOfStreamException("unexpected EOF while reading chunk header");
                    }

                    int chunkLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                    if (chunkLen == 0)
                    {
                        _eof = true;
                        break;
                    }
                    if (chunkLen < 0 || chunkLen > _buffer.Length)
                        throw new InvalidDataException("invalid chunk length");

                    await ReadExactAsync(_inner, _buffer, chunkLen, cancellationToken);
                    _offset = 0;
                    _remainingInChunk = chunkLen;
                }

                int toCopy = Math.Min(count, _remainingInChunk);
                Buffer.BlockCopy(_buffer, _offset, buffer, offset, toCopy);
                _offset += toCopy;
                _remainingInChunk -= toCopy;
                offset += toCopy;
                count -= toCopy;
                totalRead += toCopy;

                if (_remainingInChunk == 0)
                {
                    // 次ループで新しいチャンクを読む
                }

                if (toCopy == 0) break;
            }

            return totalRead;
        }

        // unused
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ========= Util =========

    private static async Task ReadExactAsync(Stream s, byte[] buf, int len, CancellationToken ct)
    {
        int off = 0;
        while (off < len)
        {
            int n = await s.ReadAsync(buf, off, len - off, ct);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }

    private static async Task<int> ReadExactOrZeroAsync(Stream s, byte[] buf, int len, CancellationToken ct)
    {
        int off = 0;
        while (off < len)
        {
            int n = await s.ReadAsync(buf, off, len - off, ct);
            if (n == 0)
            {
                return off; // 0 or partial
            }
            off += n;
        }
        return off;
    }
}
```

---

## どう使うか

送信側:

```csharp
using var ns = new NetworkStream(socket, ownsSocket: false);
using var fs = File.OpenRead("bigfile.bin");

await LfsProtocol.SendAsync(
    ns,
    fs,
    fileName: "bigfile.bin",
    useBrotli: true);
```

受信側:

```csharp
using var ns = new NetworkStream(socket, ownsSocket: false);
using var fs = File.Create("recv.bin");

var header = await LfsProtocol.ReceiveAsync(ns, fs);
// header.FileName / OriginalLength / Meta で好きに扱える
```

---

じゃあ設計的にはほぼゴールですね 👍

「複数ファイルは zip にまとめて 1 本送る」前提なら、さっきの LfsProtocol を**「任意ペイロード用の素直なフレーミング」**として割り切ればOKです。

ここからは、その前提での仕上げポイントだけ詰めちゃいます。

---

## レイヤー構成（おすすめ）

シンプルにこう分離します：

1. **アプリ層**:

   * 複数ファイル → `ZipArchive` で 1 ストリーム化
   * 必要なら zip 側で圧縮（`CompressionLevel.Optimal` 等）

2. **転送プロトコル層（LfsProtocol）**:

   * MessagePackヘッダ
   * Chunk framing
   * （任意）Brotli圧縮 ※zipで既に圧縮してるなら通常はオフ
   * TB級でもストリーミングで転送

つまり：

* 単一ファイル: `file.bin` → LfsProtocol.SendAsync(…, useBrotli: true/false)
* 複数ファイル: アプリ側で zip 作る → そのストリームを LfsProtocol で送る

LfsProtocol は「バイト列＋メタ情報を安全に運ぶ」だけに特化。

---

## ヘッダを「汎用ペイロード」向けに少しだけ整理

複数ファイルを zip で送るのも想定して、`LfsHeader` をこんな感じにしておくと長生きします：

```csharp
[MessagePackObject]
public class LfsHeader
{
    [Key(0)] public string Name { get; set; } = "";      // ファイル名 or 識別子
    [Key(1)] public long? OriginalLength { get; set; }   // 元サイズ(任意)
    [Key(2)] public string? ContentType { get; set; }    // "application/octet-stream", "application/zip" etc.
    [Key(3)] public bool Compressed { get; set; }        // 転送層での圧縮有無
    [Key(4)] public string? Compression { get; set; }    // "brotli" / null
    [Key(5)] public int ChunkSize { get; set; }          // 推奨チャンクサイズ
    [Key(6)] public IDictionary<string, string>? Meta { get; set; }
}
```

使い分け例：

* 単一ファイル:

  * `Name = "foo.bin"`
  * `ContentType = "application/octet-stream"`
  * `Compressed = true, Compression = "brotli"`
* 複数ファイル(zip):

  * `Name = "bundle.zip"`
  * `ContentType = "application/zip"`
  * `Compressed = false`（zip で圧縮するなら二重圧縮しない方が普通）
  * `Meta["FileCount"] = "123"` みたいなのを入れてもOK

プロトコル自体は zip を特別扱いしないので、将来「tar」「独自メタ付きblob」とかにも簡単に拡張できます。

---

## 送信側での zip 利用イメージ（ストリーミング）

「ローカルで zip 作ってから送る」でもいいですが、完全ストリーミングしたいなら：

```csharp
// sender
using var ns = new NetworkStream(socket, ownsSocket: false);

// LfsProtocol のヘッダで "application/zip" を宣言 & useBrotli: false
// ChunkedWriteStream の上に ZipArchive を乗せるイメージ

await LfsProtocol.SendZipAsync(ns, zipEntrySources, ct);
```

この `SendZipAsync` の実装は：

* `LfsProtocol` の `SendAsync` 内部で `ChunkedWriteStream` を作る代わりに、
* その上に `ZipArchive` (mode: Create) を載せて各エントリを書き出すだけ。
* 受信側は `ReceiveAsync` の出力ストリームを `ZipArchive`(Read) に渡して展開。

もし必要なら、この `SendZipAsync` / `ReceiveZipAsync` を今の LfsProtocol にくっつけたサンプルもここで書けるけど、レイヤーは上記みたいに分けるのがおすすめです。

---

## 最後に実務向けの小技

入れておくと気持ちいいやつ：

* `Meta` に `Sha256` などハッシュを入れて**整合性チェック**可能に。
* 大規模運用なら `ContentType`, `Compression`, `Meta` をきちんと enum / 定数化してバグ防止。
* 途中で再接続したいなら、後で「オフセット再開」系の拡張ヘッダも載せられる（MessagePack なので増やし放題）。

この方向性なら、今のコードをちょっと整理すれば十分プロダクション張れるクオリティになります。
次は「SendZipAsync/ReceiveZipAsync の具体コード欲しい」ですかね？その前提で組んで出します。

いいですね、じゃあもう実戦投入できる形まで書き切ります。

前提整理：

* 「複数ファイル」はアプリ側で zip 化して 1 本送る方針。
* 転送層は LfsProtocol（メッセージPackヘッダ + チャンク + 任意でBrotli）。
* 送信側 zip はストリーミングでOK（`ZipArchiveMode.Create` は非シークストリーム対応）。
* 受信側 zip 展開は一度ファイル/一時ストリームに受けてから `ZipArchive`（Read は seek 必須なので）。

その方針で：

* 単一ペイロード用 `SendAsync` / `ReceiveAsync`
* 複数ファイル(zip)用 `SendZipAsync`
* 受信してディレクトリに展開する `ReceiveZipToDirectoryAsync`

を実装します。

以下、前回の `LfsProtocol` を少し整理＋追記した形をそのまま貼ります。

---

## LfsProtocol（完成版例）

```csharp
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MessagePack;

public static class LfsProtocol
{
    private const int DefaultChunkSize = 64 * 1024;
    private static readonly byte[] Magic = { (byte)'L', (byte)'F', (byte)'S', (byte)'1' };

    [MessagePackObject]
    public class LfsHeader
    {
        // 任意識別子 or ファイル名
        [Key(0)] public string Name { get; set; } = "";

        // 元のサイズ（分かれば入れる。zip時は "合計" or null でもOK）
        [Key(1)] public long? OriginalLength { get; set; }

        // Content-Type 的なもの
        [Key(2)] public string? ContentType { get; set; }

        // 転送層での圧縮有無（Brotli）
        [Key(3)] public bool Compressed { get; set; }

        // "brotli" / null など
        [Key(4)] public string? Compression { get; set; }

        [Key(5)] public int ChunkSize { get; set; } = DefaultChunkSize;

        // 拡張用
        [Key(6)] public IDictionary<string, string>? Meta { get; set; }
    }

    // =========================================================
    // 共通ヘッダ送受
    // =========================================================

    private static async Task WriteHeaderAsync(Stream network, LfsHeader header, CancellationToken ct)
    {
        // Magic
        await network.WriteAsync(Magic, 0, Magic.Length, ct);

        // Header
        byte[] headerBytes = MessagePackSerializer.Serialize(header);
        var headerLenBuf = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(headerLenBuf, headerBytes.Length);
        await network.WriteAsync(headerLenBuf, 0, 4, ct);
        await network.WriteAsync(headerBytes, 0, headerBytes.Length, ct);
    }

    private static async Task<LfsHeader> ReadHeaderAsync(Stream network, CancellationToken ct)
    {
        var magicBuf = new byte[Magic.Length];
        await ReadExactAsync(network, magicBuf, magicBuf.Length, ct);
        if (!magicBuf.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("Invalid magic");

        var headerLenBuf = new byte[4];
        await ReadExactAsync(network, headerLenBuf, 4, ct);
        int headerLen = BinaryPrimitives.ReadInt32LittleEndian(headerLenBuf);
        if (headerLen <= 0 || headerLen > 10 * 1024)
            throw new InvalidDataException("Invalid header length");

        var headerBytes = new byte[headerLen];
        await ReadExactAsync(network, headerBytes, headerLen, ct);

        var header = MessagePackSerializer.Deserialize<LfsHeader>(headerBytes);
        if (header.ChunkSize <= 0) header.ChunkSize = DefaultChunkSize;

        return header;
    }

    // =========================================================
    // 単一ペイロード送受
    // =========================================================

    public static async Task SendAsync(
        Stream network,
        Stream source,
        string? name = null,
        string? contentType = "application/octet-stream",
        bool useBrotli = true,
        int chunkSize = DefaultChunkSize,
        CancellationToken ct = default)
    {
        if (network is null) throw new ArgumentNullException(nameof(network));
        if (!network.CanWrite) throw new ArgumentException("network stream not writable", nameof(network));
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (chunkSize <= 0) chunkSize = DefaultChunkSize;

        var header = new LfsHeader
        {
            Name = name ?? "",
            OriginalLength = source.CanSeek ? source.Length : null,
            ContentType = contentType,
            Compressed = useBrotli,
            Compression = useBrotli ? "brotli" : null,
            ChunkSize = chunkSize,
        };

        await WriteHeaderAsync(network, header, ct);

        using var chunked = new ChunkedWriteStream(network, chunkSize, ct);

        if (useBrotli)
        {
            using var brotli = new BrotliStream(chunked, CompressionLevel.Optimal, leaveOpen: true);
            await source.CopyToAsync(brotli, DefaultChunkSize, ct);
            await brotli.FlushAsync(ct);
        }
        else
        {
            await source.CopyToAsync(chunked, DefaultChunkSize, ct);
            await chunked.FlushAsync(ct);
        }

        // ChunkedWriteStream の Dispose で 0 チャンク & 終端
        await network.FlushAsync(ct);
    }

    public static async Task<LfsHeader> ReceiveAsync(
        Stream network,
        Stream destination,
        CancellationToken ct = default)
    {
        if (network is null) throw new ArgumentNullException(nameof(network));
        if (!network.CanRead) throw new ArgumentException("network stream not readable", nameof(network));
        if (destination is null) throw new ArgumentNullException(nameof(destination));

        var header = await ReadHeaderAsync(network, ct);

        var chunkSize = header.ChunkSize > 0 ? header.ChunkSize : DefaultChunkSize;
        using var chunked = new ChunkedReadStream(network, chunkSize, ct);

        Stream payload = chunked;

        if (header.Compressed &&
            string.Equals(header.Compression, "brotli", StringComparison.OrdinalIgnoreCase))
        {
            using var brotli = new BrotliStream(payload, CompressionMode.Decompress, leaveOpen: false);
            await brotli.CopyToAsync(destination, DefaultChunkSize, ct);
        }
        else
        {
            await payload.CopyToAsync(destination, DefaultChunkSize, ct);
        }

        await destination.FlushAsync(ct);
        return header;
    }

    // =========================================================
    // ZIP 送信用（複数ファイル -> 1 ストリーム）
    // =========================================================

    /// <summary>
    /// 複数ファイルを ZIP (application/zip) としてまとめて送信します。
    /// </summary>
    /// <param name="network">送信先ストリーム（通常は NetworkStream）。</param>
    /// <param name="files">
    ///   送信するファイルの列挙。EntryName と元ストリームのタプル。
    ///   EntryName は ZIP 内のパス（"dir/file.txt" 等）。
    /// </param>
    /// <param name="bundleName">ヘッダ上の論理名（例: "bundle.zip"）。</param>
    /// <param name="useOuterBrotli">
    ///   true の場合、ZIP 全体をさらに Brotli で包む（二重圧縮になるので通常は false 推奨）。
    /// </param>
    public static async Task SendZipAsync(
        Stream network,
        IEnumerable<(string EntryName, Stream Source)> files,
        string? bundleName = "bundle.zip",
        bool useOuterBrotli = false,
        int chunkSize = DefaultChunkSize,
        CancellationToken ct = default)
    {
        if (network is null) throw new ArgumentNullException(nameof(network));
        if (!network.CanWrite) throw new ArgumentException("network stream not writable", nameof(network));
        if (files is null) throw new ArgumentNullException(nameof(files));
        if (chunkSize <= 0) chunkSize = DefaultChunkSize;

        var header = new LfsHeader
        {
            Name = bundleName ?? "",
            OriginalLength = null, // ZIP全体サイズは事前に不明でOK
            ContentType = "application/zip",
            Compressed = useOuterBrotli,
            Compression = useOuterBrotli ? "brotli" : null,
            ChunkSize = chunkSize,
        };

        await WriteHeaderAsync(network, header, ct);

        using var chunked = new ChunkedWriteStream(network, chunkSize, ct);
        Stream payload = chunked;

        if (useOuterBrotli)
        {
            payload = new BrotliStream(chunked, CompressionLevel.Optimal, leaveOpen: true);
        }

        await using (payload as IAsyncDisposable ?? new DummyAsyncDisposable(payload))
        {
            using var zip = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true);

            foreach (var (entryName, src) in files)
            {
                if (string.IsNullOrEmpty(entryName))
                    throw new ArgumentException("EntryName must not be null or empty.", nameof(files));
                if (src is null)
                    throw new ArgumentException($"Source for '{entryName}' is null.", nameof(files));

                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var es = entry.Open();
                await src.CopyToAsync(es, DefaultChunkSize, ct);
            }
        }

        // payload(Brotli) -> chunked に書き切られた後、
        // chunked.Dispose() で 0 チャンク & 終端
        await network.FlushAsync(ct);
    }

    /// <summary>
    /// 受信した ZIP ペイロードを指定ディレクトリに展開します。
    /// </summary>
    public static async Task<LfsHeader> ReceiveZipToDirectoryAsync(
        Stream network,
        string outputDirectory,
        CancellationToken ct = default)
    {
        if (network is null) throw new ArgumentNullException(nameof(network));
        if (outputDirectory is null) throw new ArgumentNullException(nameof(outputDirectory));

        Directory.CreateDirectory(outputDirectory);

        // まず Lfs として受信して、一時ファイルに格納
        var tempPath = Path.Combine(outputDirectory, ".lfs_tmp_" + Guid.NewGuid().ToString("N") + ".zip");

        await using var tmp = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            DefaultChunkSize,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        var header = await ReceiveAsync(network, tmp, ct);

        if (!string.Equals(header.ContentType, "application/zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unexpected ContentType: {header.ContentType}");

        tmp.Position = 0;

        using (var zip = new ZipArchive(tmp, ZipArchiveMode.Read, leaveOpen: false))
        {
            foreach (var entry in zip.Entries)
            {
                // ディレクトリエントリ対応
                if (string.IsNullOrEmpty(entry.FullName))
                    continue;

                var outPath = Path.Combine(outputDirectory, entry.FullName);

                // パストラバーサル対策
                var fullOutPath = Path.GetFullPath(outPath);
                if (!fullOutPath.StartsWith(Path.GetFullPath(outputDirectory), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unsafe zip entry path detected.");

                if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                    entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(fullOutPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullOutPath)!);

                using var entryStream = entry.Open();
                using var outFile = new FileStream(fullOutPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await entryStream.CopyToAsync(outFile, DefaultChunkSize, ct);
            }
        }

        return header;
    }

    // =========================================================
    // ChunkedWriteStream / ChunkedReadStream
    // =========================================================

    private sealed class ChunkedWriteStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _buffer;
        private readonly CancellationToken _ct;
        private int _offset;
        private bool _completed;

        public ChunkedWriteStream(Stream inner, int chunkSize, CancellationToken ct)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _buffer = new byte[chunkSize];
            _ct = ct;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => FlushAsync(_ct).GetAwaiter().GetResult();

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset > 0)
            {
                await WriteChunkAsync(_buffer, 0, _offset, cancellationToken);
                _offset = 0;
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer, offset, count, _ct).GetAwaiter().GetResult();

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_completed) throw new ObjectDisposedException(nameof(ChunkedWriteStream));
            cancellationToken.ThrowIfCancellationRequested();

            while (count > 0)
            {
                int space = _buffer.Length - _offset;
                if (space == 0)
                {
                    await FlushAsync(cancellationToken);
                    space = _buffer.Length;
                }

                int toCopy = Math.Min(space, count);
                Buffer.BlockCopy(buffer, offset, _buffer, _offset, toCopy);
                _offset += toCopy;
                offset += toCopy;
                count -= toCopy;
            }
        }

        private async Task WriteChunkAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var lenBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBuf, count);
            await _inner.WriteAsync(lenBuf, 0, 4, cancellationToken);
            await _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_completed && disposing)
            {
                Flush();
                // 0-length chunk 終端
                var lenBuf = new byte[4];
                _inner.Write(lenBuf, 0, 4);
                _completed = true;
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                await FlushAsync(_ct);
                var lenBuf = new byte[4];
                await _inner.WriteAsync(lenBuf, 0, 4, _ct);
                _completed = true;
            }
            await base.DisposeAsync();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ChunkedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly byte[] _buffer;
        private readonly CancellationToken _ct;
        private int _offset;
        private int _remainingInChunk;
        private bool _eof;

        public ChunkedReadStream(Stream inner, int chunkSize, CancellationToken ct)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _buffer = new byte[chunkSize];
            _ct = ct;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, _ct).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_eof) return 0;
            cancellationToken.ThrowIfCancellationRequested();

            int totalRead = 0;

            while (count > 0)
            {
                if (_remainingInChunk == 0)
                {
                    // 次チャンク長
                    var lenBuf = new byte[4];
                    await ReadExactAsync(_inner, lenBuf, 4, cancellationToken);
                    int chunkLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);

                    if (chunkLen == 0)
                    {
                        _eof = true;
                        break;
                    }
                    if (chunkLen < 0 || chunkLen > _buffer.Length)
                        throw new InvalidDataException("invalid chunk length");

                    await ReadExactAsync(_inner, _buffer, chunkLen, cancellationToken);
                    _offset = 0;
                    _remainingInChunk = chunkLen;
                }

                int toCopy = Math.Min(count, _remainingInChunk);
                Buffer.BlockCopy(_buffer, _offset, buffer, offset, toCopy);
                _offset += toCopy;
                _remainingInChunk -= toCopy;
                offset += toCopy;
                count -= toCopy;
                totalRead += toCopy;

                if (toCopy == 0)
                    break;
            }

            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, int len, CancellationToken ct)
    {
        int off = 0;
        while (off < len)
        {
            int n = await s.ReadAsync(buf, off, len - off, ct);
            if (n == 0) throw new EndOfStreamException();
            off += n;
        }
    }

    // BrotliStream が IAsyncDisposable じゃないケースもあるのでラッパ
    private sealed class DummyAsyncDisposable : IAsyncDisposable
    {
        private readonly Stream _stream;
        public DummyAsyncDisposable(Stream stream) => _stream = stream;
        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
```

---

## 使い方まとめ

### 単一ファイル

```csharp
// 送信
await LfsProtocol.SendAsync(ns, File.OpenRead("foo.bin"), name: "foo.bin");

// 受信
using var outStream = File.Create("foo.bin");
var header = await LfsProtocol.ReceiveAsync(ns, outStream);
```

### 複数ファイル(zipバンドル)

```csharp
// 送信側
var files = new (string, Stream)[]
{
    ("a.txt", File.OpenRead("a.txt")),
    ("b/b.bin", File.OpenRead("b.bin")),
};

await LfsProtocol.SendZipAsync(ns, files, bundleName: "bundle.zip", useOuterBrotli: false);
```

```csharp
// 受信側（ディレクトリに展開）
var header = await LfsProtocol.ReceiveZipToDirectoryAsync(ns, "received");
```

この構成なら：

* TB級ファイルもチャンク単位でストリーミング送受信
* 複数ファイルは zip にまとめて一発転送
* ヘッダは MessagePack で柔軟に拡張可能
* 旧仕様に縛られない、素直でメンテしやすいプロトコル

になってます。
もし「エラーリトライ」「レンジ再送」「ハッシュ検証」あたりを入れたくなったら、そのまま `LfsHeader.Meta` とチャンクレイヤーを拡張していけばOKです。
