# 最終耐久・数値検証報告

実施日: 2026-10-10 (JST)
対象リポジトリ: `E:\VR\PointCloudVR`
基準commit: `38fe6c8ee7fa127e54920ed68f808a00afd96dc6` (`main`, 当初 `origin/main` と一致)
QA root: `E:\pcwb-qa-20261010\FinalAcceptanceTask7-20261010-173700`

## 結論

- **点数別読込: PASS**。合成PLY 10,000～2,200,000点を各3回、全15件で読み込み・Octree生成・期待描画点数を確認。
- **2時間耐久: PASS (試験時点のコード)**。隔離Windows Playerを7,200秒動作させ、固定seedの編集・切替・保存・Undo/Redo・解析キャンセルを継続。終了コード0、残留子プロセスなし。
- **最終C2C修正後の回帰: PASS**。最新PlayerでC2C境界テスト5/5、統合ワークフロー27/27。Python 61件、compileall、C#純ロジック回帰も成功。
- **最終コードでの2時間再走行: NOT_RUN**。2時間試験後に見つかったC2Cの極端座標精度バグを修正した。最終Playerでは当該修正と主要ワークフローを再確認したが、2時間耐久は再実施していない。
- **実測精度、別PC可搬性、実GUI操作、HMD、物理GPUメモリ: NOT_RUN**。合成データの数値一致を実物の計測精度保証とは扱わない。
- 旧Player終了時の `0xC0000005` / `0xC000041D` は今回の試験では再発しなかったが、根本原因の特定・恒久解決を証明したものではない。

## 環境と負荷予算

隔離したWindows Playerを1プロセスずつ実行。実測PLY・ユーザーのUnity Editor・`Assets/_Recovery/` は試験に使用していない。

| 項目 | 確認値 |
|---|---|
| OS / Unity | Windows 11 Pro build 26200 / Unity 6000.4.7f1 |
| CPU / RAM | Intel Core i9-9900K、16論理CPU / 約63.9 GiB |
| GPU | NVIDIA GeForce RTX 2080 Ti、デバイス容量 11,264 MiB |
| Python環境 | Python 3.12.4、NumPy 2.5.3、SciPy 1.18.1、Open3D 0.20.0 |
| 負荷試験中の最低空きRAM / disk | 46,044,131,328 bytes / 522,420,842,496 bytes |

空き容量を監視し、低メモリ・低ディスク条件による強制負荷は行わなかった。耐久Playerは1つのみ起動した。

GPUの容量と使用量は区別した。Unityの `GetAllocatedMemoryForGraphicsDriver()` は全試験で0を返したため、Player個別GPU使用量は **NOT_MEASURED**。別途記録した `nvidia-smi` はシステム全体値で、6回の観測では温度40～51°C、利用率6～19%、使用メモリ2,660～2,937 / 11,264 MiB。Player単体の値ではない。

## 点数別読込

各サイズを3つの独立Player実行で読み込み、成功15/15。各回で点数、Octree ready、draw countを確認した。表の時間は3回の中央値、括弧内は範囲。

| 点数 | PLY loader (ms) | load要求～Octree ready (ms) | Octree nodes | GC live heap sample |
|---:|---:|---:|---:|---:|
| 10,000 | 65 (65–66) | 1,460 (1,459–1,474) | 73 | 約7.29 MB |
| 100,000 | 62 (62–62) | 112 (112–112) | 457 | 約13.52 MB |
| 500,000 | 226 (226–227) | 393 (393–410) | 2,057 | 約44.06 MB |
| 1,000,000 | 444 (443–444) | 777 (776–778) | 3,489 | 約88.20 MB |
| 2,200,000 | 956 (938–958) | 1,723 (1,705–1,758) | 8,537 | 約182.4 MB |

