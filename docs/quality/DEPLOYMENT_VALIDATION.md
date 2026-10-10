# PointCloud_Prosse Deployment Validation

更新日: 2026-10-11
判定: Player buildとPython backend配置は`PASS`、別PC/オフライン導入は`NOT_RUN`

## Playerの配置

Windows x64 Playerをビルドすると、Editor postprocessorがPython backendの実行スクリプト、固定requirements、セットアップスクリプトをPlayer EXEの隣の`python_backend/`へコピーします。`.venv`、テスト、出力、wheelhouseは配布物へ入れません。Unity/Gitを利用者PCへ入れなくてもPlayerを直接起動できる配置を想定しています。

点群フォルダーはPlayerフォルダーの親に置きます。`PointCloudLoader`の相対既定値は`Application.dataPath/../../PointCloudData`です。

```text
Distribution/
  PointCloudData/                 # PLY、必要な計測データ
  Player/
    PointCloudVR.exe
    UnityPlayer.dll
    PointCloudVR_Data/
    python_backend/
      *.py
      requirements.txt
      Setup-Python.ps1
```

実測PLY、QAダンプ、`Assets/_Recovery`はこのパッケージへ入れないでください。

## Python

対応Pythonは3.12.x。直接依存の固定値は次の通りです。

| Package | Version |
|---|---:|
| open3d | 0.20.0 |
| numpy | 2.5.3 |
| scipy | 1.18.1 |
| fastapi | 0.143.0 |
| uvicorn | 0.54.0 |
| pydantic | 2.14.0 |
| matplotlib | 3.11.2 |

最新Playerで使った検証venvはPython 3.12.4、上記package固定値で、Pythonテスト61件が成功しました。これは同じPCの既存venvによる検証で、別PCへの可搬性の証明ではありません。transitive依存までhash付きでlockしたrequirementsではありません。

Python setupはユーザー書込領域`Application.persistentDataPath/PythonEnvironment/.venv`を使い、管理者権限を前提としません。PowerShellで`python_backend/Setup-Python.ps1`を実行するか、Python機能の初回使用時にPlayerが準備を試みます。通常setupはPython 3.12とインターネットが必要です。`-Offline -Wheelhouse <path>`は全wheelを含むwheelhouseが別途ある場合だけ使えます。wheelhouseはリポジトリ/Playerに同梱していません。

Open3D等のwheelを再配布する場合の全依存ライセンス、完全オフライン導入、インターネットなし、管理ポリシーでPowerShell/PyPIが禁止されたPCは未検証です。ライセンス確認前にPython runtime/wheelsを公開物へ同梱しないでください。

Pythonがない/壊れている場合に影響するのはPython連携処理です。点群読込、表示、編集、距離計測などのPython非依存機能は分離して利用できます。解析処理の各ボタンは実機GUIでの失敗復帰をまだ確認していません。

空白・日本語を含むPlayer/backendパスはprocess引数をquoteする実装ですが、別ドライブへの移動・日本語パスの実機試験は`NOT_RUN`です。

## 今回の再現可能な検証

| 検証 | 結果 | 証拠/条件 |
|---|---|---|
| Windows x64 Player build | `PASS` | Unity `6000.4.7f1`; QA run `HardwareDeploy-20261011-001500` (artifacts remain outside Git) |
| Python backendのPlayer隣接配置 | `PASS` | build後にrequirements、setup、主要entrypointの存在を自動検査。`.venv`なしも検査 |
| Unity EditMode | `PASS` | 2/2 |
| Unity PlayMode | `PASS` | 5/5 |
| Python backend pytest | `PASS` | 61 passed |
| Python compileall | `PASS` | `python_backend` |
| setup PowerShell parser | `PASS` | `Setup-Python.ps1` parse only。新規インストールは実行していない |
| 別PCでのセットアップ/実行 | `NOT_RUN` | 試験機なし |
| Offline wheelhouse | `NOT_RUN` | wheelhouseを同梱せず、再配布権も未確認 |

最新Playerはheadlessの強制D3D11でgraphics device/FL11.1、shader、buffer、100,000点load/Octreeまで記録しました。`-batchmode -quit`はPlayerを終了させず、隔離プロセスを試験後に強制停止しました。これは処理途中終了であり、正常終了確認やクラッシュ修正の根拠にしません。

## 利用者向け導入手順

1. 展開先に上記フォルダー構成を保ちます。
2. Playerの`python_backend/`と`PointCloudData/`が配置されていることを確認します。
3. `Player/PointCloudVR.exe`を起動し、画面上部の「PC互換性・Python診断」でGPU/APIとPython statusを確認します。
4. JSONレポートはユーザー操作で保存し、`hardware_compatibility_report.json`をサポートへ渡す場合は内容を確認します。
5. Python解析が必要ならPython 3.12.xをユーザー領域に導入し、セットアップを実行します。インターネットを使えない環境では、管理者が別途wheelhouseと依存ライセンスを準備してください。

一般公開、配布物へのPython wheel同梱、学生PCでの授業運用は未承認/未検証です。
