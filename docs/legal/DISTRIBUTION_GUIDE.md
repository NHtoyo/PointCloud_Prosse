# 配布ガイド

この文書は調査済み情報の実務チェックリストです。権利や所属機関の規程を確定する法律意見ではありません。特に`sample.ply`の配布権、プロジェクト著作権者、Unity利用条件が未解決です。

## A. GitHubからソースを取得し、利用者がUnity/Pythonを準備

- ルート`LICENSE`と`THIRD_PARTY_NOTICES.md`を同梱し、独自コードと第三者ソフトを区別します。
- 利用者は自身のUnity Editor/Runtime利用に必要な現行Unity Engine Licenseを取得・遵守します。Unity公式パッケージはUCL/UPDLとパッケージのThird Party Noticesに従います。
- Python依存は`requirements.txt`にバージョン固定がなく、Python実環境も説明と異なるため、現状では同じ環境を再現できるとは保証できません。
- リポジトリに追跡されている`PointCloudVR/Assets/StreamingAssets/sample.ply`は出所と配布権が未確認です。配布前に次のどちらかを選び、記録してください。
  1. **配布物から除外する:** `sample.ply`は作業リポジトリから削除せず、ソース配布アーカイブおよび実行ファイル・インストーラーに含めません。除外を確認できる配布用ファイル一覧を残します。
  2. **権利確認後に含める:** 出所・権利者を特定し、ソース配布と該当するバイナリ配布の双方をカバーする書面許諾を得て、その根拠と適用条件を記録します。確認が完了するまでは含めません。

## B. 愛媛大学の学生実験でソースを配布

- Aの表示・依存ライセンス条件を全て満たします。授業・研究目的であることだけで、第三者ソフトやデータの配布許諾が自動的に生じるとは扱わないでください。
- 大学または研究室が著作権者となる可能性、共同著作者、雇用・研究契約、学生・教員の成果物帰属を大学の知財担当に確認します。
- Unityの教育機関・学生向け利用資格、必要なアカウント/ライセンス、Unity Runtimeを含む配布条件は配布時点の公式条項で確認します。
- 点群提供者、撮影対象・個人情報、サンプル点群の授業内配布許可も確認します。sample.plyの権利が未解決のままなら含めません。

## C. UnityでビルドしたWindowsアプリを配布

- 有効なUnity Engine Licenseと、対象の利用・配布形態に適用されるUnityの現行条件を確認します。Unity Editor本体を同梱する形ではありませんが、アプリにはUnity Runtime/Engine由来コンポーネントが含まれます。
- `THIRD_PARTY_NOTICES.md`および`licenses/third_party/unity/`のパッケージ固有LICENSE/Third Party Noticesを、ビルドに実際に含まれるバージョンに合わせて同梱します。Unityの公式案内もライセンス・帰属・第三者通知の同梱を扱っています。
- Google ARCore由来コードやKhronos OpenXR-SDK-Source等、Unity package noticesに挙がる第三者部品の帰属・Apache-2.0条件を維持します。
- AndroidでOculus Quest Supportを有効にする配布では、Unity OpenXR noticeが指定するOculus OpenXR Mobile SDK/Meta Platform Technologies SDK License Agreementの適用条件を確認します。現在の公式条件のスナップショットは`licenses/third_party/unity/`にあります。
- manifestとlockのInput Systemバージョン差異を解消し、実際のビルドに取り込まれたpackage graphからnoticeを再生成/再確認します。
- sample.ply、フォント、その他のアセット・データを実行ファイルへ含める場合、それぞれの配布権を先に確認します。

## D. Pythonランタイムと依存を同梱したWindowsアプリ

- Python 3.12.3 conda-forge環境と現在の依存パッケージ版を記録しましたが、requirementsが未固定であり、この環境をそのまま再現するlockfileもありません。配布用にPythonディストリビューション、全wheel、OS DLLを固定し、各配布物のライセンスをビルド成果物から再確認します。
- PythonランタイムのライセンスはPython Software Foundation License 2.0系です。調査環境のPython runtime license textは`licenses/third_party/runtime/`に保存しました。実際にバンドルするディストリビューションに付随するライセンス・帰属ファイルも配布してください。
- 依存パッケージのライセンス原文は`licenses/third_party/python/`に保存しています。ライセンスにより著作権表示、NOTICE、ライセンス本文を保持し、NOTICE類を改変せず同梱します。
- Open3DのTBB、NumPy/SciPyのOpenBLAS DLL、NumPyのMicrosoft runtime DLLが現環境にあります。TBB/OpenBLASの正確なバイナリ版と出所、Microsoft runtimeの再配布資格が未確認のため、実行環境同梱はこれらの確認まで保留してください。
- バイナリを同梱しない利用者インストール型と、アプリにwheel/DLLを同梱する形態を混同しないでください。
- Pillow wheelに含まれる各codec等のライセンス本文はPillowの同梱LICENSEにあります。Open3D/Matplotlib wheelのnative static-link依存関係は現時点で完全なSBOMを確認できていないため、当該wheelの正確なbuild provenanceとnoticeを揃えるまで、Python環境同梱版の配布を保留してください。

## E. 一般向け公開・再配布

- 公開前にA〜Dのうち実際の配布形態に該当する全項目を完了します。
- 著作権者によるMIT付与権限、大学/共同研究者の権利、全データと素材の権利、Unity条件、実配布バイナリの依存ライセンスを文書で確定します。
- LICENSEの著作権者欄は未確認プレースホルダーです。`NHtoyo`を含む候補者・組織の権利とMITを許諾する権限を確認し、公開前に正式な表示へ置き換えます。
- `sample.ply`の許諾が確認されていないため、現在のリポジトリ状態をそのまま「配布権クリア」と表示できません。
- CloudCompareコードのコピーは本監査では見つかりませんでしたが、将来具体的なコード由来が判明した場合は、独自MITコードと分け、元のGPL/LGPL等の条件を満たすまで該当ファイルを含む配布を保留します。
- 著作権者、共同著作者、大学の知財担当、データ提供者、必要に応じて法務の確認を得てください。

## 配布直前チェック

1. 配布するcommit・Unity lock・Python lock・ビルド成果物のハッシュ/版を記録。
2. 各バイナリに実際に含まれる第三者ライブラリとNOTICEをスキャンし、CSVを更新。
3. Unity Engine License、UCL/UPDL、Python/wheel、native DLLの配布条件を公式の該当バージョンで確認。
4. sample.plyを含む全データ・アセットに許諾根拠を付与。根拠のないファイルは配布から外す。
5. MIT対象の権利者・権限を確定し、必要なら著作権表示を更新。
6. 同梱した各ライセンス本文が対象の実配布版に対応することを確認。
