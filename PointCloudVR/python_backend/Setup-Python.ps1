[CmdletBinding()]
param(
    [string]$PythonExe = '',
    [string]$CompanyName = 'DefaultCompany',
    [string]$ProductName = 'PointCloudVR',
    [string]$EnvironmentRoot = '',
    [string]$PipLogPath = '',
    [string]$Wheelhouse = '',
    [switch]$Offline,
    [switch]$RepairExisting,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$backend = (Resolve-Path $PSScriptRoot).Path
$requirements = Join-Path $backend 'requirements.txt'

function Write-SetupStage([string]$stage) {
    Write-Output "PCWB_STAGE:$stage"
}

function Quote-NativeArgument([string]$value) {
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $value.ToCharArray()) {
        if ($character -eq '\') {
            $slashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (2 * $slashes + 1)))
            [void]$builder.Append('"')
        }
        else {
            if ($slashes -gt 0) { [void]$builder.Append(('\' * $slashes)) }
            [void]$builder.Append($character)
        }
        $slashes = 0
    }
    if ($slashes -gt 0) { [void]$builder.Append(('\' * (2 * $slashes))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Sanitize-PipLog([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return }
    $maximumBytes = 16 * 1024 * 1024
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $lines = New-Object 'System.Collections.Generic.Queue[string]'
    $retainedBytes = 0
    $wasTruncated = $false
    $temporaryPath = $path + '.sanitized-' + [guid]::NewGuid().ToString('N')
    $backupPath = $path + '.backup-' + [guid]::NewGuid().ToString('N')
    $reader = $null
    $writer = $null
    try {
        $reader = New-Object System.IO.StreamReader($path, [System.Text.Encoding]::UTF8, $true)
        while (($line = $reader.ReadLine()) -ne $null) {
            $line = [regex]::Replace($line, '(?i)(https?://)[^/@\s]+:[^/@\s]+@', '$1[REDACTED]@')
            $line = [regex]::Replace($line, '(?i)(token|password|passwd|api[_-]?key|access[_-]?key|secret)(\s*[=:]\s*)[^\s&]+', '$1$2[REDACTED]')
            $line = [regex]::Replace($line, '(?i)([?&](?:access[_-]?token|token|api[_-]?key|signature)=)[^&\s]+', '$1[REDACTED]')
            $encoded = $line + "`r`n"
            $lineBytes = $utf8.GetByteCount($encoded)
            if ($lineBytes -gt $maximumBytes) {
                $line = $line.Substring([Math]::Max(0, $line.Length - ($maximumBytes / 4)))
                $encoded = $line + "`r`n"
                $lineBytes = $utf8.GetByteCount($encoded)
                $wasTruncated = $true
            }
            $lines.Enqueue($encoded)
            $retainedBytes += $lineBytes
            while ($retainedBytes -gt $maximumBytes -and $lines.Count -gt 1) {
                $retainedBytes -= $utf8.GetByteCount($lines.Dequeue())
                $wasTruncated = $true
            }
        }
        $reader.Dispose()
        $reader = $null
        $writer = New-Object System.IO.StreamWriter($temporaryPath, $false, $utf8)
        if ($wasTruncated) { $writer.WriteLine('[PCWB] pip log truncated to the most recent 16 MiB; older output is omitted.') }
        while ($lines.Count -gt 0) { $writer.Write($lines.Dequeue()) }
        $writer.Flush()
        $writer.Dispose()
        $writer = $null
        [System.IO.File]::Replace($temporaryPath, $path, $backupPath)
        [System.IO.File]::Delete($backupPath)
    }
    finally {
        if ($reader -ne $null) { $reader.Dispose() }
        if ($writer -ne $null) { $writer.Dispose() }
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) { Remove-Item -LiteralPath $temporaryPath -Force }
        if (Test-Path -LiteralPath $backupPath -PathType Leaf) { Remove-Item -LiteralPath $backupPath -Force }
    }
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

$basePythonProbe = "import json,os,sys; base=getattr(sys,'_base_executable',None) or sys.executable; print(json.dumps({'version':'.'.join(map(str,sys.version_info[:3])),'base_executable':os.path.realpath(base)})); raise SystemExit(0 if sys.version_info[:2]==(3,12) and os.path.isfile(base) else 1)"
if ($PythonExe -eq 'py -3.12') {
    $basePythonOutput = & py -3.12 -B -c $basePythonProbe 2>&1
}
else {
    $basePythonOutput = & $PythonExe -B -c $basePythonProbe 2>&1
}
$basePythonExitCode = $LASTEXITCODE
if ($basePythonExitCode -ne 0) {
    throw "指定されたPython 3.12本体を確認できませんでした。既存venvの基礎Pythonを確認してください: $($basePythonOutput -join ' ')"
}
$basePythonInfo = ($basePythonOutput | Select-Object -Last 1 | Out-String).Trim() | ConvertFrom-Json
if ([string]$basePythonInfo.version -notlike '3.12.*' -or
    [string]::IsNullOrWhiteSpace([string]$basePythonInfo.base_executable) -or
    -not (Test-Path -LiteralPath $basePythonInfo.base_executable -PathType Leaf)) {
    throw "Python 3.12の基礎実行ファイルが見つかりません: $($basePythonInfo | ConvertTo-Json -Compress)"
}
$PythonExe = [System.IO.Path]::GetFullPath([string]$basePythonInfo.base_executable)
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
$pipHelp = (& $venvPython -m pip install --help 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) { throw 'pipの利用可能な進捗方式を確認できませんでした。' }
$rawProgressSupported = $pipHelp -match '(?is)--progress-bar[^\r\n]*(?:\r?\n[ \t]+[^\r\n]*){0,3}?\braw\b'
$progressMode = if ($rawProgressSupported) { 'raw' } else { 'on' }
if ([string]::IsNullOrWhiteSpace($PipLogPath)) {
    $PipLogPath = Join-Path $EnvironmentRoot 'pip-install.log'
}
$PipLogPath = [System.IO.Path]::GetFullPath($PipLogPath)
$pipLogDirectory = Split-Path -Parent $PipLogPath
if (-not (Test-Path -LiteralPath $pipLogDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $pipLogDirectory -Force | Out-Null
}
$pipArgs = @('-m', 'pip', 'install', '--disable-pip-version-check')
$pipArgs += @('--no-input', '--progress-bar', $progressMode, '--log', $PipLogPath)
if (-not $rawProgressSupported) {
    Write-Output 'PCWB_PIP_PROGRESS_NOTE:このpipは機械可読なraw進捗に非対応です。通常の進捗表示と詳細ログを使用します。'
    $pipArgs += '--verbose'
}
else {
    Write-Output 'PCWB_PIP_PROGRESS_NOTE:raw進捗を受信します。'
}
Write-Output "PCWB_PIP_PROGRESS_MODE:$progressMode"
Write-Output "PCWB_PIP_LOG:$PipLogPath"
if ($Offline) {
    $pipArgs += @('--no-index', '--find-links', (Resolve-Path $Wheelhouse).Path)
}
$pipArgs += @('-r', $requirements)
Write-SetupStage 'DEPENDENCIES'
if ($PrepareOnly) {
    Write-Output 'PCWB_SETUP_PREPARED'
    exit 0
}
$pipStartInfo = New-Object System.Diagnostics.ProcessStartInfo
$pipStartInfo.FileName = $venvPython
$pipStartInfo.Arguments = (($pipArgs | ForEach-Object { Quote-NativeArgument ([string]$_) }) -join ' ')
$pipStartInfo.WorkingDirectory = $backend
$pipStartInfo.UseShellExecute = $false
$pipStartInfo.CreateNoWindow = $true
$pipStartInfo.RedirectStandardOutput = $true
$pipStartInfo.RedirectStandardError = $true
$pipStartInfo.StandardOutputEncoding = $utf8NoBom
$pipStartInfo.StandardErrorEncoding = $utf8NoBom
$pipProcess = New-Object System.Diagnostics.Process
$pipProcess.StartInfo = $pipStartInfo
try {
    if (-not $pipProcess.Start()) { throw 'pipプロセスを起動できませんでした。' }
    Write-Output "PCWB_PIP_PID:$($pipProcess.Id)"

    # Do not attach PowerShell script blocks to Process output events: those callbacks
    # run on .NET worker threads and can crash Windows PowerShell 5.1 (GetContextFromTLS).
    $stdoutBuffer = New-Object 'char[]' 4096
    $stderrBuffer = New-Object 'char[]' 4096
    $stdoutTask = $pipProcess.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
    $stderrTask = $pipProcess.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
    $stdoutDone = $false
    $stderrDone = $false
    while (-not $stdoutDone -or -not $stderrDone) {
        $receivedOutput = $false
        if (-not $stdoutDone -and $stdoutTask.IsCompleted) {
            $readCount = $stdoutTask.GetAwaiter().GetResult()
            if ($readCount -gt 0) {
                [Console]::Out.Write($stdoutBuffer, 0, $readCount)
                [Console]::Out.Flush()
                $stdoutTask = $pipProcess.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
            }
            else { $stdoutDone = $true }
            $receivedOutput = $true
        }
        if (-not $stderrDone -and $stderrTask.IsCompleted) {
            $readCount = $stderrTask.GetAwaiter().GetResult()
            if ($readCount -gt 0) {
                [Console]::Error.Write($stderrBuffer, 0, $readCount)
                [Console]::Error.Flush()
                $stderrTask = $pipProcess.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
            }
            else { $stderrDone = $true }
            $receivedOutput = $true
        }
        if (-not $receivedOutput) { Start-Sleep -Milliseconds 20 }
    }
    $pipProcess.WaitForExit()
    $pipExitCode = $pipProcess.ExitCode
}
finally {
    $pipProcess.Dispose()
    Sanitize-PipLog $PipLogPath
}
Write-Output "PCWB_PIP_EXIT:$pipExitCode"
if ($pipExitCode -ne 0) {
    throw "依存ライブラリの導入に失敗しました (pip ExitCode: $pipExitCode)。詳細ログを確認してください。"
}

Write-SetupStage 'VERIFY'
& $venvPython -m pip check
if ($LASTEXITCODE -ne 0) { throw 'pip checkで依存関係の不整合が見つかりました。' }
& $venvPython -B -c $verify $requirements
if ($LASTEXITCODE -ne 0) { throw 'Pythonまたは依存ライブラリのバージョン検証に失敗しました。' }
Write-Host "Python環境の確認が完了しました。" -ForegroundColor Green
Write-Host "環境ルート: $EnvironmentRoot"
