# PointCloud_Prosse 作業5/7 — 故障注入・復旧最終検証

実施日: 2026-10-10 (JST)
総合判定: **FAIL**（旧Windows Player終了時ネイティブクラッシュの原因未特定。今回の意図的killと復旧成功は、その解決を証明しない。）

## 範囲と実行環境

- 対象ソース: `E:\VR\PointCloudVR` の作業時点の未コミット作業ツリー。
- 隔離QA: `E:\pcwb-qa-20261010\FailureRecoveryTask5-20261010-041639`。Player試験データはテスト生成の100,000点`sample.ply`等のみ。
- `Invoke-FourthAudit.ps1 -RunCrashRecovery`で隔離コピー、Python/Unityテスト、Windows Playerを再ビルドし、復旧writer/verifyを1組実施。Playerを反復強制終了する試験は行っていない。
- ユーザーのUnity Editorは操作・終了していない。終了時にもEditor PID 30824とImport Worker 21068 / 15860を確認。
- PlayerのSHA-256: `PointCloudVR_QA.exe` `16726f6bc281a100105ae80f982a21a9332c870340bcd2e1d89bd82b76dd33c7`、`UnityPlayer.dll` `4c142dd3d8237cd3537021c75904ff5df7e3c37796fff110bdefa925a636fe6b`、`Assembly-CSharp.dll` `283629a00352639d00cc4df7548f0c395dd415dd649cd8e9f390fd6a78ce1d35`。
- Unityソース・設定・UnityIntegration資材の最終manifest: `Artifacts/source_snapshot_sha256_final.tsv`、SHA-256 `DE9DC6AE0F5B090EB39EB64A0F88B012B715CD9B334946C1506424E6B26BABD9`。
- 追加したC# fault assertionのソースSHA-256: `Program.cs` `22f3fbe535ca38b207a3fabb042a22434ae02e8bd076c357dd312e2575b3c228`。Python retry assertionのSHA-256: `test_output_generation_transactions.py` `e68e5fa1afd08216df8b7cbb63aaf0335778f2df0a9e2229a9ce664f9899870b`。
- エビデンス一式は上記QAルートの`Artifacts`に保存。Windows Application Error / WERで、このQA Playerに該当するイベントは見つからなかった。Player後に該当QAプロセスは残っていない。

## 実行結果

| 区分 | 試験・注入 | 結果 | 試験の性質 |
|---|---|---|---|
| C#既存harness | 復旧元PLYのSHA不一致、同名別ファイル、点数不一致、切断payload、checksum破損 | **PASS**。適合しない復旧データを拒否 | production storeをリンクした.NETテスト。Player UIではない |
| C#実ファイルI/O | 開いた復旧checkpointをロックしてatomic置換を拒否 | **PASS**。旧checkpointのバイト列・ラベル・revisionが保持され、一時ファイルが残らず、ロック解除後の再保存に成功。元PLY hash不変 | production `PointCloudSessionRecoveryStore.WriteAtomic` + 一時ファイル上のWindows共有ロック |
| C#実ファイルI/O | 開いた最終PLYをロックしてatomic置換を拒否 | **PASS**。旧PLYがbyte-for-byteで保持され、再読込可能。一時ファイルなし。同じ保存先への再試行と別保存先への保存が成功 | production `PlyExportService.Write` + 一時ファイル上のWindows共有ロック |
| C#実書き出し経路 | PLY書込中のprogress callbackから例外を注入 | **PASS**。既存finalを維持し、一時ファイルを清掃。同一・別の保存先で再試行成功 | production PLY writerの実ファイル出力を使用。OS障害の模擬ではない |
| Python既存fault harness | generationの配列・metadata・report書込、pointer置換で例外。並行writer | **PASS**。旧current世代と成果物を保持。pointer置換失敗後の正常retryも成功。pytest全体 **58 passed** | production Python writerを`unittest.mock`で失敗させるbackend試験。Unity Player試験ではない |
| C#テストダブル | ノイズcommit時のbuffer更新例外、resource初期化例外、古いprogress通知、operation取消等 | **PASS**（既存harnessのassertion） | fake renderer/resource等。実GPU障害・Player統合とは扱わない |
| Unity EditMode / PlayMode | 既存テスト | **PASS**、2/2・3/3 | Unity batch test |
| Windows Player復旧 | 正常に完成したcheckpoint保存後、QA probeが自プロセスを意図的kill。次のPlayer起動で検証・適用 | **PASS**。writer exit `-1`はテスト指定のkill。verify Player exit `0`。100,000点の混在ラベル復旧、`source_unchanged=True` | 実Player / 合成PLY。書込途中のクラッシュではない |
| 復旧後の継続操作 | 分類、削除・復元、保存、点群切替、保存PLY再読込 | **PASS**。保存後の90,909点、ラベル、座標を再確認 | 同一verify PlayerのQA probe。本番UIのマウス操作ではない |

