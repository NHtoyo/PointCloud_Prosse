# 故障注入結果

試験日: 2026-10-09。PASSは隔離Python/C# harnessでの対象範囲に限定する。Unity Editor/Playerの故障注入は未実施。

## 実行済み

| ID / 注入 | 期待値 | 実結果 |
|---|---|---|
| Python noise: binary writer途中でOSError | old current pointerと全old artifactを保持 | PASS。前世代run IDとartifact bytes/hashが不変 |
| Python noise: first array、metadata、removal report write failure | 不完全generationをcurrentにしない | PASS。旧世代がcurrent |
| Python generation: pointer replace failure | old pointerを保持 | PASS |
| Python noise: concurrent writer | runが分離し、currentが完全な一世代を指す | PASS。各manifest/artifact hashを照合 |
| Stem CLI: CSV生成を失敗 | JSON/CSV/画像の旧世代を混ぜず維持 | PASS。current pointerと旧artifact bytes不変 |
| Windows fsync file handle | file flushが成功し、generationを確定 | PASS。read-only handleのbad fdを検出しr+bで回帰 |
| C# NoiseFilterManager: GPU buffer updateを一度失敗 | labelsを元へ戻しUndo履歴をpublishしない | PASS。rollback buffer更新成功経路 |
| C# operation A終了後にBを開始、Aから遅延Update/Fail | Bのprogress/statusを変えない | PASS |
| C# downsample cleanupへ作業フォルダ以外を渡す | 無関係ファイルを削除しない | PASS。例外拒否と保護ファイル存在を確認 |
| C# PLY export中にsource labels/positionsを変更 | count/header/payloadは開始時snapshotで一致 | PASS。5000 vertexと最終XYZを再読込 |
| C# session recovery: wrong source hash/count、破損payload | 不一致checkpointを返さずPLY不変 | PASS |
| C# event dispatch listener exception | 後続listenerも呼び出す | PASS |
| C# bounded log/history | 上限を超えて保持しない | PASS |

実行:

- python -m pytest -q — 58 passed, 13 warnings.
- python -m compileall -q . — exit 0.
- dotnet run --project tests/PlyExportValidation/PlyExportValidation.csproj — exit 0.

## 未実行の注入

| ケース | 状態 | 理由 / 安全な次手 |
|---|---|---|
| disk full / readonly directory / file lock / rename failure on Windows | NOT_RUN | 実ファイルシステム障害を隔離VM/専用ボリュームで再現する |
| process kill at every byte/write/flush/rename boundary | NOT_RUN | temporary QA profileと合成データのWindows Playerを用意 |
| Python no-output, hang, stderr flood, child process remains after cancel | NOT_RUN | deterministic subprocess stubとprocess-tree assertionを追加 |
| GPU ComputeBuffer allocation/SetData failure in actual Unity | NOT_RUN | Unity native failureは未再現。候補resourceの初期化失敗を純C# helperで注入し、旧resourceがpublishされず候補だけ解放されることはPASS |
| PointCloudLoader A/B delayed load and scene destruction | NOT_RUN | PlayMode barrier test |
| corrupt checkpoint UI/read-only safe mode, recovery prompt acceptance | NOT_RUN | 新しいrecovery promptは今回ソース追加のみ。Unity compile/UI testが必要 |
| symlink/hardlink alias, reparse points, case-insensitive path aliases | NOT_RUN | disposable NTFS directoryでfile identityを確認 |
| multiple resolution error/recovery dialogs | NOT_RUN | Player screenshots/manual UI passが必要 |

障害が起きたかでなく、以前の正常状態と元PLYが維持されたかを合否条件とする。

## 第3次 Player実行による更新（2026-10-09）

「process kill」全般がNOT_RUNという上表の記録は第2次時点のもの。隔離Windows Playerに限り、完了済みcheckpoint保存後の意図的異常終了と再起動を実施した。marker検出、同一source hash・点数・payload checksum確認、候補生成、100,000 label適用、元合成PLY hash不変を確認し、基本のcrash/restart経路はPASS。ログは[`player_crash_write_final.log`](evidence/third_audit/player_crash_write_final.log)と[`player_crash_verify_final.log`](evidence/third_audit/player_crash_verify_final.log)。

これはcheckpointのatomic書込み途中、適用途中、GPU SetData失敗などを故意に発生させたものではない。GPU実障害、全ファイル障害、Python hang/大量出力/孤立processもNOT_RUN。全体をPASSとはしない。詳細は[第3次実行報告](THIRD_AUDIT_EXECUTION.md)。
