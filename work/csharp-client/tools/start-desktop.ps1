param([string]$Configuration)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'check-runtime.ps1') -Desktop
$exe=Join-Path (Split-Path $PSScriptRoot -Parent) 'src\SonyCamera.Desktop\bin\Release\net8.0-windows\SonyCamera.Desktop.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Desktop build is missing. Build SonyCamera.slnx first.'}
if($Configuration){& $exe --config $Configuration}else{& $exe}
if($LASTEXITCODE -ne 0){throw "Desktop exited with code $LASTEXITCODE. Check user data logs."}
