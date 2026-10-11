@echo off
setlocal

set "SETUP_SCRIPT=%~dp0PointCloudVR\python_backend\Setup-Python.ps1"
if not exist "%SETUP_SCRIPT%" (
    echo [Error] Python setup script not found: "%SETUP_SCRIPT%"
    exit /b 1
)

echo PointCloudVR Python setup requires CPython 3.12.x.
echo The script creates or verifies the user environment without removing an existing one.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SETUP_SCRIPT%"
set "RESULT=%ERRORLEVEL%"
if not "%RESULT%"=="0" (
    echo [Error] Python setup failed with exit code %RESULT%.
)
exit /b %RESULT%
