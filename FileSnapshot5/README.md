了解！ここでは、いまの設計での **AcceptService**（受け付け）と **ProcessService**（処理・送信）の役割と流れを、できるだけ噛み砕いて説明します。コードを見ながら「誰が、いつ、何を、どの資源に対して行うか」に焦点を当てます。

---

# 全体像（ざっくり）

```
[Client] --TCP--> [AcceptService] --(SyncJobをenqueue)--> [Channel] --dequeue--> [ProcessService(複数ワーカー)]
                                     ↑                                                     |
                                     |-------------------- 完了(TCS) ----------------------|
```

* **AcceptService**：TCP接続を受け付け、1行コマンド（`SYNCGET|...`）を読み、**ジョブ（SyncJob）**を `Channel` に **enqueue**。
  同時に、**その接続の Stream/Writer と完了通知用の TCS** をジョブに載せ、**処理完了まで待つ**（TCS を await）。
* **ProcessService**：`Channel` からジョブを **dequeue**。サーバ側スナップショットを見て**比較**し、**応答の行**や**必要ならファイル本体（Base64）**を**そのジョブの Stream に書く**。書き終えたら **TCS を完了** させる。

---

# AcceptService（受け付け側）

## 役割

* **TCP リッスン**（`TcpListener`）
* **接続ごとの初期I/O**（最初の1行を受信）
* **ジョブ作成→Channelに投げる**（enqueue）
* **ジョブ完了まで待つ**（TCS を await）
* **接続のリソース管理**（`TcpClient/NetworkStream/BufferedStream/Reader/Writer` を確実に `Dispose`）

## 処理の流れ（1接続あたり）

1. `AcceptTcpClientAsync` でクライアント接続を受け取る。
2. `NetworkStream` → `BufferedStream` → `StreamReader/Writer` を作成（UTF-8、AutoFlush 等）。
3. **1行コマンドを受信**：
   例）`SYNCGET|<filePath>|<clientUnixMs>|<clientSize>`
4. 形式チェック。おかしければ `ERROR|xxx` を返して終了。
5. **ジョブ（SyncJob）を組み立てる**：

   * `FilePath` / `ClientUnixMs` / `ClientSize`（比較用メタ）
   * **Writer / Stream**（この接続に書くためのハンドル）
   * **TaskCompletionSource<bool>（TCS）**（完了通知）
6. **ChannelWriter.WriteAsync** でジョブを **enqueue**。
7. **TCS を await**（= ProcessService が処理を終えるまで待つ）。
   → これにより、「Accept は軽いが、**応答が返るまでソケットを握って待つ**」モードになる。
8. 最後に `Flush`、`Dispose`（using）で**後片付け**。

### 大事なポイント

* **Stream/Writer の「所有権」**を **ジョブに渡す**。
  → そのジョブを処理する **ワーカーだけ**が **その接続**に書ける。混線しない。
* **TCS** は「**このジョブはもう書き終わりました**」の合図。
  → Accept 側が TCS を await するので、**応答を最後までクライアントへ送りきってから**ソケットを閉じられる。
* **Channel はバッファ付き（bounded）**：
  → 負荷が高い時は `WriteAsync` がブロックし、**受け付け側に自然なバックプレッシャー**がかかる（無制限にメモリに溜めない）。

---

# ProcessService（処理・送信側）

## 役割

* **ChannelReader** からジョブを **dequeue**
* サーバ側の **スナップショット**（`IFileIndex.Snapshot`）と**クライアント側メタ**の比較
* **応答行**の送信（`NOTFOUND` / `NOTMODIFIED` / `FILEB64|...`）
* 必要なら **ファイル本体を Base64 ストリーミング送信**
* 終了時に **TCS を完了** させる（Accept に「もう終わったよ」を通知）

## 並列化

* `ProcessService` は **複数ワーカー**（CPUコア数/2 など）を `WorkerLoopAsync` で起動。
* 各ワーカーは `await foreach` で `Channel` から順にジョブを取り出して処理。
* **同じ接続の Stream** を **別のジョブが触ることはない**（1ジョブに1接続の Writer/Stream が載っているから）。
  → **接続内の順序と整合性**が保たれる。