各Player全体のピークはWorking Set 0.832～0.865 GiB、Private Bytes 1.346～1.380 GiB。これはサイズ別の瞬間ピークではない。`GC.GetTotalMemory(false)` による値は処理後の近似live heapであり、累積割当量ではない。`load要求～Octree ready` には初期化・Octree待ちが含まれ、初回描画時間を単独測定したものではない。初回描画時間は **NOT_MEASURED**。

試験順によるOSファイルキャッシュの影響を排除したcold-cache試験はしていない。したがって上記はこのPC上の反復測定値であり、ディスクcold-start性能や他PC性能の保証ではない。

## 2時間耐久

隔離Windows Playerを7,200秒動作させた。ランダム操作seedは `20261010`。

| 項目 | 実測 |
|---|---:|
| 経過 / 監視 | 7,219.4秒 / 5秒間隔1,396サンプル |
| 終了 | exit code 0、停止理由なし |
| Player CPU time | 9,251.5秒 (16論理CPU全体の平均約8.0%相当) |
| 固定seed操作 | 45,464 steps |
| 点群切替 / 編集 / Undo / Redo / 保存 | 15,218 / 15,144 / 15,144 / 15,144 / 15,103 |
| 解析キャンセル | 100/100成功、Python child process起動90回、試験後残留なし |
| source fixture | SHA-256不変を確認 |
| Peak Working Set / Private Bytes | 595,623,936 / 954,867,712 bytes |
| 平均 Working Set / Private Bytes | 約0.532 / 0.881 GiB |
| Unity managed allocated / reserved | 約90.0 MiB / 180.0 MiB、reservedは試験中ほぼ一定 |
| PlayerからのGPU driver memory値 | 0 bytes (実GPU使用量の測定値とは扱わない) |

119件のheartbeatで報告された区間平均フレーム時間の中央値は21.148 ms (範囲19.567～21.849 ms)。一方、経過6,073秒時点で1 heartbeat区間の最大値 **2,875.667 ms** が1回記録された。以後の区間は通常範囲へ戻り、プロセス終了や操作ループの停止はなかったが、発生原因は特定できていない。したがって「停止なし」や「安定した平均値」を根拠に、長い一時停止がないとは結論しない。これは残存する応答性リスクである。

試験中の最低空きRAMは約42.9 GiB、最低空きdiskは約486.5 GiB。合成PLYを使った操作列で、元fixtureのハッシュは不変。試験Playerは正常終了し、残留Python子プロセスなし。Application Error / WERを照合した範囲では当該試験に一致するPlayerクラッシュ記録はなかった。

**適用範囲:** 2時間試験は、後述の極端座標C2C修正前の `PlayerBuildEnduranceFinal2`。修正はC2C近傍距離の数値表現に限定され、耐久probe本体や編集・保存処理は変えていないが、最新コード全体を2時間走らせた証拠ではない。

## 数値検証と発見した不具合

### 修正: C2C最近傍の二乗距離オーバーフロー

回帰テストとして座標が約 `2e19` の参照点を追加したところ、修正前はfloatの二乗距離が `Infinity` になり、最近傍indexを選べず失敗した。距離の比較およびAABB下限をdoubleで計算し、PointCloudManagerのC2C結果もdouble二乗距離から線形距離を得るよう修正した。float座標自体の入力精度は変わらない。

| 試験 | 結果 |
|---|---|
| 極端座標のC2C距離 | 理論値約 `1.9999999961e19`、結果 `2.0e19`。float入力表現を含む差で有限値を維持 |
| 平行平面、密度差あり | 理論12 mm、平均12 mm、最大12 mm |
| 等距離最近傍 | 理論1 mm、結果1 mm |
| 空参照点群 | 拒否、operation解放後の通常再試行成功 |
| 50k参照 / 5k query総当たり照合 | 既存C#回帰PASS。単回bench: build 42 ms + query 8 ms |

修正前の新規回帰は失敗し、修正後のC# harnessと最終Windows Playerで成功した。C2C数値ケースは最終Playerで5/5 PASS。

