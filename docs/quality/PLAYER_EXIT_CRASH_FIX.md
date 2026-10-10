# Windows Player終了時クラッシュ修正・回帰試験

実施日: 2026-10-10 (JST)
Unity: 6000.4.7f1
対象: 最新のローカル未コミット作業ツリーを隔離コピーしたWindows Player
判定: **UNRESOLVED / FAIL**

## 結論

旧第4次検証のPlayer終了時クラッシュは、今回の新規QA実行では再現しなかった。probeを含まない通常Playerでも起動時および10万点読込後の通常終了を各3回確認し、すべて終了コード0だった。機能probeを使った編集・解析・負荷・復旧試験も正常終了した。

ただし、旧ダンプが示すUnityPlayer.dll内のアクセス違反を発生させる先行条件・所有者は依然として特定できていない。原因に結びつく製品コード修正は行っていないため、修正済みとは判定しない。旧障害の原因が解決した証拠はなく、再現しないことも不存在の証明ではない。

## 原因調査の状況

前段の[PLAYER_EXIT_CRASH_INVESTIGATION.md](PLAYER_EXIT_CRASH_INVESTIGATION.md)で確認した旧障害は次のとおり。

- `0xC0000005`と`0xC000041D`は別の例外コード。旧ログでは同一プロセスに両方が出た例があり、runnerの終了コードだけでは先行例外を表さない。
- 既存PDB上の一部`0xC0000005`はUnityのstatic破棄・終了処理、別のものはPlayerのWndProc内に位置した。どちらも障害箇所であり、先行原因の特定ではない。
- 強制終了probeの`-1`は意図した終了であり、Windowsのアクセス違反と混同しない。

今回のA〜E分類:

| 分類 | 今回の判定 | 根拠 |
|---|---|---|
| A: 意図的な強制終了 | 該当あり、クラッシュ数から除外 | 復旧writerを3回、チェックポイント生成確認後に意図的にkill。各回のverifyは別Playerで正常終了。 |
| B: runner timeout / kill | 今回の試験で該当なし | 統合試験・通常Playerのいずれもtimeoutなし。 |
| C: Player自体の予期しないクラッシュ | 旧実行では確認、今回の実行では未再現 | 今回の正常終了18回、該当Application Error/WERイベント0件。 |
| D: test probeの不具合 | 単独原因を支持する証拠なし | probeなしPlayerも正常終了。旧障害との相互作用までは否定できない。 |
| E: Unity / native plugin / driver | 未確定 | 旧ダンプと今回の試験から、Unity本体・アプリのライフサイクル・先行するメモリ破壊・plugin/driverのどれかに帰属できない。 |

## 実行環境

- 新規QAルート: `E:\pcwb-qa-20261010\PlayerExitFixTask2-20261010-020000`
- 統合試験Player: `PlayerBuild\PointCloudVR_QA.exe`
- probeなしPlayer: `NoProbeBaseline\Player\NoProbe.exe`
- 入力PLYは合成フィクスチャ。実測PLYは使用・変更していない。
- probeなしビルドはQAプロジェクトの複製で作成。複製内のruntime probe、Editor用監査build script、Unity test assemblyを除外し、通常のPlayerをビルドした。本番プロジェクトおよび本番設定は変更していない。
- probeなしPlayerは引数なしで可視ウィンドウを起動し、`CloseMainWindow()`による通常のウィンドウ終了要求で閉じた。強制終了は行っていない。

## 回帰試験結果

| 条件 | 独立Player回数 | 結果と終了 |
|---|---:|---|
| probeなし・起動のみ | 3 | 3/3でウィンドウ表示、終了コード0、Unity終了ログあり |
| probeなし・10万点読込とOctree準備後 | 3 | 3/3で点数・Octreeをログ確認後に通常終了、終了コード0 |
| 編集・分類・削除/復元・Undo/Redo・保存/再読込・解析後 | 3 | 3/3機能PASS、終了コード0。1回あたり編集1,000件、Undo/Redo 200回、保存/再読込100サイクルも実行 |
| 点群切替競合と100回切替 | 3 | 3/3機能PASS、各runでA/B/C読込競合と100/100切替確認、終了コード0 |
| 負荷読込 | 3 | 3/3機能PASS。各runで100,000 / 500,000 / 1,000,000 / 2,200,000点を読込、Octreeと描画点数を確認、終了コード0 |
| クラッシュ復旧writer / verify | 3 | writerは各回チェックポイント生成後の意図的kill。verifyは3/3で復元・継続操作PASS、終了コード0 |

