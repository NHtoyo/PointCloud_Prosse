# HANDOVER_DOC: PointCloudVR

## プロジェクト名
PointCloudVR (植物点群処理ワークベンチ)

## 目的
Unity上において数千万点規模の大規模な点群データ（PLY形式等）を高速に描画し、CloudCompareに近い快適な3D空間ナビゲーション（オービット回転、2Dロール、パン、ズーム）と、点群の部分選択・アノテーション（分類ラベリング、削除、幾何形状検出、空間接続探索）を行うための研究用3Dワークベンチの構築。

## 主要フォルダ
- `Assets/` : Unity プロジェクトのアセットルート
- `Assets/PointCloudWorkbench/` : 本ワークベンチの主要機能（カメラ、エディタ、UIなど）のスクリプトとアセット
- `Assets/PointCloudWorkbench/Scripts/` : 主要スクリプト群
- `Assets/PointCloudWorkbench/Shaders/` : 主要シェーダー群

## 主要ファイル
| ファイル名 | 相対パス | 役割・機能概要 |
| :--- | :--- | :--- |
| **CloudCompareCameraController.cs** | `Assets/PointCloudWorkbench/Scripts/CloudCompareCameraController.cs` | CloudCompare互換のカメラ移動・オービット回転制御システム。入力監視と姿勢更新を担当。常に「左ドラッグ＝回転、右ドラッグ＝パン」で固定。 |
| **TrackballMath.cs** | `Assets/PointCloudWorkbench/Scripts/TrackballMath.cs` | マウススクリーン座標を仮想トラックボール上の3Dベクトルへ変換する数学計算ヘルパークラス。 |
| **PivotIndicator.cs** | `Assets/PointCloudWorkbench/Scripts/PivotIndicator.cs` | 回転のピボットポイントに表示する3軸のクロスヘアインジケータ。 |
| **CameraRotationGuide.cs** | `Assets/PointCloudWorkbench/Scripts/CameraRotationGuide.cs` | 回転中にピボットに表示される3軸ガイドおよびビューポート外周リングの描画。 |
| **PointCloudPicker.cs** | `Assets/PointCloudPicker.cs` | カメラからの視線レイと点群の最短垂直距離から最も近い点を検出してピボット座標を設定する。 |
| **PointCloudOctree.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudOctree.cs` | 点群の空間分割オクトリー（八分木）データ構造。階層的LOD用の代表点選定や空間検索を担当。 |
| **PointCloudRenderer.cs** | `Assets/PointCloudRenderer.cs` | GPU（ComputeBuffer and Procedural Shader）を利用して高精度の点群データを描画する。 |
| **PointCloudLoader.cs** | `Assets/PointCloudLoader.cs` | PLYファイルなどの点群データを非同期でロードしRendererにセットアップする。読み込み上限は2000万点。 |
| **PointCloudEditor.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudEditor.cs` | 点群のアノテーション・編集用コアエンジン。ブラシ、矩形、なげなわ、接続探索、RANSAC等の選択や、ノイズ物理除去PLYエクスポートを担当。計測値の3D描画・サイズ制御も管理。 |
| **PointCloudEditorUI.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudEditorUI.cs` | アノテーションツールのUI表示（OnGUI）、進捗モーダル表示、およびスケール校正・ダウンサンプリング設定用のポップアップダイアログ（GUI.Window）の描画を担当。 |
| **DistanceMeasurementUI.cs** | `Assets/PointCloudWorkbench/Scripts/DistanceMeasurementUI.cs` | アノテーションUIの下に連結される、距離計測（2点・折れ線・曲線の切り替え、物理単位併記の距離表示）専用のスタックUI。 |
| **MeasurementPath.cs** | `Assets/PointCloudWorkbench/Scripts/MeasurementPath.cs` | 多点にまたがる距離計測のパス（2点、折れ線、Catmull-Rom曲線）の保持と長さ計算を行う。 |
| **PointCloudScaleService.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudScaleService.cs` | 実寸法スケール(mm/unit)のJSONからの読み込み、および点群オブジェクトへのスケール適用を担当するサービスクラス。 |
| **PointCloudDownsampleService.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudDownsampleService.cs` | ダウンサンプリング実行時の各種入出力ファイルパス解決を担当するサービスクラス。 |
| **OverlayColorShader.shader** | `Assets/PointCloudWorkbench/Shaders/OverlayColorShader.shader` | 計測線やマーカー球を点群の点に隠させず、常に最前面に描画するためのカスタムZTest Alwaysアンリットシェーダー。 |
| **PointCloudController.cs** | `Assets/PointCloudController.cs` | VR環境（XRI Grab）およびPCデバッグ用に、点群オブジェクト全体の移動・回転・スケーリングといったトランスフォーム制御を担う。常に「左ドラッグ＝回転、右ドラッグ＝パン」で固定。 |
| **PointCloudManager.cs** | `Assets/PointCloudManager.cs` | 基準（Reference）と対象（Aligned）点群の管理、簡易距離比較、表示モード切り替え、および右側「CC Unity機能パネル」UI全体の描画を担当。 |
| **PointCloudData.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudData.cs` | 点群に含まれる各点の構造体 `PointData` などの共通データ定義。 |
| **run_noise_filter.py** | `python_backend/run_noise_filter.py` | CLIエントリポイント。パラメータを受け取り、バッチ処理全体の制御と結果出力を担当。 |
| **noise_filters.py** | `python_backend/noise_filters.py` | SOR / ROR / 密度 / DBSCAN フィルタの実装。DBSCAN 自動ダウンサンプリングと同期フォールバックを制御。 |
| **1_scale_calibration.py** | `python_backend/1_scale_calibration.py` | UIで入力された実寸および計測値から1 unitあたりの実寸法（mm/unit）スケールを算出して報告JSONを出力するキャリブレーションスクリプト。 |
| **2_downsample.py** | `python_backend/2_downsample.py` | 算出されたスケールを用いて、ボクセルダウンサンプリング（全体結合、部位別、個別ファイル別）を実行し、比較用レポートを生成するスクリプト。 |
| **pointcloud_io.py** | `python_backend/pointcloud_io.py` | Open3D を用いた PLY ファイル of ロード・セーブ、および Python 内部用 NPZ データのロード・セーブ。 |
| **result_writer.py** | `python_backend/result_writer.py` | 処理結果をリトルエンディアンの `.bin` ファイル、`metadata.json`、`removal_report.json` に出力。 |
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
   - 距離計測モード（ON時）は、カメラ移動やオブジェクト操作と干渉しないよう、**「マウスホイール押し込み（中ボタン）」** をトリガーとして計測点を指定します（2点/折れ線/曲線に対応）。中ボタンを押した瞬間のみ最近傍点探索を実行する最適化により、計測中のカメラ操作も軽量に動作します。