### その他の合成データ

| 機能・入力 | 理論値 / 実測値 | 判定 |
|---|---|---|
| C2C完全一致・12 mm平行移動 | 完全一致0、平行移動の平均/最大12 mm | PASS |
| 3D距離・計測sidecar/CSV再読込 | 期待0.343477458 mm、JSON/CSVとも0.343477458 mm | PASS |
| 既知直径の参照球 | 4,096点、60 mm。Player経由結果60 mm、component/inlier各4,096 | PASS |
| 部分球・複数球のPython回帰 | 部分球は直径0.06 data-unitを維持。900点/500点の2球では大きい成分を選択 | PASS |
| 12 mm円柱の茎径 | 120断面、5 mm厚の中央値11.9942989 mm、差-0.0057011 mm | 合成回帰PASS |
| テーパー円柱、2密度 | 13断面ずつ。dense median signed error -0.0190 mm / max abs 0.0245 mm、sparse -0.0301 mm / max abs 0.0397 mm | Python回帰PASS |
| 校正済みPLY | 合成factor 0.5を一度適用、marker保持、元PLY不変 | PASS |
| Downsample | 4,096点から2,862点 | PASS |
| ASCII/Binary PLY roundtrip | XYZ/RGB/class label照合 | PASS |

数値アルゴリズムの定義・閾値はこの検証目的で変更していない。上記は明示した合成入力の回帰であり、実植物、物理標準球、既知実寸との測定偏り・再現性は **NOT_RUN**。

## 最終コード回帰・Player

2時間試験後のC2C修正を含む新しい隔離Player `PlayerBuildEnduranceFinal3` をビルドした。

- Unity Windows x64 build: Succeeded、error 0、warning 2。2件はテスト専用probeの `renderer` 名が継承メンバーを隠す同一CS0108警告。製品C#警告ではない。
- Python: `pytest tests -q` **61 passed**。再利用QA venvのPython 3.12.4 / NumPy 2.5.3 / SciPy 1.18.1 / Open3D 0.20.0。
- Python `compileall -q .`: exit 0、診断なし。
- C# pure regression harness: PASS。最近傍、PLY、履歴、operation lifecycle、recovery等。
- Windows Player C2C数値probe: **5/5 PASS**。
- Windows Player統合workflow: **27/27 PASS**。編集/分類、PLY出力、計測JSON/CSV、校正PLY、点群切替、境界入力、C2C、参照球、茎径、noise/downsample等。Python subprocessはPlayerから起動。
- 日本語・空白を含む隔離ディレクトリからPlayerを起動し、既存QA venvへのjunctionを使ったPython実行を確認。既存venvを複製していない。
- 実画面のマウス/キーボード自動操作、画面サイズ/DPI別の視覚確認は今回実施していない。Player probeが本番経路を呼んだ試験とGUI試験は区別する。
- QA build環境ではOpenXR DisplayがHMD不在で初期化されなかった。デスクトップPlayerの検証結果であり、HMD実機確認は **NOT_RUN**。

最終Playerの識別情報:

| ファイル | SHA-256 |
|---|---|
| `PointCloudVR_Endurance.exe` | `16726F6BC281A100105AE80F982A21A9332C870340BCD2E1D89BD82B76DD33C7` |
| `UnityPlayer.dll` | `4C142DD3D8237CD3537021C75904FF5DF7E3C37796FFF110BDEFA925A636FE6B` |
| `Assembly-CSharp.dll` (final3) | `19BE5C456181949933402C24078F1F5553A930E272335F153F973786255E0130` |

現時点の製品ソースSHA-256:

