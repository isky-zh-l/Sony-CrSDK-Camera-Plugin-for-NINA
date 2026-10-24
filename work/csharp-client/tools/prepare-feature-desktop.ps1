param(
 [Parameter(Mandatory=$true)][string]$WrapperDll,
 [Parameter(Mandatory=$true)][string]$SdkRuntime,
 [string]$OutputDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\features-desktop-20261024-r2')
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$built=Join-Path $projectRoot 'src\SonyCamera.Desktop\bin\Release\net8.0-windows'
if(!(Test-Path -LiteralPath (Join-Path $built 'SonyCamera.Desktop.exe'))){throw 'Build the managed solution first.'}
if(Test-Path -LiteralPath (Join-Path $OutputDirectory 'native\approved-native.json')){throw 'Output package already exists. Use a fresh versioned directory; do not overwrite a loaded package.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
Get-ChildItem -LiteralPath $built -File|Where-Object {$_.Extension -in '.exe','.dll','.json','.pdb'}|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $OutputDirectory}
& (Join-Path $PSScriptRoot 'deploy-native.ps1') -WrapperDll $WrapperDll -SdkRuntime $SdkRuntime -HostDirectory $OutputDirectory -ApprovedBuilds (Join-Path $projectRoot 'deployment\approved-native-features.json') -PackageId 'a7v-abi2-features1-offline-built-20261024' -WriteConfiguration (Join-Path $OutputDirectory 'features.json') -RelativeManifestPath
Write-Output "Local feature desktop ready: $OutputDirectory. Newly added functions still require hardware acceptance. SDK files stay local; not a public package."
