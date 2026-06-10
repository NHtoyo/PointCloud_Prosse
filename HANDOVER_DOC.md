# HANDOVER_DOC: PointCloudVR

## プロジェクト名
PointCloudVR (植物点群処理ワークベンチ)

## 目的
Unity上において数千万点規模の大規模な点群データ（PLY形式等）を高速に描画し、CloudCompareに近い快適な3D空間ナビゲーション（オービット回転、2Dロール、パン、ズーム）と、点群の部分選択・アノテーション（分類ラベリング、削除、幾何形状検出、空間接続探索）を行うための研究用3Dワークベンチの構築。

## 主要フォルダ
- `Assets/` : Unity プロジェクトのアセットルート
- `Assets/PointCloudWorkbench/` : 本ワークベンチ of 主要機能（カメラ、エディタ、UIなど）のスクリプトとアセット
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
| **PointCloudData.cs** | `Assets/PointCloudWorkbench/Scripts/PointCloudData.cs` | 点群に含まれる各点の構造体 `PointData` などの共通データ定義。 |
| **run_noise_filter.py** | `python_backend/run_noise_filter.py` | CLIエントリポイント。パラメータを受け取り、バッチ処理全体の制御と結果出力を担当。 |
| **noise_filters.py** | `python_backend/noise_filters.py` | SOR / ROR / 密度 / DBSCAN フィルタの実装。DBSCAN 自動ダウンサンプリングと同期フォールバックを制御。 |
| **1_scale_calibration.py** | `python_backend/1_scale_calibration.py` | UIで入力された実寸および計測値から1 unitあたりの実寸法（mm/unit）スケールを算出して報告JSONを出力するキャリブレーションスクリプト。 |
| **2_downsample.py** | `python_backend/2_downsample.py` | 算出されたスケールを用いて、ボクセルダウンサンプリング（全体結合、部位別、個別ファイル別）を実行し、比較用レポートを生成するスクリプト。 |
| **pointcloud_io.py** | `python_backend/pointcloud_io.py` | Open3D を用いた PLY ファイルのロード・セーブ、および Python 内部用 NPZ データのロード・セーブ。 |
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

## 現在分かっている注意点
- **操作系統の一貫性**: 通常時・アノテーションツール選択時を問わず、操作系統は常に「左ドラッグ＝回転、右ドラッグ＝パン」で完全に固定されています。アノテーション適用（ブラシで塗る、なげなわ等）は「中クリック」で実行するため、カメラ操作と重複しません。オブジェクト移動に修飾キー（Ctrl等）は不要です。
- **2Dロール回転と右手系補正**: 仮想トラックボールの球外ドラッグでZ軸周りの2Dロールが実行されます。Unity（左手系）とCC（右手系）のZ軸解釈の違いを吸収するため、`rotCameraCS.z = -rotCameraCS.z;` で補正しています。
- **高負荷タスクの非同期・ゼロアロケーション**: 接続探索（Spatial Hashing + BFS）やRANSAC平面・円柱検出などの高負荷な編集処理は、非同期（`Task.Run`）で処理され、実行中は進捗バーとキャンセルボタンを表示するモーダルウィンドウで覆われます。GCアロケーションをほぼゼロに抑える最適化が入っています。
- **Gitコミットの記述規則（厳守）**: コミットメッセージは原則としてすべて日本語で記述し、かつ機能変更そのもののみを簡潔に書くこと。Gitのログ履歴を綺麗に保つため、コミットメッセージ内に「HANDOVER_DOCの更新」や「ドキュメントの追記」といった開発プロセス内・メモ書きレベルの記述は絶対に含めないこと。（ドキュメントの更新はコードの修正コミットと同時に行い、メッセージには一切言及しないこと）

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

## 設計上の決定事項
- ビルトインレンダリングパイプラインを使用。
- `Graphics.DrawProcedural`によるComputeBufferからの描画。
- UIは日本語表記を基本とする。
- Gitのコミットメッセージは原則として日本語で記述する。

## 実装履歴・変更遍歴（カテゴリ別要約）

本プロジェクトの開発において実装された機能と最適化の歴史的遍歴を、主要カテゴリ別に集約・要約して記録しています。

### 1. 実寸法（メートル/センチメートル）の自動同期とダウンサンプル自動化 (2026-06-10) 【最新】
- **Unity内の完全な実寸法同期**:
  - `PointCloudManager.cs` および `PointCloudLoader.cs` において、スケール校正の実行完了時およびプロジェクト起動時のロード完了時に、校正レポート（`scale_calibration_report.json`）から `scale_mm_per_unit` を自動ロードする仕組みを構築。
  - メートル換算されたスケールを点群オブジェクトの `transform.localScale` に適用することで、Unity空間内の点群オブジェクトを現実のメートルスケールに補正。これに伴い、`PointCloudController.cs` の初期スケールキャッシュおよび位置リセット処理とも整合性を調整。
  - C#側のRANSAC検出の許容誤差（cm）や属性フィルタの高度（Y）閾値（m）の計算において、TransformのlossyScaleを適用し、実寸法（cm, m）で正確に幾何探索・フィルタリングが動作するように改修。
