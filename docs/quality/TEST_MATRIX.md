# テストマトリクス

## 実行済み

| 領域 | コマンド・方法 | 結果 |
|---|---|---|
| Python backend | python -m pytest -q（PointCloudVR/python_backend） | PASS: 58 passed、13 deprecation warnings、7.94秒 |
| Python syntax | python -m compileall -q .（python_backend） | PASS、exit 0 |
| C# pure logic/fault harness | dotnet run --project tests/PlyExportValidation/PlyExportValidation.csproj | PASS、exit 0 |
| deterministic operation cycles | seed 20261009、100 cycle | PASS。ProgressManager状態に限定 |
| C2C KD-tree | 50,000 references / 5,000 queries | 最終run: build 47 ms + query 10 ms = 57 ms、単発（run間で55–57 ms） |
| Git whitespace | git diff --check | PASS、whitespace errorなし（既存のline-ending正規化警告あり） |

C# projectはxUnit等ではなく、失敗時に非0 exitするisolated console harnessである。dotnet runを使う。

## C#回帰とfault coverage

- ASCII CRLF、property order、点数上限、binary little/big endian、NaN、truncated PLY、XYZ欠落。
- PLY出力snapshot中のsource mutation、header/payload count、existing output preservation。
- SelectedNonDeletedのmanual deleted/noise hidden判定。
- Noise preview/commit/undo binding、同点数/異点数の別renderer、GPU更新失敗rollback。
- PointCloudProgressManagerの古いA通知、重複開始、cancel、100 deterministic cycles。
- Session recoveryのsource SHA/count/checksum不一致と元PLY不変。
- Downsample path isolation、精度の近いvoxel、同一run衝突回避、source SHA維持、無関係dir cleanup拒否。
- OutputGenerationStore manifest/artifact hash検証、Stem cache path/source fingerprint分離。
- bounded text/history、safe event dispatch、nearest-neighbor総当たり比較。
- 点群差替え候補resourceのSetData失敗を注入し、旧resource参照が維持され候補だけ解放されることを検証。

## Python故障注入

- noise resultのfirst/mid binary write、metadata、removal report失敗。
- stem CSV出力失敗。
- current pointer replace失敗。
- concurrent isolated output writers。
- すべてで前回正常generationのpointer/bytes/hash保持を検査。

## 未実施

| 領域 | 状態 | 理由 |
|---|---|---|
| Unity current diff compile | NOT_RUN | 複数Unity Editor process稼働中。今回のUI変更をcompileした証拠はない |
| Unity Edit/PlayMode | NOT_RUN | 今回差分は対話実機確認が必要 |
| Windows Player build | NOT_RUN | Player build未実行 |
| UI 1280×720等 | NOT_RUN | GameView/Player screenshotと操作を未取得 |
| 実PLY・物理真値 | NOT_RUN | データと独立計測器の受入試験なし |
| 実disk full/locked file/process kill | NOT_RUN | 隔離Windows fault labが必要 |
| Unity GPU fail/OnDestroy race | NOT_RUN | PlayMode/native graphics harnessが必要 |
| 大規模耐久/peak memory | NOT_RUN | 固定データ・PCスペック・計測器の試験なし |

## 第3次検証による更新（2026-10-09）

上のUnity/Player未実施表は第2次監査時点の履歴である。第3次の隔離コピー上の実行結果:

| 領域 | 実行方法 | 最新結果 |
|---|---|---|
| Unity 6000.4.7f1 compile | 隔離Unity projectをbatch compile | PASS。初回compileで発見したC#エラーを2ファイルで修正後に成功 |
| Unity EditMode | `-runTests -testPlatform editmode` | PASS: 2 passed、0 failed |
| Unity PlayMode | `-runTests -testPlatform playmode` | PASS: 3 passed、0 failed。scene起動、recovery store roundtrip、Python bridge依存確認 |
| Windows x64 Player | BuildPipeline、Player起動 | BuildReport success、errors=0、warnings=2。合成PLY 100,000点、Octree、Python依存確認 |
| 異常終了復旧 | 隔離Playerを強制終了後に再起動 | PASS: 100,000 label checkpointを検証候補として提示し、適用後の全label一致。元合成PLY hash不変 |
| 現行Python/C#純ロジック | pytest、compileall、dotnet harnessを再実行 | Python 58 passed、compileall PASS、C# harness PASS |

証拠: [`第3次実行報告`](THIRD_AUDIT_EXECUTION.md)、[`evidence/third_audit`](evidence/third_audit)。実UI全操作、GPU実故障、復旧途中のcrash boundary、大規模・長時間耐久、指定解像度UI、実測科学的照合は引き続きNOT_RUN。