正常終了したPlayerプロセスは合計18回。復旧writerの意図的kill 3回はこの数に含めない。全試験はテストprobeが内部APIを通して本番処理を呼ぶ方式であり、マウス・キーボードによる全機能の実操作試験とは区別する。probeなしPlayerでは通常ウィンドウ終了を確認したが、全機能を人手で操作したUI試験ではない。

統合ワークフローで確認した主な処理:

- 分類、削除・復元、Undo/Redo、PLY保存・再読込と元データ不変性
- リファレンス球直径推定、ダウンサンプリング、C2C、茎径解析
- 茎径解析キャンセル後のUI/処理解放、再試行
- 解析後の点群切替、1,000回編集、200回Undo/Redo、100回保存/再読込
- 復旧候補の検証・適用と、復旧後の分類・削除復元・保存・点群切替・再読込

試験実行物の再確認:

- Python backend: `58 passed`; `compileall`成功。
- Unity EditMode: 2/2成功。PlayMode: 3/3成功。
- probeありPlayerの新規Windows build: 成功。probeなしPlayerも新規buildで成功し、C#コンパイルを通過。
- Playerログに`XR_ERROR_FORM_FACTOR_UNAVAILABLE`（このPCでOpenXRの利用可能なHMD form factorを取得できない旨）が出たrunがある。デスクトップでの点群・解析処理と正常終了は確認したが、実HMD上のXR終了経路を検証したものではない。

## クラッシュ記録・残留プロセス

- 2026-10-10 01:50〜02:07 JSTのApplication Error 1000 / WER 1001で、Unity Player・QAルートに一致するイベントは0件。
- `%LOCALAPPDATA%\CrashDumps`に同時間帯の新規ダンプは0件。
- 試験後、QAルートをコマンドラインに含むPlayer/Python子プロセスの残留は確認されなかった。
- probeなしPlayerの全6回に`Input System ... Shutdown`等のUnity終了ログがあり、アクセス違反・Fatal Error文字列はなかった。

## 最新Playerの識別情報

SHA-256:

| 対象 | SHA-256 |
|---|---|
| `NoProbe.exe` | `16726F6BC281A100105AE80F982A21A9332C870340BCD2E1D89BD82B76DD33C7` |
| `UnityPlayer.dll` | `4C142DD3D8237CD3537021C75904FF5DF7E3C37796FFF110BDEFA925A636FE6B` |
| `NoProbe_Data\Managed\Assembly-CSharp.dll` | `7C9973F73DD183FC082ABF6E1861FAFF25EEF0F7E1883F0AA63D1D6E3A332487` |
| probeなしソース/設定manifest | `3F713BE41AA66685ED4FFAF06943A643F48B64728D3FA761F8BBE6676D4DE8FA` |

manifestとPlayerログ・結果JSONはQAルートの`Artifacts\no_probe_source_sha256.tsv`、`Artifacts\no_probe_normal_exit_results.json`、`Artifacts\PlayerRuns\`以下に保存した。既存のprobeありPlayerと新しいprobeなしPlayerはC#アセンブリが異なるため、同一ビルド扱いはしていない。

## 残る問題と次の調査

1. 現在のソースで旧ネイティブクラッシュが再現しておらず、直接の原因と修正箇所を決定できない。判定は**UNRESOLVED / FAIL**のまま。
2. 旧異常Playerと同じC#アセンブリ・設定・操作列の再構成は未完了。現PlayerのPASSを旧クラッシュの修正証明に流用しない。
3. Octree/PLY読込のworker実行中、GPU転送中、Python子プロセス稼働中、OnDestroy後callback中にPlayer終了要求を重ねる遅延注入は未実施。今回のキャンセル試験はキャンセル処理・再試行後の終了であり、子プロセス稼働中に終了させた試験ではない。
4. XR form factorを利用できない環境のため、実HMD接続時のOpenXR正常終了は未検証。
5. 実画面の全解像度・DPIでの操作性やクリック遮断は今回の対象外。

再現条件が得られたら、先に最小再現を固定し、同一条件で通常終了・timeout・意図的kill・native exceptionを別々に記録する。原因と結びついた場合に限り製品コードを最小修正し、Playerを再ビルドして本マトリクスを再実行する。

## 変更範囲・保護

- 製品コード・Unity本番設定の変更: なし。原因を特定できなかったため、推測による修正を加えていない。
- リポジトリ内で今回作成したファイル: 本報告書のみ。
- QA用Player、合成PLY、ログ、hash manifestはリポジトリ外の隔離QAルートに保存。
- 既存未コミット変更、実測PLY、ライセンス資料、`Assets/_Recovery/`は変更していない。
- Unity Editor PID 30824（import workers 21068 / 15860を含む）は稼働を確認し、終了していない。
- commit / push / stage: 実施していない。
