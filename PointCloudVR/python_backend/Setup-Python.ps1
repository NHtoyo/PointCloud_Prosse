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

function Write-SetupStage([string]$stage) {
    Write-Output "PCWB_STAGE:$stage"
}

if ([string]::IsNullOrWhiteSpace($EnvironmentRoot)) {
    $EnvironmentRoot = Join-Path $env:USERPROFILE "AppData\LocalLow\$CompanyName\$ProductName\PythonEnvironment"
}
$EnvironmentRoot = [System.IO.Path]::GetFullPath($EnvironmentRoot)
$venv = Join-Path $EnvironmentRoot '.venv'
$venvPython = Join-Path $venv 'Scripts\python.exe'
$env:MPLBACKEND = 'Agg'
$env:MPLCONFIGDIR = Join-Path $env:TEMP ('pcwb-setup-verify-' + [guid]::NewGuid().ToString('N'))

$verify = @'
import importlib, importlib.metadata as m, json, sys
from pathlib import Path
import numpy as np
import open3d as o3d
from scipy.spatial import cKDTree
from scipy.sparse import csr_matrix
from scipy.sparse.csgraph import connected_components
from matplotlib.figure import Figure
from matplotlib.backends.backend_agg import FigureCanvasAgg
expected = {}
for raw in Path(sys.argv[1]).read_text(encoding='utf-8').splitlines():
    line = raw.split('#', 1)[0].strip()
    if '==' in line:
        name, version = line.split('==', 1)
        expected[name.strip()] = version.strip()
for name in expected: importlib.import_module(name)
actual = {name:m.version(name) for name in expected}
legacy = {'open3d':'0.19.0','numpy':'2.4.6','scipy':'1.17.1','fastapi':'0.136.3','uvicorn':'0.48.0','pydantic':'2.13.4','matplotlib':'3.11.2'}
versions_ok = all(actual.get(name) in {version, legacy.get(name)} for name, version in expected.items())
if sys.version_info[:2] != (3,12) or not versions_ok: raise SystemExit(json.dumps({'python':sys.version,'packages':actual}))
points = np.array([[0.,0.,0.],[1.,0.,0.]])
assert cKDTree(points).query(points)[0].max() == 0.
assert connected_components(csr_matrix([[0,1],[1,0]]))[0] == 1
cloud = o3d.geometry.PointCloud()
cloud.points = o3d.utility.Vector3dVector(points)
assert len(cloud.voxel_down_sample(0.5).points) == 2
figure = Figure()
FigureCanvasAgg(figure)
figure.subplots().plot([0,1],[0,1])
print(json.dumps({'python':sys.version.split()[0],'packages':actual}, indent=2))
'@

Write-SetupStage 'PYTHON_CHECK'

if (Test-Path -LiteralPath $venv) {
    if (Test-Path -LiteralPath $venvPython -PathType Leaf) {
        & $venvPython -B -c $verify $requirements *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "既存のPython環境は正常です。変更せずそのまま使用します。" -ForegroundColor Green
            Write-Host "環境ルート: $EnvironmentRoot"
            return
        }
    }
    throw "指定先に既存のPython環境があります。既存環境は変更しません。新しいEnvironmentRootを指定してください: $venv"
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
Write-Output "PCWB_PYTHON:$PythonExe"
if ($Offline -and [string]::IsNullOrWhiteSpace($Wheelhouse)) {
    throw '-Offlineには完全なwheelhouseのパスを-Wheelhouseで指定してください。'
}
if ($Offline -and -not (Test-Path -LiteralPath $Wheelhouse -PathType Container)) {
    throw "wheelhouseが見つかりません: $Wheelhouse"
}

if (-not (Test-Path -LiteralPath $venv)) {
    New-Item -ItemType Directory -Path $EnvironmentRoot -Force | Out-Null
    Write-SetupStage 'VENV_CREATE'
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
$env:PYTHONUNBUFFERED = '1'
$env:PIP_NO_INPUT = '1'
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'
$env:MPLBACKEND = 'Agg'
$env:MPLCONFIGDIR = Join-Path $EnvironmentRoot 'matplotlib-config'
$pipArgs = @('-m', 'pip', 'install', '--disable-pip-version-check')
$pipArgs += @('--no-input', '--progress-bar', 'off')
if ($Offline) {
    $pipArgs += @('--no-index', '--find-links', (Resolve-Path $Wheelhouse).Path)
}
$pipArgs += @('-r', $requirements)
Write-SetupStage 'DEPENDENCIES'
& $venvPython @pipArgs
if ($LASTEXITCODE -ne 0) {
    throw '依存ライブラリの導入に失敗しました。オンライン接続、または全wheelを含むwheelhouseを確認してください。'
}

Write-SetupStage 'VERIFY'
& $venvPython -m pip check
if ($LASTEXITCODE -ne 0) { throw 'pip checkで依存関係の不整合が見つかりました。' }
& $venvPython -B -c $verify $requirements
if ($LASTEXITCODE -ne 0) { throw 'Pythonまたは依存ライブラリのバージョン検証に失敗しました。' }
Write-Host "Python環境の確認が完了しました。" -ForegroundColor Green
Write-Host "環境ルート: $EnvironmentRoot"
