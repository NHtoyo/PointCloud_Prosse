# 非同期Operationのライフサイクル

## 状態

`Idle → Running → Success / Cancelled / Failed / SuccessWithWarning`

開始時に単調増加するOperation IDと固有CancellationTokenSourceを割り当てる。ProgressManagerは同時に一つだけRunningを許可し、二つ目の要求は`TryStart()`が`null`を返す。終了通知はIDが現在IDと一致しRunningの時だけ反映される。古いOperation objectからのUpdate/Finishは拒否される。

## キャンセル

キャンセルは停止要求であり、即時停止保証ではない。呼出側のCPU loop、Python process待ち、Octree構築がtokenを確認する。CancellationTokenを見ない外部処理は処理終了まで残りうる。取消処理はFailedと分離する。

## 点群の所有権・結果反映

読込・解析結果を受け取る側は対象Renderer、`DatasetGeneration`、必要な`ContentRevision`を開始時に捕捉する。結果反映前に一致を検証する。背景選択完了はConcurrentQueueを通してUnityメインスレッドで適用する。RendererのOctree再構築は前世代のtokenをcancelし、再帰分割でもキャンセルを調べる。

NoiseFilter結果は点数・並列配列長、Renderer identity、generation、revisionが一致しない場合拒否する。Undo/Redoはbounded historyを使い、dataset/revision変更後の古い履歴を無効化する。

## 終了・破棄

Loader、manager、UIはOnDestroyで自身が所有するoperationをcancelする。OnDestroy後にUnity objectが有効とは仮定しない。Python process stopとTask完了は個別に観察する。`Task.Result`は完了確認後のcallback内に限る。外部processの`WaitForExit`はTask.Runまたはasync待機内で行い、Unityメインスレッドを同期blockしない。

## テストと限界

純C#ハーネスでA失敗→B開始、Aの遅延通知、重複開始、cancel、100 seed固定operation cycleを検証済み。Unity UIから全機能を通したdataset switch / destruction race、操作応答時間の上限、全async callsiteの並行故障は未検証。ProgressManagerは意図的にグローバル排他で、operation並列実行はサポートしない。
