param([switch]$Test, [string]$OutputDirectory = $PSScriptRoot, [ValidateSet('anycpu32bitpreferred', 'anycpu')][string]$Platform = 'anycpu32bitpreferred')
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler was not found. Install .NET Framework 4.8.' }
$source = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
if (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot $OutputDirectory }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$target = Join-Path $OutputDirectory 'CodexQuota.exe'
$buildArgs = @('/nologo', '/target:winexe', "/platform:$Platform", '/optimize+', '/codepage:65001', "/out:$target", "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')", '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Runtime.Serialization.dll', '/reference:System.Xml.dll')
$icon = Join-Path $PSScriptRoot 'assets\app.ico'
if (Test-Path -LiteralPath $icon) { $buildArgs += "/win32icon:$icon" }
& $compiler @buildArgs @source
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Write-Host "Built: $target"
if ($Test) {
    $testDir = Join-Path $PSScriptRoot 'work\tests'
    New-Item -ItemType Directory -Force -Path $testDir | Out-Null
    $testExe = Join-Path $testDir 'CodexQuota.Tests.exe'
    & $compiler /nologo /target:exe "/platform:$Platform" /codepage:65001 "/out:$testExe" "/reference:$target" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:System.Runtime.Serialization.dll /reference:System.Xml.dll (Join-Path $PSScriptRoot 'tests\ReaderTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    Copy-Item -LiteralPath $target -Destination (Join-Path $testDir 'CodexQuota.exe') -Force
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
