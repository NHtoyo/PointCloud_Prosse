# UI改善・実画面検証報告

実施日: 2026-10-10 (JST)  
Unity: 6000.4.7f1  
対象: `E:\VR\PointCloudVR` の最新ローカル作業ツリーを隔離コピーしたWindows Player  
判定: **部分改善済み / 全機能のGUI検証は未完了**

## 実施内容

- 小画面で左・中央・右パネルが画面幅に応じて縮まり、長いラベルやボタン文字が折り返されるレイアウトを確認しました。
- 中央のパイプライン・クラス・距離計測・茎径パネルと、左右の点群編集・CloudCompare機能パネルでスクロール可能な構成を維持しました。
- 点群名、総点数・表示点数・選択点数、校正状態などの状態表示を確認しました。
- UI上のマウス領域をカメラ・点群操作から除外し、処理中・エラー表示中は通常UIを前面のモーダルで遮断します。
- 主要UI背景を透明度`0.98`に統一しました。操作対象の判別性を保ちつつ、背後の文字が重なって読みにくくなる状態を抑えています。
- パイプラインとアノーションクラスの横幅が狭い場合に要素を折り返す表示を確認しました。
- ノイズフィルタUIの点群走査集計を毎フレーム実行しない構成を維持し、OnGUI中の不要な負荷を抑えています。

主な対象コード:

- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/PointCloudEditorUI.cs`
- `PointCloudVR/Assets/PointCloudManager.cs`
- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/AnnotationPipelineEditorUI.cs`
- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/FilterPipelineEditorUI.cs`
- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/NoiseFilterUI.cs`
- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/NoiseFilterManager.cs`
- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/DistanceMeasurementUI.cs`
- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/StemDiameterUI.cs`
- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/CloudCompareCameraController.cs`

## 実画面検証

隔離QAルート:  
`E:\pcwb-qa-20261010\UIImprovementTask3-20261010-060000`

入力は合成点群`cloud_A_100k.ply`です。実測PLYは使用していません。表示解像度はPlayerのクライアント領域を計測し、次の6サイズで撮影しました。

| クライアント解像度 | 視覚確認 | スクリーンショット |
|---|---|---|
| 1024×768 | PASS: コンパクト配置、文字折り返し、スクロール領域を確認 | `Artifacts\ui_final_1024x768.png` |
| 1280×720 | PASS: 横幅・高さが小さい状態で切れを確認 | `Artifacts\ui_final_1280x720.png` |
| 1366×768 | PASS: 各パネルの操作領域・文字を確認 | `Artifacts\ui_final_1366x768.png` |
| 1600×900 | PASS: 中央の茎径パネルを含む配置を確認 | `Artifacts\ui_final_1600x900.png` |
| 1920×1080 | PASS: 3列レイアウトとパネルのスクロールを確認 | `Artifacts\ui_final_1920x1080.png` |
| 2560×1440 | PASS: 詳細設定・解析結果を含む配置を確認 | `Artifacts\ui_final_2560x1440.png` |

検証時のPlayer DPIは144 (150%) でした。OS表示設定は変更していません。125%は安全に切り替えられる隔離環境がなく、**BLOCKED / NOT_RUN**です。

操作して確認した項目:

- 右側パネル上でのホイールスクロール。スクロール後も選択点数は変化しませんでした。
- ツール表示チェックのクリック。表示対象が茎径パネルへ切り替わりました。
- リファレンス球推定ダイアログを開き、選択点0点で実行。必要点数の説明を含むエラーが中央に表示されました。
- エラー外の背景クリックではダイアログが閉じず、詳細表示・閉じる操作が機能し、閉じた後に元のUIへ戻りました。
- PlayerログにGUI layout errorやC#例外は見つかりませんでした。OpenXRは利用可能なHMDがないため`XR_ERROR_FORM_FACTOR_UNAVAILABLE`を出力しました。

エラー画面・設定画面の画像:

- `Artifacts\ui_final_reference_dialog_1024.png`
- `Artifacts\ui_final_error_1024.png`
- `Artifacts\ui_1024_error_details_retest.png`
- `Artifacts\ui_1024_error_dismissed_retest.png`

