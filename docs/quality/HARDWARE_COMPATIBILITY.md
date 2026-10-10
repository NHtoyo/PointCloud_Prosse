# PointCloud_Prosse Hardware Compatibility

更新日: 2026-10-11
対象ソース基準: `77c1c1e` + この作業の未コミット差分
Unity: `6000.4.7f1`, Windows x64 Player

## 結論

点群表示はGPU描画専用です。`PointCloudRenderer`は`ComputeBuffer`、Shader Model 5.0の`StructuredBuffer`、`Graphics.DrawProcedural`を使い、CPU描画fallbackはありません。必要条件はGraphics device、Shader Model 5.0以上、PointData stride 24 bytes、必須shaderが実際にsupportedであることです。

このコードは`.compute`/`ComputeShader.Dispatch`、`GraphicsBuffer`、CUDA、NVIDIA専用APIを使いません。`supportsComputeShaders`は診断レポートに記録しますが、それだけを理由に拒否しません。Unityの説明では、ComputeBufferは通常graphics shaderからも使え、その場合の最低shader modelは4.5です。別の`supportsComputeShaders`値はComputeShader対応の確認用です。[Unity ComputeBuffer](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/ComputeBuffer.html), [Unity SystemInfo](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/SystemInfo.html)

Shader自身は`#pragma target 5.0`なので、本アプリの実要件は5.0です。D3D11 Feature Level 10_0はShader Model 4.0相当のため要件を満たさず、非対応表示になる想定です。[Microsoft Direct3D feature levels](https://learn.microsoft.com/en-us/windows/win32/direct3d11/overviews-direct3d-11-devices-downlevel-intro)

## 実測・推定マトリクス

| 構成 | 状態 | 確認できた範囲 |
|---|---|---|
| NVIDIA GeForce RTX 2080 Ti / D3D12 | `CONFIRMED` (過去のPlayer記録) | 別の過去ビルドで220万点のload/Octreeログあり。今回の最新バイナリのGPU/driver/画面描画確認ではない。VRAM使用量の測定ではない。 |
| NVIDIA / D3D11 / Feature Level 11.1 | `CONFIRMED` (最新Player、headless) | 2026-10-11の最新ビルドログに強制D3D11、FL11.1、shader初期化、ComputeBuffer作成、100,000点load、Octree readyを記録。画面表示・操作は未確認。Playerはbatchmodeの自動終了要求に応じず、隔離プロセスを試験終了後に停止。正常終了試験として数えない。 |
| D3D11 / Feature Level 11_0 | `NOT_TESTED` | テストスクリプトに強制起動ケースはあるが、今回の最新Playerでは未実行。Shader Model 5.0要件上の技術条件を満たす見込みでも、実描画・操作の確認ではない。 |
| NVIDIA / D3D12 / 最新ビルド | `NOT_TESTED` | 今回の最新ビルドで未実行。 |
| NVIDIA / Vulkan / 最新ビルド | `NOT_TESTED` | 今回は実行できず。 |
| D3D11 Feature Level 10_0 | `TECHNICALLY_UNSUPPORTED` | Shader Model 5.0要件を満たさない。Playerでの強制起動結果は未確認。 |
| AMD Radeon (RX / Radeon Graphics) | `NOT_TESTED` | 実機なし。該当GPUとdriverがSM5以上、StructuredBuffer、必要shaderをサポートすれば`TECHNICALLY_COMPATIBLE`。 |
| Intel UHD / Iris Xe / Arc | `NOT_TESTED` | 実機なし。型番ではなく実際のAPI/driver機能で判定する。IntelのAPI対応は製品・driverによって異なる。[Intel graphics API support](https://www.intel.com/content/www/us/en/support/articles/000005524/graphics.html) |
| SM5対応の旧世代GPU | `TECHNICALLY_COMPATIBLE` | API/driverが必要なshaderとbufferを提供し、実際のshaderがsupportedなら候補。機種別実機確認なし。 |
| SM5非対応GPU | `UNSUPPORTED` | 起動時に点群描画を止め、日本語の理由を表示する。 |

D3D11/D3D12/VulkanのPlayer起動フラグはUnityの公式Player引数を利用します。APIの要求フラグがログ上の実APIと一致するかも確認してください。[Unity Player command-line arguments](https://docs.unity3d.com/6000.0/Documentation/Manual/PlayerCommandLineArguments.html)

## メモリと点数

最新Playerでheadlessに確認したのは100,000点の読込とOctree生成までです。これは描画・カメラ操作のPASSではありません。220万点の記録は過去ビルドの1環境の結果で、現在のビルドや他GPUの上限保証には使いません。

`graphicsMemorySize`はGPUメモリ容量の報告値で、アプリのVRAM使用量ではありません。GPU使用量、最大点数、最低/推奨VRAMは測定しておらず、現時点で上限値は設定できません。GPU側には点bufferに加えてindex bufferがあり、CPU側にも点配列とOctreeがあります。点数上限は実機の空きRAM/VRAMとデータ内容に依存します。

| 環境区分 | 暫定条件 |
|---|---|
| 最低GPU機能 | `graphicsShaderLevel >= 50`、Graphics API有効、必須shader supported、24-byte PointDataとStructuredBuffer描画が成立すること |
| 最低OS | Windows 64-bit Playerのみを対象。最低Windows buildは未確定 |
| 最低RAM/VRAM | `NOT_ESTABLISHED`。実測根拠のある容量値なし |
| 参考実機 | Intel Core i9-9900K / RAM 64 GiB / RTX 2080 Ti (11 GiB報告容量)での過去記録。推奨仕様やVRAM使用量とは扱わない |

## GPU計算とPython

Unity GPUは点群の表示専用です。Octree、選択、PCA/茎径/球径/C2Cなどの数値処理はCPUまたはPython backendです。Python sourceにはCUDA device選択、`torch.cuda`、CuPy、NVIDIA APIは見つかりません。Open3Dの呼出しもCUDA deviceを指定していません。Open3Dは明示的にCUDA deviceを渡すAPIを持ちますが、本プロジェクトはそれを使いません。[Open3D PointCloud devices](https://open3d.org/docs/latest/tutorial/t_geometry/pointcloud.html), [Open3D Tensor defaults](https://www.open3d.org/docs/latest/tutorial/core/tensor.html)

## 起動時診断

Player起動時にWindows/architecture、CPU/RAM、GPU/vendor/driver、実Graphics API、shader level、`supportsComputeShaders`、報告GPU memory capacity、shader support、PointData strideを調べます。Python 3.12と固定package imports/versionsも確認します。`F10`または画面上部の診断ボタンから表示し、JSONを`Application.persistentDataPath/hardware_compatibility_report.json`へ保存できます。レポート本文にPC名やユーザーフォルダー絶対パスを出さない設計です。

Pythonがない場合はPython利用機能を準備不可として示し、点群表示・編集など独立機能は継続可能です。必須描画機能がない場合は点群を黙って表示しない状態にせず、操作を遮るエラー画面を出します。

## 残る確認

- 実画面での点描画、RGB/label、C2C、selection/editは未確認。
- D3D12/Vulkan/Feature Level 10_0 Player試験は未実施。
- AMD/Intel、ノートPCのswitchable graphics、仮想GPU/Remote Desktopは未実施。
- Player終了時のネイティブクラッシュは別件として未解決。今回のD3D11 batchmode停止は通常終了にもクラッシュにも分類しない。
- 画面自動化helperの初期化が2回ともpath errorで失敗し、resolution/DPIの画面検証は未実施。
