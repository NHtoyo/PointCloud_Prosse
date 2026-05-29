# 実装計画: 超高速接続探索（Connected Component Labeling）アルゴリズムの最適化

## 目的

最大20万点（あるいは数百万点）の大規模点群に対して、現在の接続探索（Connected Component Selection）が「遅い（または以前より遅く感じる）」「95.0%で止まってしまう」という課題を解決します。
CloudCompareのオクトリー・グリッド接続探索（`DgmOctree::extractCCs`）の設計思想を参考に、Unity/C#環境向けに極限までアロケーションを排除した**「連結リスト型空間ハッシュ（Linked-List Spatial Hash）＋ BFS」**アルゴリズムを実装します。

## User Review Required

> [!IMPORTANT]
> - **メモリ確保（GCアロケーション）の完全排除**: 探索開始時に `Dictionary<Vector3Int, List<int>>` を作成する現在の方式は、数百万回のヒープ割当（`new List`）を発生させ、GCスパイクと処理遅延の主因となっていました。これをC++のポインタ構造に近い**「ヘッド配列＋次インデックス配列」による連結リスト型ハッシュ**に差し替えます。
> - **95.0%フリーズ対策の徹底**: GPUへのデータ転送（`UpdatePointBuffer`）の処理で例外や遅延が発生した場合でも、進捗モーダルが閉じられるよう、メインスレッド側の完了処理に安全な `try-catch` 保護を導入します。

## Proposed Changes

### PointCloudWorkbench コア機能
接続探索アルゴリズムをゼロ・アロケーション高速版にリファクタリングします。

#### [MODIFY] [PointCloudEditor.cs](file:///E:/VR/PointCloudVR/Assets/PointCloudWorkbench/Scripts/PointCloudEditor.cs)
* **`ApplyConnectionSelection` のリファクタリング**:
  * 従来の `Dictionary<Vector3Int, List<int>>` の使用を廃止。
  * `int[] head`（バケット数サイズ）と `int[] next`（点群数サイズ）のフラットな配列を使用する、高速・アロケーションフリーな空間ハッシュ（Linked-List Spatial Hashing）を構築。
  * キュー操作を `Queue<int>` クラスから、事前確保されたフラットな `int[] queue` 配列と `qHead`, `qTail` ポインタによる操作に変更し、BFSのオブジェクト生成コストを完全にゼロ化。
  * 27近傍セルの走査時、同一バケット内の複数座標の衝突（ハッシュ衝突）を正しく判定し、正確な距離計算（二乗距離）で連結判定を行う。
* **メインスレッド `Update` の例外保護**:
  * `finishedConnectionFlag` 処理内で `targetRenderer.UpdatePointBuffer()` を呼び出す際、`try-catch` で囲み、エラー時でも必ず `PointCloudProgressManager.Instance.Complete()` を実行させて進捗ウィンドウが95.0%で閉じなくなる不具合を解消。

## Verification Plan

### Automated / Manual Verification
1. **処理速度の測定**:
   - 20万点〜500万点のPLY点群を読み込み、接続探索（Connect）ツールを実行する。
   - 探索処理（ハッシュ構築＋BFS）が **数ミリ秒〜数十ミリ秒** で完了することを確認する。
2. **メモリ（GC）プロファイリング**:
   - Unityの Profiler で探索実行時の GC Alloc が **0バイト**（または極微小）であることを確認する。
3. **95.0%停止バグの確認**:
   - 探索完了後にフリーズすることなく、UI進捗ダイアログが確実にクローズし、選択結果が即座に点群表示に反映されることを確認する。
