@echo off
echo ========================================================
echo PointCloudVR - Python Environment Automatic Setup
echo ========================================================
echo.
echo IMPORTANT: Run this BEFORE opening Unity.
echo Requires internet. May take 3-5 minutes.
echo.

cd /d "%~dp0\PointCloudVR\python_backend"
if %ERRORLEVEL% NEQ 0 (
    echo [Error] Cannot find PointCloudVR\python_backend folder.
    echo Please run this bat from inside PointCloud_Prosse folder.
    pause
    exit /b 1
)

:: -------------------------------------------------------
:: [1/4] Find compatible Python (3.8 - 3.12)
::       open3d does NOT support Python 3.13+
:: -------------------------------------------------------
echo [1/4] Checking Python version...
set PYTHON_CMD=

:: Prefer py -3.11 via Python Launcher (most reliable)
py -3.11 --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Found: Python 3.11 ^(py launcher^)
    set PYTHON_CMD=py -3.11
    goto :check_venv
)

:: Try py -3.12
py -3.12 --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Found: Python 3.12 ^(py launcher^)
    set PYTHON_CMD=py -3.12
    goto :check_venv
)

:: Try py -3.10
py -3.10 --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Found: Python 3.10 ^(py launcher^)
    set PYTHON_CMD=py -3.10
    goto :check_venv
)

:: Try py -3.9
py -3.9 --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo Found: Python 3.9 ^(py launcher^)
    set PYTHON_CMD=py -3.9
    goto :check_venv
)

:: Check default python - reject 3.13 and above
python --version >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    python --version 2>&1 | findstr /C:"Python 3.13" >nul
    if %ERRORLEVEL% EQU 0 (
        echo [Warning] Python 3.13 is not compatible with open3d.
        echo           Installing Python 3.11 alongside...
        goto :install_py311
    )
    python --version 2>&1 | findstr /C:"Python 3.14" >nul
    if %ERRORLEVEL% EQU 0 (
        echo [Warning] Python 3.14 is not compatible with open3d.
        echo           Installing Python 3.11 alongside...
        goto :install_py311
    )
    echo Found: Python ^(default^)
    set PYTHON_CMD=python
    goto :check_venv
)

echo Python not found. Installing Python 3.11...
goto :install_py311

:: -------------------------------------------------------
:: Install Python 3.11
:: -------------------------------------------------------
:install_py311
echo.
echo Downloading Python 3.11.9 installer...
set PYTHON_URL=https://www.python.org/ftp/python/3.11.9/python-3.11.9-amd64.exe
set PYTHON_INSTALLER=%TEMP%\python311_installer.exe

curl -L -o "%PYTHON_INSTALLER%" "%PYTHON_URL%"
if %ERRORLEVEL% NEQ 0 (
    echo [Error] Download failed. Check internet connection.
    echo Manual download: https://www.python.org/downloads/release/python-3119/
    pause
    exit /b 1
)

echo Installing Python 3.11 silently...
"%PYTHON_INSTALLER%" /quiet InstallAllUsers=0 PrependPath=1 Include_test=0
if %ERRORLEVEL% NEQ 0 (
    echo [Error] Python installation failed.
    pause
    exit /b 1
)
del "%PYTHON_INSTALLER%"

for /f "tokens=*" %%i in ('powershell -NoProfile -Command "[System.Environment]::GetEnvironmentVariable(\"Path\",\"User\")"') do set PATH=%%i;%PATH%

echo Python 3.11 installed.
set PYTHON_CMD=py -3.11

:: -------------------------------------------------------
:: [2/4] Setup virtual environment (.venv)
:: -------------------------------------------------------
:check_venv
echo.
echo [2/4] Setting up virtual environment (.venv)...

:: If .venv exists, check if it was built with Python 3.13+ (incompatible)
if exist ".venv\Scripts\python.exe" (
    .venv\Scripts\python.exe --version 2>&1 | findstr /C:"Python 3.13" >nul
    if %ERRORLEVEL% EQU 0 (
        echo [Info] Existing .venv uses Python 3.13 - incompatible. Recreating...
        rmdir /s /q ".venv"
        goto :create_venv
    )
    .venv\Scripts\python.exe --version 2>&1 | findstr /C:"Python 3.14" >nul
    if %ERRORLEVEL% EQU 0 (
        echo [Info] Existing .venv uses Python 3.14 - incompatible. Recreating...
        rmdir /s /q ".venv"
        goto :create_venv
    )
    echo Virtual environment is compatible. Skipping creation.
    goto :upgrade_pip
)

:: .venv folder exists but broken (no python.exe)
if exist ".venv" (
    echo [Info] Removing incomplete .venv...
    rmdir /s /q ".venv"
)

:create_venv
%PYTHON_CMD% -m venv .venv
if %ERRORLEVEL% NEQ 0 (
    echo [Error] Failed to create virtual environment.
    pause
    exit /b 1
)
echo Virtual environment created.

:: -------------------------------------------------------
:: [3/4] Upgrade pip
:: -------------------------------------------------------
:upgrade_pip
echo.
echo [3/4] Upgrading pip...
.venv\Scripts\python.exe -m pip install --upgrade pip >nul

:: -------------------------------------------------------
:: [4/4] Install dependencies
:: -------------------------------------------------------
echo.
echo [4/4] Installing dependencies (open3d, numpy, scipy...)
echo     Note: open3d may take 1-3 minutes. Please wait.
if exist "requirements.txt" (
    .venv\Scripts\pip.exe install -r requirements.txt
) else (
    .venv\Scripts\pip.exe install open3d numpy scipy fastapi uvicorn pydantic
)

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [Error] Dependency installation failed.
    echo - Check internet connection.
    echo - Python 3.8 to 3.12 is required. 3.13+ is NOT supported by open3d.
    pause
    exit /b 1
)

echo.
echo ========================================================
echo   Setup Complete^^! Environment is ready.
echo ========================================================
echo.
echo Next steps:
echo   1. Open Unity Hub - open PointCloudVR folder
echo   2. Open VRTestScene and press Play
echo.
pause
