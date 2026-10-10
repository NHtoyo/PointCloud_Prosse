# CloudCompare由来コードの出所監査

監査日: 2026-10-09

対象リポジトリHEAD: `2748339ce2734ca6e9ff49e725944e39f38b5117`

公式比較用CCCoreLib checkout: `8f83c8be6d9270cd83c79d33c74a24c1a96f309f` (2026-09-28)
確認範囲: ファイル検索、Git履歴、指定された主要実装ファイル、CloudCompare noise filterと局所ノイズ実装の比較。

## ライセンスの区別

CloudCompare本体のGitHub READMEはGPL-3.0を案内しますが、同じ公式リポジトリの`license.txt`はGPL version 2 or laterと記しています。個々のファイルのヘッダーを優先し、これらの表示だけで特定ファイルの正確なライセンスを決めないでください: [CloudCompare README](https://github.com/CloudCompare/CloudCompare), [official license.txt](https://github.com/CloudCompare/CloudCompare/blob/master/license.txt)。

CCCoreLibはCloudCompare本体とは別プロジェクトです。公式READMEでは、ライブラリ全体はLGPL-2.0-or-later、CMakeファイルはMITとし、ファイル単位のSPDXヘッダーを使うと説明しています: [CCCoreLib README](https://github.com/CloudCompare/CCCoreLib/blob/master/README.md)。したがって、CCCoreLib由来コードを含む場合は各ファイルのSPDXと元の著作権表示を確認し、CloudCompare本体のGPLと混同しない必要があります。

作業時に参照したローカル`CloudCompare-master_sankou`スナップショットのREADMEはGPL-3.0へのリンクを示す一方、同梱`license.txt`はGPL-2.0-or-laterと記載していました。また、そのローカルCCCoreLibサブモジュールは未初期化でした。このためローカル資料の不一致を独自に解消したとは扱わず、公式リポジトリとファイル単位の表示を根拠にしました。

## 確認した対象

| PointCloud_Prosse側 | 確認結果 |
|---|---|
| `PointCloudVR/Assets/PointCloudWorkbench/Scripts/PointCloudEditor.cs` | CloudCompare名のコメントやCloudCompare風の接続探索への言及があります。CloudCompare固有API、著作権表示、SPDXヘッダー、ソース断片は検索範囲で確認できませんでした。コメント中の参照元がある機能は個別に履歴確認を継続してください。 |
| `PointCloudVR/Assets/PointCloudWorkbench/Scripts/PointCloudOctree.cs` | 独自Unity/C#の点群LOD・空間データ構造として確認。CCCoreLibのOctreeクラス名・APIをそのまま取り込んだ証拠は見つかりませんでした。数学的に類似する目的だけではコピー判定していません。 |
| `PointCloudVR/Assets/PointCloudWorkbench/Scripts/CloudCompareCameraController.cs` | CloudCompare風の操作を意図するクラス名・振る舞いの説明はありますが、参照先のコード、構造的な逐語一致、第三者著作権表示は確認できませんでした。名称・操作模倣のみをコードコピーとは扱いません。 |
| `PointCloudVR/Assets/PointCloudWorkbench/Scripts/TrackballMath.cs` | Unity側の数学実装。特定のCloudCompare関数やコード断片との一致を示す証拠は見つかりませんでした。 |
| `PointCloudVR/Assets/PointCloudWorkbench/Scripts/PointCloudRenderer.cs` / `PointCloudVR/Assets/PointCloudWorkbench/Shaders/PointCloudShader.shader` | Unity/C#およびShader実装。CloudCompare由来のコード、固有の名前・コメント・著作権表示は確認できませんでした。 |
| `PointCloudVR/python_backend/noise_filters.py` | 下記のnoise filter比較を実施。類似するのは目的と局所平面残差というアルゴリズム概念です。 |
| `PointCloudVR/python_backend/filter_pipeline.py` | パイプライン制御はPython側の構成。CCCoreLib APIやソース断片は確認できませんでした。 |
| RANSAC / 参照球 / 茎径のPython処理 | 本監査でソース検索・履歴を確認した範囲にCloudCompareまたはCCCoreLibのコード移植の痕跡は見つかりませんでした。個別ファイルの網羅的な法的由来証明ではありません。 |

## noise filterの比較

比較対象は、CCCoreLib公式checkoutの`src/CloudSamplingTools.cpp`にある`CloudSamplingTools::noiseFilter`（上記commit）と、本プロジェクトの`noise_filters.py`にある`compute_cc_noise`です。

- CCCoreLib実装はoctree/cellを用いて近傍を取得し、問い合わせ点を近傍から除いたうえで`Neighbourhood::getLSPlane()`の最小二乗平面を求めます。絶対距離または近傍残差の標準偏差ベースの判定を持ち、設定により孤立点除去も行います。
- PointCloud_Prosse実装はSciPy `cKDTree`による近傍検索をバッチ化し、NumPyの共分散・固有値計算で局所平面を評価します。近傍探索、データ構造、配列処理、バッチ制御は別の実装です。相対しきい値の組み立てやradius経路にも差があります。
- 関数名、実装言語、近傍取得方法、PCA計算経路に一致する特徴的なコード断片は確認できませんでした。平面残差を使うという数学的な考え方の共通性だけをもって移植と断定しません。
- Git履歴には「CloudCompare風」「CloudCompareのNoise filter仕様に合わせたUI」等の仕様参照コミットがあり、既知の設計参考を示しますが、それ自体はソースコードコピーの証拠ではありません。

## 結論と限界

今回確認できた範囲では、指定された実装にCloudCompare/CCCoreLibのソースコードを直接コピーまたは翻案した証拠は見つかりませんでした。確認可能な状態での暫定分類は「操作・仕様・数学的概念の参考」であり、CloudCompare由来コードを第三者コードとして同梱しているとは判断していません。

これは「どこにもコピーが絶対に存在しない」という保証ではありません。全コミット・全過去ブランチ・過去に削除された外部コード・画像/資料・全ての上流実装を機械的に照合したものではなく、設計者による未記録の翻案を確認できるものでもありません。新たに具体的な由来資料が見つかった場合は、該当ファイルの著作権・元バージョン・ファイル単位ライセンスを確認するまで独自MIT範囲から除外してください。

参照: [CloudCompare](https://github.com/CloudCompare/CloudCompare), [CCCoreLib](https://github.com/CloudCompare/CCCoreLib), [CCCoreLib licensing](https://github.com/CloudCompare/CCCoreLib/blob/master/README.md)。
