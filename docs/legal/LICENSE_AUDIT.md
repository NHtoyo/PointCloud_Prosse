# ライセンス監査

監査日: 2026-10-09

対象: `NHtoyo/PointCloud_Prosse` の `main`、HEAD `2748339ce2734ca6e9ff49e725944e39f38b5117`
監査は配布準備のための技術的な確認であり、法律意見ではありません。

## 結論

独自開発コードにMIT Licenseを採用する場合の標準本文をルート`LICENSE`に配置しました。著作権者欄は未確認プレースホルダーのため、これは権利確認や正式な公開許諾が完了したことを意味しません。意図する範囲は、プロジェクト権利者が保有し、第三者由来でない独自コードに限ります。Unity、Unityパッケージ、Python依存、CloudCompare等の第三者コード、サンプル点群その他のデータ・素材をMITとして再ライセンスするものではありません。

`LICENSE`の著作権者欄は`[COPYRIGHT HOLDER TO BE CONFIRMED]`という未確認プレースホルダーです。`NHtoyo`やリポジトリ所有者が法的著作権者、MIT許諾権限者であるとは確認できていません。GitHubアカウントやGit author名だけでは、大学・雇用主との権利帰属や共同著作者の有無も確定できません。正式公開前に本人および必要に応じて所属機関の確認が必須です。

## 確認した構成

- リポジトリルート: `E:\VR`。ブランチ`main`、originは`https://github.com/NHtoyo/PointCloud_Prosse.git`。
- ローカルHEADとGitHub `origin/main`は確認時点で一致。sparse-checkoutではありません。
- 既存のソースファイルに統一的なSPDX/著作権ヘッダーは見つかりませんでした。LICENSE追加後の独自コードの範囲は権利者確認を前提とします。
- `PointCloudVR/Assets/StreamingAssets/sample.ply`はGit管理されていますが、データの出所・撮影者・配布許諾が不明です。MITの対象とせず、配布前に許諾を記録するか、配布物から除外してください。
- `PointCloudVR/python_backend/requirements.txt`は7つの直接依存を列挙しますが、バージョン固定がありません。調査した`.venv`ではPython 3.12.3 (conda-forge)を使用していました。READMEはPython 3.11を自動導入すると説明しますが、setup scriptは複数バージョンを選択できるため、説明・許容範囲・実環境が一致していません。
- Unity Editorは`6000.4.7f1`。`Packages/manifest.json`のInput Systemは`1.7.0`、`packages-lock.json`の解決版は`1.19.0`です。再現可能なビルドの前にどちらを正とするか確認が必要です。
- Windows Python環境にはOpen3Dの` tbb12.dll`、NumPy/SciPyのOpenBLAS DLL、NumPyの`msvcp140-*.dll`がありました。TBB/OpenBLASの当該ビルド版とMicrosoft runtimeの出所・再配布条件は、現環境のバイナリだけでは完全に確定できません。
- Pillow 12.3.0のWindows wheelにはBrotli、FreeType、HarfBuzz、LittleCMS、libavif、libjpeg-turbo、libpng、libwebp、OpenJPEG、TIFF、XZ、zlib-ng等のnoticeが同梱されており、wheelのlicenseファイルを保存しました。Open3D/Matplotlibのnative extensionに静的リンクされた全依存関係は現環境から完全には列挙できていません。

## 適用範囲と例外

1. `LICENSE`は独自に作成され、配布権限が確認できたプロジェクトコードに対するMIT方針を示します。第三者コードやデータに対するライセンス付与権限を生み出すものではありません。
2. Unity本体/RuntimeおよびUnity公式パッケージはUnityの適用ライセンスとEngine License条件に従います。Unity Companion License (UCL)やUnity Package Distribution License (UPDL)はMITではありません。
3. Pythonパッケージは各パッケージのライセンスに従います。パッケージ原文・UnityのThird Party Noticesは`licenses/third_party/`に保存し、実環境スナップショットは`docs/legal/DEPENDENCY_INVENTORY.csv`に記録しました。
4. CloudCompareを参考にした考え方や画面操作だけでは、CloudCompare本体のGPLが自動的に適用されるとは判断しません。コードの複製・翻案があれば、そのファイルの元ライセンスを別途遵守します。個別調査は[CC_CODE_PROVENANCE_AUDIT.md](CC_CODE_PROVENANCE_AUDIT.md)を参照してください。
5. PLYその他の点群、画像、フォント、サンプルデータは、各権利者の許可が確認できるまでMIT対象外として扱います。

GitHubの公式説明も、ライセンスがない公開リポジトリには通常の著作権法が適用され、再配布等の許諾が自動で付くわけではないと説明しています: [GitHub Docs: Licensing a repository](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/licensing-a-repository)。MIT本文は[OSIの標準文面](https://opensource.org/license/mit)に合わせています。

## 未確認事項の優先度

### ソース配布前に必須

- `LICENSE`のプレースホルダーを確定するため、著作権者、共同著作者、大学・雇用主等の権利帰属、およびMITを付与する権限を文書で確認する。確認前はこのリポジトリ全体をMIT許諾済みと表示しない。
- `sample.ply`を含む全データ・素材の出所とソース再配布許諾を確認するか、配布用アーカイブから除外する。作業ツリーのファイル削除はこの監査では行わない。
- CloudCompare由来コードが後から特定された場合、該当箇所の由来・ライセンスを調べ、独自MIT対象との境界を確定する。

### exe配布前に必須

- 上記の著作権者・MIT許諾権限と、アプリに含める全データ・素材の配布権を確認する。sample.plyを同梱するなら書面許諾、そうでなければビルド成果物からの除外を確認する。
- 対象プラットフォーム・用途に対するUnity Engine Licenseの条件、実際のUnity package graph、同梱すべき各LICENSE/NOTICEを配布時点の公式条件で確認する。
- Pythonランタイム・依存wheel・native DLLの実際の配布版を固定し、Open3D/Matplotlibのstatic-link依存、TBB/OpenBLASの正確な版、Microsoft Visual C++ Runtimeの再配布根拠を確認する。
- Unity Input Systemのmanifest/lock差異を解消し、最終ビルドの依存バージョンとライセンス原文の対応を確認する。

### 推奨改善

- Python依存のlockfileと、各配布ビルドから生成するSBOM/notice一覧を整備する。READMEのPython導入説明とsetup scriptの許容バージョンも一致させる。
- 正式著者名・著者順・所属・引用希望・公開版/DOIが確定した後に`CITATION.cff`を作成する。
- 独自コードと第三者由来コードの由来記録、権利者確認記録、バージョン付きライセンス監査を継続する。

ソース配布またはexe配布に該当する「必須」項目が解決するまでは、その配布形態を「全て再配布許可済み」と表示しないでください。選択肢と実務手順は[DISTRIBUTION_GUIDE.md](DISTRIBUTION_GUIDE.md)に記載しました。

## 引用情報

`CITATION.cff`は作成していません。Git履歴上のユーザー名だけでは正式著者名を確定できず、確認済みの論文・DOI・公開バージョンもありません。正確な著者名、著者順、所属、ソフトウェアの引用希望、公開版/DOIが確定した後に作成してください。引用ファイルは著作権ライセンスの代わりにはなりません。
