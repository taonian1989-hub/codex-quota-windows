param([string]$Version = '1.0.2')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a semantic version such as 1.0.2.' }
$projectRoot = $PSScriptRoot
$outputRoot = Join-Path $projectRoot 'outputs'
$runName = $Version + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$buildRoot = Join-Path $projectRoot ('work\release_build\' + $runName)
$stageRoot = Join-Path $projectRoot ('work\release_stage\' + $runName)
New-Item -ItemType Directory -Force -Path $buildRoot,$stageRoot,$outputRoot | Out-Null
$archive = Join-Path $outputRoot ('CodexQuota-' + $Version + '-windows.zip')
if (Test-Path -LiteralPath $archive) { throw ('Release archive already exists: ' + $archive) }
& (Join-Path $projectRoot 'build.ps1') -Test -OutputDirectory $buildRoot
$exe = Join-Path $buildRoot 'CodexQuota.exe'
$actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
if ($actualVersion -ne ($Version + '.0')) { throw ('Binary version does not match package version: ' + $actualVersion) }
Copy-Item -LiteralPath $exe -Destination $stageRoot
foreach ($name in @('LICENSE','THIRD_PARTY_NOTICES.md','CHANGELOG.md')) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $stageRoot
}
$portableReadme = @"
# Codex Quota for Windows v$Version

Windows 10 / 11 Codex 额度托盘工具。

1. 将整个 ZIP 解压到任意文件夹。
2. 双击 CodexQuota.exe，无需管理员权限。
3. 图标可能位于任务栏「^」区域；左键打开详情，右键操作或退出。

需要 .NET Framework 4.8。不需要 Node.js、Python、API Key 或 Codex CLI。

外圈代表 5 小时余量，月相代表每周余量，数字图标为 5 小时剩余百分比。

程序运行时只读本地 Codex 会话日志，不联网、不读取 auth.json。
额度是最近一次请求写入日志的快照；没有新日志就没有新读数。

默认目录：CODEX_HOME 下的 sessions；未设置时为用户目录下 .codex\sessions。
可通过设置更改目录、刷新间隔、深浅主题、数字图标和 Windows 登录自启。

卸载前先关闭已启用的登录自启，退出后删除文件夹。

完整说明与源码：https://github.com/taonian1989-hub/codex-quota-windows
版本更新：https://github.com/taonian1989-hub/codex-quota-windows/releases

MIT 许可证，原项目声明见 THIRD_PARTY_NOTICES.md。
"@
Set-Content -LiteralPath (Join-Path $stageRoot 'README.md') -Value $portableReadme -Encoding utf8
$innerSums = Get-ChildItem -LiteralPath $stageRoot -File | Sort-Object Name | ForEach-Object {
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
}
Set-Content -LiteralPath (Join-Path $stageRoot 'SHA256SUMS.txt') -Value $innerSums -Encoding ascii
Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $archive -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Value ($archiveHash + '  ' + [IO.Path]::GetFileName($archive)) -Encoding ascii
Write-Host ('Release archive: ' + $archive)
Write-Host ('SHA256: ' + $archiveHash)
