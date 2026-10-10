# 品質監査

監査日: 2026-10-09
対象: PointCloud_Prosseの現ローカル作業ツリー（基準コミット 2748339ce2734ca6e9ff49e725944e39f38b5117 後の差分を含む）。

## 現在の判定

| 区分 | 判定 |
|---|---|
| Python backend tests | PASS: 58 passed、13 dependency deprecation warnings |
| Python compileall | PASS |
| C#純ロジック fault harness | PASS |
| 今回差分のUnity compile / Play Mode / Player | NOT_RUN |
| 研究室・授業・一般配布 | NOT READY |

以前の監査で記録した隔離コピーUnity compileは、その時点の差分に限る過去証拠であり、今回の復旧UI等を含む現状compileの証拠ではありません。現Unity Editorを停止・競合させないため、今回別Editorを起動していません。

上記のNOT_RUN記述は第3次検証以前の履歴である。2026-10-09に最新の未コミット作業ツリーを隔離コピーし、Unity compile、Edit/PlayMode、Windows x64 Player、合成データの実行時Python検証、完了済みcheckpointの強制終了後復旧を実施した。結果と制限は[第3次実行報告](THIRD_AUDIT_EXECUTION.md)に記録した。これにより当該検証項目は限定範囲でPASSへ更新するが、実UI操作、GPU故障、全障害行列、実測点群の数値妥当性、長時間負荷は未検証のため、研究室・授業・配布判定はNOT READYのまま。

## 主な再確認・対応

- PLY parserはformat、XYZ schema、有限値、payload長を検証し、不正データを部分成功扱いしない。
- PLY exportは対象を先にsnapshotし、count/header/payloadを同じ配列に固定する。
- Downsampleはrunごとに作業場所と出力名を分離し、source hashを維持。cleanupは生成形式外の任意dirを拒否。
- Noise historyはrenderer/generation/revisionにbindingし、label update/GPU update失敗をrollbackする。historyは各stack 16 MiB上限。
- Noise/stemのmulti-file結果はimmutable generation + hash manifest + current pointerで公開。
- Stem cacheはpath/source fingerprintを照合し、不明・staleなoverlayを現結果として描かない。
- 校正は別PLYを作り、markerはファイル名でなくPLY内情報から判定。
- stdout/stderr/Stem UI queueをbounded化、PointCloudLoaded subscriberを個別例外分離。
- 異常終了復旧は検証済みlabel candidateを自動適用せず、ユーザー確認を求めるよう修正。

細目、残存リスク、配布条件は[信頼性監査](RELIABILITY_AUDIT.md)と[リリース判定](RELEASE_READINESS.md)を参照してください。
