# PointCloud_Prosse Usability Validation

更新日: 2026-10-11
実画面判定: `NOT_RUN`

## 検証できたこと

- 最新コードでUnity EditMode 2/2、PlayMode 5/5、Windows x64 Player buildに成功。
- Playerから診断判定を呼ぶPlayMode試験で、SM5未満、shader不在、PointData stride不一致の拒否を確認。`supportsComputeShaders=false`だけでは拒否しないことも確認。
- D3D11 headless PlayerでGPU初期化、shader初期化、50,000点ComputeBuffer、100,000点load、Octree readyをログ確認。画面描画・色・カメラ・入力とは別の確認。
- ノートPC向けに編集ツールの点入力を`Ctrl+左ドラッグ`で代替する実装があり、画面ヒントを更新。ただし実GUIでは未確認。

画面操作用computer-use helperは初期化を2回試しましたが、両方とも内部kernel asset path errorで使えませんでした。そのためスクリーンショットを取得できず、GUI操作をPASSにしていません。1024x768、1280x720、1600x900、1920x1080、125%/150% DPIはすべて`NOT_RUN`です。

## 処理対象の現行仕様

| 機能 | 対象点 | 選択 | 削除済み | ノイズ非表示 |
|---|---|---|---|---|
| 茎径 | `AllVisible`入力。全クラスラベルを含む | 条件にしない | 除外 | 除外 |
| リファレンス球直径 | `SelectedNonDeleted` | 選択点のみ | 除外 | 除外 |
| C2C | 両点群のraw point positions | 条件にしない | 現在は含む | 現在は含む |
| Noise filter | 元PLY + current deleted mask | 条件にしない | maskで除外 | noise-hiddenを別maskで渡していない |
| Downsampling | `AllVisible`相当の最新annotation export | 条件にしない | 除外 | 除外 |
| PLY `AllVisible`/`CleanedVisible` | 見えている点 | 条件にしない | 除外 | 除外 |
| PLY `SelectedVisible`/`SelectedNonDeleted` | 見えている選択点 | 必須 | 除外 | 除外 |

PLY exportはラベル下位8 bitを出力し、selection/deletion/noiseなどの高位mask bitは出力しません。距離計測はdata-space座標を保存し、現在のDisplayScaleで長さをmm表示します。既存の解析定義は変更していません。C2C/Noise filterで選択・非表示状態をどう扱うべきかの製品仕様判断は今回変更していません。

## 別PCで再実施する手順

1. synthetic PLYを`PointCloudData`へ置き、Playerを起動します。GPU/APIを比較する間、同じPLYを使います。
2. `tests/UnityIntegration/Test-GraphicsApiMatrix.ps1`をPowerShellで実行します。各APIで新しい出力フォルダーを指定し、D3D11、D3D12、Vulkan、必要に応じD3D11 FL10_0/FL11_0を1回ずつ試します。公式Unity flags `-force-d3d11`, `-force-d3d12`, `-force-vulkan`, `-force-feature-level-10-0`, `-force-feature-level-11-0`を使用します。
3. 画面上の実点群、RGB/label、カメラ、選択・編集、C2Cを確認し、スクリプトのy/n入力を記録します。API起動ログだけではvisual confirmationになりません。
4. 1280x720に加え、1024x768、1600x900、1920x1080を別々のrunにします。OSのDPI設定変更は管理者/PC所有者が隔離PCで行い、125%/150%の実画面を記録します。
5. 編集操作ではbrush、marquee、lasso、connect、measurementを個別に試します。トラックパッドでは`Ctrl+左ドラッグ`を試し、`Ctrl`を押さないカメラ左ドラッグと誤作動しないことを確認します。
6. 選択点、分類、削除/復元、Undo/Redo後にPLY保存し、再読込後の点数・ラベル・座標を比較します。解析後にエラーを起こした場合は閉じる/キャンセルから再操作できるかも記録します。
7. GPU、driver、Graphics API、shader level、画面解像度、point count、アプリlog、JSON診断レポート、実画面スクリーンショットを同じ試験IDに保存します。PC名/ユーザー名入りパスは共有前に除去します。

## 残る操作確認

- 点群をファイルから選ぶ、実表示、色・ラベル表示、カメラ/trackpad操作。
- 選択、分類、削除、復元、Undo/Redo、点群切替、保存・再読込。
- Python解析、C2C、キャンセル、失敗後の再操作。
- 表示対象/解析対象の確認UI、error modalの閉じやすさ、診断report保存。
- 上記全画面サイズと高DPI。
- Windows Player終了時ネイティブクラッシュは未解決の別件。今回のheadless probeは自動正常終了しなかったため、正常終了を確認したとは扱わない。