## 現在分かっている注意点
- **操作系統の一貫性**: 通常時・アノテーションツール選択時・計測モード時を問わず、ナビゲーション操作系統は常に「左ドラッグ＝カメラ回転、右ドラッグ＝パン、スクロール＝ズーム」で完全に固定されています。アノテーション適用（ブラシで塗る、なげなわ等）および距離計測の点指定はすべて **「中クリック（ホイール押し込み）」** で行います。オブジェクト移動に修飾キー（Ctrl等）は不要です。
- **2Dロール回転と右手系補正**: 仮想トラックボールの球外ドラッグでZ軸周りの2Dロールが実行されます。Unity（左手系）とCC（右手系）のZ軸解釈の違いを吸収するため、`rotCameraCS.z = -rotCameraCS.z;` で補正しています。
- **高負荷タスクの非同期・ゼロアロケーション**: 接続探索（Spatial Hashing + BFS）やRANSAC平面・円柱検出などの高負荷な編集処理は、非同期（`Task.Run`）で処理され、実行中は進捗バーとキャンセルボタンを表示するモーダルウィンドウで覆われます。GCアロケーションをほぼゼロに抑える最適化が入っています。
- **Gitコミットの記述規則（厳守）**: コミットメッセージは原則としてすべて日本語で記述し、かつ機能変更そのもののみを簡潔に書くこと。Gitのログ履歴を綺麗に保つため、コミットメッセージ内に「HANDOVER_DOCの更新」や「ドキュメントの追記」といった開発プロセス内・メモ書きレベルの記述は絶対に含めないこと。（ドキュメントの更新はコードの修正コミットと同時に行い、メッセージには一切言及しないこと）

## 次回作業時に読むべきファイル
- `Assets/PointCloudWorkbench/Scripts/CloudCompareCameraController.cs`（カメラ制御の調整時）
- `Assets/PointCloudController.cs`（オブジェクト操作の調整時）
- `Assets/PointCloudWorkbench/Scripts/PointCloudEditor.cs`（編集・アノテーションロジックの追加時）
- `Assets/PointCloudWorkbench/Scripts/PointCloudEditorUI.cs`（UI調整・ポップアップウィンドウ調整時）
- `Assets/PointCloudWorkbench/Scripts/DistanceMeasurementUI.cs`（計測UI調整時）
- `Assets/PointCloudWorkbench/Shaders/OverlayColorShader.shader`（最前面描画シェーダー調整時）

