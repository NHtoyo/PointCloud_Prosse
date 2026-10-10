[CmdletBinding()]
param(
    [string]$QaRoot = (Join-Path $env:TEMP ("PCWB-FourthAudit-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string]$UnityExe = 'E:\VR\UnityEditor\6000.4.7f1\Editor\Unity.exe',
    [string]$PythonExe = (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python312\python.exe'),
    [string]$PythonVenvSource = '',
    [switch]$SkipPythonInstall,
    [switch]$SkipUnityTests,
    [switch]$SkipPlayerBuild,
    [switch]$IncludeLoadTiers,
    [switch]$RunPlayerWorkflow,
    [switch]$RunPlayerE2EOrder2,
    [switch]$RunPlayerRace,
    [switch]$RunPlayerLoadTiers,
    [ValidateRange(1, 10)][int]$LoadTierRepeats = 1,
    [switch]$RunCrashRecovery
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$repoRoot = (Resolve-Path (Join-Path $projectRoot '..')).Path
$qaRootFull = [System.IO.Path]::GetFullPath($QaRoot)
$qaProject = Join-Path $qaRootFull 'PointCloudVR'
$fixtureRoot = Join-Path $qaRootFull 'PointCloudData'
$playerRoot = Join-Path $qaRootFull 'PlayerBuild'
$artifactRoot = Join-Path $qaRootFull 'Artifacts'
$runToken = Split-Path $qaRootFull -Leaf
$safeProduct = ($runToken -replace '[^A-Za-z0-9_-]', '_')

if ($qaRootFull.StartsWith($repoRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "QAコピーはGit作業ツリーの外にしてください: $qaRootFull"
}
if (Test-Path -LiteralPath $qaRootFull) {
    throw "既存データ保護のため、既存のQA出力先は再利用・上書きしません。新しいQaRootを指定してください: $qaRootFull"
}
if (-not (Test-Path -LiteralPath $UnityExe -PathType Leaf)) { throw "Unity Editorが見つかりません: $UnityExe" }
if (-not (Test-Path -LiteralPath $PythonExe -PathType Leaf)) { throw "指定Pythonが見つかりません: $PythonExe" }

function Invoke-PlayerWithMetrics {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string]$Arguments,
        [Parameter(Mandatory)][string]$MetricsPath
    )

    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -PassThru
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $samples = [System.Collections.Generic.List[object]]::new()
    $peakWorkingSet = 0L
    $peakPrivateBytes = 0L
    $peakCpuSeconds = 0.0
    while ($true) {
        $process.Refresh()
        if ($process.HasExited) { break }
        $workingSet = [long]$process.WorkingSet64
        $privateBytes = [long]$process.PrivateMemorySize64
        $cpuSeconds = [double]$process.TotalProcessorTime.TotalSeconds
        $peakWorkingSet = [Math]::Max($peakWorkingSet, $workingSet)
        $peakPrivateBytes = [Math]::Max($peakPrivateBytes, $privateBytes)
        $peakCpuSeconds = [Math]::Max($peakCpuSeconds, $cpuSeconds)
        $samples.Add([pscustomobject]@{
            elapsed_ms = $watch.ElapsedMilliseconds
            working_set_bytes = $workingSet
            private_bytes = $privateBytes
            cpu_seconds = $cpuSeconds
        })
        Start-Sleep -Milliseconds 500
    }
    $watch.Stop()
    $process.Refresh()
    $metrics = [ordered]@{
        process_id = $process.Id
        exit_code = $process.ExitCode
        elapsed_ms = $watch.ElapsedMilliseconds
        peak_working_set_bytes = [Math]::Max($peakWorkingSet, [long]$process.PeakWorkingSet64)
        peak_private_bytes = $peakPrivateBytes
        cpu_seconds = [Math]::Max($peakCpuSeconds, [double]$process.TotalProcessorTime.TotalSeconds)
        sample_interval_ms = 500
        samples = @($samples)
    }
    $metrics | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $MetricsPath -Encoding utf8
    return [pscustomobject]@{ ExitCode = $process.ExitCode; MetricsPath = $MetricsPath }
}

New-Item -ItemType Directory -Path $qaRootFull, $fixtureRoot, $playerRoot, $artifactRoot | Out-Null
$copyLog = Join-Path $artifactRoot 'snapshot_copy.log'
$robocopyArgs = @(
    $projectRoot, $qaProject, '/E', '/COPY:DAT', '/DCOPY:DAT', '/R:1', '/W:1', '/NP', "/LOG:$copyLog",
    '/XD', '.git', 'Library', 'Temp', 'Obj', 'Logs', 'UserSettings', 'Build', 'Builds', '.venv',
    (Join-Path $projectRoot 'Assets\_Recovery'),
    'output', 'output_generations', 'bench_output', '__pycache__',
    '/XF', '*.pyc', (Join-Path $projectRoot 'Assets\_Recovery.meta')
)
& robocopy @robocopyArgs | Out-Null
if ($LASTEXITCODE -ge 8) { throw "隔離コピーに失敗しました。robocopy exit=$LASTEXITCODE : $copyLog" }

$testAssets = Join-Path $PSScriptRoot 'Assets'
$testAssetCopyLog = Join-Path $artifactRoot 'test_assets_copy.log'
& robocopy $testAssets (Join-Path $qaProject 'Assets') /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NP "/LOG:$testAssetCopyLog" | Out-Null
if ($LASTEXITCODE -ge 8) { throw "隔離テスト資材の配置に失敗しました。robocopy exit=$LASTEXITCODE : $testAssetCopyLog" }
$packageManifestPath = Join-Path $qaProject 'Packages\manifest.json'
$manifest = Get-Content -LiteralPath $packageManifestPath -Raw | ConvertFrom-Json
if (-not $manifest.dependencies.PSObject.Properties['com.unity.test-framework']) {
    $manifest.dependencies | Add-Member -NotePropertyName 'com.unity.test-framework' -NotePropertyValue '1.4.6'
    $manifest | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $packageManifestPath -Encoding utf8
}

$fixtureGenerator = Join-Path $PSScriptRoot 'New-FourthAuditFixtures.py'
$generatorArgs = @($fixtureGenerator, '--output', $fixtureRoot)
if ($IncludeLoadTiers) { $generatorArgs += '--include-load-tiers' }
$fixtureLog = Join-Path $artifactRoot 'fixture_generation.log'
& $PythonExe @generatorArgs *> $fixtureLog
if ($LASTEXITCODE -ne 0) { throw "合成PLY生成に失敗しました。python exit=$LASTEXITCODE : $fixtureLog" }

$venvPython = Join-Path $qaProject 'python_backend\.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    if (-not [string]::IsNullOrWhiteSpace($PythonVenvSource)) {
        $sourceVenv = [System.IO.Path]::GetFullPath($PythonVenvSource)
        if (-not (Test-Path -LiteralPath (Join-Path $sourceVenv 'Scripts\python.exe') -PathType Leaf)) {
            throw "PythonVenvSourceに有効なWindows venvがありません: $sourceVenv"
        }
        $venvCopyLog = Join-Path $artifactRoot 'python_venv_copy.log'
        & robocopy $sourceVenv (Join-Path $qaProject 'python_backend\.venv') /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NP "/LOG:$venvCopyLog" | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "隔離venvの複製に失敗しました。robocopy exit=$LASTEXITCODE : $venvCopyLog" }
    }
    else {
        if ($SkipPythonInstall) { throw 'SkipPythonInstall指定ですが隔離Python venvがありません。' }
        & $PythonExe -m venv (Join-Path $qaProject 'python_backend\.venv')
        if ($LASTEXITCODE -ne 0) { throw "隔離venv作成に失敗しました。exit=$LASTEXITCODE" }
    }
}
if (-not $SkipPythonInstall) {
    $env:PYTHONNOUSERSITE = '1'
    & $venvPython -m pip install -r (Join-Path $qaProject 'python_backend\requirements.txt') *> (Join-Path $artifactRoot 'python_install.log')
    if ($LASTEXITCODE -ne 0) { throw "隔離venvへの依存導入に失敗しました。exit=$LASTEXITCODE" }
    & $venvPython -m pip install -r (Join-Path $PSScriptRoot 'requirements-test.txt') *> (Join-Path $artifactRoot 'python_test_install.log')
    if ($LASTEXITCODE -ne 0) { throw "隔離venvへのPythonテスト依存導入に失敗しました。exit=$LASTEXITCODE" }
}
& $venvPython -c "import sys, numpy, scipy, open3d; print(sys.executable); print('numpy='+numpy.__version__); print('scipy='+scipy.__version__); print('open3d='+open3d.__version__)" *> (Join-Path $artifactRoot 'python_environment.log')
if ($LASTEXITCODE -ne 0) { throw "隔離Python依存確認に失敗しました。exit=$LASTEXITCODE" }