画像はすべて`E:\pcwb-qa-20261010\UIImprovementTask3-20261010-060000\Artifacts\`に保存しました。変更前の比較画像は同じディレクトリの`ui_after_1024x768.png`等です。

## 機能別の確認状況

| 機能 | 結果 | 範囲・制約 |
|---|---|---|
| 点群読み込み・状態表示 | PASS | 合成100,000点をPlayerで読込。対象ファイル名、点数、校正状態を表示。Octreeも生成。 |
| 表示・カメラ操作 | PARTIAL | 操作モードUIとカメラ操作案内を確認。全軌道・ズーム操作は未網羅。 |
| 選択、分類、削除・復元、Undo/Redo | NOT_RUN (GUI) | コントロールの配置確認のみ。過去の内部probe試験は実画面操作の代用として数えていません。 |
| ノイズ除去・ダウンサンプリング | PARTIAL | パイプラインパレット、設定領域、右側ボタンの到達性を確認。今回のGUIから処理を実行していません。 |
| 茎径解析 | PARTIAL | 前段のPlayerで合成100,000点に対する解析開始・進捗・完了表示を確認。最終ビルドでは保存済み結果の再読込を確認。これはUI/連携試験であり、実植物の科学的妥当性確認ではありません。 |
| 球直径推定 | PARTIAL | 設定画面と点数不足エラーを確認。十分な選択点による成功経路は今回未実施。 |
| C2C | NOT_RUN | 比較点群がないためボタンの無効理由表示を確認。計算は未実施。 |
| 距離計測 | PARTIAL | 画面・モード切替を確認。点追加・保存・CSV出力の完了経路は未実施。検証時にできた未保存下書きはキャンセル済み。 |
| スケール校正 | NOT_RUN | 操作ボタンの配置のみ確認。校正計算は未実施。 |
| PLY/CSV/JSON保存 | NOT_RUN (GUI) | 保存先確認を伴うGUI書き出しは未実施。 |
| 進捗・キャンセル・エラー | PARTIAL | 進捗表示と0点エラー、背後クリック遮断、詳細・閉じる・復帰を確認。GUI上で実行中キャンセルは未実施。 |
| クラッシュ復旧 | NOT_RUN (UI) | 復旧ダイアログの適合性・復旧/復旧しないの一連操作は今回未実施。 |

## ビルド・終了確認

- 隔離プロジェクトを`ThirdAuditBuild.BuildWindows`で再ビルド: **Succeeded**, C# errors 0, warnings 2。
- 2件の警告はいずれもQA用`ThirdAuditWorkflowProbe.renderer`が`Component.renderer`を隠す`CS0108`です。
- 最終Playerは合成100,000点を読み込み、ログ上でOctree生成とキャッシュ済み茎径結果の読込を確認しました。
- Playerは`CloseMainWindow()`による通常終了要求後にプロセス終了し、Input Systemのshutdownログが出ました。PowerShellから終了コード値を取得できなかったため、終了コード0とは報告しません。
- 指定時間帯のWindows Application Error 1000 / WER 1001は0件。QAルートを含むPlayer/Python子プロセスも終了後に残っていません。
- Player SHA-256: `16726F6BC281A100105AE80F982A21A9332C870340BCD2E1D89BD82B76DD33C7`
- `Assembly-CSharp.dll` SHA-256: `FB7D5D03F781223ADCC3051117596AF501548FADF243F6E798BF61D25E525E2F`
- `UnityPlayer.dll` SHA-256: `4C142DD3D8237CD3537021C75904FF5DF7E3C37796FFF110BDEFA925A636FE6B`
- ビルドログ: `Artifacts\unity_player_build_final.log`; Playerログ: `Artifacts\Player_UI_final_1024.log`。

## 未解決事項

1. 実画面での点選択、分類、削除・復元、Undo/Redo、保存、各解析の正常・キャンセル経路、点群切替、復旧UIを一連で確認する必要があります。
2. テキスト編集中のショートカット抑止、処理中の他ボタン連打、保存中の点群切替などの実操作競合は未検証です。
3. 125%表示と実HMD/OpenXRは未検証です。
4. 終了時ネイティブクラッシュの判定は前段の[PLAYER_EXIT_CRASH_FIX.md](PLAYER_EXIT_CRASH_FIX.md)どおり**UNRESOLVED / FAIL**のままです。今回の通常終了1回で解決とみなしません。
5. 実測PLYを使った数値・画質・科学的妥当性検証はしていません。
6. 1024×768で複数の中央パネルを同時表示すると3D表示領域がかなり狭くなります。未使用パネルを右側の表示切替で隠す運用は確認しましたが、狭い画面用の単一パネル/ドロワー表示は未実装です。

作業中のUnity Editor PID 30824は稼働を確認し、終了していません。元データ、`Assets/_Recovery/`、ライセンス資料は変更していません。commit / push / stageは実施していません。

## 次の作業

実測データと復旧スナップショットを使わない隔離QA環境で、入力・編集・Undo/Redo・保存・再読込・キャンセル・点群切替・復旧を実GUIから順番に実施し、操作前後の点数・ラベル・ファイル出力を照合します。125%表示はOS設定を触らずに用意できる専用環境が必要です。

## 作業3 UI切替・入力干渉修正 (2026-10-10)

この追記は上記の旧状態を更新します。旧画面の同時積み上げ表示は解消しましたが、最新差分の実画面操作・目視は完了していません。

### 修正

- 中央領域に「分類 / ノイズ / 距離 / 茎径 / 閉じる」の固定タブを追加し、中央パネルを一度に1つだけ表示するようにしました。詳細パラメータやパイプライン編集機能は削除していません。
- 以前の複数パネル設定は、茎径、距離、ノイズ、分類の順で1つに正規化して保存します。右側の旧チェック項目も同じ排他的なボタン選択へ変更しました。
- 中央スクロールをタブ下に分離し、表示中パネル上でのみホイールを中央UIへ渡します。空の3D領域はUIヒット領域から外し、カメラ・点群操作へ通します。タブとスクロールバーは引き続き3D操作を遮断します。
- UI上でのTabによる操作モード切替、なげなわのEnter/Space確定を抑止します。非表示の分類・ノイズパネルのDelete/Ctrlショートカットが動く経路も止めました。
- 左パネルの表示数を「表示可能」から実際の描画可視点数に変更しました。削除bitとノイズ非表示bitを既存の統計集計中に数えるため、OnGUIごとの点群全走査は追加していません。

変更コード・テスト:

- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/PointCloudEditorUI.cs`
- `PointCloudVR/Assets/PointCloudManager.cs`
- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/PointCloudEditor.cs`
- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/FilterPipelineEditorUI.cs`
- `PointCloudVR/Assets/PointCloudWorkbench/Scripts/AnnotationPipelineEditorUI.cs`
- `PointCloudVR/tests/UnityIntegration/Assets/Tests/PlayMode/RuntimeRecoveryAndSceneTests.cs`