| ファイル | SHA-256 |
|---|---|
| `Assets/PointCloudManager.cs` | `B973E328B73AFC25863C76C37842E1A0AFA8CDD400DAEE2AA34CB7191A4CA308` |
| `Assets/PointCloudWorkbench/Scripts/ExactNearestNeighbor3D.cs` | `C4396BD42FFCDD8252DCC8C0F4D323DEAF26B9EFB02582B07FCF51E695E2B537` |
| `python_backend/tests/test_reference_sphere_algorithm.py` | `9A8230FB9DE0E11936931E210302DC99A26A5CCEDE3C4B1547CD187EF3083EA8` |
| `python_backend/tests/test_stem_diameter_algorithm.py` | `C27E437735189489A6D9EB8E1CAB7E9D81994934E42835EE1283E7EF5DC2189D` |
| `tests/PlyExportValidation/Program.cs` | `AC95AD24ECFE6BB59F23AD552B130CCE9455677C7BBD7FA2B83D47BC579A230A` |
| `tests/UnityIntegration/Assets/ThirdAuditWorkflowProbe.cs` | `9511A1E8E0ED089B29B622A201FF82A38976DF3A43F024252A9811C9DC2B7683` |

最終C2C/統合workflowは `Assembly-CSharp.dll` final3で実施。2時間のPlayerは `Assembly-CSharp.dll` final2 (`4148B2892A78E90230D809C0A94875C94AAAA7EE429785101F16CB80D786B037`) であり、両者を同一ビルドとして扱わない。

## Python可搬性

PlayerからPython subprocessを起動し、QA venv内のPythonと必須数値ライブラリを使う経路を確認。空白・日本語を含む隔離Player配置でも成功した。ただし、テストではQA用に準備済みのvenvへのjunctionを使用しており、venvそのものの移動・再構築、Python未導入、依存不足、別Windows PC、ユーザー権限差は **NOT_RUN**。システム全体のPythonは変更していない。よって別PC可搬性は未保証。

## 未実施・残存リスク

- **NOT_RUN:** 最終final3コードでの2時間耐久再試験。
- **未解決:** 一時的な最大2.876秒フレーム遅延の原因。再発頻度を判定する追加の計測が必要だが、今回の平均値で隠さない。
- **未解決:** 過去に記録された `0xC0000005` / `0xC000041D` 終了クラッシュの根本原因。今回の対象Player実行ではexit0で、該当WERイベントなしだったが、一般的に解消した証明ではない。
- **NOT_RUN:** 実植物点群、実物標準球、校正済み既知長による科学的妥当性。
- **NOT_RUN:** 別PC/Pythonクリーンセットアップ/HMD実機、GUI実操作、125%/150% DPI・複数解像度。
- **NOT_MEASURED:** Player単体GPUメモリ、独立した初回描画時間。
- **NOT_RUN:** ノイズ除去・Downsampleの実測植物に対する品質評価。合成workflowで処理経路は通したが、科学的品質の合否は主張しない。

従って、合成データ・このPC・監督下の研究室内試験に限った部分的な信頼性証拠は得たが、学生授業利用や一般配布、実測精度の受入をPASSとは判定しない。

## 再現用成果物

QA成果物は以下に保持。元の実測データ領域とは分離している。

- 2時間Player log: `E:\pcwb-qa-20261010\FinalAcceptanceTask7-20261010-173700\Artifacts\player_endurance_2h_final.log`
- 2時間外部resource samples: `...\Artifacts\player_endurance_2h_final_resource_samples.json`
- 2時間操作TSV: `...\Artifacts\PlayerRuns\fourth-player-20261010-092137-55e87de7.tsv`
- 点数別3反復: `...\Artifacts\player_load_tiers_final_repeat01.log` ～ `repeat03.log`
- C2C最終Player: `...\Artifacts\player_c2c_numerical_final3.log`、`...\Artifacts\PlayerRuns\fourth-player-20261010-112541-9db580d8.tsv`
- 最終統合Player: `...\Artifacts\PlayerRuns\fourth-player-20261010-112638-fd10baf3.tsv`
- GPU global snapshots: `...\Artifacts\gpu_telemetry.txt`
