# 第4次検証 実行報告

実施日: 2026-10-10 (JST)  
対象: 第3次検証後のローカル作業ツリーを隔離コピーしたWindows x64 Player  
基準資料: [THIRD_AUDIT_EXECUTION.md](THIRD_AUDIT_EXECUTION.md)  
判定: **FAIL / 配布不可**。主要な合成データ機能はPlayer内で動作したが、複数のPlayerが終了時に異常終了した。クラッシュ原因は未特定。

## 1. 対象と再現性

ユーザーが使用中のUnity Editorや実データを使わず、隔離プロジェクト `E:\pcwb-qa-20261009\FourthAuditFinal7` とその合成PLYだけで検証した。Playerを強制終了したのはこの隔離コピーに対するチェックポイント試験だけ。元のUnity Editorは終了していない。製品コードの修正は行っていない。

第4次の再実行資材を `PointCloudVR/tests/UnityIntegration/` に保存した。Unity EditMode/PlayMode試験、Player操作probe、ビルドスクリプト、合成PLY生成、起動・競合・負荷・復旧を起動するPowerShell runnerと手順を含む。実行時生成の `__pycache__` はテスト資材ではない。

| 識別対象 | 値 |
|---|---|
| 隔離QAルート | `E:\pcwb-qa-20261009\FourthAuditFinal7` |
| ビルド済Player | `PlayerBuild\PointCloudVR_QA.exe` |
| 実Player実行時のソース・設定・依存関係・試験コード一覧SHA-256 | `54E7CEF76778289711CC3AFBE01E6BAACEE865649F9BEEDB427C4114B24C71F8` (`Artifacts/source_snapshot_sha256_final.tsv` のSHA-256) |
| Player EXE SHA-256 | `16726F6BC281A100105AE80F982A21A9332C870340BCD2E1D89BD82B76DD33C7` |
| UnityPlayer.dll SHA-256 | `4C142DD3D8237CD3537021C75904FF5DF7E3C37796FFF110BDEFA925A636FE6B` |
| Assembly-CSharp.dll SHA-256 | `4D93F1F608FC9E47586737024497C7BC274098B9C5E324AE2B9D7BCBF7FAAB26` |
| 検証後に修正したrunner SHA-256 | `6BE02D8B7A22B884D49E079CCD1E499DE44D41D2F7D9F7B03616E08D2FFFC29F` |

この検証はGitHub `main` 単独ではなく、ローカルの未コミット変更を含むスナップショットが対象。各元ファイルとUnity製品アセンブリのハッシュ一覧は `Artifacts/source_snapshot_sha256_final.tsv`、Playerバイナリ一覧は `Artifacts/player_binary_sha256.tsv` にある。Player実行後、runnerの復旧結果照合だけに不具合が見つかり修正したため、一覧SHAは実Player実行時点のもの、runner SHAは修正後のものを別記した。修正後runnerをUnity Player一式で再実行はしていない。実Playerの明示ステータスを修正後の述語で再評価した結果は復元PASS・継続PASS・終了FAILだった。

## 2. 結果一覧

PASSは記載した限定条件の範囲だけを示す。Playerが異常終了した実行は、機能処理が通っても実行全体をPASSとはしない。