### UI機能棚卸し

以下は最新差分でのUI経路と検証範囲です。「内部/自動」はPlayerのテストprobeが本番処理経路を呼び出した結果で、マウス・キーボードによるGUI操作を意味しません。

| 機能 | UI上の入口 | 最新の確認 |
|---|---|---|
| PLY読込・点群切替 | 左パネルのPLY一覧 | 内部Player試験あり。GUIクリックは未実施 |
| 表示・カメラ・点群位置合わせ | 右パネルのモードボタン、点サイズ、中心合わせ/リセット | 入力遮断をコード修正。実画面操作は未実施 |
| 選択・分類 | 左のツール選択、中央「分類」タブ、分類クラス/適用/Undo/Redo | 内部probeで分類・履歴を確認。GUI操作は未実施 |
| 削除・復元 | 左パネル操作と削除bit操作 | 内部probeで確認。GUI操作は未実施 |
| ノイズ除去・ダウンサンプリング | 中央「ノイズ」、右パネルの実行/設定 | 内部probeでプレビュー/確定/Undo/Redo、Downsampleを確認。GUI操作は未実施 |
| 茎径 | 中央「茎径」タブ、解析/キャンセル/結果表示 | 合成円柱のPlayer内部試験とキャンセル後再試行はPASS。GUI操作は未実施 |
| 球直径・スケール校正 | 右パネルの球推定・校正ボタン、設定ダイアログ | 最新GUI操作は未実施。既存UI試験の0点エラー確認は旧ビルドの記録 |
| C2C | 右パネルの比較実行、閾値、統計 | 合成点群の内部Player試験はPASS。GUI操作は未実施 |
| 距離計測・CSV/JSON | 中央「距離」タブ、計測一覧、書出し | 内部Player試験でJSON/CSVと再読込を確認。GUI操作は未実施 |
| PLY保存/再読込 | 左パネルのPLY書出しと読込一覧 | ASCII/Binary、XYZ/RGB/label往復を内部Playerで確認。GUI操作は未実施 |
| 進捗・キャンセル・エラー | 進捗/通知モーダル | 茎径キャンセル後の内部回復はPASS。最新画面で表示/閉じる操作は未実施 |
| クラッシュ復旧 | 起動時の復旧確認ダイアログ | 内部復旧試験あり。最新GUIの適合性表示・選択操作は未実施 |

