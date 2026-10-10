# 耐久・性能試験報告

試験日: 2026-10-09。実測はこのPCでの単発測定。危険な終了試験は行っていない。

## 実行済み

| 試験 | 条件 | 結果 |
|---|---|---|
| Python backend | python -m pytest -q | PASS: 58 passed, 13 warnings, 7.94 s。警告はMatplotlib依存のPyParsing deprecation |
| Python構文 | python -m compileall -q . | PASS, exit 0 |
| C#純ロジック/fault harness | dotnet run --project tests/PlyExportValidation/PlyExportValidation.csproj | PASS, exit 0。noise transaction、PLY snapshot、recovery store、operation lifecycle等 |
| deterministic operation sequence | seed 20261009, 100 cycles、最大10 progress/cancel events | PASS。PointCloudProgressManagerの状態モデルのみ |
| exact nearest neighbor benchmark | 50,000 references / 5,000 queries | 最終run: build 47 ms、query 10 ms、total 57 ms。過去runを含め55–57 msで変動する単発microbenchmark |
| Unity Editor | Unity 6000.4.7f1の複数プロセスが既存起動中 | 今回の差分に対するUnity compile/Play ModeはNOT_RUN。Editorを終了せず、同じprojectへ別Editorを起動していない |

以前の監査文書にある隔離コピーbatch compile結果は過去の差分に対する履歴であり、今回追加した復旧UI等のcompile証拠ではない。

## 実施していない耐久・統合試験

- 実PLYで100回以上のload/switch、解析開始/cancel、ランダム編集操作。
- hours-long endurance、数百万点でのpeak RAM/VRAM、GC、CPU、disk throughput。
- Windows Player build、クリーンなユーザープロファイル、VR headset、GPU fallback。
- 実Unity UIでの復旧確認、Undo/Redo、点群切替と遅延callbackの競合。
- Unityを異常終了させた後のrestart/recovery。現Editorを強制終了する試験は行わない。
- 1280×720等のUI視認性・入力遮断。

したがって、56 msの近傍検索測定や純C#状態モデルの100 cycleを、アプリ全体の応答性・耐久性・クラッシュ耐性に外挿しない。

## 第3次検証で追加した実行確認（2026-10-09）

この報告の上記は第2次監査時点の記録。第3次検証では、隔離Windows x64 Playerで合成PLY 100,000点の読込とOctree準備、PlayerからのPython依存確認、およびテスト専用Playerの異常終了・再起動復旧を実施した。詳細は[`THIRD_AUDIT_EXECUTION.md`](THIRD_AUDIT_EXECUTION.md)および`evidence/third_audit/`を参照。

クラッシュ復旧は完了済みcheckpointを再起動後に読み、確認候補として提示し、テストbootstrapから適用する範囲で成功した。合成元PLYのSHA-256は前後一致。復旧UIの人手操作、書込み途中クラッシュ、その他の復旧境界条件は未試験である。

大規模点群・100/1000回反復・長時間耐久、メモリ/VRAM/GC計測、VR headset実機、および指定解像度のUI試験は今回も未実施。従って本試験はアプリ全体の耐久性や授業利用可能性を示すものではない。