| ID | 検証 | 結果 | 実測・根拠 |
|---|---|---|---|
| F4-ENV | 隔離Python環境 | PASS | QAプロジェクト内venvをPlayerから選択。Python 3.12.3、NumPy 2.5.3、SciPy 1.18.1、Open3D 0.20.0。システムPythonは変更していない。 |
| F4-PYTEST | Python backend | PASS | `pytest tests -q`: 58 passed。`Artifacts/pytest.log`。 |
| F4-COMPILE | Python構文 | PASS | `python -m compileall -q .` 成功。`Artifacts/compileall.log`。 |
| F4-UNITY-EDIT | Unity EditMode | PASS | 2 passed / 0 failed。隔離コピー。 |
| F4-UNITY-PLAY | Unity PlayMode | PASS | 3 passed / 0 failed。隔離コピー。 |
| F4-BUILD | Windows x64 Playerビルド | PASS | BuildReport成功、エラー0、警告2、出力約99.9 MB。 |
| F4-EDIT-CLASS-UNDO-REDO | 分類・Undo/Redo | PASS | Player probeで64点を分類、Undoで復元、Redoで再適用。 |
| F4-EDIT-DELETE-RESTORE | 削除・復元 | PASS | 64点の削除と復元を確認。 |
| F4-SAVE-RELOAD-PLY | 保存・再読込 | PASS | 100,000点を書き出して再読込。元PLYのSHA-256とXYZを維持、ラウンドトリップ一致。 |
| F4-PY-REFERENCE-SPHERE | 球直径推定 | PASS | 合成データ4,096点、推定data直径0.05、表示換算60 mm、最大連結成分/inlier各4,096。 |
| F4-PY-DOWNSAMPLE | ダウンサンプリング | PASS | 4,096点から2,862点。入力ファイルhash不変。 |
| F4-C2C | C2C比較 | PASS | 合成平行面の理論値12 mm、Player結果12 mm。 |
| F4-STEM | 茎径 | PASS | 合成円柱の期待径12 mmに対し5 mm断面径中央値11.9943 mm、120/120有効。 |
| F4-STEM-CANCEL | 茎径キャンセルと再試行 | PASS (限定) | キャンセル後に旧結果/overlay保持、処理状態解放。直後の同解析を再実行して成功。別解析の競合・各処理段階のキャンセルは未検証。 |
| F4-EDIT-STRESS | 編集・履歴ストレス | PASS (限定) | 1,000分類呼び出し失敗0。履歴上限5を考慮した40組でUndo 200/200・Redo 200/200。保存/再読込100回成功、元データ不変。 |
| F4-LOAD-RACE-ABC | 起動直後のA/B/C要求 | PASS (限定) | A読み込み中のB/C要求は拒否され、Aのpath・点数・世代・Octreeが一致。続けてB/C逐次読込成功。古いGPU完了通知の遅延注入は未実施。 |
| F4-LOAD-SWITCH-100 | 点群切替 | PASS | A/B/C相当の合成点群切替100回。最終path、100,000点、世代、Octree、描画点数を照合。 |
| F4-LOAD-TIER-* | 読み込み負荷 | PASS (ロード限定) | 100k: 1.679秒、500k: 0.410秒、1M: 0.790秒、2.2M: 1.801秒。Octreeと読込直後の全点描画を確認。時間はこのPC・合成PLYの単回計測で性能保証ではない。 |
| F4-RECOVERY-KILL | チェックポイント後の異常終了・復旧 | 部分PASS / 実行FAIL | 隔離Playerを強制終了し、再起動で異常終了marker、checkpoint、混在ラベル、復元候補、適用後ラベル、元PLY不変を確認。復旧後の分類・削除復元・保存・点群切替・保存PLY再読込もステータス上成功。ただし検証Playerが終了時にクラッシュしたためケース全体はFAIL。 |
| F4-RUNNER-STATUS | 復旧結果の集計回帰 | PASS (照合器のみ) | 元runnerはステータス項目の連続順を仮定し、追加された`loaded_saved_cloud`/`reloaded_positions`等で成功を誤ってFAIL判定した。修正後PowerShell述語を既存Player statusへ適用し、`recoveryPassed=True`、`continuationPassed=True`を確認。Player自体の再実行ではなく、同じ実行証拠に対する集計修正の確認。 |
| F4-UI-MOUSE | 実画面のマウス操作 | 部分PASS | Windows Playerを1600×900で表示し、UIから点群読込、パネル切替、カメラドラッグ、3Dブラシの中クリック選択、分類適用、Undo/Redo、削除・復元、別点群への切替を実操作。点数表示とログを照合。試験画面の永続スクリーンショットは保存していない。 |
| F4-PLAYER-EXIT | Player終了安定性 | **FAIL** | 通常ワークフロー/負荷/復旧後のPlayerが異常終了。記録された終了コードは `-1073740771` または `-1073741819`。Windows Application Errorに `UnityPlayer.dll`、例外 `0xc0000005` の記録あり。原因は未特定。 |

操作probe内から本番のUnityコンポーネント/処理経路を呼んだ試験と、実画面でマウスを操作した試験は上記のとおり別に記録した。内部API試験をGUI試験の代わりとはしていない。

## 3. 起動・競合・復旧の詳細

- 起動時のA/B/C競合では、ロード中の切替要求を現状実装が拒否した。状態が混ざらないことは確認したが、要求をキューイングする設計や、キャンセル後の自動切替を試したものではない。
- 100回の逐次切替と2.2M点までの読込は通った。旧GPU転送完了の遅延イベントを注入した競合、切替と編集/保存/解析の同時要求、キャンセル直後の読込再試行は未実施。
- クラッシュ試験はQA用Playerのみで、混在ラベルを含む100,000点データを使った。異常終了後のcheckpoint復元および復旧後操作の記録は成功。ただしPlayer終了クラッシュが残り、製品の安定性としては合格にできない。
- runnerの復旧照合は最初、成功項目の連続順を仮定していた。照合を項目ごとの独立判定へ修正し、同じPlayerのstatus記録を再評価した。これで個別機能の成功とPlayer終了異常を独立して分類できる。修正後runnerで新しいPlayer runはまだ実施していない。
- 完了前の書込、Flush前後、checksum/完了marker前後、古いcheckpoint整理、適用途中それぞれへの障害注入は行っていない。破損・切断・checksum不一致、同名/同点数の別PLY、複数世代などのマトリクスも未実施。

## 4. UI・可搬性・負荷の未実施

次は **NOT_RUN**。実行していないものを合格扱いしない。

