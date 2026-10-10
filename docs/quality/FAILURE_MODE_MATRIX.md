# 故障モード・影響マトリクス

監査日: 2026-10-09。対象はローカル作業ツリー（基準 2748339ce2734ca6e9ff49e725944e39f38b5117 後の変更を含む）。

判定: PASSは記載した自動テスト範囲のみ、PARTIALは保護があるが統合検証が未完、NOT_RUNは実行していない。

| ID | 故障モード / 影響 | 現在の保護 | 検証状態と残存リスク |
|---|---|---|---|
| R-01 | ダウンサンプルが元PLYを上書き | GUID別作業ディレクトリ、source/stage/output分離、出力は新規名、PlyExportは一貫スナップショット | PASS 純C#でパス分離と元PLY SHA-256不変を検証。実リンク同一性はNOT_RUN |
| R-02 | 近いボクセル値や再実行で出力衝突 | float.ToString("R")から作る設定tokenとGUIDを出力名に含める。上書きしない | PASS 1.01/1.04等、同一条件再実行、別フォルダを比較。敵対的な同時ファイル作成はNOT_RUN |
| R-03 | ノイズ解析失敗で前回の正常結果を喪失 | Pythonは世代別に書き、全artifact manifest後にcurrent pointerを置換。旧世代は保持 | PASS 初回/途中/metadata/report/pointer失敗で既存世代・hashを検証。実ディスク枯渇・強制終了はNOT_RUN |
| R-04 | 茎径JSON/CSV/画像が別runで混在 | run ID付き世代ディレクトリ、完了manifest/hash、単一current pointer | PASS CLI CSV故障で旧pointerとartifact bytesを確認。電源断・OS crashはNOT_RUN |
| R-05 | 別点群へノイズUndo/Redo | Renderer参照、dataset generation、content revisionと履歴を結合。ラベル更新とGPU同期に失敗時rollback | PASS 同点数/異点数別Rendererと注入buffer失敗を純C#で検証。Unity実操作NOT_RUN |
| R-06 | hidden/deleted/selected点の対象差 | SelectedNonDeletedはmanual DeletedBitとnoise hidden bitを除外。PLY書出しとUI点数で共通判定 | PARTIAL export状態bit表を純C#検証。球径、C2C、ノイズ、茎径など全機能の意味は統合監査未完 |
| R-07 | PLY header点数とpayload点数不一致 | 書出し開始時に対象点をcompact immutable snapshot化し、同じ配列からcount/header/payload作成 | PASS 書出し中の元配列変更を注入し、5000点のheader/payload一致を検証 |
| R-08 | 古い非同期処理Aが処理Bや別点群を変更 | Operation IDで進捗/完了通知を遮断。主要な結果適用箇所でRenderer/generation/revision確認 | PARTIAL 遅延A通知とseed付き100 lifecycle cycleはPASS。全consumer、点群切替中のUnity実行はNOT_RUN |
| R-09 | 古い茎径結果を現行結果として表示 | 保存先/入力fingerprint照合。stale結果は閲覧用扱いとしoverlayを非表示 | PASS path/count/fingerprintの純C#テストとsource assertion。実UI確認NOT_RUN |
| R-10 | 同名PLYの茎径cache衝突 | 正規化source pathを含むhashで別ディレクトリを分離 | PASS 純C#で同名・別pathを検証 |
| R-11 | scale保存失敗でin-memory座標が変化 | 補正PLYを元配列から別ファイルへatomic作成し、保存後にloaderが座標変更を確定。逆数rollbackを使用しない | PASS source array不変、marker付き値、既存出力保持を純C#確認。Unity UI統合NOT_RUN |
| R-12 | filename suffixだけで校正済みと誤判定 | PLY内のcalibration markerのみで判定 | PASS markerなし _calibrated_mm.ply を未校正と判定 |
| R-13 | Python大量ログでRAM無制限増加 | BoundedTextBuffer head/tail保持。Stem UI queueにも件数・行長上限 | PARTIAL buffer容量とtruncationを純C#確認。実子プロセス大量stdout/stderrはNOT_RUN |
| R-14 | 1 listener例外で後続の点群読込通知が止まる | SafeEventDispatch.InvokeEachで個別に呼出し、失敗を分離 | PASS 3 listener中1例外でも後続呼出しを確認。Unity scene integration NOT_RUN |
| R-15 | 大規模データでRAM/VRAM枯渇 | Annotation/Noiseの4 history stackを各16 MiB、snapshot単体も16 MiBまでに制限。log queue bounded。PLY exportはcompact snapshotを保持 | PARTIAL 静的所有量と推定を文書化。全アプリpeak RAM/VRAM/GC実測、数百万点enduranceはNOT_RUN |
| R-16 | 点群切替時のGPU確保/SetData失敗で、既存点群を差し替え途中のCPU状態と混在させる | CPU配列・境界・annotation・point/index ComputeBufferを事前構築し、SetData成功後だけ参照を切り替える。Octreeは失敗時も全点表示へ退避 | PARTIAL: 汎用候補リソースの初期化失敗をC# harnessで注入。実Unity ComputeBuffer失敗とRenderer統合はNOT_RUN |
| REC-01 | 異常終了後に古いlabelを無確認適用 | 現在はsource SHA・点数・payload hash検証後、ユーザーに復元/破棄を尋ねるUIを追加 | コードレビュー済み、Unity compile/Play ModeはNOT_RUN。リカバリー失敗時read-only modeも未実装 |

他の機能に対して個別に保護があることは、すべての障害モードが解決済みであることを意味しない。特に非同期競合、GPU失敗、クラッシュ復元、UI解像度はUnityで追加検証が必要。
