param(
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [Parameter(Mandatory=$true)][string]$SourceArchive
)
# Release packaging. Does not deploy, approve existing native packages, or load the SDK.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
$SourceArchive=(Resolve-Path -LiteralPath $SourceArchive).Path
if((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output+'.zip'))){throw 'Use a new output directory; preserve existing artifacts'}
$built=Join-Path $root 'src/nina/src/SonyA7V.Camera/bin/Release/net8.0-windows'
$decoder=Join-Path $root 'artifacts/decoder'
$wrapper=Join-Path $root 'artifacts/native/sony_wrapper.dll'
$sdkLock=Get-Content -LiteralPath (Join-Path $root 'config/sdk-runtime-lock.json') -Raw|ConvertFrom-Json
$identity=Get-Content -LiteralPath (Join-Path $decoder 'identity.json') -Raw|ConvertFrom-Json
foreach($name in @('libraw.dll','SonyArw6.dll')){if((Get-FileHash -LiteralPath (Join-Path $decoder $name)).Hash -ne $identity.$name){throw 'Decoder identity mismatch'}}
New-Item -ItemType Directory -Path $output,(Join-Path $output 'native'),(Join-Path $output 'config'),(Join-Path $output 'licenses') -Force|Out-Null
foreach($name in @('SonyA7V.Camera.dll','SonyCamera.Client.dll','icon.png')){Copy-Item -LiteralPath (Join-Path $built $name) -Destination $output}
foreach($name in @('SonyArw6.dll','libraw.dll','identity.json')){Copy-Item -LiteralPath (Join-Path $decoder $name) -Destination (Join-Path $output 'native')}
Copy-Item -LiteralPath $wrapper -Destination (Join-Path $output 'native')
Copy-Item -LiteralPath (Join-Path $root 'config/settings.json') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'config/sdk-runtime-lock.json'),(Join-Path $root 'config/camera-models.json') -Destination (Join-Path $output 'config')
$files=@([ordered]@{role='wrapper';base='native';path='sony_wrapper.dll';sha256=(Get-FileHash -LiteralPath $wrapper).Hash})
foreach($item in $sdkLock.sdkFiles){$adapter=$item.path.StartsWith('CrAdapter/');$files+=[ordered]@{role=$item.role;base=$(if($adapter){'host'}else{'native'});path=$(if($adapter){$item.path}else{[IO.Path]::GetFileName($item.path)});sha256=$item.sha256}}
$models=(Get-Content -LiteralPath (Join-Path $root 'config/camera-models.json') -Raw|ConvertFrom-Json).models
[ordered]@{schemaVersion=1;packageId='sony-crsdk-0.5.4';backend='production';abiVersion=2;supportedModels=$models;structureSizes=[ordered]@{SwError=184;SwCameraInfo=168;SwParameters=32;SwCaptureResult=2304;SwExposureRequest=40;SwExposureCapabilities=56;SwExposureInfo=464};files=$files}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $output 'config/native-manifest.template.json') -Encoding utf8
foreach($name in @('Install.ps1','Uninstall.ps1')){Copy-Item -LiteralPath (Join-Path $root ('installer/'+$name)) -Destination $output}
New-Item -ItemType Directory -Path (Join-Path $output 'docs'),(Join-Path $output 'assets/images') -Force|Out-Null
foreach($name in @('INSTALL.md','USAGE.md','COMPATIBILITY.md')){Copy-Item -LiteralPath (Join-Path $root ('docs/'+$name)) -Destination (Join-Path $output 'docs')}
foreach($name in @('sony-shared-architecture.png','sony-controls.png')){Copy-Item -LiteralPath (Join-Path $root ('assets/images/'+$name)) -Destination (Join-Path $output 'assets/images')}
foreach($name in @('LICENSE','THIRD-PARTY-NOTICES.md')){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $output}
foreach($name in @('COPYRIGHT','LICENSE.LGPL','LICENSE.CDDL')){Copy-Item -LiteralPath (Join-Path $root ('third_party/LibRaw/'+$name)) -Destination (Join-Path $output 'licenses')}
Copy-Item -LiteralPath $SourceArchive -Destination (Join-Path $output 'corresponding-source.zip')
(Get-Content -LiteralPath (Join-Path $root 'installer/README.md') -Raw -Encoding utf8).Replace('../docs/','docs/')|Set-Content -LiteralPath (Join-Path $output 'README.md') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $root 'installer/SOURCE.md') -Destination $output
$allowed=@('SonyA7V.Camera.dll','SonyCamera.Client.dll','native/sony_wrapper.dll','native/libraw.dll','native/SonyArw6.dll')
$entries=@(Get-ChildItem -LiteralPath $output -File -Recurse|Sort-Object FullName|ForEach-Object {
 $relative=$_.FullName.Substring($output.Length+1).Replace('\','/')
 if($_.Extension -in @('.dll','.lib','.exe') -and $relative -notin $allowed){throw "Forbidden package binary: $relative"}
 [ordered]@{path=$relative;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
[ordered]@{schemaVersion=1;version='0.5.4.0';files=$entries}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'package-integrity.json') -Encoding utf8
Compress-Archive -Path (Join-Path $output '*') -DestinationPath ($output+'.zip') -CompressionLevel Optimal
Get-FileHash -LiteralPath ($output+'.zip')
Write-Output 'Release package built without SDK binaries. No deployment, hardware action or existing approval changed.'
