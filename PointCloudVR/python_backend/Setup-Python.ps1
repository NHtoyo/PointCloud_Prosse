[CmdletBinding()]
param(
    [string]$PythonExe = '',
    [string]$CompanyName = 'DefaultCompany',
    [string]$ProductName = 'PointCloudVR',
    [string]$EnvironmentRoot = '',
    [string]$Wheelhouse = '',
    [switch]$Offline,
    [switch]$RepairExisting
)

$ErrorActionPreference = 'Stop'
$backend = (Resolve-Path $PSScriptRoot).Path
$requirements = Join-Path $backend 'requirements.txt'

if ([string]::IsNullOrWhiteSpace($EnvironmentRoot)) {
    $EnvironmentRoot = Join-Path $env:USERPROFILE "AppData\LocalLow\$CompanyName\$ProductName\PythonEnvironment"
}
$EnvironmentRoot = [System.IO.Path]::GetFullPath($EnvironmentRoot)
$venv = Join-Path $EnvironmentRoot '.venv'
$venvPython = Join-Path $venv 'Scripts\python.exe'

$verify = @'
import importlib, importlib.metadata as m, json, sys
expected = {'open3d':'0.20.0','numpy':'2.5.3','scipy':'1.18.1','fastapi':'0.143.0','uvicorn':'0.54.0','pydantic':'2.14.0','matplotlib':'3.11.2'}
for name in expected: importlib.import_module(name)
actual = {name:m.version(name) for name in expected}
if sys.version_info[:2] != (3,12) or actual != expected: raise SystemExit(json.dumps({'python':sys.version,'packages':actual}))
print(json.dumps({'python':sys.version.split()[0],'packages':actual}, indent=2))
'@

if (Test-Path -LiteralPath $venv) {
    if (Test-Path -LiteralPath $venvPython -PathType Leaf) {
        & $venvPython -B -c $verify
        if ($LASTEXITCODE -eq 0 -and -not $RepairExisting) {
            Write-Host "既存のPython環境は正常です。変更せずそのまま使用します。" -ForegroundColor Green
            Write-Host "環境ルート: $EnvironmentRoot"
            return
        }
    }
    if (-not $RepairExisting) {
        throw "既存のPython環境は要件を満たしていません。内容は変更していません。確認後に修復する場合のみ -RepairExisting を指定してください: $venv"
    }
}

if ([string]::IsNullOrWhiteSpace($PythonExe)) {
    $launcher = Get-Command py -ErrorAction SilentlyContinue
    if ($launcher) {
        & $launcher.Source -3.12 --version *> $null
        if ($LASTEXITCODE -eq 0) { $PythonExe = 'py -3.12' }
    }
    if ([string]::IsNullOrWhiteSpace($PythonExe)) {
        $python = Get-Command python -ErrorAction SilentlyContinue
        if ($python) {
            $version = & $python.Source --version 2>&1
            if ($version -match '^Python 3\.12\.') { $PythonExe = $python.Source }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($PythonExe)) {
    throw 'Python 3.12が見つかりません。Python 3.12をユーザー権限で導入するか、-PythonExeで実行ファイルを指定してください。'
}
if ($Offline -and [string]::IsNullOrWhiteSpace($Wheelhouse)) {
    throw '-Offlineには完全なwheelhouseのパスを-Wheelhouseで指定してください。'
}
if ($Offline -and -not (Test-Path -LiteralPath $Wheelhouse -PathType Container)) {
    throw "wheelhouseが見つかりません: $Wheelhouse"
}

if (-not (Test-Path -LiteralPath $venv)) {
    New-Item -ItemType Directory -Path $EnvironmentRoot -Force | Out-Null
    if ($PythonExe -eq 'py -3.12') {
        & py -3.12 -m venv $venv
    }
    else {
        & $PythonExe -m venv $venv
    }
    if ($LASTEXITCODE -ne 0) { throw "Python仮想環境を作成できませんでした (exit=$LASTEXITCODE)。" }
}
if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    throw "仮想環境のPythonが見つかりません: $venvPython"
}

$env:PYTHONNOUSERSITE = '1'
$pipArgs = @('-m', 'pip', 'install', '--disable-pip-version-check')
if ($Offline) {
    $pipArgs += @('--no-index', '--find-links', (Resolve-Path $Wheelhouse).Path)
}
$pipArgs += @('-r', $requirements)
& $venvPython @pipArgs
if ($LASTEXITCODE -ne 0) {
    throw '依存ライブラリの導入に失敗しました。オンライン接続、または全wheelを含むwheelhouseを確認してください。'
}

& $venvPython -c $verify
if ($LASTEXITCODE -ne 0) { throw 'Pythonまたは依存ライブラリのバージョン検証に失敗しました。' }
Write-Host "Python環境の確認が完了しました。" -ForegroundColor Green
Write-Host "環境ルート: $EnvironmentRoot"
