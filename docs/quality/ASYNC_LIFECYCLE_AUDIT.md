# 非同期処理ライフサイクル監査

監査日: 2026-10-09。

## 契約と現状

| ID | 契約 | 実装状況 / 試験 |
|---|---|---|
| A1 | operationに所有者と一意ID | PointCloudProgressManagerは単一slotとmonotonic IDを持つ。重複TryStartを拒否。pure C# PASS |
| A2 | finish/cancel/errorは該当operationだけを変更 | PointCloudOperationはID照合。古いAのUpdate/FailをB開始後に注入し、Bが不変であることを確認 |
| A3 | 終了済みcallbackは後続に干渉しない | Progress manager層はPASS。PythonBridge/UI全コールバックの統合網羅はNOT_RUN |
| A4 | 子process/temp/output ownershipが明確 | downsample workdirはrun GUID、Python outputsはgeneration/run ID。Python cancel後のprocess残存・すべてのtemp cleanupはNOT_RUN |
| A5 | scene destroy/dataset switch後は結果を反映しない | 主要なselector、noise、stem等でrenderer/generation/revision checksを確認。全componentのOnDestroy barrier testはNOT_RUN |
| A6 | cancel受付と停止完了を区別 | CancellationTokenの要求とoperation CompleteCancelledを分離。process kill latency/timeout/late exitはNOT_RUN |

## 確認した代表経路

- 背景選択結果は開始時のSourcePointsとdataset generationを捕捉し、適用時に現在Rendererと照合する。
- NoiseFilterManagerはRenderer identity、DatasetGeneration、ContentRevisionで解析結果と履歴を束縛する。適用時のbuffer失敗ではlabel rollbackを試みる。
- Stem Diameterはoperation/run IDとimmutable output generationを照合し、stale cacheをoverlayとして描画しない。
- PointCloudProgressManagerの100-cycle seeded test（seed 20261009）はprogress manager状態のみをモデル化する。Editor全体のランダム操作を再現するmodel-based testではない。

## 競合・未実施

- A開始→cancel→B開始→A遅延stdout/error、PLY切替/再切替、scene destroy、Undo中解析完了のPlay Mode barrier tests。
- PointCloudLoaderの多重読み込み中、前requestのworker I/Oを停止できるかと古いGPU uploadの排除。
- Unity object解放とTask continuationのmain-thread affinityを全経路で確認。
- Python processのtimeout、stdout/stderr同時大量出力、キャンセル後の子孫process、プロセス残存。
- Downsampleはfinal PLY保存後にloaderへ再読込を依頼する。再読込失敗時も出力PLYは保持されるが、UI operation statusと点群表示の整合はUnity実機で確認が必要。

この監査は非同期契約の一部を純ロジックと静的追跡で確認した段階であり、統合レベルの競合安全性をPASSとはしない。