$hashRoots = @('Assets', 'Packages', 'ProjectSettings', 'python_backend') | ForEach-Object { Join-Path $qaProject $_ }
$hashRows = foreach ($file in Get-ChildItem -LiteralPath $hashRoots -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](\.venv|output|output_generations|bench_output|__pycache__|Library|Temp|Obj|Logs)[\\/]'
}) {
    $relative = [System.IO.Path]::GetRelativePath($qaProject, $file.FullName)
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash`t$relative"
}
$testHashRows = foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](\.git|__pycache__)[\\/]'
}) {
    $relative = [System.IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName)
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash`tTEST/$relative"
}
($hashRows + $testHashRows) | Set-Content -LiteralPath (Join-Path $artifactRoot 'source_snapshot_sha256_prebuild.tsv') -Encoding utf8

$testPython = Join-Path $qaProject 'python_backend\.venv\Scripts\python.exe'
$pythonTestLog = Join-Path $artifactRoot 'pytest.log'
Push-Location (Join-Path $qaProject 'python_backend')
try {
    & $testPython -m pytest tests -q *> $pythonTestLog
    $pythonTestExit = $LASTEXITCODE
    if ($pythonTestExit -ne 0) { throw "Python backend tests failed. exit=$pythonTestExit : $pythonTestLog" }
    & $testPython -m compileall -q . *> (Join-Path $artifactRoot 'compileall.log')
    if ($LASTEXITCODE -ne 0) { throw "Python compileall failed. See $(Join-Path $artifactRoot 'compileall.log')" }
    & $testPython -m pip freeze --all | Set-Content -LiteralPath (Join-Path $artifactRoot 'python_environment_freeze.txt') -Encoding utf8
}
finally { Pop-Location }

