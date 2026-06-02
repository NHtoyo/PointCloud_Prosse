# HANDOVER_DOC: PointCloudVR

## プロジェクト名
PointCloudVR (植物点群処理ワークベンチ)

## 目的
Unity上において数千万点規模の大規模な点群データ（PLY形式等）を高速に描画し、CloudCompareに近い快適な3D空間ナビゲーション（オービット回転、2Dロール、パン、ズーム）と、点群の部分選択・アノテーション（分類ラベリング、削除、幾何形状検出、空間接続探索）を行うための研究用3Dワークベンチの構築。

## 主要フォルダ
- `Assets/` : Unity プロジェクトのアセットルート
- `Assets/PointCloudWorkbench/` : 本ワークベンチの主要機能（カメラ、エディタ、UIなど）のスクリプトとアセット
- `Assets/PointCloudWorkbench/Scripts/` : 主要スクリプト群

## 主要ファイル
| ファイル名 | 相対パス | 役割・機能概要 |
| :--- | :--- | :--- |
| **CloudCompareCameraController.cs** | `Assets/PointCloudWorkbench/Scripts/CloudCompareCameraController.cs` | CloudCompare互換のカメラ移動・オービット回転制御システム。入力監視と姿勢更新を担当。常に「左ドラッグ＝回転、右ドラッグ＝パン」で固定。 |
| **TrackballMath.cs** | `Assets/PointCloudWorkbench/Scripts/TrackballMath.cs` | マウススクリーン座標を仮想トラックボール上の3Dベクトルへ変換する数学計算ヘルパークラス。 |
| **PivotIndicator.cs** | `Assets/PointCloudWorkbench/Scripts/PivotIndicator.cs` | 回転のピボットポイントに表示する3軸のクロスヘアインジケータ。 |
| **CameraRotationGuide.cs** | `Assets/PointCloudWorkbench/Scripts/CameraRotationGuide.cs` | 回転中にピボットに表示される3軸ガイドおよびビューポート外周リングの描画。 |
| **PointCloudPicker.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudPicker.cs` | カメラからの視線レイと点群の最短垂直距離から最も近い点を検出してピボット座標を設定する。 |
| **PointCloudOctree.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudOctree.cs` | 点群の空間分割オクトリー（八分木）データ構造。階層的LOD用の代表点選定や空間検索を担当。 |
| **PointCloudRenderer.cs** | `Assets/PointCloudRenderer.cs` | GPU（ComputeBufferとProcedural Shader）を利用して高精度の点群データを描画する。 |
| **PointCloudLoader.cs** | `Assets/PointCloudLoader.cs` | PLYファイルなどの点群データを非同期でロードしRendererにセットアップする。読み込み上限は2000万点。 |
| **PointCloudEditor.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudEditor.cs` | 点群のアノテーション・編集用コアエンジン。ブラシ、矩形、なげなわ、接続探索、RANSAC等の選択や、ノイズ物理除去PLYエクスポートを担当。 |
| **PointCloudEditorUI.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudEditorUI.cs` | アノテーションツールのUI表示（OnGUI）や進捗モーダル表示、およびノイズ除去セクションの描画・委譲を担当。 |
| **PointCloudController.cs** | `Assets/PointCloudController.cs` | VR環境（XRI Grab）およびPCデバッグ用に、点群オブジェクト全体の移動・回転・スケーリングといったトランスフォーム制御を担う。常に「左ドラッグ＝回転、右ドラッグ＝パン」で固定。 |
| **PointCloudManager.cs** | `Assets/PointCloudManager.cs` | 基準（Reference）と対象（Aligned）点群の管理、簡易距離比較、表示モード切り替えを担当。 |
| **PointCloudData.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudData.cs` | 点群に含まれる各点の構造体 `PointData` などの共通 of データ定義。 |
| **run_noise_filter.py** | `python_backend/run_noise_filter.py` | CLIエントリポイント。パラメータを受け取り、バッチ処理全体の制御と結果出力を担当。 |
| **noise_filters.py** | `python_backend/noise_filters.py` | SOR / ROR / 密度 / DBSCAN フィルタの実装。DBSCAN of 自動ダウンサンプリングと同期フォールバックを制御。 |
| **pointcloud_io.py** | `python_backend/pointcloud_io.py` | Open3D を用いた PLY ファイル of ロード・セーブ、および Python 内部用 NPZ データ of ロード・セーブ。 |
| **result_writer.py** | `python_backend/result_writer.py` | 処理結果をリトルエンディアン of `.bin` ファイル、`metadata.json`、`removal_report.json` に出力。 |
| **PythonBridge.cs** | `Assets/PointCloudWorkbench/Scripts/PythonBridge.cs` | Pythonプロセスを非同期で詳細パラメータ引数を渡して起動・監視し、リトルエンディアンバイナリを高速デシリアライズするブリッジ。 |
| **NoiseFilterResult.cs** | `Assets/PointCloudWorkbench/Scripts/NoiseFilterResult.cs` | デシリアライズされた各種ノイズスコア、クラスタID、削除理由マスクを保持するデータクラス。 |
| **NoiseFilterManager.cs** | `Assets/PointCloudWorkbench/Scripts/NoiseFilterManager.cs` | プレビューフラグ適用、非表示確定、および最大5世代のUndo/Redo履歴スタックを管理するデータ層マネージャ。 |
| **NoiseFilterUI.cs** | `Assets/PointCloudWorkbench/Scripts/NoiseFilterUI.cs` | 各種ノイズフィルタパラメータ（SOR, ROR, DBSCAN）のトグル・スライダー設定および非同期プロセス実行とUndo/Redo制御を担当するUIクラス。 |

## 実行方法
1. Unity Editor（バージョン 6000.4.7f1）で `E:\VR\PointCloudVR` プロジェクトを開く。
2. `Assets/VRTestScene.unity` シーンを開く。
3. エディタの Play ボタン（再生）を押す。
   - PLY点群データが自動で非同期ロードされ、自動センタリングされます。
   - PC上でのナビゲーション操作は、常に **「左ドラッグ ＝ カメラ回転（オービット）」**、**「右ドラッグ ＝ カメラ平行移動（パン）」**、**「マウスホイール回転 ＝ ズーム」** で操作可能です。
   - オブジェクト自体のトランスフォーム操作は、修飾キーなしのドラッグ（**左ドラッグ ＝ オブジェクト回転**、**右ドラッグ ＝ オブジェクト平行移動**）で行います。
   - アノテーションツール選択時（選択画面）は、カメラおよびオブジェクトの操作系統はそのまま維持され、選択適用のみ **「マウスホイール押し込み（中ボタン）」** をトリガーとして行います。

## 現在分かっている注意点
- **操作系統の一貫性**: 通常時・アノテーションツール選択時を問わず、操作系統は常に「左ドラッグ＝回転、右ドラッグ＝パン」で完全に固定されています。アノテーション適用（ブラシで塗る、なげなわ等）は「中クリック」で実行するため、カメラ操作と重複しません。オブジェクト移動に修飾キー（Ctrl等）は不要です。
- **2Dロール回転と右手系補正**: 仮想トラックボールの球外ドラッグでZ軸周りの2Dロールが実行されます。Unity（左手系）とCC（右手系）のZ軸解釈の違いを吸収するため、`rotCameraCS.z = -rotCameraCS.z;` で補正しています。
- **高負荷タスクの非同期・ゼロアロケーション**: 接続探索（Spatial Hashing + BFS）やRANSAC平面・円柱検出などの高負荷な編集処理は、非同期（`Task.Run`）で処理され、実行中は進捗バーとキャンセルボタンを表示するモーダルウィンドウで覆われます。GCアロケーションをほぼゼロに抑える最適化が入っています。

## 次回作業時に読むべきファイル
- `Assets/PointCloudWorkbench/Scripts/CloudCompareCameraController.cs`（カメラ制御の調整時）
- `Assets/PointCloudController.cs`（オブジェクト操作の調整時）
- `Assets/PointCloudWorkbench/Scripts/PointCloudEditor.cs`（編集・アノテーションロジックの追加時）
- `Assets/PointCloudWorkbench/Scripts/PointCloudEditorUI.cs`（UI調整時）
- `python_backend/run_noise_filter.py`（ノイズフィルタのバックエンド CLI 調整時）
- `python_backend/noise_filters.py`（ノイズフィルタのアルゴリズム調整・拡張時）
- `Assets/PointCloudWorkbench/Scripts/PythonBridge.cs`（Python連携・デシリアライズ調整時）
- `Assets/PointCloudWorkbench/Scripts/NoiseFilterManager.cs`（ノイズフラグ適用・履歴管理調整時）

## 未確認事項
- VRデバイス（Meta Quest等）を接続した実機環境での XRI Grab インタラクションの詳細な動作確認。
- 2000万点を超える極めて巨大な点群ファイルをロードした際のカリング・LODパフォーマンスの挙動。
- **Undo履歴のメモリ効率化（将来の課題）**: `NoiseFilterManager.cs` で全 label 配列を毎回丸ごとコピーしているため、今後は変更が入った index と旧値だけを持つ差分履歴にリファクタリングしてメモリ消費量を圧縮することが望ましい。
- **Downsampleモードプレビューの正式サポート（将来の課題）**: ダウンサンプル点群数と元点群数の不一致クラスタエラーを回避するため、UIから一時的に Downsample プレビューボタンを無効化。今後は、ダウンサンプリングされた M 点の結果を元の N 点に引き伸ばす（KDTree 1-NN 等でラベル伝播）ロジックを backend/frontend 双方で統合することが望ましい。


## 実装履歴・変更遍歴
- **点群レンダリングの修正**: Procedural Shaderにおける点サイズの最小値制限（2.0ピクセル以上）およびCull Off/ZWrite On/ZTest LEqualの設定を行い、点群が非表示になる問題を解決。
- **カメラ制御 (CloudCompareCameraController.cs)**: 点群の非同期ロード後に自動で中心位置へカメラをセンタリングするポーリング処理を実装。
- **2Dロール機能の実装**: `CloudCompareCameraController.cs`に、画面の端（トラックボール半径の外側）をドラッグすることでZ軸周りに2Dロール回転する機能を実装。操作ガイド用の外周リング(`ringViewport`)を追加。
- **カメラ回転の反転修正**: 左ドラッグでの回転方向がマウスの動きと逆になる不具合を修正（`Quaternion.FromToRotation(prev, curr)`に引数順を変更）。さらにUnity（左手系）とCC（右手系）のZ軸解釈の違いにより2Dロールのみが逆回転する問題を `rotCameraCS.z = -rotCameraCS.z;` で補正。
- **カメラ・エディタ機能の疎結合リファクタリング**: 
  - `CloudCompareCameraController.cs` から、数学計算（`TrackballMath.cs`）、ピボット表示（`PivotIndicator.cs`）、回転ガイド（`CameraRotationGuide.cs`）、点群ピッキング（`PointCloudPicker.cs`）を個別のクラスへ分離し、単一責任に整理。
  - `PointCloudEditor.cs` からGUI表示およびコントロール部分を `PointCloudEditorUI.cs` に分離し、アノテーションデータ処理ロジックとの疎結合化を実現。
- **点群読み込み上限の拡張**: `PointCloudLoader.cs` の点群読み込み上限のデフォルト値を2000万点（20,000,000）へ拡張。さらにUnityのシーン内に古い「200万点制限」の設定値がシリアライズ保存されている場合でも、`Awake` 時に自動的に検知して2000万点へ強制引き上げを行う自動拡張ロジックを実装。これにより、500万点規模のデータも切り捨てられずに全点ロードされるように修正。
- **CloudCompare互換の空間的LOD & カリングの移植**:
  - `PointCloudOctree.cs` を新規作成し、空間的八分木（Octree）を構築するアルゴリズムをC#で実装。点群のロード完了後に `Task.Run` を使ってバックグラウンドで構築を行い、メインスレッドをフリーズさせない仕組みを構築。
  - `PointCloudRenderer.cs` を修正し、毎フレームカメラの視錐台プレーンとオクトリー境界球の衝突判定（カリング）、およびカメラ距離に基づいた画角スケール判定（階層的LOD）を実行。抽出された点インデックスのみを `drawIndexBuffer` に格納し、GPUへ転送する仕組みを構築。
  - `PointCloudShader.shader` に `StructuredBuffer<int> _Indices` を追加し、頂点シェーダーで間接参照を解決するよう修正。これにより、既存のVR互換な6頂点ビルボード展開ロジックを維持しつつ、描画点数を劇的に削減。
  - `PointCloudEditorUI.cs` にLOD有効化トグル、閾値調整スライダー、オクトリー構築ステータス表示、およびLODによる描画点数/適用率のリアルタイム統計表示を追加。
- **アノテーション・選択操作のオクトリー空間高速化 (フェーズ3)**:
  - `PointCloudEditor.cs` にて、マウス位置の点検出（ピッキング `FindClosestPointOnRay`）、ブラシペイント（`ApplyBrushSelection`）、およびスクリーン矩形選択（`ApplyMarqueeSelection`）を線形スキャン（500万ループ）からオクトリーを介した空間探索（トラバース）にリファクタリング。
  - ピッキング時の Ray vs Sphere 衝突判定、およびブラシ時の Sphere vs Sphere 重複判定を実装し、判定負荷を 500万回 → 数千回以下へ劇的に削減。
  - これにより、500万点データでアノテーションブラシや選択操作を動かした際の深刻なCPUスパイク・フレームレート低下を完全に排除。
- **アノテーションUIの統合再設計と高度な選択機能の完全移植 (フェーズ3完了)**:
  - `PointCloudEditorUI.cs` の OnGUI インターフェースを、すべての選択ツールや新抽出アルゴリズムをスマートに集約できるよう全面的に再設計。
  - なげなわ選択、接続探索、RANSAC幾何検出、および属性カラーフィルタの各実行ボタンとパラメータスライダー（許容誤差、接続距離、属性しきい値等）を新設。
  - 要素数の増加に伴うUIの肥大化を防ぐため、アコーディオン式の折りたたみヘッダ（Foldout）および全体の垂直スクロールビューを実装し、操作性とレイアウトの高級感を向上。
  - なげなわツール動作中、クリックしたスクリーン座標の頂点群を滑らかに結ぶ2Dラインおよびマウス位置への予測線をOnGUI内でリアルタイムテクスチャ描画（`DrawLine`）するロジックを実装。
- **汎用的な非同期処理・キャンセルシステムとUI大型化の適用**:
  - `PointCloudProgressManager.cs` を新規作成。時間のかかる処理（接続探索、RANSAC、エクスポート等）の進捗（0%〜100%）、状況メッセージ、および `CancellationToken` を一元管理するシステムを構築。
  - `PointCloudEditor.cs` の `ApplyConnectionSelection`、`ApplyRansacSelection`、および `ExportLabeledPoints` を `Task.Run` による完全非同期処理へ移行。
  - 非同期実行中は、誤入力を防ぐためアノテーションのピッキングやブラシペイントなどのユーザー入力を完全にロック。
  - `PointCloudEditorUI.cs` にて、画面中央にモーダルな進捗ウィンドウ（OnGUI）をポップアップ描画。進捗率を視覚化するスライドバー、状況表示、および非同期処理を即時割り込み中断できる「キャンセル」ボタンを設置。
  - 画面の高解像度化に伴う文字の視認性低下を解消するため、操作パネルの幅を `450f` へ拡大し、各種フォントサイズ（ラベル・ボタン: 14pt、ヘッダ: 15pt〜22pt）を一括で大型化。
  - モーダル背景の暗転用とプログレスバー枠用にテクスチャキャッシュを実装し、毎フレームのテクスチャ新規作成に伴うGCスパイク（カクつき）を排除。
- **ゼロアロケーション連結リスト空間ハッシュによる超高速接続探索 (接続探索の最適化 & 95%ハング対策)**:
  - 接続探索において、従来の `Dictionary<Vector3Int, List<int>>` によるヒープ割当（数百万点に対する `new List` 生成によるGCスパイク）を完全に排除。
  - C++のポインタ構造に近い `int[] head` および `int[] next` を用いた「連結リスト型空間ハッシュ（Linked-List Spatial Hash）」と、`int[] queue` を用いた配列ベースのBFSを導入し、探索全体のヒープアロケーションを **完全にゼロ（0 Alloc）** に最適化。これにより、数百万点スケールでも数ミリ秒〜数十ミリ秒で探索が完了。
  - メインスレッドでのGPU書き出し処理 `targetRenderer.UpdatePointBuffer()` を `try-catch` で保護。例外発生時でも確実に `PointCloudProgressManager.Complete()` を実行させて進捗モーダルが95.0%のまま閉じなくなる不具合を解消。
  - RANSAC幾何形状検出（平面・鉛直円柱検出）において、250回のモデル評価イテレーションを `Parallel.For` で並列化。イテレーション内での `new List<int>` によるインライア点リストのヒープアロケーションを完全に排除し、単に個数をカウントする「アロケーションフリー評価」に変更。最良モデルパラメータのみをスレッドセーフに保存し、ループ終了後に並列処理で最終インライアを1回だけ全点適用する設計に変更。
  - データのメモリ読み込み帯域負荷を大幅に削減するため、`PointData[]` 構造体配列の代わりに `Vector3[]` 座標配列を直接参照するキャッシュフレンドリーな構造に最適化。
  - 進捗状況の更新 (`pm.Update`) に `System.Diagnostics.Stopwatch` による `50ms` の時間スロットリング制御を導入。文字列生成によるGCスパイクおよびスレッドプールの待機ブロックを排除。
  - これにより、GC停止やスレッド同期待ちによるCPUのアイドル状態を排除し、計算中はマルチコアCPUの使用率を極限まで上昇させ、処理速度を劇的に向上させた。
- **ピッキング精度の向上（最短距離ベースの探索）**:
  - `FindClosestPointOnRay` および `FindClosestPointIndexOnRay` にて、カメラからの視線深度（手前の優先）で点を評価するのではなく、**視線レイとの3D最短距離（垂直距離 `distSq`）が最小の点**を優先して選ぶアルゴリズムに刷新。これにより、カーソルで狙った対象を正確にピッキングできるよう大幅に改善。
- **選択ツール使用時のインテリジェントな中ボタン適用化と操作系統の完全統一**:
  - アノテーションツール（Brush, Marquee, Lasso, Connect等）がアクティブな時、左クリック・右クリックのカメラ基本操作を阻害しないよう、選択適用トリガーを **マウスホイール押し込み（中ボタン/Mo- **空中白モヤ除去フィルタ (White Haze Filter) の実装（デフォルトON）**:
  - **背景と実装の変遷**:
    1. **初期の課題**: `rei2.ply` に多数存在する「明るく、低彩度な白〜灰色系の霞（ノイズ）」が幾何フィルタ（SOR等）をすり抜けてしまうため、RGB色情報をベースとした除去フィルタを考案。
    2. **初期実装のズレ**: 当初は単に White Haze 検出マスクを `removeMask` に OR 結合するだけであったため、「白いだけの点」が幾何学的性質と無関係に強制削除候補となっていました。
    3. **解決方針（母集団からの除外）**: 「White Haze 候補（白い霞）は、後続の幾何ノイズ除去（SOR/ROR/CC/DBSCAN）の計算において、近傍探索や平面推定の計算結果を狂わせるため、**計算母集団から最初に取り除く**べきである」という仕様に修正。
    4. **プレビューと Commit の挙動一致**: 計算からは事前に除外するが、最終的には「ユーザーが Commit したときに画面上のノイズ候補（水色も含む）がすべて消去される」挙動にするため、Unity側の `CommitRemoval()` で `previewMask`（水色＋幾何ノイズの全候補）の全点を非表示化するように改修。
  - **処理フロー図 (backend - `noise_filters.py`)**:
    ```text
    [ 入力点群: N点 ]
           │
           ├─► compute_white_haze()
           │     └─► 判定: Brightness >= brightness_min (190.0) 
           │               && Saturation <= saturation_max (0.20)
           │     └─► `white_haze_candidate_mask` (水色プレビュー用 / 削除用)
           │
           ▼
    [ active_points の抽出 ]
           │   ※ `active_mask = ~white_haze_candidate_mask`
           ▼
    [ 幾何フィルタの計算 ] (active_points のみを対象に計算)
           ├─► cKDTree の構築 (active_points のみで作成するため、白モヤが近傍探索を狂わせない)
           ├─► SOR / ROR / Density
           ├─► CC (局所平面推定) ──► 白モヤのない綺麗な面で平面を推定
           └─► DBSCAN (クラスタリング)
           ▼
    [ 計算結果の復元 (マッピング) ]
           │   ※ 抽出した active_points で計算した各スコアを、元の N点 長の配列にマッピング復元
           │   ※ White Haze 候補点では sor_score=0, cc_score=0, cluster_id=-1, reason=0 となる
           ▼
    [ マスクとプレビューの合成 ]
           ├─► `final_remove_mask` = 幾何フィルタ（SOR | ROR | CC | DBSCAN | Density）の和
           └─► `preview_mask` = final_remove_mask | white_haze_candidate_mask (白モヤも表示)
    ```
  - **Python側 (`noise_filters.py`, `run_noise_filter.py`)**:
    - `brightness = (R+G+B)/3` と `saturation = (max(R,G,B)-min(R,G,B))/max(R,G,B)` を計算し、`brightness >= brightness_min` かつ `saturation <= saturation_max` の条件で点群を抽出する `compute_white_haze` 関数を実装。
    - パラメータ `brightness_min` (初期値: 190.0) と `saturation_max` (初期値: 0.20) をコマンドライン引数で制御可能にし、全幾何フィルタの適用前（最優先）で `active_mask` を作り母集団から除外。
    - `result_writer.py` に `white_haze_score.bin` (float32)、`preview_mask.bin` (uint8)、`preview_reason.bin` (int32) を追記出力。
  - **C# / Unity側**:
    - `NoiseFilterResult.cs` に `RemovalReason.WhiteHaze = 7` および `whiteHazeScore`, `previewMask`, `previewReason` などの配列を実装し、バイナリ読込みとC#側データモデルへの転送を実装。
    - `NoiseFilterUI.cs` にて、白モヤフィルタのトグル（デフォルトON）と輝度・彩度の2つのスライダーUIを追加。
    - ノイズプレビュー時の凡例 (OnGUI) の高さを 210f に広げ、**空中白モヤ（水色: `(0.0, 0.85, 1.0)`）** の項目を追加。
    - `PointCloudShader.shader` にて、理由コード `noiseReason == 7` のときにプレビュー色として水色を出力する分岐を追加。
    - `NoiseFilterManager.cs` の `CommitRemoval` で、`NOISE_CANDIDATE_BIT`（プレビュー候補）が立っているすべての点を `NOISE_HIDDEN_BIT`（確定非表示）にするように変更し、白モヤと幾何ノイズが同時に消去されるように実装。
��、スライダー位置に応じて段階的な丸めロジック（1万点未満は1,000刻み、10万点未満は5,000刻み、100万点未満は5万刻み、100万点以上は10万刻み）を導入。

## 設計上の決定事項
- ビルトインレンダリングパイプラインを使用。
- `Graphics.DrawProcedural`によるComputeBufferからの描画。
- UIは日本語表記を基本とする。
- Gitのコミットメッセージは原則として日本語で記述する。

## 技術的な調査・解析メモ
- **CloudCompareの2Dロール（画面垂直軸回転）の仕組み**:
  - マウス位置を仮想トラックボール（半球）上の3Dベクトルに変換する際、クリック位置が球の半径の外側（`d2 > 1`）にある場合は Z 成分を `0` にクランプし、XY平面上の単位円上のベクトルとして扱う。
  - ドラッグ開始点と現在の点で得られた2つのXY平面上のベクトルから回転行列を計算（`FromToRotation`）すると、外積の計算により回転軸が必ず Z 軸（画面に対して垂直かつ画面中心を通る軸）になる。これにより、特別な分岐処理なしに「球の外側のドラッグで2Dロール回転」を実現している。
  - **Unityでの座標系補正 (Z軸反転)**:
    - Unityは左手系、CloudCompare(OpenGL)は右手系を採用しているため、X/Y軸周りのトラックボール回転の方向を一致させた状態（`FromToRotation(prev, curr)`）で2Dロールを行うと、Z軸（ロール）周りの回転方向だけが逆になる。
    - これを解決するため、計算した回転 Quaternion の Z成分のみを反転（`rotCameraCS.z = -rotCameraCS.z;`）させて適用することで、直感的な回転方向と2Dロールの自然な回転を両立させている。

- **Codex CLI非インタラクティブ実行時のエラー原因と対策**:
  - **原因**: PowerShell経由（`Start-Process` や直接実行）でCodex CLIの検索タスクをバックグラウンド実行させた際、実行ポリシー（`ExecutionPolicy`）によるプロファイルスクリプトの読み込みブロックが発生。さらに非インタラクティブオプション（`-a never`）と標準入力（stdin）の待機が競合し、プロセスがハングアップ（入力待ちで停止）する現象が発生。
  - **対策**: PowerShell環境固有の制限を回避するため、実行シェルをコマンドプロンプト（`cmd.exe /c`）に切り替え。同時に、Codexがユーザー入力待ちで停止しないよう、標準入力を完全に遮断するリダイレクト（`< NUL`）を付与。これにより、エラーでの停止を防ぎつつ、Web検索とレポート出力（`docs/research/` への保存）を完全なバックグラウンドタスクとして安定稼働させる手法を確立。

- **接続探索のゼロアロケーション配列キャッシュ化 (遅延・GC対策)**:
  - `ApplyConnectionSelection` 実行時に、毎回巨大な配列（`connQueue`, `connCellBucketHead`, `connCellNext` 等）を `new` していた処理を廃止。クラスのメンバ変数としてキャッシュ化し、点群のロード・点数変更時のみ確保し直して `Array.Fill` / `Array.Clear` で再利用する設計に変更。これにより、数百万〜数千万点の点群スケールであっても探索開始時のメモリ確保の遅延（GCスパイク）を完全に排除。

- **CloudCompare風セル単位接続探索への変更 (3億回超の重複走査を解消する抜本的最適化)**:
  - 従来の点単位BFSでは、20万点到達時に距離判定（`sqrMagnitude`）が1億回〜3億回以上発生し、接続距離外の巨大な隣接クラスタ点群を繰り返し走査する問題がボトルネックになっていた。
  - `external_repos/CCCoreLib` の `AutoSegmentationTools::labelConnectedComponents` および `DgmOctree::extractCCs` を調査し、CloudCompareでは点ごとの厳密距離探索ではなく、占有セル同士の隣接関係（ラベリング）を使って接続成分を効率的に抽出していることを確認。
  - Unity側の `ApplyConnectionSelection` を、点間距離BFSからCloudCompare風のセル単位BFSへ刷新。
  - `connectionRadius` をセルサイズとし、点群をまずセルにまとめ（ハッシュマップ類似構造）、開始点が属するセルから26近傍の占有セルへ隣接探索を広げ、訪問したセル内の全点を選択対象にするアルゴリズムに変更。
  - これにより、大量の距離判定計算（`sqrMagnitude`）を完全に排除し、接続探索の計算量を点ペア候補数依存から「占有セル数と点数」に依存する形へ劇的に削減。10秒以上かかっていた処理が数ミリ秒〜数十ミリ秒で完了する圧倒的な高速化を実現した。
  - *注意点*: この方式はセル接続であり、厳密な点間距離接続より境界部分がやや広く選択される場合があるが、実用上はCloudCompareの接続成分とほぼ同一の挙動を示す。
- **NeRF点群のモヤ・浮遊点除去機能（Phase A：Pythonバックエンド）の実装**:
  - Python仮想環境（venv）と `requirements.txt` を用いて、Open3D, NumPy, SciPy などの開発環境を構築。
  - `pointcloud_io.py` にて Open3D を用いた PLY ファイルのロード・セーブ、および Python 内部用 NPZ データのロード・セーブを実装。
  - `noise_filters.py` にて SOR（統計的外れ点除去）、ROR（半径外れ点除去）、局所密度、および DBSCAN クラスタリングアルゴリズムを実装。
  - DBSCAN 処理において、30万点を超える場合は自動でボクセルダウンサンプリングを適用し、結果を KDTree 1-NN を用いて元の点群全体に伝播（ラベル伝播）させることで、出力ファイルの点数を元の点群数と一致させるマージロジックを実装。
  - Windows環境における子プロセス競合（ハングアップ）を避けるため、DBSCAN 処理を安定した同期処理（シングルプロセス）にリファクタリング。
  - `result_writer.py` にて、C# 連携用として明示的にリトルエンディアンで `.bin` バイナリを出力し、かつ `metadata.json` / `removal_report.json` を生成する機能を実装。
  - `run_noise_filter.py` にて、CLI 引数によるパラメータ指定および Full モード / Downsample Preview モード（`preview.ply` を出力）の実行エントリポイントを構築。
  - `tests/` 内に人工テストデータの自動生成（`generate_test_data.py`）と、各フィルタ仕様（浮遊点除去90%以上、線状構造のSoft/Strong、小クラスタとノイズの100%除去）を検証する自動テスト（`test_filters.py`）を実装し、すべてパスすることを確認。
  - `rei1.ply` (327万点) に対する Full モードでの実行が正常に行われ、1分強で約44万点のノイズ候補（SORおよび小クラスタ）を検出・出力できることを確認。
- **NeRF点群のモヤ・浮遊点除去機能（Phase B：Unityデータ層との連携）の実装**:
  - `NoiseFilterResult.cs` を新規作成。各種ノイズスコア、クラスタID、削除理由、および `RemovalReason` 列挙型を定義。
  - `PythonBridge.cs` を新規作成。`System.Diagnostics.Process` を用いて Python プロセスを非同期で安全に実行し、標準出力・標準エラーをリダイレクトして進捗監視を行うブリッジを構築。また、CancellationTokenによるプロセスの強制終了（キル）制御を実装。
  - `PythonBridge.cs` 内に、リトルエンディアン形式の `.bin` ファイルを `File.ReadAllBytes` と `Buffer.BlockCopy` によって高速に復元するゼロアロケーション指向のデシリアライズ処理を実装。
  - `NoiseFilterManager.cs` を新規作成。点群の `PointData.label` 内の上位ビット領域（bit18: 削除候補, bit19: 確定非表示）をビット演算で非破壊操作するプレビュー適用（`ApplyPreview`）および非表示確定（`CommitRemoval`）ロジックを実装。
  - `NoiseFilterManager.cs` 内に、メモリ消費を保護（最大5世代）しつつ `label` 配列のディープコピーを用いて完全な元戻しを可能にするスタックベースの `Undo` / `Redo` 履歴管理システムを実装。
- **NeRF点群のモヤ・浮遊点除去機能（Phase C：Unity表示層との連携）の実装**:
  - `PointCloudShader.shader` を改修。頂点シェーダー内で確定非表示ビット（`isNoiseHidden` = bit19）を判定し、該当点の頂点スケールを0にして画面外へカリング（描画スキップ）する最適化処理を追加。
  - `PointCloudShader.shader` 内で、プレビュー候補ビット（`isNoiseCandidate` = bit18）および理由コード（`noiseReason` = bit20-22）を判定し、SOR (赤)、ROR (橙)、低密度 (紫)、小クラスタ (黄)、その他 (ピンク) のノイズ理由色にリアルタイムで上書きするカラーオーバーレイ処理を実装。
  - 通常ラベルID（`classId`）が下位16ビットを使用しているため、データ競合を防ぐ目的でノイズ除去の理由コード（0〜5）を完全に空いている上位ビット（bit20〜22）にマッピング。
  - `NoiseFilterManager.cs` 内の `ApplyPreview` メソッドを改修し、プレビュー適用時に `NOISE_CANDIDATE_BIT` の付与と同時に `reason` コードをシフトして `label` に書き込むビット操作処理を実装。また、プレビュー解除およびリセット時には理由ビットも同時にクリアするクリーンアップ処理を追記。
- **リポジトリのクリーンアップ（不要・外部コードの履歴抹消）**:
  - `CloudCompare-master_sankou/` (外部コード) や `scratch/` (一時スクリプト)、ゴミファイル（`hub_help.txt`、`query`、`implementation_plan_original.md`）が過去のコミットによって誤ってリモートリポジトリに公開されていたため、過去のすべてのコミット履歴から完全に抹消する履歴書き換えを実行。
  - ローカルの実ファイルは削除せず保持したまま、`.gitignore` を更新してこれらのファイルを追跡対象外に登録。
  - `git filter-branch`、古い参照（`refs/original/`）の削除、および `git gc` を用いたガベージコレクションによってリポジトリデータベースを最適化し、新履歴を `main` へ強制プッシュ。
- **NeRF点群のモヤ・浮遊点除去機能（Phase D：UI統合 & Phase E：物理エクスポート）の実装**:
  - `NoiseFilterUI.cs` を新規作成し、各種フィルタ（SOR, ROR, DBSCAN）のパラメータスライダーや有効化トグル、および非同期実行・確定（Commit）・Undo/Redo・リセットボタンなどのGUI操作コントロールを統合。
  - 非同期解析実行時のスレッドセーフ対策（UnityException回避）として、メインスレッドへのシグナル完了検知フラグを用いた適用処理を実装。
  - `PointCloudEditorUI.cs` を改修し、アノテーションパネル内に折りたたみセクション「🧹 空中モヤ・浮遊点ノイズ除去」を追加して、描画を `NoiseFilterUI` に委譲。
  - `PointCloudEditor.cs` に `ExportCleanedPoints()` メソッドを追加し、確定非表示となったノイズ頂点を物理的に除外した新しいPLYファイルを ASCII 形式で非同期（バックグラウンドタスク）書き出しする機能を実装。同時に Python 側の解析サマリー（`removal_report.json`）をエクスポート先に自動コピーする連携機能を追記。
- **ノイズ除去連携の不整合修正・高速化最適化の完了**:
  - `run_noise_filter.py` に `--filters` オプションを追加し、UnityとPython間の起動引数の不一致（起動時クラッシュ）を修正。
  - `noise_filters.py` を改修し、SOR/ROR/密度のそれぞれで cKDTree を構築する処理を廃止し、最初に構築した1つの `cKDTree` インスタンスを各フィルタで再利用（使い回し）するように最適化。これにより、327万点規模の `rei1.ply` の Full 解析処理時間が 約75秒 から **55.16秒** へ劇的に短縮（約20〜30%の高速化）。
  - `noise_filters.py` の `run_all_filters()` において、UI側のチェックボックス（有効フラグ）と連動させ、無効なフィルタの重い計算をスキップしてダミーの初期化データを返すように実装。
  - 点数不一致による UnityException を防ぐため、`NoiseFilterUI.cs` 上の「Downsample (プレビュー)」ボタンを一時的に非活性化（無効化）し、安定動作する「Full」モードに統一。
  - DBSCAN タイムアウト機能の誤った表記と実際の実装差異を解消するため、不要なタイムアウト警告や監視変数をコードから整理。

- **Pythonプロセスの堅牢なエラーハンドリング・タイムアウト機構の実装 (フリーズ・空回り防止)**:
  - **背景**: Pythonプロセスが初期設定ミス（仮想環境の欠落、パッケージ未インストール、構文エラー等）で異常終了した場合や、内部ループでフリーズ（ハング）した場合に、C#側が非同期タスクの終了を検知できず、進捗モーダル（0.0%表示）が閉じなくなってUnity側が操作不能になる問題に対処。
  - **対策（タイムアウト制限）**: `PythonBridge.cs` の監視ループにおいて、開始時から **180秒（3分）** が経過してもプロセスが終了しない場合、強制的にプロセスを Kill (`process.Kill()`) し、`TimeoutException` をスローする安全機構を導入。
  - **対策（リアルタイムログ）**: Python起動引数に `-u` (unbuffered) を追加して標準出力のバッファリングを解除し、`process.OutputDataReceived` および `process.ErrorDataReceived` で受け取った内容を即座に Unity の `UnityEngine.Debug.Log` / `LogError` へ出力することで、動作ログやエラーTracebackが即座にコンソールで見えるように改善。
  - **対策（エラーモーダル画面の提示）**: 非同期処理で例外をキャッチした際、単に進捗表示を非表示にするのではなく、`PointCloudProgressManager` に `IsError` 状態とエラーメッセージ（`ErrorMessage`）を格納。`PointCloudEditorUI.cs` の OnGUI 描画処理にて、エラー状態を検知した場合は「❌ 解析エラー」の専用ダイアログに切り替え、エラーのスクロール表示および「閉じる」ボタンを表示。これにより、ユーザーが明示的にエラーを確認し、モーダルを閉じてUnityの操作に復帰できる安全なエラーハンドリングフローを確立。
- **OnGUIトグル（チェックボックス）のスタイル不具合修正**:
  - `PointCloudEditorUI.cs` および `NoiseFilterUI.cs` において、`GUILayout.Toggle` のスタイルに `textStyle`（ラベル用スタイル）を直接指定していたため、チェックボックスのマーク（ON/OFF状態を示すチェック画像）が表示されなくなっていた問題を修正。
  - `GUI.skin.toggle` を継承した専用の `toggleStyle` を定義・初期化し、`DrawNoiseFilterSection` を介して `NoiseFilterUI` 側の各トグルに適用することで、チェックマークが正常に表示されるように修正。これにより、各フィルタが有効（ON）か無効（OFF）かを視覚的に判別できるようになりました。

- **CloudCompare互換の局所平面推定ノイズ除去UIおよびC#ブリッジの改修**:
  - `noise_filters.py` における `compute_cc_noise` の改良（チャンク分割処理による省メモリ化、KNN/Radiusモード、しきい値判定モード、孤立点除去オプションの拡張）に合わせ、C#ブリッジおよびUnity UIを刷新。
  - **PythonBridge / CLI**: `PythonBridge.cs` および `run_noise_filter.py` に `--cc_use_knn`, `--cc_radius`, `--cc_remove_isolated`, `--cc_use_relative` のパラメータ受け渡し・パースを追加。
  - **Unity UI (`NoiseFilterUI.cs`)**:
    - 「平面推定ノイズ除去」セクションを最上部（主役）に配置変更し、植物点群での誤消去に対する警告ラベルを追加。
    - 近傍モード（KNN/Radius）としきい値モード（相対シグマ/絶対誤差）の切り替えを、相互排他的トグルボタンで実装。
    - 選択されたモードに連動し、対応するパラメータ設定スライダー（近傍数 `k` または 近傍半径 `radius`、および標準偏差倍率 `Sigma` または 絶対誤差 `Error`）のみを動的表示するように改修。
    - 「近傍不足の孤立点も除去する」オプションをUI上に追加。

- **ノイズ除去プレビュー・確定非表示の不具合調査と修正**:
  - GPUバッファ（ComputeBuffer）へのラベル書き込みとReadbackテストが成功していることをログから確認。
  - 頂点シェーダー `PointCloudShader.shader` に一時的に追加されていた強制非表示テスト用コード（`isNoiseHidden = true`）を削除し、本来のビット演算による非表示処理に戻しました。
  - プレイモードの再起動（停止→再再生）を促し、GPUへのシェーダー適用とマテリアル再生成を確実にするよう案内しました。

- **Pythonプロセスのタイムアウト判定ロジックの改善（無通信タイムアウト化）**:
  - 500万点規模の「平面推定ノイズ除去」実行時に、処理時間が180秒の固定制限を超えて強制終了してしまう不具合に対処。
  - プロセス起動時からの単純な時間経過ではなく、標準出力・標準エラーログが最後に出力されてからの時間（無通信時間・Idle Timeout）を監視するように [PythonBridge.cs](file:///E:/VR/PointCloudVR/Assets/PointCloudWorkbench/Scripts/PythonBridge.cs) のタイムアウト判定ロジックを変更。
  - これにより、ログが更新され処理が進んでいる限りは強制終了されず、ログが完全に途切れてフリーズしたときだけ3分（180秒）で安全に強制終了する堅牢な実装へと改善しました。

- **ノイズプレビュー凡例（レジェンド）の画面左下への表示実装**:
  - ノイズ除去のプレビュー表示時に、どの色がどのノイズフィルタに対応しているかをユーザーが識別しやすくするため、画面左下にカラー凡例ウィンドウ（インディゴ半透明背景）を OnGUI で描画するよう [NoiseFilterUI.cs](file:///E:/VR/PointCloudVR/Assets/PointCloudWorkbench/Scripts/NoiseFilterUI.cs) に実装。
  - 各項目（SOR、ROR、低密度、DBSCAN、CC平面推定）に対応するカラーボックス（テクスチャ着色）と、視認性の高い大きめのテキスト（フォントサイズ 14px 太字）をレイアウト。プレビュー中（`IsPreviewActive`）のみ動的に表示される設計としました。

- **空中白モヤ除去フィルタ (White Haze Filter) の実装（デフォルトON）**:
  - **背景**: `rei2.ply` に多数存在する「明るく、低彩度な白〜灰色系のノイズ点（霞）」に対応するため、既存の幾何学的フィルタ（SOR/ROR/CC/DBSCAN）に加え、色情報をベースとした除去フィルタを追加。
  - **Python側 (`noise_filters.py`, `run_noise_filter.py`)**: 
    - `brightness = (R+G+B)/3` と `saturation = (max(R,G,B)-min(R,G,B))/max(R,G,B)` を計算し、`brightness >= brightness_min` かつ `saturation <= saturation_max` の条件で点群を抽出する `compute_white_haze` 関数を実装。
    - パラメータ `brightness_min` (初期値: 190) と `saturation_max` (初期値: 0.20) をコマンドライン引数で制御可能にし、全フィルタの適用前（最優先）でフィルタ処理するように実装。除去理由 (reason) コードに `7` を割り当て。
    - `result_writer.py` に `white_haze_score.bin` (float32) を追記出力。
  - **C# / Unity側**:
    - `NoiseFilterResult.cs` に `RemovalReason.WhiteHaze = 7` および `whiteHazeScore` を追加し、バイナリ読込みとC#側データモデルへの転送を実装。
    - `NoiseFilterUI.cs` にて、白モヤフィルタのトグル（デフォルトON）と輝度・彩度の2つのスライダーUIを追加。
    - ノイズプレビュー時の凡例 (OnGUI) の高さを 210f に広げ、**空中白モヤ（水色: `(0.0, 0.85, 1.0)`）** の項目を追加。
    - `PointCloudShader.shader` にて、理由コード `noiseReason == 7` のときにプレビュー色として水色を出力する分岐を追加。

- **進捗バーの段階的かつ滑らかな更新の実装（重み付けとチャンク処理）**:
  - **背景**: フィルタパイプライン実行中、進捗バーが「0% -> 40% -> 80%」のように一瞬で大雑把に飛び、処理時間の長いステップ（SORやCC平面推定）で画面が停止したように見える現象を改善。
  - **対策（Python側 チャンク化）**: `noise_filters.py` の `compute_sor`, `compute_ror`, `compute_density` において、cKDTreeへのクエリを一括で投げるのではなく、50,000〜100,000点ごとのチャンクに分割してループ処理するように改修。各フィルタに関数コールバック `progress_callback` を追加し、チャンク完了ごとに進捗率を報告可能とした。`compute_cc_noise` にも同様のコールバックを組み込み。
  - **対策（パイプライン全体の重み計算）**: `filter_pipeline.py` において、各フィルタの処理負荷（所要時間）の目安となる重み係数（SOR: 30, CC: 32, ROR: 15, Density: 15, DBSCAN: 3, WhiteHaze: 1）を定義。実行されるフィルタ構成から全体の総重みを算出し、ステップごとの開始%と終了%を動的に割り振ることで、全体の進捗が 0%〜100% の範囲で一貫して定速に近い形で進むように設計。進捗の報告には `[Progress] {全体での％} {メッセージ}` の形で標準出力へフラッシュ出力するように実装。また、DBSCAN などの非チャンク処理でも開始時(0.0)・終了時(1.0)のコールバックを強制的に呼ぶことで、進捗がフリーズする隙間を排除。
  - **対策（C#側 パースとマッピング）**: `PythonBridge.cs` の `OutputDataReceived` イベントハンドラにおいて、標準出力に `[Progress] ` プレフィックスが含まれる場合、文字列から進捗パーセントを抽出するパース処理を追加。パースした 0〜100% の値を、C#のノイズ処理全体の進捗である `0.2f` 〜 `0.8f` (20%〜80%) に線形マッピングして `PointCloudProgressManager.Instance.Update` へ細かく通知するよう実装。
  - **対策（コマンドライン不具合修正）**: `run_noise_filter.py` を引数指定で直接CLIから動かした際に、`wh_brightness` などの引数定義が漏れていてクラッシュする不具合（AttributeError）があったため、Argumentを追加して修正。

- **重複ステップによる進捗逆戻りバグの修正および大量ログ出力によるデッドロック（フリーズ）防止**:
  - **背景**: 同一種類のフィルタ（例：平面推定CC）を複数配置した際、進捗率が「20% -> 60%」へ突然スキップした後に「40%」に戻る不具合が発生。また、数百万点以上の大容量点群の処理時に、進捗ログが短時間・大量に生成されることでOSの標準出力バッファが満杯になり、Pythonプロセスが書き込み待機で完全にハングアップ（フリーズ）するデッドロック現象が発生した。
  - **対策（重複ステップの進捗バグ修正）**: `filter_pipeline.py` において、ループの実行順序に合わせて進捗範囲 (`start_pct` から `end_pct`) を動的に計算し、コールバック関数にクロージャとして保持させる設計に変更。これにより、重複したフィルタがあっても巻き戻ることなく、実行順に滑らかに進むように修正。
  - **対策（ログ間引きによるデッドロック防止）**: `filter_pipeline.py` 内に `ProgressThrottler` クラスを導入し、進捗率が前回の出力から「0.5%以上変化したとき」または「開始(0.0)・終了(1.0)」のときのみに限定して `print(..., flush=True)` を実行するように制限。これにより、数百万点の処理時に数万回のログが出力されてパイプバッファが詰まる問題を完全に排除し、処理全体の高速化と安定性を確保した。

- **ブロック非選択時のパラメータ設定UI非表示化（レイアウトの最適化）**:
  - **背景**: 順次実行レーン内のどのブロックも選択されていない場合でも、下段のパラメータ設定UI用エリアが常に表示された状態になっており、画面領域を無駄に占有していた。
  - **対策（動的レイアウト制御）**: `FilterPipelineEditorUI.cs` の `OnGUI` 処理を改修。ブロックが選択されているか (`selectedBlockIndex >= 0`) を判定し、非選択時はバー全体の高さを `TOP_H` (160f) のみに縮小してパラメータ用の下段エリアを完全に隠すようにした。ブロック選択時にのみバー全体の高さを `TOP_H + PARAM_H` (300f) に拡張し、区切り線と `DrawParamPanel` を描画してパラメータUIを動的に表示する仕様とした。

- **フィルタ処理のマルチコア並列化・アルゴリズム高速化・メモリ書き込みの最適化**:
  - **背景**: 数百万点規模の点群に対するノイズ除去において、Python側のI/O処理や、シングルスレッドによる近傍探索、および一時配列の大量メモリ確保が処理全体のボトルネックとなっていた。
  - **対策（マルチコア並列化）**: SciPy `cKDTree` のクエリ時に `workers` 数を自動検知（環境変数 `POINTCLOUD_FILTER_WORKERS` にて CPU コア数 - 1 に既定）して並列クエリを実行するラッパー (`_tree_query`, `_tree_query_ball_point`) を導入。近傍探索の処理速度をマルチスレッド化で大幅に短縮。
  - **対策（アルゴリズム最適化）**: ROR フィルタにおいて、球探索 (`query_ball_point`) を行う代わりに、指定した最小近傍数+1番目の隣接点までの距離を query して判定する高速オプション (`exact_count=False`) を実装。処理負荷を劇的に低減。
  - **対策（メモリマージのゼロコピー化）**: `filter_pipeline.py` において、各フィルタが個別の中間配列を確保するのを廃止し、開始時に全サイズ配列を一度だけ初期化してループ内で直接インデックス指定マージを行う構造に刷新。GC負荷とメモリオーバーヘッドをほぼゼロに削減。
  - **対策（Density フィルタのパーセンタイル化）**: 密度の絶対閾値スライダーを、全体分布に対する「下位何%（percentile）」を指定してカットする方式（デフォルト3%）に変更。
  - **対策（堅牢なバイナリロード）**: パイプライン内で無効化されたフィルタのバイナリ書き出しをスキップし、C#側でファイルがない場合に自動で初期化配列を作成する Optional 読み込み関数を実装。I/O時間と不要なファイル生成を削減。
  - **対策（レーンUIへの機能統合とレイアウト刷新）**: 順次実行レーンUIの下部に「標準構成」「Undo / Redo」「適用・確定」ボタンを直接配置し、操作の流れを直感的に。また、ブロック非選択時は下段のパラメータエリアを隠す動的レイアウトを導入して画面占有率を改善。

- **プリセット管理機能とUX改善 (GUI.Window化)**:
  - `NoiseFilterPresetManager.cs` を実装し、ノイズ除去パイプラインのブロック構成と各パラメータをJSON形式で `Application.persistentDataPath` に保存・読み込み・削除・リネームできるプリセット機能を導入。
  - プリセットのUIにおいて、単なる `GUI.Box` ではなく独立した `GUI.Window` と `GUI.BringWindowToFront` を用いる設計に刷新。
  - これにより、ポップアップ背景クリック時に背後の3Dビューの視点操作が暴発する「クリック貫通」を完全に防止。
  - また、プリセット名のテキスト入力時に `Delete` キー等を押した際、Unityエディタのショートカットと競合する問題を、`Event.current.Use()` と適切なフォーカス制御によって解決。文字入力が阻害される不具合を解消しました。
