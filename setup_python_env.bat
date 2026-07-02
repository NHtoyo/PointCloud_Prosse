@echo off
chcp 65001 >nul
echo ========================================================
echo PointCloudVR - Python Environment Automatic Setup
echo ========================================================
echo.

cd /d "%~dp0\PointCloudVR\python_backend"

echo [1/3] Checking Python Installation...
python --version >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [Error] Python is not installed or not in PATH!
    echo Please install Python 3.10+ from https://www.python.org/
    echo Make sure to check "Add Python to PATH" during installation.
    pause
    exit /b 1
)

echo [2/3] Creating Python Virtual Environment (.venv)...
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

echo.
echo [3/3] Installing Dependencies...
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
echo You can now run the noise filter tools inside Unity.
echo ========================================================
pause