if (-not $SkipUnityTests) {
    foreach ($platform in @('EditMode', 'PlayMode')) {
        $resultFile = Join-Path $artifactRoot ("unity_" + $platform.ToLowerInvariant() + '_results.xml')
        $logFile = Join-Path $artifactRoot ("unity_" + $platform.ToLowerInvariant() + '.log')
        $testArguments = @('-batchmode', '-projectPath', $qaProject, '-runTests', '-testPlatform', $platform,
            '-testResults', $resultFile, '-logFile', $logFile)
        $testProcess = Start-Process -FilePath $UnityExe -ArgumentList $testArguments -WindowStyle Hidden -Wait -PassThru
        if ($testProcess.ExitCode -ne 0) { throw "Unity $platform tests failed. exit=$($testProcess.ExitCode) : $logFile" }
        if (-not (Test-Path -LiteralPath $resultFile -PathType Leaf)) {
            throw "Unity $platform test runner exited without a result XML: $logFile"
        }
        [xml]$testResults = Get-Content -LiteralPath $resultFile -Raw
        $run = $testResults.'test-run'
        if (-not $run -or [int]$run.total -le 0 -or [int]$run.failed -gt 0 -or $run.result -ne 'Passed') {
            throw "Unity $platform tests did not pass: result=$($run.result), total=$($run.total), failed=$($run.failed) : $resultFile"
        }
    }
}

if (-not $SkipPlayerBuild) {
    $env:PCWB_QA_COMPANY_NAME = 'PCWB_IntegrationQA'
    $env:PCWB_QA_PRODUCT_NAME = $safeProduct
    $env:PCWB_QA_BUILD_PATH = Join-Path $playerRoot 'PointCloudVR_QA.exe'
    $buildLog = Join-Path $artifactRoot 'windows_player_build.log'
    $buildArguments = @('-batchmode', '-projectPath', $qaProject, '-executeMethod', 'ThirdAuditBuild.BuildWindows',
        '-logFile', $buildLog, '-quit')
    $buildProcess = Start-Process -FilePath $UnityExe -ArgumentList $buildArguments -WindowStyle Hidden -Wait -PassThru
    if ($buildProcess.ExitCode -ne 0) { throw "Windows Player build failed. exit=$($buildProcess.ExitCode) : $buildLog" }

    $playerPython = Join-Path $playerRoot 'python_backend'
    foreach ($requiredFile in @('requirements.txt', 'Setup-Python.ps1', 'run_noise_filter.py', 'run_stem_diameter.py', 'run_reference_sphere.py')) {
        if (-not (Test-Path -LiteralPath (Join-Path $playerPython $requiredFile) -PathType Leaf)) {
            throw "Windows Player build did not stage required Python backend file: $requiredFile"
        }
    }
    if (Test-Path -LiteralPath (Join-Path $playerPython '.venv')) {
        throw 'Player build unexpectedly bundled a Python virtual environment.'
    }
}

