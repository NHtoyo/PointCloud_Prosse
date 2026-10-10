[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PlayerExe,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('D3D11', 'D3D11_FL11', 'D3D11_FL10', 'D3D12', 'Vulkan')]
    [string[]]$Cases = @('D3D11', 'D3D12', 'Vulkan', 'D3D11_FL10'),
    [ValidateSet('1024x768', '1280x720', '1600x900', '1920x1080')]
    [string]$Resolution = '1280x720',
    [string]$CompanyName = 'DefaultCompany',
    [string]$ProductName = 'PointCloudVR'
)

$ErrorActionPreference = 'Stop'
$player = (Resolve-Path -LiteralPath $PlayerExe).Path
$out = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $out) { throw "既存の出力を上書きしません。新しい出力先を指定してください: $out" }
New-Item -ItemType Directory -Path $out | Out-Null
$parts = $Resolution.Split('x')
$width = [int]$parts[0]
$height = [int]$parts[1]

function Quote-Argument([string]$Value) {
    '"' + ($Value -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"'
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($case in $Cases) {
    $flags = switch ($case) {
        'D3D11' { @('-force-d3d11') }
        'D3D11_FL11' { @('-force-d3d11', '-force-feature-level-11-0') }
        'D3D11_FL10' { @('-force-d3d11', '-force-feature-level-10-0') }
        'D3D12' { @('-force-d3d12') }
        'Vulkan' { @('-force-vulkan') }
    }
    $logPath = Join-Path $out "$case-Player.log"
    $playerArgs = @($flags) + @('-screen-width', "$width", '-screen-height', "$height", '-screen-fullscreen', '0', '-logFile', (Quote-Argument $logPath))
    $argumentLine = ($playerArgs -join ' ')
    $started = Get-Date
    Write-Host "Starting $case. A visible Player window will open." -ForegroundColor Cyan
    Write-Host '操作: 点群が実際に描画されるか、色/ラベル、カメラ操作、点選択・編集、C2C表示を確認してください。画面を記録してからPlayerを通常のUI操作で終了します。'
    $process = Start-Process -FilePath $player -ArgumentList $argumentLine -PassThru
    $process.WaitForExit()
    $ended = Get-Date
    $visualAnswer = Read-Host '点群が実際に見え、カメラ操作を確認できましたか [y/n]'
    $colorAnswer = Read-Host 'RGBまたはラベル色が画面に反映されましたか [y/n]'
    $editAnswer = Read-Host '点の選択・編集とC2C表示を確認できましたか [y/n]'
    $logText = if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Raw } else { '' }
    $apiLine = ($logText -split "`r?`n" | Where-Object { $_ -match 'Direct3D|Vulkan|Graphics API|Shader level|graphics device' } | Select-Object -First 12) -join "`n"
    $results.Add([pscustomobject]@{
        case = $case
        requested_flags = $flags -join ' '
        resolution = $Resolution
        process_id = $process.Id
        exit_code = $process.ExitCode
        started_local = $started.ToString('o')
        ended_local = $ended.ToString('o')
        log_path = [System.IO.Path]::GetFileName($logPath)
        observed_graphics_lines = $apiLine
        visual_draw_confirmed = ($visualAnswer -match '^(?i:y|yes)$')
        color_label_confirmed = ($colorAnswer -match '^(?i:y|yes)$')
        edit_and_c2c_confirmed = ($editAnswer -match '^(?i:y|yes)$')
        visual_confirmation = 'USER_RECORDED_AFTER_VISIBLE_PLAYER_RUN'
    })
    Write-Host "Closed $case (exit=$($process.ExitCode)); UI confirmations were recorded separately from the process/API log." -ForegroundColor Yellow
}

$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'graphics_api_matrix.json') -Encoding utf8
Write-Host "Results: $out"
