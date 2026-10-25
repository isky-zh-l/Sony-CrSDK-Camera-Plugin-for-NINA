param(
 [Parameter(Mandatory=$true)][string]$PackageDirectory,
 [Parameter(Mandatory=$true)][string]$SonySdkRoot,
 [Parameter(Mandatory=$true)][string]$NinaApp,
 [Parameter(Mandatory=$true)][string]$TestRoot
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$TestRoot=[IO.Path]::GetFullPath($TestRoot)
if(Test-Path -LiteralPath $TestRoot){throw 'Choose a new test directory'}
$hostCopy=Join-Path $TestRoot 'host'
$plugin=Join-Path $TestRoot 'plugins\SonyA7V.Camera'
$backups=Join-Path $TestRoot 'backups'
New-Item -ItemType Directory -Path $hostCopy -Force|Out-Null
Copy-Item -LiteralPath (Join-Path $NinaApp 'NINA.exe'),(Join-Path $NinaApp 'NINA.Core.dll') -Destination $hostCopy
$results=@()
function Run-Setup([string[]]$Extra,[bool]$ExpectSuccess,[string]$Case){
 $previous=$ErrorActionPreference
 try {
  $ErrorActionPreference='Continue'
  $output=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PackageDirectory 'Install.ps1') -SonySdkRoot $SonySdkRoot -NinaApp $hostCopy -PluginDirectory $plugin -BackupRoot $backups @Extra 2>&1
  $code=$LASTEXITCODE
 } finally {$ErrorActionPreference=$previous}
 $output|Out-File -LiteralPath (Join-Path $TestRoot ($Case+'.log')) -Encoding utf8
 if(($code -eq 0) -ne $ExpectSuccess){throw "Unexpected installer result: $Case ($code)"}
 $script:results+=[pscustomobject]@{case=$Case;passed=$true;exitCode=$code}
}
Run-Setup @('-ValidateOnly') $true 'validate-only'
if((Test-Path -LiteralPath $plugin) -or (Test-Path -LiteralPath $backups)){throw 'ValidateOnly wrote deployment files'}
$payload=Join-Path $PackageDirectory 'SonyA7V.Camera.dll'
$original=[IO.File]::ReadAllBytes($payload)
try {[IO.File]::WriteAllBytes($payload,($original+[byte]0));Run-Setup @('-ValidateOnly') $false 'reject-corrupt-payload'}
finally {[IO.File]::WriteAllBytes($payload,$original)}
$conflict=Join-Path $hostCopy 'CrAdapter\Cr_PTP_USB.dll'
New-Item -ItemType Directory -Path (Split-Path $conflict -Parent) -Force|Out-Null
[IO.File]::WriteAllText($conflict,'not the approved adapter')
Run-Setup @('-ValidateOnly') $false 'reject-conflicting-adapter'
if([IO.File]::ReadAllText($conflict) -ne 'not the approved adapter'){throw 'Conflict file was overwritten'}
if(!([IO.Path]::GetFullPath($conflict).StartsWith($TestRoot+'\',[StringComparison]::OrdinalIgnoreCase))){throw 'Invalid test cleanup path'}
Remove-Item -LiteralPath $conflict
Run-Setup @() $true 'fresh-install'
$settings=Get-Content -LiteralPath (Join-Path $plugin 'settings.json') -Raw|ConvertFrom-Json
if($settings.saveOriginalRaw -ne 'never'){throw 'Fresh install did not default to FITS-only copy policy'}
$config=Get-Content -LiteralPath (Join-Path $plugin 'sony-a7v.json') -Raw|ConvertFrom-Json
if((Get-FileHash -LiteralPath (Join-Path $plugin $config.manifestPath)).Hash -ne $config.approvedManifestSha256){throw 'Installed manifest pin mismatch'}
# Validate using the real C# client, in a fresh process located next to the cloned adapters.
# --mode validate never initializes the SDK or contacts a camera.
$acceptance=Join-Path $projectRoot 'src\managed\tests\SonyCamera.Acceptance\bin\Release\net8.0'
foreach($file in Get-ChildItem -LiteralPath $acceptance -File|Where-Object {$_.Extension -in '.exe','.dll','.json'}){Copy-Item -LiteralPath $file.FullName -Destination $hostCopy -Force}
$validationConfig=Join-Path $hostCopy 'client-validation.json'
[ordered]@{manifestPath=(Join-Path $plugin $config.manifestPath);approvedManifestSha256=$config.approvedManifestSha256;allowTestPackage=$false}|ConvertTo-Json|Set-Content -LiteralPath $validationConfig -Encoding utf8
& (Join-Path $hostCopy 'SonyCamera.Acceptance.exe') --mode validate --config $validationConfig --run-dir (Join-Path $TestRoot 'client-validation') *> (Join-Path $TestRoot 'client-validation.log')
if($LASTEXITCODE -ne 0){throw 'Real client rejected installed native package'}
$results+=[pscustomobject]@{case='real-client-offline-validation';passed=$true;exitCode=0}
$receipt=Get-ChildItem -LiteralPath $backups -Filter 'install-receipt.json' -Recurse|Select-Object -First 1
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PackageDirectory 'Uninstall.ps1') -ReceiptPath $receipt.FullName *> (Join-Path $TestRoot 'fresh-uninstall.log')
if($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath (Join-Path $plugin 'SonyA7V.Camera.dll'))){throw 'Fresh uninstall failed'}
if(!(Test-Path -LiteralPath (Join-Path $plugin 'settings.json'))){throw 'Uninstall removed user settings'}
$results+=[pscustomobject]@{case='fresh-uninstall-preserves-settings';passed=$true;exitCode=0}
# Upgrade an existing file, preserve custom settings, then restore the previous file from backup.
[IO.File]::WriteAllText((Join-Path $plugin 'SonyA7V.Camera.dll'),'previous plugin marker')
[IO.File]::WriteAllText((Join-Path $plugin 'settings.json'),'{"saveOriginalRaw":"also","liveViewColor":false,"rawCopyFolder":"custom"}')
$settingsHash=(Get-FileHash -LiteralPath (Join-Path $plugin 'settings.json')).Hash
$cachePath=Join-Path $TestRoot 'user-cache'
$previousDeployment=[ordered]@{manifestPath='native/approved-native.json';approvedManifestSha256=('0'*64);receiveRoot=$cachePath}
$previousDeployment|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $plugin 'sony-a7v.json') -Encoding utf8
$previousDeploymentHash=(Get-FileHash -LiteralPath (Join-Path $plugin 'sony-a7v.json')).Hash
Run-Setup @() $true 'upgrade-preserves-settings'
$upgraded=Get-Content -LiteralPath (Join-Path $plugin 'sony-a7v.json') -Raw|ConvertFrom-Json
if($upgraded.receiveRoot -ne $cachePath -or $upgraded.approvedManifestSha256 -eq ('0'*64)){throw 'Upgrade must preserve cache but replace native approval'}
$results+=[pscustomobject]@{case='upgrade-preserves-cache-and-refreshes-approval';passed=$true;exitCode=0}
if((Get-FileHash -LiteralPath (Join-Path $plugin 'settings.json')).Hash -ne $settingsHash){throw 'Upgrade overwrote settings'}
$receipt=Get-ChildItem -LiteralPath $backups -Filter 'install-receipt.json' -Recurse|Sort-Object LastWriteTime -Descending|Select-Object -First 1
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PackageDirectory 'Uninstall.ps1') -ReceiptPath $receipt.FullName *> (Join-Path $TestRoot 'restore-previous.log')
if($LASTEXITCODE -ne 0 -or [IO.File]::ReadAllText((Join-Path $plugin 'SonyA7V.Camera.dll')) -ne 'previous plugin marker'){throw 'Restore previous plugin failed'}
if((Get-FileHash -LiteralPath (Join-Path $plugin 'sony-a7v.json')).Hash -ne $previousDeploymentHash){throw 'Rollback did not restore previous deployment config'}
$results+=[pscustomobject]@{case='restore-previous-plugin';passed=$true;exitCode=0}
$results|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $TestRoot 'results.json') -Encoding utf8
$results|Format-Table
Write-Output 'PASS: Windows PowerShell 5 installer validation, fresh install, integrity/conflict rejection, client validation, settings preservation and restore. No real NINA or camera was used.'
