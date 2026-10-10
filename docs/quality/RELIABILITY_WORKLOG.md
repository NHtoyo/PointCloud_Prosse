# 信頼性監査作業ログ

日付: 2026-10-09
基準: 2748339ce2734ca6e9ff49e725944e39f38b5117の後のローカル変更を含む
Git操作: commit/push/reset/restoreは実施しない（依頼条件）。

## 実施したこと

- 開始時のgit status/diff/statと未追跡項目を確認。ライセンス関連ファイルとAssets/_Recoveryは保持し、対象外データとして扱った。
- PointCloudDownsampleService、PlyExportService、Python result writer、noise history、progress operation、stem cache、scale writer、recovery store/loader/UIを再読。
- Python 58件、compileall、隔離C# harnessを現差分で実行。
- 既存Unity Editorプロセスが複数動作中であることを確認。稼働中Editorを止めず、今回差分のUnity compile/PlayMode/Player buildは行わなかった。

## 発見と変更

| ID | 発見 | 対応 | 検証 |
|---|---|---|---|
| R-01/R-02 | Downsample input/output/tempが名前規則で衝突し得る | GUID別作業フォルダ、精度を保持したparameter token、新規final path、作業フォルダ削除guard | 純C# path/hash/削除guard試験PASS |
| R-03/R-04 | 解析結果群を先に消す/複数runが混在する経路 | runごとのimmutable generation、artifact manifest/hash、current pointer置換 | Python fault/concurrency tests PASS |
| R-05 | Noise historyが異なるdatasetへ残り、GPU更新前にCPU labelsが確定する経路 | renderer/generation/revision binding、同期失敗時のlabel rollback、成功後だけ履歴確定 | 純C#故障注入PASS |
| R-07 | PLY countと書込対象が可変配列の別走査 | compact immutable snapshotからheaderとpayloadを生成 | 書出し中の元配列変更注入PASS |
| R-09/R-10 | Stem cache同名source衝突・fingerprint不明結果の誤表示 | 正規化source path hash、未知fingerprintはstale、stale overlayを隠す | C# cache tests/source assertion PASS |
| R-11/R-12 | scale save時の逆数rollbackとsuffix依存 | 別PLYへatomic出力してからloader adopt、markerのみ判定 | pure C# source/output不変テストPASS |
| R-13/R-14 | 大量ログ蓄積とevent listener例外伝播 | bounded log buffers、listenerごとのsafe dispatch | C# helper tests PASS |
| R-16 | 点群差替えでComputeBuffer確保/SetDataが失敗すると、既存点群参照の切替後に例外となる | CPU cache/bounds/annotationとpoint/fallback-index ComputeBufferを先に準備し、SetData成功後に差替え。octree開始失敗は点群読込失敗にせず全点fallback表示 | candidate resource初期化失敗注入はC# harness PASS。実Unity ComputeBuffer/Renderer試験NOT_RUN |
| REC-01 | valid checkpointをユーザー確認なしで自動適用 | 検証後pendingに保持し、復元/破棄の確認UIを追加 | コードレビューのみ。Unity compile/UI test NOT_RUN |
| R-15 | Annotation履歴が5件数だけで点数比例のRAM上限がなかった | annotation/noise historyを各stack 16 MiBにし、transaction snapshotも16 MiBで事前拒否。GPU更新失敗時rollback | BoundedHistory/noise純C#確認。PointCloudEditorのUnity統合compileはNOT_RUN |

## 実行コマンドと結果

- PointCloudVR/python_backend: python -m pytest -q — 58 passed, 13 dependency deprecation warnings, 7.94 s。
- PointCloudVR/python_backend: python -m compileall -q . — exit 0。
- PointCloudVR: dotnet run --project tests/PlyExportValidation/PlyExportValidation.csproj — exit 0。
- 同C# harnessの最終NN microbenchmark: 50,000 references / 5,000 queries、build 47 ms、query 10 ms、total 57 ms（単発。過去runも55–57 msで変動）。
- `git diff --check` — whitespace errorなし。既存ファイルのLF/CRLF正規化警告のみ。
- docs/quality・docs/legalのMarkdown相対リンク22ファイルを確認し、欠落なし。
- 最終git statusを確認。commit/push/reset/restoreは未実施。

## 未実施・次の作業

1. Unity 6000.4.7f1で今回差分をcompileし、Edit/Play Mode testsを行う。現在のユーザーEditorを閉じずに実施可能な隔離手順を用意する。
2. 復旧確認UIの4解像度表示、復元/破棄、GPU failure rollbackをPlay Modeで確認。
3. Windows Playerで実PLY、Python失敗/cancel、点群切替中の遅延結果、保存失敗後の復帰を試験。
4. 合成データの隔離Playerでcrash/restart復旧、checkpoint書込途中kill、read-only fallback方針を確認。
5. symlink/hardlink、敵対的な同時出力、disk full/locked file、実プロセス大量ログをWindows上で試験。
6. 大規模点群のpeak RAM/VRAM/GC/CPUと入力遅延を実測。今回は静的な上限見積りのみ。

## 完了判定

コード修正と純Python/C#回帰は完了。包括的な信頼性監査・配布準備は未完了。授業/研究室/一般配布はNOT READY。履歴にある内容を検証済み範囲以上に解釈しない。

## 第3次実行検証追記（2026-10-09）

第3次検証では前項作成時点から後のコードを対象にするため、未コミット差分と未追跡ファイルを含めたSHA-256一覧を作成し、隔離Unity projectとのファイル一致を確認した。詳細な証拠とNOT RUN項目は[`THIRD_AUDIT_EXECUTION.md`](THIRD_AUDIT_EXECUTION.md)を参照。

- Unity 6000.4.7f1の隔離batch compileで複数のC# compile errorを発見。`PointCloudRenderer.cs`の初期化/例外型/全index fallbackと`StemDiameterUI.cs`の`AppendLine`呼び出しを修正した。修正後compile成功。その他の製品コード変更はなし。
- 修正済みコードでEditMode 2件、PlayMode 3件を実行し全件成功。Python全58 tests、compileall、.NET fault/state harnessも再実行して成功。
- 隔離Windows x64 Playerをbuildして合成PLY 100,000点とOctreeをロード。Player内Python bridgeのNumPy/SciPy/Open3D検証が成功。
- テストPlayerのみを強制終了し、再起動後に異常終了markerとチェックポイントを検証。復元候補を作り、明示的なApplyメソッドで全ラベル復元。合成元PLY hashは不変。
- 既存Editorは終了していない。実データは使っていない。試験用Player/プロジェクトのみを使用した。

この試験はPhase 2の通常UI操作、Phase 3の中断点ごとのcrash injection、Phase 4のGPU/ファイル/Python全障害、Phase 6〜8の長時間・複数解像度・科学的実データ試験を完了したものではない。これらはNOT RUNで、リリース判定はNOT READYのまま。
