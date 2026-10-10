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
