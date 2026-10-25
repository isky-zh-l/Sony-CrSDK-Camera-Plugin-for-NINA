param([switch]$SkipDeploy,[string]$PackageCache)
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:MSBuildEnableWorkloadResolver='false'
$logs=Join-Path $PSScriptRoot 'artifacts\logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$stamp=[DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyyMMdd-HHmmss-fff')
$restoreArgs=@()
if($PackageCache){$restoreArgs+="-p:RestorePackagesPath=$PackageCache"}
$previous=$ErrorActionPreference
try {
$ErrorActionPreference='Continue'
& dotnet build (Join-Path $PSScriptRoot 'SonyCamera.slnx') -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false @restoreArgs --configfile (Join-Path $PSScriptRoot 'NuGet.Config') 2>&1 | Tee-Object -FilePath (Join-Path $logs "build-$stamp.txt")
$buildExit=$LASTEXITCODE
} finally {$ErrorActionPreference=$previous}
if($buildExit -ne 0){throw '构建失败，请查看日志'}
if(!$SkipDeploy){ Write-Output '构建完成。原生依赖需通过 tools\deploy-native.ps1 显式指定合法本地来源准备；构建不自动复制SDK。' }
