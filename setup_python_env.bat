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
:: [1/4] Python チェック → なければ自動インストール
:: -------------------------------------------------------
echo [1/4] Checking Python Installation...
set PYTHON_CMD=

:: まず python コマンドを確認
python --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Python found: 'python' command available.
    set PYTHON_CMD=python
    goto :setup_venv
)

:: 次に py (Python Launcher) を確認
py --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Python found: 'py' command available.
    set PYTHON_CMD=py
    goto :setup_venv
)

:: どちらも見つからない → 自動インストール
echo Python not found. Downloading Python 3.11 installer...
set PYTHON_URL=https://www.python.org/ftp/python/3.11.9/python-3.11.9-amd64.exe
set PYTHON_INSTALLER=%TEMP%\python_installer.exe

curl -L -o "%PYTHON_INSTALLER%" "%PYTHON_URL%"
if %ERRORLEVEL% NEQ 0 (
    echo [Error] Failed to download Python installer.
    echo ネットワーク接続を確認するか、https://www.python.org/ から手動でインストールしてください。
    pause
    exit /b 1
)

echo Installing Python silently (this may take a minute)...
"%PYTHON_INSTALLER%" /quiet InstallAllUsers=1 PrependPath=1 Include_test=0
if %ERRORLEVEL% NEQ 0 (
    echo [Error] Python installation failed.
    pause
    exit /b 1
)
del "%PYTHON_INSTALLER%"

:: PATH を即座に反映するため PowerShell で環境変数を更新
for /f "tokens=*" %%i in ('powershell -NoProfile -Command "[System.Environment]::GetEnvironmentVariable(\"Path\",\"Machine\")"') do set PATH=%%i;%PATH%

echo Python installed successfully.
set PYTHON_CMD=python

:: -------------------------------------------------------
:: [2/4] 仮想環境の作成
:: -------------------------------------------------------
:setup_venv
echo.
echo [2/4] Creating Python Virtual Environment (.venv)...
if exist ".venv\Scripts\python.exe" (
    echo Virtual environment already exists and is healthy, skipping creation.
    goto :upgrade_pip
)

if exist ".venv" (
    echo [Warning] .venv folder exists but python.exe is missing. Recreating...
    rmdir /s /q ".venv"
)

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
    echo [Error] Failed to install dependencies!
    echo インターネット接続を確認してください。
    echo もし "pip is not recognized" と出た場合、手動で以下を実行してください:
    echo   PointCloudVR\python_backend\.venv\Scripts\pip install -r requirements.txt
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

