# 第3次品質検証 実行報告

実施日: 2026-10-09
基準コミット: `2748339ce2734ca6e9ff49e725944e39f38b5117`
判定対象: 基準コミット後の既存未コミット変更を含むローカル作業ツリー。commit/pushは行っていない。

## 対象スナップショット

- 検証開始前のGit差分・未追跡ファイルを保った状態から、Unityプロジェクトの隔離コピー `E:\pcwb-qa-20261009\PointCloudVR` を作成した。GitHubのmainだけを検証したものではない。
- スナップショット一覧は [`latest_source_snapshot_sha256.tsv`](evidence/third_audit/latest_source_snapshot_sha256.tsv)、コピー側一覧は [`latest_qa_copy_sha256.tsv`](evidence/third_audit/latest_qa_copy_sha256.tsv)。ソース一覧のSHA-256は `F09FBBC6862BDBB21BD828452BB2EC7C2D4FAA14AAB688D6A3EFF54143A3076F`。
- Unityプロジェクト内の対象198ファイルを比較し、198一致・不一致0。`Assets/_Recovery/` の8ファイルもコピーに含めた。試験用PLYは `E:\pcwb-qa-20261009\PointCloudData\sample.ply` の合成データで、実データ `E:\VR\PointCloudData` は使っていない。
- Unity Test Framework、テストアセンブリ、クラッシュ注入用スクリプト、ビルド用スクリプト、Player用Python環境は隔離コピーだけに置いた。元のUnityプロジェクトへ追加していない。

## 今回見つけて直したコンパイル不具合

隔離コピーの最初のUnityコンパイルで、最新作業ツリーに次のC#エラーがあったため、同じソース作業ツリーで修正し、コピーを更新して再コンパイルした。

| ファイル | 原因と修正 |
|---|---|
| `PointCloudVR/Assets/PointCloudRenderer.cs` | 色配列がない分岐で`nextCachedColors`が未代入。空色配列で初期化。`Exception`型の参照を`System.Exception`等に明示。存在しない`RecreateFullIndexBuffer`呼出を、既存の全点index buffer生成処理に置換。 |
| `PointCloudVR/Assets/PointCloudWorkbench/Scripts/StemDiameterUI.cs` | 引数必須の`BoundedTextBuffer.AppendLine(string)`を引数なしで呼んでいたため、空行文字列を渡す。 |

この2ファイル以外の製品コードは今回変更していない。初回のコンパイル失敗ログと修正後ログは [`unity_compile_editor.log`](evidence/third_audit/unity_compile_editor.log)、[`unity_compile_after_fixes.log`](evidence/third_audit/unity_compile_after_fixes.log)。修正後はUnity C#コンパイル成功。Unityサービスへの通信・ライセンス診断ログは出たが、C#コンパイルエラーは0件。

## 実行結果