$playerExe = Join-Path $playerRoot 'PointCloudVR_QA.exe'
$playerProcessResults = [System.Collections.Generic.List[object]]::new()
$playerIssues = [System.Collections.Generic.List[string]]::new()
foreach ($mode in @(@{ Switch = $RunPlayerWorkflow; Flag = '--pcwb-fourth-player-workflow'; Name = 'player_workflow' },
                    @{ Switch = $RunPlayerE2EOrder2; Flag = '--pcwb-e2e-order2'; Name = 'player_e2e_order2' },
                    @{ Switch = $RunPlayerRace; Flag = '--pcwb-fourth-load-race'; Name = 'player_load_race' },
                    @{ Switch = $RunPlayerLoadTiers; Flag = '--pcwb-fourth-load-tiers'; Name = 'player_load_tiers' })) {
    if (-not $mode.Switch) { continue }
    $repeatCount = if ($mode.Name -eq 'player_load_tiers') { $LoadTierRepeats } else { 1 }
    for ($repeat = 1; $repeat -le $repeatCount; $repeat++) {
        $runName = if ($repeatCount -gt 1) { '{0}_run{1:D2}' -f $mode.Name, $repeat } else { $mode.Name }
        $playerLog = Join-Path $artifactRoot ($runName + '.log')
        $playerArgs = "-screen-fullscreen 0 -screen-width 1600 -screen-height 900 $($mode.Flag) -logFile `"$playerLog`""
        $playerProcess = Invoke-PlayerWithMetrics -FilePath $playerExe -Arguments $playerArgs `
            -MetricsPath (Join-Path $artifactRoot ($runName + '_resource_samples.json'))
        $runReport = Get-ChildItem -LiteralPath (Join-Path $artifactRoot 'PlayerRuns') -Filter 'fourth-player-*.tsv' -File |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $probeRows = @()
        if ($runReport) { $probeRows = @(Import-Csv -LiteralPath $runReport.FullName -Delimiter "`t") }
        $probeFailed = @($probeRows | Where-Object { $_.result -ne 'PASS' }).Count -gt 0
        $functionalStatus = if ($probeRows.Count -gt 0 -and -not $probeFailed) { 'PASS' } else { 'FAIL_OR_NO_REPORT' }
        $shutdownStatus = if ($playerProcess.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }
        $playerProcessResults.Add([pscustomobject]@{
            mode = $runName; exit_code = $playerProcess.ExitCode; functional_status = $functionalStatus
            shutdown_status = $shutdownStatus; report = if ($runReport) { $runReport.FullName } else { $null }
            log = $playerLog; resource_samples = $playerProcess.MetricsPath
        })
        if ($functionalStatus -ne 'PASS') { $playerIssues.Add("$($runName): probe report contains failures or is missing") }
        if ($shutdownStatus -ne 'PASS') { $playerIssues.Add("$($runName): Player exit code $($playerProcess.ExitCode)") }
    }
}