## 未確認事項
- VRデバイス（Meta Quest等）を接続した実機環境での XRI Grab インタラクションの詳細な動作確認。
- 2000万点を超える極めて巨大な点群ファイルをロードした際のカリング・LODパフォーマンスの挙動。
- **Undo履歴のメモリ効率化（将来の課題）**: `NoiseFilterManager.cs` で全 label 配列を毎回丸ごとコピーしているため、今後は変更が入った index と旧値だけを持つ差分履歴にリファクタリングしてメモリ消費量を圧縮することが望ましい。
- **Downsampleモードプレビューの正式サポート（将来の課題）**: ダウンサンプル点群数と元点群数の不一致クラスタエラーを回避するため、UIから一時的に Downsample プレビューボタンを無効化。今後は、ダウンサンプリングされた M 点の結果を元の N 点に引き伸ばす（KDTree 1-NN 等でラベル伝播）ロジックを backend/frontend 双方で統合することが望ましい。

## 設計上の決定事項
- ビルトインレンダリングパイプラインを使用。
- `Graphics.DrawProcedural`によるComputeBufferからの描画。
- UIは日本語表記を基本とする。
- Gitのコミットメッセージは原則として日本語で記述する。

## 実装履歴・変更遍歴（カテゴリ別要約）

### 1. 距離計測機能の多点・曲線対応およびコードの責務分離 (2026-06-11)【最新】
- **距離計測機能の拡張 (折れ線・曲線)**:
  - `MeasurementPath.cs` を新規追加し、従来の2点間計測だけでなく、任意の複数点を用いた「折れ線 (Polyline)」や「曲線 (Catmull-Rom スプライン)」での長さ計測に対応。
  - `PointCloudEditor.cs` を改修し、計測マーカーおよび線の動的生成・更新を `MeasurementPath` と連動するよう拡張。
  - `DistanceMeasurementUI.cs` に計測モード（2点 / 折れ線 / 曲線）の切り替えボタン、および「直近点を削除」ボタンを追加。
- **サービスクラスへの責務分離 (リファクタリング)**:
  - `PointCloudScaleService.cs` を新規追加し、`PointCloudManager.cs` にハードコードされていたスケールレポート（JSON）の読み込みと適用ロジックを分離。
  - `PointCloudDownsampleService.cs` を新規追加し、`PointCloudEditorUI.cs` にあったダウンサンプリング時のパス生成処理を分離。これらにより既存のUIおよびマネージャクラスの責務を整理・軽量化。

### 2. 2点間距離計測の3D描画バグ修正とUIの完全分離・入力競合解消（2026-06-10）
- **イベント横取りバグの解消によるボタンクリックの正常化**:
  - `AnnotationPipelineEditorUI.cs` および `FilterPipelineEditorUI.cs` のドラッグ＆ドロップ（D&D）処理における `MouseUp` イベントの不適切な消費（ドラッグ状態でなくても画面全体の MouseUp イベントを `Event.current.Use()` で消去していた）を修正。これにより、二点間距離計測UIの「計測ツールをON」ボタン等が正常にクリックに反応するよう挙動を修正。
- **最前面描画（ZTest Always）によるCC互換表示の実現**:
  - `OverlayColorShader.shader`（深度テストを無視して常に最前面に描画するアンリットカラーシェーダー）を新規作成。
  - 計測用3Dマーカー（赤・青）および `LineRenderer`（黄）に本シェーダーを適用し、密集した点群の奥や内側に隠されることなく常に最前面にくっきりと描画されるように修正。
- **マーカー・計測線の縮小とCC寄りへの簡素化**:
  - 選択中に出る大きな球体や太い線をやめ、点マーカーのサイズを従来の約 1/10 の `0.00025f / Mathf.Max(scaleX, 0.0001f)` （0.25mm相当）、計測線の太さを `0.00008f` （0.08mm相当）に縮小し、CloudCompareに近いスッキリした表示に改善。
