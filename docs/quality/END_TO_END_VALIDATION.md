# PointCloud_Prosse 作業4/7 — E2E検証結果

実施日: 2026-10-10 (JST)
対象: `E:\VR\PointCloudVR` の検証時点の作業ツリー。既存の未コミット差分を含む。
QAルート: `E:\pcwb-qa-20261010\E2EValidationTask4-20261010-134000`

## 判定

- 機能横断probe: **PASS**。Windows Player内の3シナリオで計44件の検査がすべてPASSし、各Player終了コードは0。
- 実GUI: **一部PASS / 一部BLOCKED**。合成点群の読み込みと表示、3DブラシUIの選択、再起動後の計測表示を画面で確認した。マウスによる点選択と分類・Undo/Redoの一連のGUI操作は確認できていない。
- 総合: **FAIL**。第4次検証から引き継いだ終了時ネイティブクラッシュは根本原因未特定で、`PLAYER_EXIT_CRASH_FIX.md` の判定が `UNRESOLVED / FAIL` のまま。今回の終了成功は旧クラッシュが解消した証明ではない。

## 実行環境と識別情報

- Unity `6000.4.7f1`。Playerは作業ツリー外の新規隔離コピーからビルドした。
- Python backendは別QAコピーに複製したvenvを使用。`python -m pytest tests -q`: **58 passed**、`python -m compileall -q .`: **PASS**。
- Unity EditMode: **2/2 PASS**。Unity PlayMode: **3/3 PASS**。
- 既存C#検証: `dotnet run --project PointCloudVR/tests/PlyExportValidation/PlyExportValidation.csproj --configuration Release`: **PASS**。
- 合成点群: 主シナリオ100,000点、球推定用4,096点、茎径用既知径12 mmの点群ほか。実測PLYは使っていない。
- 主シナリオ入力PLY SHA-256: `f4be485121de864b5413f7a84f5b7351a65236ca5e0cf65258e4700991f70545`。処理前後で不変。
- Player SHA-256: `PointCloudVR_QA.exe` `16726f6bc281a100105ae80f982a21a9332c870340bcd2e1d89bd82b76dd33c7`; `UnityPlayer.dll` `4c142dd3d8237cd3537021c75904ff5df7e3c37796fff110bdefa925a636fe6b`; `Assembly-CSharp.dll` `879d1e27e5f37ee3634b01123d648880ca1dad088f254036775c0695ddb1468d`。
- 全ソース・テスト資材のハッシュ一覧: `Artifacts/source_snapshot_sha256_final.tsv`。ビルドPlayerのハッシュ一覧: `Artifacts/player_binary_sha256.tsv`。

## Windows Player統合試験

以下は**実Player内のテストprobeから本番コンポーネント／処理経路を呼んだ試験**であり、実画面のマウス操作とは区別する。

| シナリオ | 結果 | 主な確認 |
|---|---|---|
| `player_workflow` | **27/27 PASS**, exit 0 | 読み込み、分類/Undo/Redo、削除/復元、PLY保存/再読込、球径、ダウンサンプリング、C2C、茎径、ノイズ解析/プレビュー/確定/Undo/Redo、距離計測JSON/CSV、校正PLY、境界入力、同名ファイル分離、状態復元、編集/保存ストレス |
| `player_e2e_order2` | **14/14 PASS**, exit 0 | 順序を変えた計測、ノイズ、C2C、球径、ダウンサンプリング、茎径、キャンセル後の保持と再試行、点群復帰 |
| `player_load_race` | **3/3 PASS**, exit 0 | A/B/C読込競合、逐次切替、100回点群切替 |

再現可能な実行コマンド:

以下は実行記録の再現用コマンド例。指定済みの`QaRoot`は今回の証拠であり、再実行時は別の新しい`QaRoot`へ置き換える。

