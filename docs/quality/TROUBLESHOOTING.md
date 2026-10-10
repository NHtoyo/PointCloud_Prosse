# トラブルシューティング

## 点群が読み込めない

- ファイル先頭に`ply`、`format`、`element vertex N`、`end_header`があるか確認します。
- XYZのpropertyが3つともあるか、vertex propertyがscalarか確認します。
- binary payloadがheaderの点数・property型と一致しているか確認します。
- 今の読込器はvertex list propertyとvertexより前の非空elementを拒否します。CloudCompare等で標準的なvertex-first PLYとして再書出しし、元ファイルは残してください。
- 失敗時はUnity Progress/Error詳細とConsoleの`RecoverableOperationError`を確認してください。

## 読込中に時間がかかる

ファイル読込・デコードはバックグラウンドで進みます。キャンセルを押すと現在の読込を中断します。Unity側のGPU buffer作成とoctree構築は依然として時間を要することがあり、Profiler実測は未実施です。

## Python機能が動かない

- Pythonを手動導入し、Unityを再起動します。`python`または`py`が使えることを確認します。
- `python_backend/.venv`内の依存検証が失敗した場合、`requirements.txt`とpipのエラー詳細を確認します。
- 依存取得に失敗した場合はネットワーク、プロキシ、書込権限を確認します。アプリはOS全体へPythonを自動インストールしません。

## C2C計算が失敗する

- 両方の点群が空でないこと、変換後座標が有限値であることを確認します。
- 計算中に対象点群を切り替えた場合、結果は採用せず破棄されます。
- 最新のUnity Consoleと進捗エラー詳細を添えて報告してください。

## 茎径解析で支持点不足・結果が欠ける

- 入力が主茎を表す点群であること、対象点数、中心線軸、最大連結成分設定を確認します。
- 断面ごとの`calculation_status`とdiagnosticsを見て、支持点不足と角度coverage不足を区別します。
- パラメータを変える前に入力点群のXYZ・mm解釈と表示倍率を混同していないか確認します。アルゴリズム閾値を場当たり的に変えて解決しないでください。
- 結果の実寸精度は既知寸法の基準物で検証してください。

## 計測JSONと点群が一致しない

計測サイドカーの点群識別・fingerprint警告を確認してください。元PLYを上書きせず、バックアップした複製で復旧を試します。メタデータ不一致時にJSONを手作業で差替えないでください。