- **ダウンサンプリングの完全自動・ワンクリック連携と自動再ロード**:
  - `PointCloudEditorUI.cs` の `ExecuteDownsampling()` を改修し、現在ロードされている点群ファイルの親ディレクトリから、一時ファイルパス（`_labeled.ply`）および出力フォルダ（`downsample/`）を自動決定。
  - 実行時に、メモリ上の最新のアノテーション（ラベリング）データを高速バイナリ形式で一時ファイルへ自動保存し、その保存完了を待って Python ダウンサンプリングスクリプト（`2_downsample.py`）を実行する非同期連携フローを構築。
  - ダウンサンプリング完了後、生成された全体ファイル（`[ファイル名]_labeled_downsampled.ply`）を自動検知して再ロードし、表示点群を差し替えてカメラを再センタリングする機能を統合。
  - すでに `_labeled` や `_downsampled` が付いたファイルや `downsample/` フォルダ配下のファイルがロードされた状態から再度ダウンサンプリングを実行しても、多重にファイル名が汚染されない（`_labeled_labeled` などの重複付与を防止する）堅牢なファイルパス・ベース名解決処理を構築。さらに、再ロード時に `loader.fileName` に `downsample/` 相対パスを設定することで、以降のパス解決（`GetFilePath()`）が破綻しないように対策を実装。

### 2. スケール校正とダウンサンプリングのUnity UI統合 (2026-06-10)
- **Pythonスクリプトの引数対応・pandas依存排除**:
  - `1_scale_calibration.py`（基準球実寸60mm等のUI直接入力）および `2_downsample.py` を対話的 `input()` から引数（`argparse`）対応 of CLIへとリファクタリング。`pandas` を排除し標準 `csv` でのレポート生成に最適化。
  - 各ファイルを `python_backend/` に整理。
- **C#ブリッジの構築とUIの完全統合**:
  - `PythonBridge.cs` に非同期でPythonプロセスを実行し、出力される `[Progress]` に応じて進捗バーを更新する機能を実装。
  - 右側の「☁ CloudCompare Unity機能パネル」 OnGUI に、スケール校正とダウンサンプリングのパラメータ設定モーダル（PlayerPrefsでの永続化対応）と実行ボタンを統合。重複していた古いUI描画を完全に削除。
  - Open3D の Tensor API (`o3d.t.io`) を使用し、PLYからラベリング属性（`label`）を直接読み込んで部位別（茎・葉・果実等）に自動分割する処理をバックエンドに構築。

### 3. アノテーションパレットUIの高度化とマルチレイヤー対応 (2026-06-03)
- **ドラッグ＆ドロップによる並び替えとID動的再割り当て**:
  - `AnnotationPipelineEditorUI.cs` にて、クラスブロックをD&Dで並び替え、左から順にID（1以上）を自動再割り当てするパレットを実装。
  - ID変更時に、すでにアノテーションされている全レイヤーの点群データ（数百万点）の旧IDを新IDへ動的に置換するマッピング処理（`RemapClassIds`）を実装し、アノテーション崩れや色崩れを防止。
- **マルチレイヤーアノテーションデータ構造**:
  - `Dictionary<string, byte[]>` を用いて「部位」や「構造物」などのアノテーションデータをメモリ上で分離保持し、切り替え・新規追加・削除を行えるレイヤー管理セクションを追加。
- **アノテーション履歴 (Undo/Redo)**:
  - `PointCloudEditor.cs` にアノテーションのUndo/Redo履歴スタック（最大10世代）を実装し、パレット上部から容易にやり直しができるように改善。
- **日本語入力（IME）改善とキー競合の解消**:
  - テキスト入力フィールドへの入力フォーカス（`GUIUtility.keyboardControl != 0`）を監視する安全対策を実装。日本語入力（変換やBackspaceでの修正）を行った際に、DeleteやBackspaceによるアノテーションクラスやモヤ処理フィルタブロックの誤削除が走る競合を完全に解消。

### 4. 空中モヤ・浮遊点ノイズ除去パイプライン (2026-05〜2026-06)
- **ハイパフォーマンス・ノイズフィルタの実装**:
  - Python側に SOR (統計的外れ点), ROR (半径外れ点), 局所密度, DBSCAN, 局所平面推定 (CC) の各幾何フィルタを実装。
  - **空中白モヤ除去フィルタ (White Haze Filter)**: RGB色情報を基に明るく低彩度な白い霞を検出する色フィルタを実装。白モヤ候補を幾何計算の母集団から事前に排除することで、SORやCC平面推定の精度低下を防止。
- **C#連携とリアルタイムプレビュー**:
  - `NoiseFilterResult.cs` や `NoiseFilterManager.cs` を通じて、Pythonでの解析結果（binバイナリ）を高速デシリアライズしてロード。
  - `PointCloudShader.shader` を改修し、ノイズプレビュー（SOR=赤、白モヤ=水色等）をリアルタイムにカラーオーバーレイ描画。
  - ノイズが確定非表示となった点（カリング）を物理的に除外したクリーンアップPLYの非同期エクスポートを実装。
  - Pythonプロセスの無通信タイムアウト制御（3分）および進捗更新の段階的スロットリング処理により、ハングアップを完全に回避。

### 5. カメラ制御・LOD・操作系統のCloudCompare互換最適化 (2026-05)
- **カメラ制御 (CloudCompareCameraController.cs)**:
  - 常に「左ドラッグ＝回転、右ドラッグ＝パン、スクロール＝ズーム」で操作系統を統一。
  - 仮想トラックボールの球外ドラッグで画面 Z 軸周りの 2D ロール回転を実現。
- **オクトリーによる空間的LOD & 描画カリング**:
  - 空間分割八分木（`PointCloudOctree.cs`）をバックグラウンドで構築。
  - 視錐台プレーン衝突判定とカメラ距離に基づく階層的LOD（画角スケール判定）により、GPUへの転送点数を劇的に削減。
  - アノテーションのブラシペイント、矩形、なげなわ、および接続探索（セル単位BFSへの刷新）の空間探索をオクトリートラバースにリファクタリングし、数百万点データでもCPUスパイクなしで動作する超高速化を実現。