### 最新スナップショットの自動検証

隔離QA: `E:\pcwb-qa-20261010\Task3UIRefactorFinal-20261010-224900`。Python仮想環境は既存QA環境をコピーし、依存再インストールはしていません。
基準Git SHA: `48298e67ba7b10032ff82be13f94ec1d508266d3`。最終Playerの識別SHA-256: `PointCloudVR_QA.exe` `16726F6BC281A100105AE80F982A21A9332C870340BCD2E1D89BD82B76DD33C7`、`Assembly-CSharp.dll` `924ADCC40DA6F9194CDBEA43D6988FCEB463C1E917E7D42F593F1C2FC9BE49D7`、`UnityPlayer.dll` `4C142DD3D8237CD3537021C75904FF5DF7E3C37796FFF110BDEFA925A636FE6B`。

- Python: pytest **61 passed**、`compileall`成功。
- Unity EditMode: **2/2 passed**。
- Unity PlayMode: **4/4 passed**。中央ワークスペース選択が排他的で、閉じる操作ですべて非表示になる回帰テストを含みます。
- Windows x64 Player: 最新スナップショットからbuild成功。Playerの自動統合ワークフローは合成100,000点を使い、処理結果PASS、通常終了code 0。
- 自動ワークフローは選択/分類/削除/復元/Undo/Redo、PLY往復、ノイズ処理、downsample、茎径解析とキャンセル・再試行、C2C、距離計測文書、校正済みPLYなどを内部経路で検証。物理GUI操作の代わりにはしていません。
- Unityコンパイルエラーなし。警告はテストprobeの`ThirdAuditWorkflowProbe.renderer`が`Component.renderer`を隠す`CS0108`のみ。
- QA artifactsに記録された`PointCloudVR_QA.exe` SHA-256: `16726F6BC281A100105AE80F982A21A9332C870340BCD2E1D89BD82B76DD33C7`。
- この1回の正常終了で既知の終了クラッシュを解決とは扱いません。[PLAYER_EXIT_CRASH_FIX.md](PLAYER_EXIT_CRASH_FIX.md)の判定は引き続き**UNRESOLVED / FAIL**です。

### 実画面・解像度検証

最新コードでの実画面スクリーンショット取得とボタン操作は**BLOCKED / NOT_RUN**です。Windows UI操作ヘルパーの初期化が`failed to write kernel assets: 指定されたパスが見つかりません。 (os error 3)`で失敗し、再試行後も復旧しませんでした。画面を操作・撮影したと偽っていません。

| クライアント解像度 | 最新コードの目視確認 |
|---|---|
| 1024×768 | NOT_RUN |
| 1280×720 | NOT_RUN |
| 1366×768 | NOT_RUN |
| 1600×900 | NOT_RUN (Player内部ワークフローのみ実行) |
| 1920×1080 | NOT_RUN |
| 2560×1440 | NOT_RUN |
| 125% / 150% DPI | 最新コードではNOT_RUN。旧スクリーンショット報告の150%記録は今回のタブ変更を含まないため流用しない |

旧報告のスクリーンショットは履歴資料として残していますが、この差分の表示確認の根拠にはしていません。したがって、配置、文字切れ、クリック遮断、実パネル上のホイール、タブ切替、モーダル操作は最新Playerで未確認です。

### 残課題

- 画面操作でタブ、スクロール、3D領域への入力通過、右パネルからの切替、パネル閉じる操作を確認する。
- 1024×768から2560×1440の6解像度で通常/処理中/エラー/詳細設定を撮影する。125%/150% DPIは変更権限のある隔離環境でのみ確認する。
- 選択、分類、削除/復元、保存、各解析、キャンセル、点群切替、復旧をGUIから通しで確認する。
- 終了時ネイティブクラッシュは未解決のため、学生実験・一般配布の安定性判定はしない。
