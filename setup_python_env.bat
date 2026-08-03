@echo off
chcp 65001 >nul
echo ========================================================
echo PointCloudVR - Python Environment Automatic Setup
echo ========================================================
echo.
echo 【重要】このスクリプトはUnityで Play する前に一度だけ実行してください。
echo インターネット接続が必要です。完了まで数分かかります。
echo.

cd /d "%~dp0\PointCloudVR\python_backend"
if %ERRORLEVEL% NEQ 0 (
    echo [Error] PointCloudVR\python_backend フォルダが見つかりません。
    echo このbatファイルは PointCloud_Prosse フォルダ直下に置いてください。
    pause
    exit /b 1
)

:: -------------------------------------------------------
:: [1/4] Pythonのバージョン確認 → 3.8〜3.12 が必要
::        3.13以上または未インストールの場合は 3.11 を自動インストール
:: -------------------------------------------------------
echo [1/4] Checking Python version compatibility...
set PYTHON_CMD=
set NEED_INSTALL=1

:: Python 3.11 が py ランチャー経由で使えるか確認（最優先）
py -3.11 --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Python 3.11 found via py launcher.
    set PYTHON_CMD=py -3.11
    set NEED_INSTALL=0
    goto :check_venv_compat
)

:: system の python のバージョンを取得して確認
python --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    :: バージョン番号の先頭部分を取得（例: "Python 3.11.9" → "3.11"）
    for /f "tokens=2" %%v in ('python --version 2^>^&1') do set PY_VER=%%v
    echo Detected Python version: %PY_VER%

    :: メジャー.マイナーを取り出す（例: 3.11.9 → 3.11）
    for /f "tokens=1,2 delims=." %%a in ("%PY_VER%") do (
        set PY_MAJOR=%%a
        set PY_MINOR=%%b
    )

    :: 3.13以上は open3d 非対応のため Python 3.11 を別途インストール
    if %PY_MAJOR% GEQ 3 (
        if %PY_MINOR% GEQ 13 (
            echo [Warning] Python %PY_VER% is too new. open3d requires Python 3.8-3.12.
            echo           Python 3.11 will be installed alongside your current Python.
            set NEED_INSTALL=1
            goto :install_python311
        )
        :: 3.8〜3.12 は OK
        if %PY_MINOR% GEQ 8 (
            echo Python %PY_VER% is compatible with open3d.
            set PYTHON_CMD=python
            set NEED_INSTALL=0
            goto :check_venv_compat
        )
        :: 3.7以下は古すぎる
        echo [Warning] Python %PY_VER% is too old. Installing Python 3.11...
        set NEED_INSTALL=1
        goto :install_python311
    )
)

:: py launcher で確認
py --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    for /f "tokens=2" %%v in ('py --version 2^>^&1') do set PY_VER=%%v
    echo Detected Python version: %PY_VER% (via py launcher)
    for /f "tokens=1,2 delims=." %%a in ("%PY_VER%") do (
        set PY_MAJOR=%%a
        set PY_MINOR=%%b
    )
    if %PY_MAJOR% GEQ 3 (
        if %PY_MINOR% GEQ 13 (
            echo [Warning] Python %PY_VER% is too new. Installing Python 3.11...
            set NEED_INSTALL=1
            goto :install_python311
        )
        if %PY_MINOR% GEQ 8 (
            echo Python %PY_VER% is compatible.
            set PYTHON_CMD=py
            set NEED_INSTALL=0
            goto :check_venv_compat
        )
    )
)

:: Python が見つからない場合
echo Python not found. Python 3.11 will be installed.

:install_python311
echo.
echo Downloading Python 3.11.9 installer...
set PYTHON_URL=https://www.python.org/ftp/python/3.11.9/python-3.11.9-amd64.exe
set PYTHON_INSTALLER=%TEMP%\python311_installer.exe

curl -L -o "%PYTHON_INSTALLER%" "%PYTHON_URL%"
if %ERRORLEVEL% NEQ 0 (
    echo [Error] Failed to download Python installer.
    echo ネットワーク接続を確認するか、https://www.python.org/downloads/release/python-3119/ から
    echo 手動でインストールしてください。
    pause
    exit /b 1
)