Python `compileall`は成功。runner全体と新規Windows Playerのビルドも終了コード0。検証ログにOpenXRの`XR_ERROR_FORM_FACTOR_UNAVAILABLE`とUnity batch側のlicensing handshake警告が出たが、EditMode/PlayModeのXMLはPassedで、Player復旧処理も完了した。HMD接続での試験ではない。

Player復旧verifyのstatus:

```text
unclean_marker=True; checkpoint_read=True; labels=True; candidate_pending=True;
applied=True; applied_labels=True; source_unchanged=True;
continuation=passed=True; classified=True; delete_restore=True; saved=True;
switched=True; loaded_saved_cloud=True; reloaded_labels=True;
reloaded_positions=True; reloaded_points=90909
```

旧通常終了クラッシュ`0xC0000005` / `0xC000041D`は根本原因未特定のまま。今回のwriterの`-1`は意図的killであり、この2例外の再現・修正とは数えない。旧問題の判定は[PLAYER_EXIT_CRASH_FIX.md](PLAYER_EXIT_CRASH_FIX.md)の**UNRESOLVED / FAIL**を維持する。

## 未実施・判定

| 状態 | 項目 | 理由・境界 |
|---|---|---|
| **NOT_RUN** | Player内のファイル作成不可、ディスクfull、実flush失敗、manifest/完了状態更新失敗、最終一時ファイル削除失敗 | C#のロック置換失敗とPython writerのmock故障は試験したが、これらの全境界をUnity Playerで注入していない |
| **NOT_RUN** | checkpointのデータ書込中、flush前後、checksum確定中、置換直前・直後の強制終了 | 現行の専用probeがkillするのはcheckpoint完成後のみ。Playerの異常終了を繰り返さない条件に従い、途中kill用注入点は追加していない |
| **NOT_RUN** | Player経由のPython実行ファイルなし/import失敗、spawn失敗、nonzero、timeout/hang、途中kill、stdout/stderr大量、孤立子process、欠落/不正/NaN/点数不一致結果 | Python backendのgeneration書込故障とは別。PythonBridgeに決定的な子プロセスfault adapterがなく、今回はPlayer経路へ作り込んでいない |
| **NOT_RUN** | UnityのComputeBuffer/GraphicsBuffer確保、SetData、GPU転送、Octree構築中のnative failure | 既存C# fakeのrollback試験のみ。実GPUドライバを故障させる注入はしていない |
| **NOT_RUN** | 読込・保存・noise・downsample・sphere・C2C・接続探索等の各キャンセル段階、A→cancel→B、scene破棄、Undo中の遅延完了通知 | 今回のPlayerではキャンセル競合を網羅していない。C# operation状態テストや前段の茎径キャンセル試験をPlayer全機能の証明として流用しない |
| **NOT_RUN** | 復旧不一致時のPlayer UI安全退避、復旧適用途中のGPU失敗、複数checkpoint世代、ディスクfull、復旧後再クラッシュ | 元PLY identity等の拒否はC# harness、正常候補の復旧UI経路はPlayerで確認。全組合せは未確認 |
| **BLOCKED** | 通常終了ネイティブクラッシュの原因判定 | 旧実行のnative例外はあるが、この回では通常クラッシュを再現していない。今回の意図的kill後に該当WER/Application Errorはないものの、原因特定や不存在証明にはならない |

通常Unityアプリへ故障注入機能を追加していない。Player内で故障させるには、Unity版とテスト専用buildを明確に分けたfault adapterが必要だが、今回はUnity native終了問題が残り、必要性と安全境界を確認できる対象に限定した。

## 変更ファイル

- `PointCloudVR/tests/PlyExportValidation/Program.cs`: Windowsファイルロックによる実置換失敗、旧データ保持、一時ファイル清掃、retry、同名別PLY、切断checkpointの検査を追加。
- `PointCloudVR/python_backend/tests/test_output_generation_transactions.py`: pointer置換故障後のgeneration retry検査を追加。
- `docs/quality/FAILURE_RECOVERY_FINAL.md`: 本報告書。

製品C# / Python実装、Unity本番設定、実測PLY、`Assets/_Recovery/`、ライセンス資料は変更していない。commit / push / stageは行っていない。

### 再実行したコマンド

```powershell
dotnet run --project tests/PlyExportValidation/PlyExportValidation.csproj --configuration Release
& '<isolated-venv>\Scripts\python.exe' -m pytest tests -q
& '<isolated-venv>\Scripts\python.exe' -m compileall -q .
& .\tests\UnityIntegration\Invoke-FourthAudit.ps1 -QaRoot '<new-QA-root>' `
  -PythonVenvSource '<existing-isolated-venv>' -SkipPythonInstall -RunCrashRecovery
```
