# セッション復旧設計

## 保存範囲と保護

- 元PLYは読み取り専用。復旧ファイルはApplication.persistentDataPath/PointCloudVR/Recovery/配下に保存し、元PLYへ書き戻さない。
- 現行checkpointはPointData.label配列のみ。分類・選択・削除/非表示bitを含む。XYZ/RGB、計測JSON、カメラ/UI状態、解析途中の処理、Undo/Redoは含まない。
- .pcwbrはmagic/version、点数、revision、source PLY SHA-256、label payload SHA-256を持つ。tempへ書いてflushし、replace/moveで確定する。
- 起動markerの存在を異常終了の手掛かりとする。復旧候補読込はPLYのSHA、点数、形式、checksumが一致した場合だけ成立する。
- 検証済み候補は自動適用せず、UIで対象PLY・点数・保存範囲を示して「復元する」または「復旧データを破棄して続行」を選ばせる。
- 復元適用時は現在のlabelを一時保存し、dataset generation・配列長・処理状態を再確認する。GPU更新失敗時はCPU labelをrollbackし、再同期を試す。
- 破棄操作は復旧checkpointのみを削除する。元PLYは変更しない。削除に失敗した場合は確認UIを閉じず、失敗内容を表示する。
- checkpoint snapshotは最大100,000点/frameでコピーし、圧縮・書込みはバックグラウンドへ移す。編集静止4秒後に開始し、書込失敗は30秒後に再試行する。

## 今回の修正

前回実装が有効なcheckpointをユーザー確認なしで適用することを確認した。今回、PointCloudEditorは検証後のラベルをpendingとして保持し、PointCloudEditorUIがモーダル選択を表示するように変更した。確認中は点群編集入力と他の点群UIを止める。これはソース上の変更であり、Unityコンパイル・実画面でのボタン操作は未検証。

## 既知の制約

- 確認UIはlabelだけを復元する。PLY、XYZ、RGB、measurement、camera、undo journalは復元しない。
- Unity/Player再起動を通したend-to-end crash recovery試験は未実施。復旧保存中に強制終了した場合、最後に確定済みのcheckpointまで編集を失う可能性がある。
- 4秒のquiet period、最大100,000点/frameのsnapshot処理中に強制終了すれば最新変更が含まれないことがある。
- 一世代checkpointのみ。複数時点から選ぶ機能はない。
- SHA-256全読込の時間・メモリを大容量PLYで計測していない。
- 復旧ファイルが壊れていた場合に読み取り専用modeへ移る仕組みはない。現状は候補を適用せず通常点群を維持し、警告をログに残す。
- 異常終了markerはアプリ単位で、複数起動を防ぐmutexではない。

## 第3次実行確認（2026-10-09）

隔離Windows Playerと合成PLYで、完了済みcheckpoint保存→テストPlayerだけを強制終了→再起動→異常終了marker検出→PLY hash/点数/checksum検証→復元候補の生成→適用メソッド実行までを確認した。100,000件のlabelが全件一致し、合成元PLYのSHA-256も前後一致した。ログは[`player_crash_write_final.log`](evidence/third_audit/player_crash_write_final.log)と[`player_crash_verify_final.log`](evidence/third_audit/player_crash_verify_final.log)。

適用はテストbootstrapから`ApplyPendingRecovery()`を呼び出したもので、UI上のユーザー操作確認ではない。checkpoint書込み途中の強制終了、復元適用途中のGPU例外、拒否/破棄ボタン、読み取り専用fallbackは未試験。従って上の「restart/recovery試験未実施」は完了済みcheckpointの基本経路に限り更新され、他の境界試験は引き続き必要。

## 次の受入条件

隔離Playerと合成PLYで、復元/破棄選択、PLY hash不変、誤fingerprint拒否、復元中GPU更新失敗、書込中kill→再起動、checkpoint破損、読み込み専用fallbackを検証する。confirmation UIの見切れを1280×720、1366×768、1920×1080、2560×1440で確認する。
