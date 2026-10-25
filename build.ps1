param(
 [switch]$Native,
 [string]$SonySdkRoot=(Join-Path $PSScriptRoot 'deps\sony\SimpleCli'),
 [string]$NinaApp=$env:NINA_APP,
 [string]$VisualStudioRoot,
 [string]$PackageCache
)
$ErrorActionPreference='Stop'
if(!$NinaApp){throw 'Specify -NinaApp <NINA 3.2.0.9001 installation directory> or set NINA_APP.'}
$NinaApp=(Resolve-Path -LiteralPath $NinaApp).Path
if((Get-Item -LiteralPath (Join-Path $NinaApp 'NINA.Core.dll')).VersionInfo.FileVersion -ne '3.2.0.9001'){throw 'This release targets NINA 3.2.0.9001.'}
$env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot 'artifacts\dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='0'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:MSBuildEnableWorkloadResolver='false'
if($Native){
 $argsNative=@{SdkRoot=$SonySdkRoot;BuildDirectory=(Join-Path $PSScriptRoot 'artifacts\native')}
 if($VisualStudioRoot){$argsNative.VisualStudioRoot=$VisualStudioRoot}
 & (Join-Path $PSScriptRoot 'src\native\build.ps1') @argsNative
 & (Join-Path $PSScriptRoot 'tools\Build-Decoder.ps1')
}
$clientArgs=@{SkipDeploy=$true}
if($PackageCache){$clientArgs.PackageCache=$PackageCache}
& (Join-Path $PSScriptRoot 'src\managed\build.ps1') @clientArgs
$project=Join-Path $PSScriptRoot 'src\nina\src\SonyA7V.Camera\SonyA7V.Camera.csproj'
# Windows PowerShell 5 can turn harmless native stderr into terminating errors.
$previous=$ErrorActionPreference
try {
 $ErrorActionPreference='Continue'
 $restoreArgs=@()
 if($PackageCache){$restoreArgs+="-p:RestorePackagesPath=$PackageCache"}
 & dotnet build $project -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false "-p:NinaApp=$NinaApp" @restoreArgs --configfile (Join-Path $PSScriptRoot 'src\managed\NuGet.Config')
 $buildExit=$LASTEXITCODE
} finally {$ErrorActionPreference=$previous}
if($buildExit -ne 0){throw 'Camera plugin build failed.'}
Write-Output 'Build complete. No camera initialized; no system plugin or SDK approval modified.'