```powershell
& .\PointCloudVR\tests\UnityIntegration\Invoke-FourthAudit.ps1 `
  -QaRoot 'E:\pcwb-qa-20261010\E2EValidationTask4-20261010-134000' `
  -PythonVenvSource 'E:\pcwb-qa-20261010\E2EValidationTask4-20261010-100500\PointCloudVR\python_backend\.venv' `
  -SkipPythonInstall -RunPlayerWorkflow -RunPlayerE2EOrder2 -RunPlayerRace
```

上記のQAルートは証拠として保持し、再実行時は別の新しい`QaRoot`を指定する。今回の負荷試験では編集1,000回、Undo/Redo各200回、保存/再読込100回、点群切替100回を実施した。3本のprobe Playerの最大Working Setは約571–581 MiB、最大Private Bytesは約891–895 MiBだった。

## 数値・保存の確認

- 茎径: 合成12 mm茎、指定5 mm断面で120断面。中央値 **11.9942989 mm**、overlay準備完了。
- 球直径: 合成球の推定直径 **60 mm**、最大成分4,096点・fit inlier 4,096点。
- C2C: 理論値12 mmに対し平均 **12 mm**。
- ダウンサンプリング: 4,096点から2,862点へ減少。
- ノイズ: Playerから`NoiseFilterUI.RunNoiseFilterAnalysis`の本番入口を起動し、100,000点分の結果を受領。合成入力では全点が候補となったため、結果の機械的適用・commit/undo/redoを検証したが、これは実植物データでの妥当性を意味しない。
- ASCII/Binary PLY: `AllVisible`、`SelectedVisible`、`SelectedNonDeleted`、`CleanedVisible`を出力。読戻し後の点数、順序付きXYZ、RGB、クラスラベルの下位8 bitが期待値と一致。元PLYのSHAは不変。
- 校正PLY: 合成倍率0.5で座標が一度だけ補正され、markerと点数が一致。元PLYは不変。倍率の実世界での正しさは検証対象外。
- 通常PLYの`label`は1 byteのクラスラベルとして比較した。選択・削除・ノイズ候補など実行時上位bitのPLY往復保存は仕様上確認しておらず、全32 bit保存のPASSとはしていない。
- 計測JSON/CSV: 計測ID、2点、長さ、弦長、CSVヘッダーと値を照合。長さは`PointCloudRenderer.DataLengthToMillimeters()`の換算結果と一致。
- 別プロセスで再起動したPlayerに同一SHAのQA点群と生成済み計測sidecarを読み込ませ、計測一覧に1件・2点・0.3 mmと表示されることを画面で確認した。選択中ID自体は点群ロード時にリセットされるが、記録データは再読込された。
- 0点・1点PLYは構造上有効。不正header、途中で切れたbinary、NaN/Infinityを含むPLYは検証APIが拒否。0点/1点をPlayerの通常編集フローへ読み込ませた試験ではない。

## GUI試験と画面証拠

probeを付けないQA Windows Playerを画面表示し、実際のクリックで合成PLY一覧から`cloud_A_100k.ply`へ切り替えた。Playerログ上でファイルパス、10万点、Octree生成を確認。点群が描画された状態、3Dブラシモードの選択表示、別プロセス再起動後の計測一覧もスクリーンショットで確認した。

画面証拠は次に保存した。

- Player UI全体: `Artifacts/gui_player_after_input.png`
- 点群表示: `Artifacts/gui_after_camera_click.png`
- 3Dブラシ選択状態: `Artifacts/gui_after_brush_click.png`
- 再起動後の計測行: `Artifacts/gui_after_restart.png`

点群上で中クリックを2回試したが選択表示は0点のままだった。注入した物理入力がアプリの点ヒット位置・入力経路に正しく対応したと確定できないため、選択・分類・UI Undo/Redoは**BLOCKED**とし、PASS扱いしない。ノイズ、茎径、球推定、C2C、ダウンサンプリング、エクスポートも画面のボタンを押した試験ではなく、Player内probeによる試験である。

解像度・表示倍率の全組合せは未検証。実画面確認はPlayerを最大化した150% DPI環境が中心で、1024×768、1280×720、1366×768、1600×900等の個別サイズでの文字切れ・重なり・クリック透過は**NOT_RUN**。

## 点群状態と入力対象の相違

現在の実装から確認できた範囲は次のとおり。これは対象点の現状説明であり、今回、科学的定義や製品コードは変更していない。

| 機能 | 現在確認できた入力 |
|---|---|
| C2C | `PointCloudRenderer.GetPositions()`が全点の座標を返し、`PointCloudManager.CompareClouds()`がその配列全体を使う。削除bit・ノイズ非表示bitでは除外しない。 |
| 茎径 | 一時PLYを`ExportPointMode.AllVisible`で作る。PLY出力フィルタは削除点とノイズ非表示点を除外する。 |
| 球径推定 | 選択点かつ削除/ノイズ非表示でない点を使用する経路。 |
| ノイズ再解析 | 元PLYを入力にし、UnityからPythonへ渡すmaskは削除bitのみ。既にノイズ非表示になった点はこのmaskでは除外されない。 |
| 計測 | 点群local座標をJSONに保存し、距離結果は現在の表示スケール経由でmmに換算する。 |

従って、「削除点」「ノイズ非表示点」をC2Cとノイズ再解析でも除外すべきかは機能間で対象が異なる。CloudCompare互換性・再解析の意図を含む仕様判断が必要なため、今回ここを独断で統一していない。通常PLYはクラスラベルと実行時フラグも区別している。

## 未実施・要追試

- 全点非表示/全点削除、NaN/InfinityをPlayerへ直接ロード、不正/途中破損sidecar、読み取り専用、既存出力名衝突の拒否後に正常処理へ戻る一連の操作。
- 高位label bitを含む復旧後の全状態保存、全PLY形式での完全なbit単位往復、実測PLYの座標・ラベル比較。
- 同一点群を編集した後の全解析再実行、古いstem/noise/C2C結果の拒否、stem解析結果cacheのプロセス再起動後の再表示。今回の「保存結果再表示」PASSは計測sidecarのみ。
- 全解析種別のキャンセル競合、保存途中の破損注入、クラッシュチェックポイントの各段階障害注入。
- Player GUIから一連の編集・解析全操作、各解像度・表示倍率。GUIでの点選択と分類はBLOCKED。
- 実植物点群・実基準物による科学的精度。今回の数値は合成点群のみ。

## 終了・例外記録

- 3本のprobe Playerは終了コード0。画面確認用Playerには通常のウィンドウ終了を要求してUnityの終了ログを確認したが、その起動の終了コードは採取していない。別に起動した確認用Playerでは終了コード0を採取し、UnityログにPhysics/Input System/CodeReloadManagerの終了処理が記録された。
- QA Playerを対象とするWindows Application Error event 1000は直近確認範囲でなし。一方、04:00:22 JSTにWindows Error Reporting event 1001 `RADAR_PRE_LEAK_64`を1件確認した。これは今回のアクセス違反クラッシュとは別のメモリ診断イベントであり、原因は未特定。Private Bytesが約0.9 GiBに達した計測値と合わせ、追加調査が必要。
- 以前の終了時アクセス違反`0xC0000005`/`0xC000041D`は再現できていないが、発生条件と原因は未確定。今回の正常終了回数は不存在の証明ではなく、プロジェクト全体の終了クラッシュ判定は引き続き**FAIL**。

## 今回の変更とGit

製品コード・数値アルゴリズムは変更していない。テストで見つかったCSV判定の誤り（ヘッダー名をデータ行に要求）と、点群再読込時に選択IDまで復元されるという誤前提を、計測ID・値・点数でデータ再読込を検証する形へ修正した。テスト用fixture/runnerは新規QAコピーと別順序・負荷シナリオを再実行できるようにした。

commit、push、stageは行っていない。作業ツリーの既存未コミット変更および`Assets/_Recovery/`には手を加えていない。
