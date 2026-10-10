# データ完全性監査

日付: 2026-10-09。判定対象は現ローカル作業ツリー。PASSは当該自動試験範囲に限定する。

## 不変条件

| ID | 不変条件 | 根拠・実装 | 状態 |
|---|---|---|---|
| D1 | 明示操作なしに元点群を変更・削除しない | downsampleはunique staging/final、PLY exportはtemp+replace、新scaleは別PLY、recoveryはpersistentDataPath。元PLY hashをC#試験で比較 | PARTIAL: 対象経路でPASS。全機能・symlink/hardlinkは未検証 |
| D2 | 編集失敗で半端な状態を残さない | noise label mutationはGPU更新成功後のみhistory確定、失敗時にCPU/GPU同期をrollback。点群切替は候補CPU/GPU resource準備後に参照を切替。scale adoptionも別file save後 | PARTIAL: helper fault harness有。実Unity GPU failure・全編集ツールのrenderer例外試験なし |
| D3 | 結果を入力・parameter・operation IDへ結び付ける | Python generation manifest/artifact hashes/run ID、stem source identity/cache fingerprint、Unity operation ID | PARTIAL: noise/stemに自動試験。全Python機能を同一契約で統合していない |
| D4 | count/配列/header/payload整合 | PlyExportのcompact snapshotからcount/header/payload。PLY readerは欠損XYZ/truncated/nonfiniteを拒否 | PASS: 合成C# regressions。全実PLY format/schemaは未検証 |
| D5 | 長さ単位・校正状態が明確 | 校正PLYのmarkerのみを信用し、座標scale変換は別PLY生成。既存単位/アルゴリズムはこの監査で変更しない | PARTIAL: marker・保存座標を確認。全UI/解析経路の単位監査や基準物体の真値確認は未実施 |
| D6 | 古い結果を現結果として扱わない | operation ID、renderer/generation/revision guards、stem cache source hash/count/fingerprint、stale overlay off | PARTIAL: helper/cache/lifecycle試験。全consumerのUnity競合試験なし |
| D7 | 後続runの失敗で正常保存結果を失わない | Pythonのimmutable generation + pointer更新。旧runはretained | PASS: injected write/metadata/report/pointer failuresで前世代維持。実電源断/OS crashは未検証 |

## PLY・編集・結果の対象意味

- PLY exportのSelectedNonDeletedはmanual deletedとnoise hidden bitを除外し、transient selection/reason bitsを出力labelから落とす。
- PlyExportServiceは書出し開始時のcompact snapshotを保持するため、ヘッダ点数と書込み中に変化したlive配列を混ぜない。
- noise preview/commit/undo/redoは対象Rendererのdataset generationとcontent revisionに束縛し、別点群の履歴を無効化する。
- 球直径、C2C、noise、stem等すべての点対象の科学的意味を今回のC#試験だけで確定したとはしない。各機能横断の選択/hidden/deleted matrixが残る。

## 残存確認

- Windowsでreal file identityを用いるsymlink/hardlink実験、locked output、read-only directory、disk full、強制終了を未実施。
- Unity UIのPointData labelsとGPU描画表示が失敗直後も一致するかPlay Modeで未確認。
- calibration/measurement/stemの既知長さ標準物体による実測精度比較を未実施。
- checkpointはlabelのみ。位置/色/measurement/カメラ/UI/Undo履歴は復旧しない。保存対象を拡張したとは見なさない。

## メモリ所有量の静的見積り

PointDataの主要フィールドはVector3(12 bytes)+uint(4)+int(4)+float(4)で、配列要素は概ね24 bytes。2.2M点ならCPU配列約52.8 MB、同サイズComputeBufferも約52.8 MB相当。実際のVRAM allocation/driver overheadは未測定。

点群の差し替えは失敗時に旧点群を保持するため、候補point/index GPU bufferを事前に作る。切替中は旧・新の点データとGPU bufferが一時共存する。PointData overloadでは、旧/新PointDataとGPU buffer、2組のposition/color cache、annotation labels、index bufferを合わせ、単純推定で約166 bytes/point、2.2M点で約365 MB（約348 MiB）の一時メモリになり得る（CPU/GPU合算、配列/driver overhead別）。これは静的推定で実測ではなく、メモリ逼迫時は新規読み込みが失敗し旧点群を維持する設計。実機peak検証が残る。

PLY exportのcompact snapshotはXYZ(12)+color(4)+label(4)で約20 bytes/出力点。2.2M点なら約44 MBの追加snapshotがあり得る。Python側のPLY/NumPy copy、Octreeのindex/node、GPU staging、managed GC peakは別枠で、今回測定していない。

注釈Undo/Redoとnoise Undo/Redoはそれぞれstackごと16 MiB、4 stack合計の保持上限は64 MiB。1つのtransaction/rollback snapshotも16 MiBを超える点群では、注釈変更またはnoise mutationを変更前に拒否する。16 MiBは4,194,304個のint32 labelに相当する。これにより一操作あたりの一時label copyを制限するが、全アプリメモリ上限ではない。

復旧はlabel snapshotを最大100,000点/frameで作成するが、完了配列は全label分を保持し、圧縮用追加bufferも使う。数百万点での実測peakは未実施。
