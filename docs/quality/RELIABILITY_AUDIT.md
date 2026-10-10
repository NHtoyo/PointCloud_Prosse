# 第2次信頼性監査

監査日: 2026-10-09  
基準コミット: 2748339ce2734ca6e9ff49e725944e39f38b5117  
対象: 最新mainの公開内容だけでなく、その後のローカル差分・未追跡品質資料を含む作業ツリー。

## 結論

R01〜R15の危険経路を再追跡し、出力世代管理、点群/operation binding、PLYのimmutable snapshot、ノイズlabel rollback、scale出力分離、ログ/履歴上限、イベント例外分離を追加・確認した。復旧候補を無確認で適用する問題も見つけ、明示選択UIへ変更した。

Python 58 tests、compileall、隔離C# fault harnessは成功。今回の変更後Unity compile/Play Mode/Windows Playerは未実行。Unity Editor複数プロセスが稼働中のため現projectを別Editorで開いていない。よって研究室・授業・一般配布はNOT READY。

## 前回P0/P1修正の再確認

| 項目 | 再確認 | 判定 |
|---|---|---|
| PLY構文/有限値/途中切れ検査 | PointCloudPlyReaderと合成ASCII/LE/BE/破損payload tests | 純C# PASS、Unity loader統合はNOT_RUN |
| C2C厳密近傍 | ExactNearestNeighbor3Dを総当たりおよび遠方最近傍と比較 | 純C# PASS、単発benchmark 56 ms |
| Undo履歴のbounded化 | bounded history / dataset revision guard | helper PASS、全UI操作NOT_RUN |
| GPU読み戻し除去 | Render更新経路はCPU→GPU SetDataを使用し、同期GPU GetDataを通常経路で呼ばない構造 | 静的確認のみ。ProfilerでstallがないことはNOT_RUN |
| Python自動install | 既存方針の静的確認 | 今回の統合クリーン環境試験NOT_RUN |
| 起動scene | EditorBuildSettings差分は既存のローカル変更として保持 | Unity起動/Player build NOT_RUN |
| 既存安定性修正 | R01〜R15の追加追跡と専用harness | 各項目の限定状況はFAILURE_MODE_MATRIX参照 |

## 今回の重要修正と再現条件

- Downsample: _labeled等のsuffixから一時名を作ると入力と衝突し得る。GUID専用作業場と新規final pathへ分離。source hash維持、path collision、無関係フォルダcleanup拒否を純C#で検証。
- Noise history: 同点数の別RendererへのUndoとGPU SetData失敗を注入。renderer/generation/revision binding、rollback、GPU成功後のhistory確定を検証。
- Multi-file results: artifact途中/metadata/report/CSV/pointer更新失敗を注入。旧generationのpointer/hashが維持される。
- PLY export: count後にlive配列が変化する競合を模擬。開始時snapshotだけをcount/header/payloadへ使う。
- Recovery: 異常終了markerと正しいcheckpointがあれば自動適用していた。検証後pendingに保持し、復元/破棄の選択を求めるUIへ変更。候補確認中のcheckpoint自動上書きも止めた。コード差分のみでUnity UI検証は未実施。
- PointCloudRenderer: 新規点群のpoint/index ComputeBufferとCPU side arraysを先行構築し、GPU SetData完了後に現renderer stateを差し替える。候補resource初期化失敗の汎用harness試験はPASS、実Unity GPU/Renderer統合はNOT_RUN。切替時は旧・新GPU資源が一時共存し、2.2M点で概算約365MB（約348MiB）CPU/GPU合算のピークを見込む。
- Logs/Memory: bounded text buffer、Stem UI line/queue上限、Noise undo/redo各stack 16 MiBを追加・確認。全アプリmemory budgetは保証しない。
- Annotation history: stack 5段だけでは点数比例のメモリ上限にならないため、注釈Undo/Redoにも各16 MiB上限を追加。rollback snapshotも16 MiBで事前拒否し、GPU反映失敗時に変更を戻す。今回Unity compile未実施。

## 自動試験

- Python: 58 passed、13 dependency deprecation warnings。
- Python compileall: exit 0。
- C# pure logic fault harness: exit 0。
- operation model: seed 20261009、100 lifecycle cycles。ProgressManagerのみをモデル化。
- exact NN benchmark: 50k references + 5k queries、最終runはbuild 47 ms / query 10 ms / total 57 ms。過去の単発runを含め55–57 msで変動。
- 現差分のUnity compile、Play Mode、Player build、実PLY/基準器測定、crash/restart、複数解像度はNOT_RUN。

## 残る高リスク

1. 復旧失敗時のread-only modeがない。チェックポイントはlabelに限定し、自動修復可能と主張しない。
2. 今回の復旧確認UIとnoise renderer rollbackはUnity compile/Play Modeで動作確認していない。
3. 全async consumer、点群A/B切替、OnDestroyと遅延callbackをUnity barrier testで網羅していない。
4. 3M点級でCPU/GPU/RAM/GC/出力時間、checkpoint SHA I/Oを測っていない。静的見積りはDATA_INTEGRITY_AUDIT参照。
5. disk full、permission、locked file、process kill、symlink/hardlink/reparse pointは実Windows故障注入していない。
6. 茎径/球径/距離の既知真値に対する計測精度は評価していない。

詳細: FAILURE_MODE_MATRIX.md、FAULT_INJECTION_RESULTS.md、DATA_INTEGRITY_AUDIT.md、ASYNC_LIFECYCLE_AUDIT.md、RECOVERY_DESIGN.md、ENDURANCE_TEST_REPORT.md、RELEASE_READINESS.md。
