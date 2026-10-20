param(
    [Parameter(Mandatory = $true)][string]$Baseline,
    [string]$Candidate = (Join-Path (Split-Path $PSScriptRoot -Parent) 'CodexQuota.exe'),
    [ValidateSet('anycpu','anycpu32bitpreferred')][string]$BaselinePlatform = 'anycpu',
    [ValidateSet('anycpu','anycpu32bitpreferred')][string]$CandidatePlatform = 'anycpu32bitpreferred'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $projectRoot ('work\performance_runs\' + (Get-Date -Format 'yyyyMMdd_HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$corpus = Join-Path $runRoot 'corpus'
New-Item -ItemType Directory -Force -Path $corpus | Out-Null
$stamp = [DateTimeOffset]::UtcNow.AddMinutes(-2).ToString('o')
$reset = [DateTimeOffset]::UtcNow.AddHours(2).ToUnixTimeSeconds()
$quota = '{"timestamp":"' + $stamp + '","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":24,"window_minutes":300,"resets_at":' + $reset + '},"secondary":{"used_percent":38,"window_minutes":10080,"resets_at":' + $reset + '},"plan_type":"plus"}}}'
$line = '{"type":"response_item","payload":{"type":"message","content":"' + ('基准测试文本' * 100) + '"}}' + "`n"
$padding = $line * 180
for ($i = 0; $i -lt 32; $i++) {
    [IO.File]::WriteAllText((Join-Path $corpus ('session_' + $i.ToString('D2') + '.jsonl')), $padding + $quota + "`n", [Text.UTF8Encoding]::new($false))
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
foreach ($entry in @(@{Name='baseline';File=$Baseline;Platform=$BaselinePlatform}, @{Name='candidate';File=$Candidate;Platform=$CandidatePlatform})) {
    $folder = Join-Path $runRoot $entry.Name
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $app = Join-Path $folder 'CodexQuota.exe'
    Copy-Item -LiteralPath $entry.File -Destination $app
    $runner = Join-Path $folder 'PerformanceBench.exe'
    & $compiler /nologo /target:exe ('/platform:' + $entry.Platform) /optimize+ /codepage:65001 ('/out:' + $runner) ('/reference:' + $app) ('/win32manifest:' + (Join-Path $projectRoot 'app.manifest')) /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll (Join-Path $PSScriptRoot 'PerformanceBench.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark compilation failed' }
    foreach ($mode in @('reader','tray','quiet')) {
        & $runner $mode $corpus | Tee-Object -FilePath (Join-Path $runRoot ($entry.Name + '_' + $mode + '.csv'))
        if ($LASTEXITCODE -ne 0) { throw ('Benchmark failed: ' + $entry.Name + ' ' + $mode) }
    }
}
Write-Host ('Results: ' + $runRoot)