| 検証 | 結果 | 根拠・制限 |
|---|---|---|
| Python backend | PASS: 58 passed、13件の依存ライブラリdeprecation warning | [`pytest_final.log`](evidence/third_audit/pytest_final.log) |
| Python構文 | PASS: `python -m compileall -q .` | [`compileall_final.log`](evidence/third_audit/compileall_final.log) |
| C#純ロジック/fault harness | PASS。履歴・安全なevent dispatch・noise rollback・dataset binding・generation・downsample path・PLY・recovery・selection・cancellation等 | [`dotnet_harness_final.log`](evidence/third_audit/dotnet_harness_final.log)。Unity GPU APIの実故障注入ではない |
| Unity EditMode | PASS: 2 passed、0 failed | [`editmode_results.xml`](evidence/third_audit/editmode_results.xml)。隔離コピーに限ったテスト |
| Unity PlayMode | PASS: 3 passed、0 failed | [`playmode_results_with_python.xml`](evidence/third_audit/playmode_results_with_python.xml)。起動scene、recovery store roundtrip、Python依存確認。ヘッドセットなしのためXR実機動作は対象外 |
| Windows x64 build | PASS: BuildReport Succeeded、errors=0、warnings=2、約99.8 MB | [`windows_x64_build_crash_test_final.log`](evidence/third_audit/windows_x64_build_crash_test_final.log)。警告はOpenXR Input Systemのdeprecated型。配布物ではない |
| Windows Player smoke | PASS: 合成PLY 100,000点読込、Octree準備、Python/NumPy/SciPy/Open3D環境確認 | [`player_python_smoke.log`](evidence/third_audit/player_python_smoke.log)。同一PCのPython環境であり、別PCへの可搬性は未検証 |
| 異常終了後の復旧 | PASS: 使い捨てPlayerを意図的に終了し再起動。異常終了marker、checkpoint、全100,000ラベルを検証し、復元候補から適用。PLY SHA-256は前後とも `A042CD6FC5FB4A423C4EF2FD443375460226326F24A32F5EB86E604E233D867C` | [`player_crash_write_final.log`](evidence/third_audit/player_crash_write_final.log)、[`player_crash_verify_final.log`](evidence/third_audit/player_crash_verify_final.log)。UI上の復元ボタンを人が操作する試験ではなく、同じ公開適用メソッドを試験bootstrapから呼び出した |

クラッシュ復旧では最初、試験コードが読み込み中の初期点群を拾い、描画位置cacheの点数を使って不一致を作った。本体は点数不一致を拒否した。試験コードを「対象の合成PLYの読込完了」かつ`PointData`点数参照に直し、同条件で再試験して上表の成功を得た。製品コードはこの試験条件の修正では変更していない。

ヘッドレスPlayerはOpenXRの`XR_ERROR_FORM_FACTOR_UNAVAILABLE`（HMDなし）をログに出す。デスクトップの点群読込とPython確認は通ったが、VR headset接続時の動作を証明しない。

## 未実施と残存リスク

次は未実施であり、PASSとして扱わない。

- Phase 2の全操作シナリオA/B/Cを、通常の実UI入力で端から端まで実施する試験。解析中キャンセル競合、保存競合、UI連打、全削除・全非表示、壊れたsidecar等の総合試験。
- Phase 3.2の書込み途中・marker更新前後・復旧適用途中のクラッシュ点ごとの試験。復元確認UIを人が操作する確認、破棄選択、read-only fallbackの確認。
- Phase 4のGPU実故障、ファイルI/O障害全種類、Python未導入・依存不足・timeout・大量stdout/stderr・不正JSON・孤立processの全件注入。純C# harnessのPASSはこれらを代替しない。
- Phase 5のR-01〜R-15全項目を個別の再現入力・最新テスト・artifactで再マッピングする監査。今回の.NET harnessは広い回帰セットだが全IDを個別実証したわけではない。
- Phase 6の10,000〜2,200,000点規模別RAM/VRAM/GC/frame time、点群切替100回、編集1,000回、Undo/Redo 200回、解析cancel 100回、数時間耐久試験。Player smokeの100,000点ロードだけでは性能保証にならない。
- Phase 7の指定6解像度・125/150%表示での実UI操作とスクリーンショット視認判定。
- Phase 8のUnity上での科学的数値回帰、実植物データ・実測基準物体との照合。Python unit testsの合格は実測妥当性を証明しない。
- Phase 10の授業シナリオ全体、別ユーザープロファイル・別PCでのインストールとPython可搬性。

したがって、研究室内試験・学生実験・一般配布は引き続き **NOT READY**。今回実証したのは、最新ローカル差分のコンパイル、限定的なUnity tests、合成データ上のPlayer起動/Python環境、完了済みcheckpointからの確認後復旧、元合成PLYの非破壊性までである。「どの操作でも落ちない」とは主張しない。

## Git状態

commit/pushは実施していない。既存の未コミット・未追跡変更を維持し、作業ファイルのstageもしていない。作業ツリーには今回以前からの多数の変更があるため、今回の2つのC#修正と監査文書・証拠だけを既存変更から切り離してcommitする必要がある。
