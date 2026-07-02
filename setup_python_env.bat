@echo off
chcp 65001 >nul
echo ========================================================
echo PointCloudVR - Python Environment Automatic Setup
echo ========================================================
echo.

cd /d "%~dp0\PointCloudVR\python_backend"

:: -------------------------------------------------------
:: [1/4] Python チェック → なければ自動インストール
:: -------------------------------------------------------
echo [1/4] Checking Python Installation...
python --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Python is already installed.
    goto :setup_venv
)

echo Python not found. Downloading Python 3.11 installer...
set PYTHON_URL=https://www.python.org/ftp/python/3.11.9/python-3.11.9-amd64.exe
set PYTHON_INSTALLER=%TEMP%\python_installer.exe

:: curl は Windows 10 以降は標準搭載
curl -L -o "%PYTHON_INSTALLER%" "%PYTHON_URL%"
if %ERRORLEVEL% NEQ 0 (
    echo [Error] Failed to download Python installer.
    echo Please install Python 3.10+ manually from https://www.python.org/
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

:: -------------------------------------------------------
:: [2/4] 仮想環境の作成
:: -------------------------------------------------------
:setup_venv
echo.
echo [2/4] Creating Python Virtual Environment (.venv)...
if not exist ".venv" (
    python -m venv .venv
    if %ERRORLEVEL% NEQ 0 (
        echo [Error] Failed to create virtual environment!
        pause
        exit /b 1
    )
    echo Virtual environment created successfully.
) else (
    echo Virtual environment already exists, skipping creation.
)

:: -------------------------------------------------------
:: [3/4] pip アップデート
:: -------------------------------------------------------
echo.
echo [3/4] Upgrading pip...
.venv\Scripts\python -m pip install --upgrade pip >nul

:: -------------------------------------------------------
:: [4/4] 依存ライブラリのインストール
:: -------------------------------------------------------
echo.
echo [4/4] Installing Dependencies...
if exist "requirements.txt" (
    .venv\Scripts\pip install -r requirements.txt
) else (
    .venv\Scripts\pip install open3d numpy scipy fastapi uvicorn pydantic
)

if %ERRORLEVEL% NEQ 0 (
    echo [Error] Failed to install dependencies!
    pause
    exit /b 1
)

echo.
echo ========================================================
echo Environment Setup Completed Successfully!
echo You can now open the project in Unity and press Play.
echo ========================================================
pause