if ($RunCrashRecovery) {
    $writeLog = Join-Path $artifactRoot 'player_crash_write.log'
    $verifyLog = Join-Path $artifactRoot 'player_crash_verify.log'
    $writeProcess = Invoke-PlayerWithMetrics -FilePath $playerExe `
        -Arguments "-screen-fullscreen 0 -screen-width 1600 -screen-height 900 --pcwb-crash-write -logFile `"$writeLog`"" `
        -MetricsPath (Join-Path $artifactRoot 'player_crash_write_resource_samples.json')
    $statusPath = Join-Path $env:USERPROFILE ("AppData\LocalLow\PCWB_IntegrationQA\" + $safeProduct + '\third-audit-status.txt')
    $writeStatus = if (Test-Path -LiteralPath $statusPath) { Get-Content -LiteralPath $statusPath -Raw } else { '' }
    $writePassed = $writeStatus -match 'CHECKPOINT_WRITTEN'
    if (-not $writePassed) { $playerIssues.Add("crash_write: no checkpoint status; exit=$($writeProcess.ExitCode)") }
    $verifyProcess = Invoke-PlayerWithMetrics -FilePath $playerExe `
        -Arguments "-screen-fullscreen 0 -screen-width 1600 -screen-height 900 --pcwb-crash-verify -logFile `"$verifyLog`"" `
        -MetricsPath (Join-Path $artifactRoot 'player_crash_verify_resource_samples.json')
    $verifyStatus = if (Test-Path -LiteralPath $statusPath) { Get-Content -LiteralPath $statusPath -Raw } else { '' }
    $recoveryRequired = @(
        'unclean_marker=True', 'checkpoint_read=True', 'labels=True', 'candidate_pending=True',
        'applied=True', 'applied_labels=True', 'source_unchanged=True'
    )
    $continuationRequired = @(
        'continuation=passed=True', 'classified=True', 'delete_restore=True', 'saved=True',
        'switched=True', 'loaded_saved_cloud=True', 'reloaded_labels=True', 'reloaded_positions=True'
    )
    $recoveryPassed = @($recoveryRequired | Where-Object {
        $verifyStatus -notmatch "(?:^|;)$([regex]::Escape($_))(?:;|$)"
    }).Count -eq 0
    $continuationPassed = @($continuationRequired | Where-Object {
        $verifyStatus -notmatch "(?:^|;)$([regex]::Escape($_))(?:;|$)"
    }).Count -eq 0
    $verifyFunctionalStatus = if ($recoveryPassed -and $continuationPassed) { 'PASS' } else { 'FAIL' }
    $verifyShutdownStatus = if ($verifyProcess.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }
    $playerProcessResults.Add([pscustomobject]@{
        mode = 'crash_recovery'; write_exit_code = $writeProcess.ExitCode; write_status = if ($writePassed) { 'PASS_EXPECTED_KILL' } else { 'FAIL' }
        write_resource_samples = $writeProcess.MetricsPath
        verify_exit_code = $verifyProcess.ExitCode; verify_resource_samples = $verifyProcess.MetricsPath
        functional_status = $verifyFunctionalStatus
        shutdown_status = $verifyShutdownStatus; status_path = $statusPath; verify_log = $verifyLog
    })
    if (-not $recoveryPassed) { $playerIssues.Add('crash_recovery: checkpoint restoration checks failed') }
    if (-not $continuationPassed) { $playerIssues.Add('crash_recovery: post-recovery edit/save/switch checks failed') }
    if ($verifyShutdownStatus -ne 'PASS') { $playerIssues.Add("crash_recovery: Player exit code $($verifyProcess.ExitCode) after recovery") }
}

$playerProcessResults | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifactRoot 'player_process_results.json') -Encoding utf8

$finalHashRows = foreach ($file in Get-ChildItem -LiteralPath $hashRoots -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](\.venv|output|output_generations|bench_output|__pycache__|Library|Temp|Obj|Logs)[\\/]'
}) {
    $relative = [System.IO.Path]::GetRelativePath($qaProject, $file.FullName)
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash`t$relative"
}
$finalTestHashRows = foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](\.git|__pycache__)[\\/]'
}) {
    $relative = [System.IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName)
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash`tTEST/$relative"
}
($finalHashRows + $finalTestHashRows) | Set-Content -LiteralPath (Join-Path $artifactRoot 'source_snapshot_sha256_final.tsv') -Encoding utf8

$playerBinaryPaths = @(
    (Join-Path $playerRoot 'PointCloudVR_QA.exe'),
    (Join-Path $playerRoot 'UnityPlayer.dll'),
    (Join-Path $playerRoot 'PointCloudVR_QA_Data\Managed\Assembly-CSharp.dll')
)
$binaryHashRows = foreach ($path in $playerBinaryPaths) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        "$( (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() )`t$([System.IO.Path]::GetRelativePath($playerRoot, $path))"
    }
}
$binaryHashRows | Set-Content -LiteralPath (Join-Path $artifactRoot 'player_binary_sha256.tsv') -Encoding utf8

$summary = [ordered]@{
    qa_root = $qaRootFull
    unity_project = $qaProject
    player = $playerExe
    fixture_manifest = Join-Path $fixtureRoot 'fixtures.json'
    python_tests = Get-Content -LiteralPath $pythonTestLog -Raw
    player_process_results = @($playerProcessResults)
    unresolved_player_issues = @($playerIssues)
    source_hash_manifest = Join-Path $artifactRoot 'source_snapshot_sha256_final.tsv'
    player_binary_hash_manifest = Join-Path $artifactRoot 'player_binary_sha256.tsv'
}
$summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $artifactRoot 'run_summary.json') -Encoding utf8

@(
    "qa_root=$qaRootFull",
    "unity_project=$qaProject",
    "fixtures=$fixtureRoot",
    "artifacts=$artifactRoot",
    "player=$playerExe",
    "player_python=$(Join-Path $playerRoot 'python_backend')",
    "source_hash_manifest=$(Join-Path $artifactRoot 'source_snapshot_sha256_final.tsv')"
) | Set-Content -LiteralPath (Join-Path $artifactRoot 'run_paths.txt') -Encoding utf8
Write-Output "隔離QA準備が完了しました: $qaRootFull"
if ($playerIssues.Count -gt 0) { throw "一部Player統合試験で失敗しました。詳細: $(Join-Path $artifactRoot 'run_summary.json')" }