echo Installing Python 3.11 silently (this may take a minute)...
"%PYTHON_INSTALLER%" /quiet InstallAllUsers=0 PrependPath=1 Include_test=0
if %ERRORLEVEL% NEQ 0 (
    echo [Error] Python installation failed.
    pause
    exit /b 1
)
del "%PYTHON_INSTALLER%"

:: PATH を即座に反映
for /f "tokens=*" %%i in ('powershell -NoProfile -Command "[System.Environment]::GetEnvironmentVariable(\"Path\",\"User\")"') do set PATH=%%i;%PATH%

echo Python 3.11 installed successfully.

:: インストール後は py -3.11 で明示的に使う
py -3.11 --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    set PYTHON_CMD=py -3.11
) else (
    set PYTHON_CMD=python
)

:: -------------------------------------------------------
:: [2/4] 仮想環境の作成
::        既存の .venv が open3d 非対応バージョンで作られた場合も削除・再作成
:: -------------------------------------------------------
:check_venv_compat
echo.
echo [2/4] Creating Python Virtual Environment (.venv)...

:: .venv の python.exe が存在するか確認
if not exist ".venv\Scripts\python.exe" (
    :: .venv フォルダ自体があれば壊れているので削除
    if exist ".venv" (
        echo [Info] Removing incomplete .venv folder...
        rmdir /s /q ".venv"
    )
    goto :create_venv
)

:: .venv が存在する場合、そのPythonバージョンを確認
for /f "tokens=2" %%v in ('.venv\Scripts\python.exe --version 2^>^&1') do set VENV_VER=%%v
for /f "tokens=1,2 delims=." %%a in ("%VENV_VER%") do (
    set VENV_MAJOR=%%a
    set VENV_MINOR=%%b
)

echo Existing .venv Python version: %VENV_VER%

:: .venv が3.13以上で作られていたら削除して作り直す
if %VENV_MAJOR% GEQ 3 (
    if %VENV_MINOR% GEQ 13 (
        echo [Warning] .venv was created with Python %VENV_VER% which is incompatible with open3d.
        echo           Deleting and recreating with compatible Python...
        rmdir /s /q ".venv"
        goto :create_venv
    )
)

echo Virtual environment already exists and is compatible. Skipping creation.
goto :upgrade_pip

:create_venv
%PYTHON_CMD% -m venv .venv
if %ERRORLEVEL% NEQ 0 (
    echo [Error] Failed to create virtual environment!
    echo 手動で以下を実行してみてください:
    echo   cd PointCloudVR\python_backend
    echo   python -m venv .venv
    pause
    exit /b 1
)
echo Virtual environment created successfully.

:: -------------------------------------------------------
:: [3/4] pip アップデート
:: -------------------------------------------------------
:upgrade_pip
echo.
echo [3/4] Upgrading pip...
.venv\Scripts\python.exe -m pip install --upgrade pip >nul

:: -------------------------------------------------------
:: [4/4] 依存ライブラリのインストール
:: -------------------------------------------------------
echo.
echo [4/4] Installing Dependencies (open3d, numpy, scipy...)
echo   ※ Open3D のインストールには1〜3分かかります。しばらくお待ちください。
if exist "requirements.txt" (
    .venv\Scripts\pip.exe install -r requirements.txt
) else (
    .venv\Scripts\pip.exe install open3d numpy scipy fastapi uvicorn pydantic
)

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [Error] Failed to install dependencies!
    echo.
    echo 考えられる原因:
    echo   1. インターネット接続を確認してください
    echo   2. Python バージョンが対応していない可能性があります
    echo      .venv\Scripts\python.exe --version を実行して確認してください
    echo      （Python 3.8〜3.12 が必要）
    echo   3. このスクリプトをもう一度実行すると .venv を作り直して再試行します
    pause
    exit /b 1
)

echo.
echo ========================================================
echo   Environment Setup Completed Successfully!
echo ========================================================
echo.
echo 次のステップ:
echo   1. Unity Hub から PointCloudVR フォルダを開く
echo   2. VRTestScene を開いて Play ボタンを押す
echo.
pause
