# 障害注入テスト計画

実際に実行した注入と結果は[FAULT_INJECTION_RESULTS.md](FAULT_INJECTION_RESULTS.md)を正とする。この文書は、未実施の統合試験を追跡する計画である。

## 実装済みisolated harness

dotnet run --project tests/PlyExportValidation/PlyExportValidation.csproj は純C# test doublesと一時ディレクトリを使う。Python testsはunittest.mockでwriter/pointer failuresを注入する。両方ともUnity GPU/scene/processの統合を代替しない。

Covered: operation stale notification, duplicate operation, noise buffer update failure/rollback, history source binding, PLY count snapshot, corrupt recovery data, downsample path cleanup guard, immutable output generation failure, pointer replace failure, concurrent Python writers, bounded logs/history, event listener failure.

## 優先して追加する試験

| 優先 | 注入 | 安全な環境 | 合格条件 |
|---|---|---|---|
| P0 | 復旧candidate pending中にpoint cloud switch / recovery writeが競合 | PlayMode合成PLY | stale候補を適用しない。旧checkpointが確認前に上書きされない |
| P0 | recovery GPU apply失敗、checkpoint破損、権限拒否 | disposable Windows Player | CPU labelを戻し、元PLY不変、警告・再試行/破棄が操作可能 |
| P0 | downsample出力後にPLY load failure | disposable Player | final PLY保持、UIが成功と誤表示しない |
| P0 | Python process hang/cancel/stdout flood/child残留 | deterministic subprocess stub | UI復帰、対象process tree終了、bounded memory |
| P1 | Disk full、read-only、locked file、rename fail | disposable volume/VM | 旧generationと元PLYが維持される |
| P1 | Unity OnDestroy中のTask完了、dataset A/B delayed callback | PlayMode deterministic barrier | 解放済みobject・別点群へ適用しない |
| P1 | GPU buffer allocation/SetData partial exception | QA graphics device | label rollback、明確なrecoverable state |
| P2 | symlink/hardlink/reparse/case-only aliases | disposable NTFS folder | input/work/output identityが重ならない |
| P2 | 複数解像度でmodal操作 | Windows Player | 全テキスト・ボタン可視、背後操作不可 |

実行時はUnityログを消して成功扱いにせず、operation状態、元PLY hash、manifest、出力hash、点数、GPU表示を記録する。Assets/_Recoveryやユーザー実PLYを障害注入対象にしない。
