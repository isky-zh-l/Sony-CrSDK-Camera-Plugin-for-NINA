param(
 [Parameter(Mandatory=$true)][string]$WrapperDll,
 [Parameter(Mandatory=$true)][string]$SdkRuntime,
 [Parameter(Mandatory=$true)][string]$HostDirectory,
 [string]$ApprovedBuilds=(Join-Path (Split-Path $PSScriptRoot -Parent) 'deployment\approved-native-builds.json'),
 [string]$PackageId='a7v-abi2-crsdk202-20261023',
 [switch]$TestOnly,
 [string]$TestCoreDll,
 [string]$WriteConfiguration,
 [switch]$RelativeManifestPath,
 [string[]]$SupportedModels=@('ILCE-7M5')
)
$ErrorActionPreference='Stop'
if($RelativeManifestPath -and !$WriteConfiguration){throw '-RelativeManifestPath requires -WriteConfiguration'}
$approval=Get-Content -LiteralPath $ApprovedBuilds -Raw|ConvertFrom-Json
if($approval.schemaVersion -ne 1){throw 'Unsupported local preparation approval format'}
if((Get-FileHash -LiteralPath $WrapperDll).Hash -ne $approval.wrapperSha256){throw '包装DLL与维护者批准记录不一致；先核对原生升级交接，不会自动批准未知文件'}
if($TestOnly){if(!$TestCoreDll -or (Get-FileHash -LiteralPath $TestCoreDll).Hash -ne $approval.testCoreSha256){throw '测试替身不是已审核隔离构建'}}
elseif($TestCoreDll){throw 'TestCoreDll只能与TestOnly一起使用'}
# Local preparation requires a reviewed allowlist and legally obtained SDK.
# Hashes establish identity, not vendor authentication or redistribution permission.
foreach($item in $approval.sdkFiles){
 $source=Join-Path $SdkRuntime $item.path
 if((Get-FileHash -LiteralPath $source).Hash -ne $item.sha256){throw "SDK本地来源与批准记录不一致：$($item.role) $source"}
}
$hostRoot=[IO.Path]::GetFullPath($HostDirectory)
$native=Join-Path $hostRoot 'native'
if(Test-Path -LiteralPath (Join-Path $native 'approved-native.json')){throw '目标已有清单。升级请准备新版本目录，避免覆盖正在使用的原生包。'}
New-Item -ItemType Directory -Path $native,(Join-Path $hostRoot 'CrAdapter') -Force|Out-Null
Copy-Item -LiteralPath $WrapperDll -Destination (Join-Path $native 'sony_wrapper.dll') -Force
$files=@([ordered]@{role='wrapper';base='native';path='sony_wrapper.dll';sha256=$approval.wrapperSha256})
foreach($item in $approval.sdkFiles){
 $isAdapter=$item.path.StartsWith('CrAdapter/')
 $relative=if($isAdapter){$item.path}else{[IO.Path]::GetFileName($item.path)}
 $base=if($isAdapter){'host'}else{'native'}
 $destination=Join-Path $(if($isAdapter){$hostRoot}else{$native}) $relative
 $source=if($TestOnly -and $item.role -eq 'core'){$TestCoreDll}else{Join-Path $SdkRuntime $item.path}
 Copy-Item -LiteralPath $source -Destination $destination -Force
 $files+=[ordered]@{role=$item.role;base=$base;path=$relative;sha256=(Get-FileHash -LiteralPath $destination).Hash}
}
$manifest=Join-Path $native 'approved-native.json'
[ordered]@{schemaVersion=1;packageId=$PackageId;backend=$(if($TestOnly){'test'}else{'production'});abiVersion=2;supportedModels=$SupportedModels;structureSizes=[ordered]@{SwError=184;SwCameraInfo=168;SwParameters=32;SwCaptureResult=2304;SwExposureRequest=40;SwExposureCapabilities=56;SwExposureInfo=464};files=$files}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $manifest -Encoding utf8
$pin=(Get-FileHash -LiteralPath $manifest).Hash
$receipt=[ordered]@{packageId=$PackageId;manifest=$manifest;approvedManifestSha256=$pin;preparationApprovalSha256=(Get-FileHash -LiteralPath $ApprovedBuilds).Hash;trust='Explicit local preparation from maintainer-reviewed allowlist and legally obtained SDK. Not vendor authentication or redistribution permission.';backend=$(if($TestOnly){'test'}else{'production'})}
$receipt|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $hostRoot 'local-preparation-receipt.json') -Encoding utf8
if($WriteConfiguration){
 $configPath=[IO.Path]::GetFullPath($WriteConfiguration)
 New-Item -ItemType Directory -Path (Split-Path $configPath -Parent) -Force|Out-Null
 $configuredManifest=$manifest
 if($RelativeManifestPath){. (Join-Path $PSScriptRoot 'relative-manifest-path.ps1');$configuredManifest=Get-RelativeManifestPath $configPath $manifest}
 [ordered]@{manifestPath=$configuredManifest;approvedManifestSha256=$pin;allowTestPackage=[bool]$TestOnly}|ConvertTo-Json|Set-Content -LiteralPath $configPath -Encoding utf8
 Write-Output "已从本机审核准备步骤写入宿主批准配置：$configPath"
}
Write-Output "本机原生包：$manifest"
Write-Output "批准清单SHA256：$pin"
Write-Output 'SDK来源目录仅在本准备步骤使用。运行时不再访问来源目录。SDK未确认再分发许可，不上传或纳入公开包。'
