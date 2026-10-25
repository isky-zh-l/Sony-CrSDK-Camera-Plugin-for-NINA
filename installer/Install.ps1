param(
 [string]$SonySdkRoot,
 [string]$NinaApp,
 [string]$PluginDirectory=(Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0\SonyA7V.Camera'),
 [string]$BackupRoot=(Join-Path $env:LOCALAPPDATA 'SonyCrSDKPlugin\backups'),
 [switch]$ValidateOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Assert-NoReparse([string]$Path){
 $current=[IO.Path]::GetFullPath($Path)
 while($current){
  if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "不接受链接或重解析目录：$current"}
  $parent=Split-Path $current -Parent
  if($parent -eq $current){break};$current=$parent
 }
}
function Get-PackagePath([string]$Relative){
 $resolved=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Relative))
 if(!$resolved.StartsWith($PSScriptRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw "包内路径越界：$Relative"}
 Assert-NoReparse $resolved
 return $resolved
}
if(!$SonySdkRoot){$SonySdkRoot=Read-Host '请输入官方 SDK 解压后的 SimpleCli 目录'}
if(!$NinaApp){$NinaApp=Read-Host '请输入包含 NINA.exe 的安装目录'}
$SonySdkRoot=(Resolve-Path -LiteralPath $SonySdkRoot).Path
$NinaApp=(Resolve-Path -LiteralPath $NinaApp).Path
$PluginDirectory=[IO.Path]::GetFullPath($PluginDirectory)
$BackupRoot=[IO.Path]::GetFullPath($BackupRoot)
foreach($path in @($SonySdkRoot,$NinaApp,$PluginDirectory,$BackupRoot)){Assert-NoReparse $path}
if($PluginDirectory -eq [IO.Path]::GetPathRoot($PluginDirectory) -or $PluginDirectory -eq $NinaApp){throw '插件目标必须是独立子目录'}
if($BackupRoot.StartsWith($PluginDirectory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '备份目录不能位于插件目录内'}
$hostCore=Join-Path $NinaApp 'NINA.Core.dll'
if(!(Test-Path -LiteralPath (Join-Path $NinaApp 'NINA.exe')) -or (Get-Item -LiteralPath $hostCore).VersionInfo.FileVersion -ne '3.2.0.9001'){throw '当前候选包仅适配 NINA 3.2.0.9001；请检查安装目录和版本'}
$integrity=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'package-integrity.json') -Raw -Encoding utf8|ConvertFrom-Json
if($integrity.schemaVersion -ne 1){throw '安装包清单版本不匹配'}
foreach($entry in $integrity.files){
 # The default is hashed in the release, but this file is intentionally user-editable after extraction.
 if($entry.path -eq 'settings.json'){continue}
 if((Hash (Get-PackagePath $entry.path)) -ne $entry.sha256){throw "安装包文件损坏：$($entry.path)"}
}
$sdkLock=Get-Content -LiteralPath (Get-PackagePath 'config\sdk-runtime-lock.json') -Raw -Encoding utf8|ConvertFrom-Json
$template=Get-Content -LiteralPath (Get-PackagePath 'config\native-manifest.template.json') -Raw -Encoding utf8|ConvertFrom-Json
if($template.backend -ne 'production'){throw '不能安装测试替身包'}
$sdkRuntime=Join-Path $SonySdkRoot 'external\crsdk'
foreach($entry in $sdkLock.sdkFiles){
 $sdkFile=Join-Path $sdkRuntime $entry.path
 Assert-NoReparse $sdkFile
 if(!(Test-Path -LiteralPath $sdkFile) -or (Hash $sdkFile) -ne $entry.sha256){throw "SDK 文件或版本不匹配：$($entry.path)。请使用官方 2.02.00 Windows x64 包；不要从其他电脑随意拼接 DLL。"}
}
if((Hash (Get-PackagePath 'native\sony_wrapper.dll')) -ne ($template.files|Where-Object role -eq 'wrapper').sha256){throw '包装 DLL 与批准模板不匹配'}
# Do not overwrite an incompatible adapter shared with another camera plugin.
foreach($entry in $sdkLock.sdkFiles|Where-Object {$_.path.StartsWith('CrAdapter/')}){
 $destination=Join-Path $NinaApp $entry.path
 Assert-NoReparse $destination
 if((Test-Path -LiteralPath $destination) -and (Hash $destination) -ne $entry.sha256){throw "已有不同版本的共享适配器：$destination。请先确认其他插件依赖，安装器不会自动覆盖。"}
}
if($ValidateOnly){Write-Output 'PASS：安装包、NINA 版本及本地 SDK 校验通过。未写入文件、加载 SDK 或连接相机。';return}
$hostExe=Join-Path $NinaApp 'NINA.exe'
foreach($process in @(Get-Process -Name NINA -ErrorAction SilentlyContinue)){
 try {$processPath=$process.Path} catch {throw '无法确认 NINA 运行状态，请先关闭所有 NINA 实例'}
 if(!$processPath -or $processPath -eq $hostExe){throw '目标 NINA 正在运行；请正常退出后重新安装。不会强制结束进程。'}
}
$session=Join-Path $BackupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
$stage=Join-Path $session 'prepared'
New-Item -ItemType Directory -Path $stage -Force|Out-Null
$preparedPlugin=Join-Path $stage 'plugin'
New-Item -ItemType Directory -Path (Join-Path $preparedPlugin 'native') -Force|Out-Null
foreach($name in @('SonyA7V.Camera.dll','SonyCamera.Client.dll','settings.json','icon.png')){Copy-Item -LiteralPath (Get-PackagePath $name) -Destination $preparedPlugin}
foreach($name in @('sony_wrapper.dll','SonyArw6.dll','libraw.dll','identity.json')){Copy-Item -LiteralPath (Get-PackagePath ('native\'+$name)) -Destination (Join-Path $preparedPlugin 'native')}
foreach($entry in $sdkLock.sdkFiles){
 if(!$entry.path.StartsWith('CrAdapter/')){Copy-Item -LiteralPath (Join-Path $sdkRuntime $entry.path) -Destination (Join-Path $preparedPlugin 'native')}
}
$manifestPath=Join-Path $preparedPlugin 'native\approved-native.json'
$template|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $manifestPath -Encoding utf8
# Preserve the user-owned cache location while refreshing all native approval fields.
$receiveRoot=''
$existingConfig=Join-Path $PluginDirectory 'sony-a7v.json'
if(Test-Path -LiteralPath $existingConfig){
 Assert-NoReparse $existingConfig
 $previousConfig=Get-Content -LiteralPath $existingConfig -Raw -Encoding utf8|ConvertFrom-Json
 $rootProperty=@($previousConfig.PSObject.Properties|Where-Object {$_.Name -ieq 'receiveRoot'})
 if($rootProperty.Count -gt 1){throw 'Existing receiveRoot is ambiguous'}
 if($rootProperty.Count -eq 1 -and $null -ne $rootProperty[0].Value){
  if($rootProperty[0].Value -isnot [string]){throw 'Existing receiveRoot must be a string'}
  $receiveRoot=[string]$rootProperty[0].Value
  if($receiveRoot -and ($receiveRoot -notmatch '^[A-Za-z]:[\\/]' -or $receiveRoot.IndexOf([char]0) -ge 0)){throw 'Existing receiveRoot must be an absolute local path'}
 }
}
[ordered]@{manifestPath='native/approved-native.json';approvedManifestSha256=(Hash $manifestPath);receiveRoot=$receiveRoot}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $preparedPlugin 'sony-a7v.json') -Encoding utf8
$operations=@()
foreach($file in Get-ChildItem -LiteralPath $preparedPlugin -File -Recurse){
 $relative=$file.FullName.Substring($preparedPlugin.Length+1)
 $destination=[IO.Path]::GetFullPath((Join-Path $PluginDirectory $relative))
 if(!$destination.StartsWith($PluginDirectory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '目标路径越界'}
 # Settings are user-owned. Keep existing choices on upgrade.
 if($relative -eq 'settings.json' -and (Test-Path -LiteralPath $destination)){continue}
 $operations+=[pscustomobject]@{source=$file.FullName;target=$destination}
}
foreach($entry in $sdkLock.sdkFiles|Where-Object {$_.path.StartsWith('CrAdapter/')}){$operations+=[pscustomobject]@{source=(Join-Path $sdkRuntime $entry.path);target=(Join-Path $NinaApp $entry.path)}}
$records=@();$index=0
try {
 foreach($operation in $operations){
  Assert-NoReparse $operation.target
  $exists=Test-Path -LiteralPath $operation.target
  $backup=Join-Path $session ('before\'+$index.ToString('D3'))
  $oldHash=$null
  if($exists){New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force|Out-Null;Copy-Item -LiteralPath $operation.target -Destination $backup;$oldHash=Hash $backup}
  $record=[ordered]@{target=$operation.target;existed=$exists;backup=$backup;previousSha256=$oldHash;installedSha256=(Hash $operation.source)}
  $records+=$record
  New-Item -ItemType Directory -Path (Split-Path $operation.target -Parent) -Force|Out-Null
  Copy-Item -LiteralPath $operation.source -Destination $operation.target -Force
  if((Hash $operation.target) -ne $record.installedSha256){throw "安装后校验失败：$($operation.target)"}
  $index++
 }
} catch {
 $failure=$_
 for($i=$records.Count-1;$i -ge 0;$i--){$record=$records[$i];try {if($record.existed){Copy-Item -LiteralPath $record.backup -Destination $record.target -Force}elseif(Test-Path -LiteralPath $record.target){Remove-Item -LiteralPath $record.target -Force}}catch{Write-Warning "回滚失败，请使用备份恢复：$($record.target)"}}
 throw $failure
}
$receipt=Join-Path $session 'install-receipt.json'
[ordered]@{schemaVersion=1;version=$integrity.version;pluginDirectory=$PluginDirectory;hostDirectory=$NinaApp;backupDirectory=$session;files=$records}|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $receipt -Encoding utf8
Write-Output "安装完成：$PluginDirectory"
Write-Output "备份与卸载记录：$receipt"
Write-Output '可以打开 NINA，选择 Sony CrSDK Camera。默认不另存 ARW 副本；请在 NINA 开启保存并选 FITS/XISF。'
Write-Output '本脚本未启动 NINA、初始化 SDK、操作相机或修改驱动。'