## 処理の流れ（1ジョブ）

1. `Snapshot` から `FilePath` を引いて、**存在チェック**：

   * なければ `NOTFOUND` を書いて終了。
2. サーバ側の **サイズ・更新時刻（Unixミリ秒）** を取得。
   **変更判定**：`serverSize != clientSize` または `serverMtime > clientMtime`。
3. 変更なし → `NOTMODIFIED` を書いて終了。
4. 変更あり → **送信**：

   * まず **ヘッダ行**：`FILEB64|<name>|<rawLen>|<b64Len>` を `Writer.WriteLineAsync(...)`
   * つづいて **Base64 本文をストリーミング送信**：
     `FileStream` を `ToBase64Transform` に通し、**ちょうど `b64Len` 文字**分を `Stream` に書く
     （大きなファイルでも**RAMに展開せず**、一定バッファで流す）
   * `Stream.FlushAsync`
5. すべて書き終えたら **TCS.TrySetResult(true)**。
   → Accept 側の `await tcs.Task` が解放され、接続をクリーンに閉じられる。

### 大事なポイント

* **「このジョブだけがこの Stream に書く」**という排他が、**ジョブ設計で保証**されている。
  → `SingleReader=false/SingleWriter=false` は **Channel 内部の並行性**の話で、Stream 排他は**ここで担保**。
* **Base64 送出**は「**テキストだけで完結**」するので、Reader とバイナリ混在の罠を避けやすい。
  （効率重視なら生バイナリ直送でも可。ただし**行読み（ヘッダ）と生バイトを同じストリームで**扱う実装上の注意が必要）
* **例外時**は `ERROR|processing_failed` を書き、**TCS を完了**して Accept 側を解放。

---

# もう少し踏み込んだ設計の意図

* **責務分離**

  * Accept は「**接続の寿命管理＋受信1行＋キュー投入**」に徹する。
  * Process は「**重い処理（スナップ比較・ファイル読み・ネット送信）**」に専念。
* **バックプレッシャー**

  * `Channel.CreateBounded` のおかげで、処理が追いつかないときは Accept 側の `WriteAsync` が待つ。
    → メモリやソケット枯渇を**自然に抑制**。
* **シャットダウンの綺麗さ**

  * `AcceptService` 終了時に `Channel.Writer.TryComplete()`
  * `ProcessService` の `ReadAllAsync` が自然に終わる
  * さらに Accept は**アクティブな接続タスクを追跡**して `WhenAll` で待つ

---

# 典型的な質問への即答

* **Q: 同時に大量接続が来たら？**
  A: `HashSet<Task>` で接続タスクを追跡＋`SemaphoreSlim` で**同時処理数制限**が有効。`Channel` もバッファ上限でブレーキをかける。

* **Q: 同じ接続に複数ワーカーが書いたら？**
  A: しません。**1ジョブ=1接続**の Writer/Stream を持つので、**そのジョブを処理するワーカーだけが書く**設計です。

* **Q: なんで TCS？**
  A: Accept が「**送信完了を確実に待ってから**接続を閉じる」ための同期ポイント。処理層が完了したら必ず合図を返す。

---

# まとめ

* **AcceptService**

  * 接続を受け、1行を読み、**Stream/Writer と TCS を添えたジョブ**を enqueue。
  * **TCS await** で送信完了まで待ち、後片付け。

* **ProcessService**

  * ジョブを dequeue、**スナップショットと比較**し、
    `NOTFOUND / NOTMODIFIED / FILEB64+本文` を **そのジョブの Stream に送出**。
  * 終わったら **TCS 完了**で Accept に合図。

この2役が「**接続寿命の管理**」と「**重い処理の並行化**」をきれいに分担しつつ、**同じ接続のストリームに対する排他**をジョブ構造で保証している――というのが設計のキモです。
