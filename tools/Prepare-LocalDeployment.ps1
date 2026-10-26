param(
 [Parameter(Mandatory=$true)][string]$ReviewedWrapperSha256,
 [string]$SonySdkRoot=(Join-Path (Split-Path $PSScriptRoot -Parent) 'deps\sony\SimpleCli'),
 [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$wrapper=Join-Path $root 'artifacts\native\sony_wrapper.dll'
if($ReviewedWrapperSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or (Get-FileHash -LiteralPath $wrapper).Hash -ne $ReviewedWrapperSha256){throw 'Supply the independently reviewed hash of this locally built wrapper.'}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Choose a NEW local output directory.'}
$built=Join-Path $root 'src\nina\src\SonyA7V.Camera\bin\Release\net8.0-windows'
$decoder=Join-Path $root 'artifacts\decoder'
foreach($p in @((Join-Path $built 'SonyA7V.Camera.dll'),(Join-Path $built 'SonyCamera.Client.dll'),(Join-Path $decoder 'SonyArw6.dll'),(Join-Path $decoder 'libraw.dll'))){if(!(Test-Path -LiteralPath $p)){throw "Build input missing: $p"}}
$sdkLock=Get-Content -LiteralPath (Join-Path $root 'config\sdk-runtime-lock.json') -Raw -Encoding utf8|ConvertFrom-Json
$sdkRuntime=Join-Path $SonySdkRoot 'external\crsdk'
foreach($item in $sdkLock.sdkFiles){if((Get-FileHash -LiteralPath (Join-Path $sdkRuntime $item.path)).Hash -ne $item.sha256){throw "SDK version/hash mismatch: $($item.path). Review upgrades separately."}}
New-Item -ItemType Directory -Path $output -Force|Out-Null
$plugin=Join-Path $output 'plugin'
New-Item -ItemType Directory -Path $plugin -Force|Out-Null
Copy-Item -LiteralPath (Join-Path $built 'SonyA7V.Camera.dll'),(Join-Path $built 'SonyCamera.Client.dll') -Destination $plugin
$approval=Join-Path $output 'reviewed-local-build.json'
[ordered]@{schemaVersion=1;wrapperSha256=$ReviewedWrapperSha256;sdkFiles=$sdkLock.sdkFiles}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $approval -Encoding utf8
& (Join-Path $root 'src\managed\tools\deploy-native.ps1') -WrapperDll $wrapper -SdkRuntime $sdkRuntime -HostDirectory $plugin -ApprovedBuilds $approval -PackageId 'sony-crsdk-local-build' -WriteConfiguration (Join-Path $plugin 'sony-a7v.json') -RelativeManifestPath -SupportedModels ((Get-Content -LiteralPath (Join-Path $root 'config/camera-models.json') -Raw|ConvertFrom-Json).models)
# CrAdapter must be installed beside NINA.exe, not inside the installed plugin.
Move-Item -LiteralPath (Join-Path $plugin 'CrAdapter') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'config\settings.json') -Destination $plugin
Copy-Item -LiteralPath (Join-Path $decoder 'SonyArw6.dll'),(Join-Path $decoder 'libraw.dll'),(Join-Path $decoder 'identity.json') -Destination (Join-Path $plugin 'native')
Write-Output "PRIVATE local deployment prepared at $output. Contains Sony runtime: do not upload or redistribute."
Write-Output 'Install plugin contents in the NINA plugin directory; install CrAdapter beside NINA.exe. No system files were changed.'