- **中クリックによる計測点指定と視点移動ラグの解消（パフォーマンス最適化）**:
  - 左クリックは視点移動や通常操作に使うため、計測点の指定には使わないようにし、計測モード中は**中クリック（マウスホイール押し込み）**で1点目・2点目を選択する方式に変更。
  - 計測モード中に視点が重くなる問題を解決するため、毎フレーム点群へのレイ探索を行うのではなく、**中クリックした瞬間のみ最近傍点探索を行う**ように変更し、カメラ操作の快適性を維持。
- **スケール校正UIのポップアップ（ダイアログ）化とUI完全分離**:
  - 実寸法（mm）の入力やPython校正スクリプトを実行する「スケール校正UI」は、左側パネルから完全に削除し、以前と同様の**画面中央にポップアップ表示されるダイアログウィンドウ**（ダウンサンプリング設定と同様の構成）に差し戻し。右パネルの「スケール校正を実行」ボタンから呼び出す。
  - アノテーションUI・モヤ処理UIの下に並ぶ縦並びのパネルは「二点間距離計測ツール」専用の `DistanceMeasurementUI.cs` に整理。ここでは「計測ON/OFF」「計測距離（unit単位に加え、メートル/ミリメートルも併記）」「リセット」のみを取り扱うように論理的・物理的に完全分離。重複する無駄な起動ボタンも完全に削除。

### 2. 実寸法（メートル/センチメートル）の自動同期とダウンサンプル自動化 (2026-06-10)
- **Scale calibration とダウンサンプリング連携**:
  - `PointCloudManager.cs` および `PointCloudLoader.cs` において、スケール校正の実行完了時およびロード完了時に、校正レポートから `scale_mm_per_unit` を自動ロードし、点群の Transform スケールにメートル換算で反映。
  - C#側のRANSAC検出の許容誤差（cm）や属性フィルタの高度（Y）閾値（m）の計算において、TransformのlossyScaleを適用し、実寸法（cm, m）で正確に幾何探索・フィルタリングが動作するように改修。
- **ダウンサンプリングの完全自動・ワンクリック連携と自動再ロード**:
  - `PointCloudEditorUI.cs` の `ExecuteDownsampling()` を改修し、ダウンサンプリング実行時にメモリ上の最新アノテーションデータをバイナリ一時ファイル（`_labeled.ply`）に自動保存後、Pythonスクリプトを実行。
  - 完了後、生成された全体ファイル（`[ファイル名]_labeled_downsampled.ply`）を自動検知して再ロードし、表示点群を差し替えてカメラを再センタリングする機能を統合。

### 3. アノテーションパレットUIの高度化とマルチレイヤー対応 (2026-06-03)
- **ドラッグ＆ドロップによる並び替えとID動的再割り当て**:
  - `AnnotationPipelineEditorUI.cs` にて、クラスブロックをD&Dで並び替え、左からID（1以上）を自動再割り当てし、アノテーションされている全レイヤーの点群データの旧IDを新IDへ動的に置換するマッピング処理（`RemapClassIds`）を実装。
- **マルチレイヤーアノテーションデータ構造**:
  - `Dictionary<string, byte[]>` を用いてアノテーションデータをレイヤー別に分離保持し、切り替え・追加・削除を行えるレイヤー管理セクションを追加。

### 4. 空中モヤ・浮遊点ノイズ除去パイプライン (2026-05〜2026-06)
- **ハイパフォーマンス・ノイズフィルタの実装**:
  - Python側に SOR (統計的外れ点), ROR (半径外れ点), 局所密度, DBSCAN, 局所平面推定 (CC) の各幾何フィルタを実装。
  - **空中白モヤ除去フィルタ (White Haze Filter)**: RGB色情報を基に明るく低彩度な白い霞を検出する色フィルタを実装し、SOR等の幾何精度向上に寄与。
- **C#連携とリアルタイムプレビュー**:
  - 解析結果（binバイナリ）を高速デシリアライズしてロード。リアルタイムプレビュー描画および物理的にノイズを除去したPLYの非同期エクスポートを実装。

### 5. カメラ制御・LOD・操作系統のCloudCompare互換最適化 (2026-05)
- **カメラ制御 (CloudCompareCameraController.cs)**:
  - 常に「左ドラッグ＝カメラ回転、右ドラッグ＝パン、スクロール＝ズーム」で固定。仮想トラックボールの球外ドラッグで画面 Z 軸周りの 2D ロール回転を実現。
- **LOD & カリング**:
  - 空間分割八分木（オクトリー）の構築による階層的LOD描画と視錐台カリングにより描画点数を劇的に削減。
