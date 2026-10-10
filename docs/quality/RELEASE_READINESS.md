# リリース判定

監査日: 2026-10-09。基準コミット: 2748339ce2734ca6e9ff49e725944e39f38b5117。判定は現ローカル作業ツリーと今回の検証範囲に対するもの。

## 提供区分

| 区分 | 判定 | 根拠 |
|---|---|---|
| 開発者のローカル試作 | 条件付きPASS | Python 58 tests、compileall、C# fault/state harnessが成功。バックアップ済みデータ上で結果照合し、現状コードを開発者が検証する限定用途。今回のUnity差分は未compile |
| 研究室内試験 | NOT READY | Unity compile/Play Mode、実PLYの入出力照合、scale/径の真値確認、クラッシュ復旧、実操作競合が未実施 |
| 学生・授業利用 | NOT READY | Unity受入試験、誤操作/復旧UI、読み取り専用fallback、複数解像度、授業PCのクリーン環境が未確認 |
| Windows EXE一般配布 | NOT READY | Windows Player、Python未導入/オフライン、依存物と権利、GPU障害、クラッシュ復旧、実データ耐久試験が未完了 |

## PASSとした範囲

- Python: python -m pytest -q — 58 passed, 13 dependency deprecation warnings。
- Python: python -m compileall -q . — PASS。
- C#純ロジック: dotnet run --project tests/PlyExportValidation/PlyExportValidation.csproj — PASS。Unity Editor/Playerのcompileは含まれない。
- Python世代保存では、失敗した次runで前世代pointerとartifact bytesが保持される故障注入を実施。
- PLY exportでは一貫スナップショット、元データ不変、header/payload count一致を純C# harnessで確認。

## リリース前の必須ゲート

1. 現行差分をUnity 6000.4.7f1でcompileし、EditMode/PlayModeを実行。Editor稼働中の既存projectへ別Editorを重ねない。
2. 隔離Windows PlayerでPLY load/switch、選択/削除/undo、noise preview/commit、analysis cancel/retry、保存失敗後の操作復帰を実機確認。
3. 復旧確認UIの復元/破棄、source hash/point count mismatch、GPU update失敗rollbackを検証し、異常終了→restartも使い捨てデータで試験。
4. 復旧失敗時のread-only fallbackまたは明示的な安全方針を決定・実装。
5. 実PLYと基準物体でscale/測定結果を照合し、編集・出力・再読込後のXYZ/RGB/label/hashを比較。
6. 1280×720、1366×768、1920×1080、2560×1440でメッセージ、ボタン、画面入力を確認。
7. 数百万点規模のpeak RAM/VRAM/GC/CPU/応答時間を記録し、Python子プロセス大量ログとcancelの残存を確認。
8. 権利者、sample.ply等の配布データ、第三者noticeをdocs/legalの公開前条件に従い確定。

未実行項目はPASSにしない。純C#/Python試験の成功はUnity統合や測定科学的妥当性を保証しない。

## 第3次実行検証による更新（2026-10-09）

上記のうちUnity compile/PlayMode、Windows Player起動、Python実行、合成PLYの異常終了後復旧は隔離コピー上で限定的に実行し、結果は[`THIRD_AUDIT_EXECUTION.md`](THIRD_AUDIT_EXECUTION.md)に記録した。Unity compile/EditMode/PlayMode、Player smoke、100,000点のcheckpoint recoveryがPASSしたため、これらを「未実施」とする上記の記述は履歴であり、現状判定は以下の通り更新する。

- 開発者のローカル試作: 条件付きPASS。隔離Windows PlayerとローカルPython環境の限定smokeは成功。ただし実UIでの主要ワークフロー未確認。
- 研究室内試験、学生・授業利用、Windows EXE一般配布: NOT READYのまま。全操作・競合、UI解像度、GPU故障、実測数値、長時間/大規模負荷、別PC環境は未確認。
- 第3次実行検証は合成PLYで行い、実測点群の安全性・科学的妥当性を検証したものではない。