- UI解像度1024×768、1280×720、1366×768、1920×1080、2560×1440、および125%/150% Windows表示スケール。実画面試験は1600×900のみ。
- ノイズ除去、全てのエラー/進捗/復旧案内、パネル閉鎖・不正入力・背後点群へのクリック漏れの全UI経路。画面上のすべてのボタン操作。
- Python未検出、依存不足/破損、プロセス起動失敗、強制終了、timeout、大量stdout/stderr、不正結果ファイル。正常な隔離venvで起動できたことだけを確認した。別PCでの可搬性も未検証。
- ノイズ除去Player実行、全解析の各段階でのキャンセル、結果保存途中/反映直前の競合、古い処理から新しい処理への遅延完了通知注入。
- チェックポイント各書込段階の故障注入、復旧ファイル破損マトリクス、長時間耐久2時間、解析開始/キャンセル100回。
- CPU/RAM/GCはサンプルしたが、実GPU割当量は測れていない。UnityのGraphics Driver allocated値は0を返し、`graphicsMemorySize`はGPU容量値であり使用量ではない。2.2Mロード時のサンプル最大は約867 MB working set / 約1,379 MB private bytes。GPUメモリ余裕を立証した結果ではない。
- 実植物データ/実測標準物体を使った科学的妥当性。今回の径・球・C2Cは合成データ上の数値回帰のみ。
- 実HMDでのVR実機確認。PlayerログにHMD未接続を示す`XR_ERROR_FORM_FACTOR_UNAVAILABLE`がある。

実行artifactは `E:\pcwb-qa-20261009\FourthAuditFinal7\Artifacts` に保存。主要ファイルは`run_summary.json`、`player_process_results.json`、各`player_*.log`、各`*_resource_samples.json`、Unity test XML、hash manifest、`PlayerRuns\*.tsv`。この隔離QAディレクトリは調査用に保持し、削除していない。

## 5. 第3次検証から解消した範囲

第3次報告で未実施だった項目のうち、今回、Playerでの編集/分類/削除/復元/Undo/Redo、保存・再読込、解析の数値経路、茎径キャンセルと再試行、100回の点群切替、最大2.2M点の読込、復旧後の操作継続を合成データで追加確認した。

一方、第3次で未実施とされたUI解像度/DPIマトリクス、Python故障マトリクス、checkpoint各段階のfault injection、長時間耐久、実植物データ検証、Player終了クラッシュは解消していない。とくに今回もPlayer終了時クラッシュを再現したため、前回の復旧成功をもって安定稼働とは判定できない。

## 6. リリース判定

| 段階 | 判定 | 理由 |
|---|---|---|
| 研究室内試験 | **NOT READY** | 合成データで主要経路は試せたが、Player終了時のネイティブクラッシュが再現。原因修正・修正後再検証が必要。 |
| 学生実験 | **NOT READY** | 上記クラッシュに加え、表示解像度/DPI、エラー時の回復案内、Python環境故障時の操作性が未検証。 |
| 一般配布 | **NOT READY** | クラッシュ、別PC可搬性、VRハードウェア、実測精度、故障注入/耐久性が未解決・未実証。 |

## 7. 実行コマンドと再実行

QA runnerは本番プロジェクトにテストコードを注入せず隔離コピーへ試験用probeを配置する。実行例:

```powershell
cd E:\VR
pwsh -ExecutionPolicy Bypass -File .\PointCloudVR\tests\UnityIntegration\Invoke-FourthAudit.ps1 `
  -QaRoot E:\pcwb-qa-20261009\FourthAuditFinal7 `
  -IncludeLoadTiers -RunPlayerWorkflow -RunPlayerRace -RunPlayerLoadTiers -RunCrashRecovery
```

今回の全シナリオは最後まで成功していない。上記コマンドはPlayer終了異常も含めて非0終了を返し得る。再実行時は新しいQAルートを指定し、既存証拠を上書きしないこと。

## 8. 変更・Git

今回追加/整備した再利用可能な試験資材は `PointCloudVR/tests/UnityIntegration/`。検証後に復旧結果を誤判定するrunner照合も修正した。製品コード、Unity本番シーン/設定、実測PLY、`Assets/_Recovery/`、ライセンス関連ファイルはこの第4次検証のためには変更していない。今回の報告書は本ファイル。

ユーザー指定によりcommit/pushはしていない。作業ツリーには本件以前からの変更と未追跡ファイルが多数あるため、今回の試験資材/報告書だけをそれらから分離して扱うこと。

推奨commit message案（実施はしていない）: `Add fourth-stage Windows Player integration audit`

## 9. 次の対応

1. `UnityPlayer.dll`のアクセス違反を隔離Playerで再現し、クラッシュdumpとUnity/Windows診断情報を用いて原因を特定する。例外を握りつぶさず、最小再現を作る。
2. 原因に対応する失敗回帰テストを追加し、必要最小限の修正後、Playerを再ビルドして本報告のワークフロー/競合/負荷/復旧を再実行する。
3. クラッシュが解消した後、UIの残り解像度/DPI、Python異常系、checkpoint故障点、遅延イベント競合、耐久試験を順に実施する。
4. 最後に実植物PLYと実測寸法を持つ標準物体で研究上の数値妥当性を確認する。

**この報告の証拠は合成点群およびこのWindows PCに限定される。Playerクラッシュが残るため、研究室内試験・学生実験・一般配布のいずれも開始判定は不可。**
