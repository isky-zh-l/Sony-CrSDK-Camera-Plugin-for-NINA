param([Parameter(Mandatory=$true)][string]$NinaApp,[string]$RealRawIndex,[string]$SonySdkRoot)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$SonySdkRoot){$SonySdkRoot=Join-Path $root 'deps/sony/SimpleCli'}
$env:DOTNET_CLI_HOME=Join-Path $root 'artifacts/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$previous=$ErrorActionPreference
try {
 $ErrorActionPreference='Continue'   # python and dotnet write progress to stderr; Stop would abort on harmless output
 & python (Join-Path $root 'tests/test_decoder.py') $root
 $pythonExit=$LASTEXITCODE
} finally {$ErrorActionPreference=$previous}
if($pythonExit -ne 0){throw 'Synthetic decoder regression failed'}
$project=Join-Path $root 'tests/MultiCamera/MultiCamera.csproj'
try {
 $ErrorActionPreference='Continue'
 & dotnet build $project -c Release "-p:NinaApp=$NinaApp" -m:1 -nodeReuse:false -p:UseSharedCompilation=false
 $buildExit=$LASTEXITCODE
} finally {$ErrorActionPreference=$previous}
if($buildExit -ne 0){throw 'Multi-camera test build failed'}
$output=Join-Path $root 'tests/MultiCamera/bin/Release/net8.0-windows'
$native=Join-Path $output 'native'
New-Item -ItemType Directory -Path $native,(Join-Path $output 'CrAdapter') -Force|Out-Null
foreach($name in @('SonyArw6.dll','libraw.dll','identity.json')){Copy-Item -LiteralPath (Join-Path $root ('artifacts/decoder/'+$name)) -Destination $native -Force}
Copy-Item -LiteralPath (Join-Path $root 'artifacts/multicamera/synthetic-raw') -Destination $output -Recurse -Force
$lock=Get-Content -LiteralPath (Join-Path $root 'config/sdk-runtime-lock.json') -Raw|ConvertFrom-Json
$sdk=Join-Path $SonySdkRoot 'external/crsdk'
$wrapper=Join-Path $root 'artifacts/native/sony_wrapper.dll'
Copy-Item -LiteralPath $wrapper -Destination $native -Force
$files=@([ordered]@{role='wrapper';base='native';path='sony_wrapper.dll';sha256=(Get-FileHash -LiteralPath $wrapper).Hash})
foreach($entry in $lock.sdkFiles){
 $source=Join-Path $sdk $entry.path
 if((Get-FileHash -LiteralPath $source).Hash -ne $entry.sha256){throw "SDK source identity mismatch: $($entry.path)"}
 if($entry.role -eq 'core'){$source=Join-Path $root 'artifacts/native/fake/Cr_Core.dll'}
 $adapter=$entry.path.StartsWith('CrAdapter/')
 $relative=if($adapter){$entry.path}else{[IO.Path]::GetFileName($entry.path)}
 $base=if($adapter){'host'}else{'native'}
 $target=Join-Path $(if($adapter){$output}else{$native}) $relative
 Copy-Item -LiteralPath $source -Destination $target -Force
 $files+=[ordered]@{role=$entry.role;base=$base;path=$relative;sha256=(Get-FileHash -LiteralPath $target).Hash}
}
Copy-Item -LiteralPath (Join-Path $root 'config/camera-models.json') -Destination $output -Force
$manifestPath=Join-Path $native 'approved-native.json'
$models=(Get-Content -LiteralPath (Join-Path $root 'config/camera-models.json') -Raw|ConvertFrom-Json).models
$manifestJson=[ordered]@{schemaVersion=1;packageId='multicamera-ISOLATED-FAKE';backend='test';abiVersion=2;supportedModels=$models;structureSizes=[ordered]@{SwError=184;SwCameraInfo=168;SwParameters=32;SwCaptureResult=2304;SwExposureRequest=40;SwExposureCapabilities=56;SwExposureInfo=464};files=$files}|ConvertTo-Json -Depth 8
# Windows PowerShell 5.1 has no utf8NoBOM encoder name; write BOM-free UTF-8 explicitly.
[IO.File]::WriteAllText($manifestPath,$manifestJson,(New-Object System.Text.UTF8Encoding($false)))
[ordered]@{manifestPath='native/approved-native.json';approvedManifestSha256=(Get-FileHash -LiteralPath $manifestPath).Hash;receiveRoot=(Join-Path $output 'receive')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'sony-a7v.json') -Encoding utf8
'{}'|Set-Content -LiteralPath (Join-Path $output 'settings.json') -Encoding utf8
$previousCorpus=$env:SW_REAL_CORPUS_INDEX
try {
 if($RealRawIndex){$env:SW_REAL_CORPUS_INDEX=(Resolve-Path -LiteralPath $RealRawIndex).Path}else{$env:SW_REAL_CORPUS_INDEX=$null}
 $ErrorActionPreference='Continue'
 & (Join-Path $output 'MultiCamera.exe') ([IO.Path]::GetFullPath($NinaApp))
 $runExit=$LASTEXITCODE
}finally{$env:SW_REAL_CORPUS_INDEX=$previousCorpus;$ErrorActionPreference='Stop'}
if($runExit -ne 0){throw 'Multi-camera offline regression failed'}
