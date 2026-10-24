param([string]$PackageDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\features-desktop-20261024-r2'))
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'check-runtime.ps1') -Desktop
$exe=Join-Path $PackageDirectory 'SonyCamera.Desktop.exe'
$config=Join-Path $PackageDirectory 'features.json'
if(!(Test-Path -LiteralPath $exe) -or !(Test-Path -LiteralPath $config)){throw 'Feature package missing. Build first, then use tools\prepare-feature-desktop.ps1 with your legal external SDK.'}
& $exe --config $config
if($LASTEXITCODE -ne 0){throw "Feature desktop exited with code $LASTEXITCODE"}
