param(
 [Parameter(Mandatory=$true)][string]$SonySdkRoot,
 [Parameter(Mandatory=$true)][string]$NinaApp,
 [Parameter(Mandatory=$true)][string]$DataDirectory,
 [string]$ResultFile,
 [switch]$ValidateOnly
)
# Explicit first-run import. Copies approved bytes; never loads a DLL or opens a camera.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
foreach($module in @('Microsoft.PowerShell.Management','Microsoft.PowerShell.Utility')){Import-Module (Join-Path $PSHOME ('Modules/'+$module+'/'+$module+'.psd1'))}
trap {
 if($ResultFile -and [IO.Path]::GetFullPath($ResultFile).StartsWith([IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){
  try {NoLinks $ResultFile;[IO.File]::WriteAllText($ResultFile,$_.Exception.Message)}catch{}
 }
 Write-Error $_ -ErrorAction Continue
 if($_.CategoryInfo.Category -eq [System.Management.Automation.ErrorCategory]::PermissionDenied -or $_.Exception.GetBaseException() -is [UnauthorizedAccessException]){exit 5}
 exit 1
}
function NoLinks([string]$Path){
 $current=[IO.Path]::GetFullPath($Path)
 while($current){
  if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "不接受链接路径：$current"}
  $current=Split-Path $current -Parent
 }
}
function Hash([string]$Path){NoLinks $Path;return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function CheckPe([string]$Path){
 $stream=[IO.File]::OpenRead($Path)
 try {
  $reader=New-Object IO.BinaryReader($stream)
  if($stream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D){throw 'Not PE'}
  $stream.Position=0x3c;$offset=$reader.ReadInt32()
  if($offset -lt 64 -or $offset -gt $stream.Length-26){throw 'Invalid PE header'}
  $stream.Position=$offset
  if($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664){throw 'Requires AMD64 DLL'}
  $stream.Position=$offset+20;$size=$reader.ReadUInt16();$flags=$reader.ReadUInt16()
  if($size -lt 112 -or $offset+24+$size -gt $stream.Length -or !($flags -band 0x2000) -or $reader.ReadUInt16() -ne 0x20b){throw 'Requires PE32+ DLL'}
 }finally{$stream.Dispose()}
}
$package=$PSScriptRoot
$SonySdkRoot=(Resolve-Path -LiteralPath $SonySdkRoot).Path
$NinaApp=(Resolve-Path -LiteralPath $NinaApp).Path
$DataDirectory=[IO.Path]::GetFullPath($DataDirectory)
foreach($path in @($package,$SonySdkRoot,$NinaApp,$DataDirectory)){NoLinks $path}
if($DataDirectory -eq [IO.Path]::GetPathRoot($DataDirectory) -or $DataDirectory.StartsWith($NinaApp.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $DataDirectory -eq $NinaApp){throw 'User data must be separate from the host'}
$hostVersion=[version](Get-Item -LiteralPath (Join-Path $NinaApp 'NINA.Core.dll')).VersionInfo.FileVersion
if(!(Test-Path -LiteralPath (Join-Path $NinaApp 'NINA.exe')) -or $hostVersion -lt [version]'3.2.0.9001' -or $hostVersion.Major -ne 3){throw 'Requires NINA 3.x, minimum 3.2.0.9001; other major versions are not verified.'}
$integrity=Get-Content -LiteralPath (Join-Path $package 'package-integrity.json') -Raw -Encoding utf8|ConvertFrom-Json
if($integrity.schemaVersion -ne 1){throw 'Invalid package integrity schema'}
foreach($name in @('Configure-Sdk.ps1','native/sony_wrapper.dll','config/sdk-runtime-lock.json','config/native-manifest.template.json')){
 $entry=@($integrity.files|Where-Object path -eq $name)
 if($entry.Count -ne 1 -or (Hash (Join-Path $package $name)) -ne $entry[0].sha256){throw "公开包校验失败：$name"}
}
$lockPath=Join-Path $package 'config/sdk-runtime-lock.json'
$lock=Get-Content -LiteralPath $lockPath -Raw -Encoding utf8|ConvertFrom-Json
$roles=@('core','monitor','monitorPf','usbAdapter','ipAdapter','libusb','libssh')
$paths=@('Cr_Core.dll','monitor_protocol.dll','monitor_protocol_pf.dll','CrAdapter/Cr_PTP_USB.dll','CrAdapter/Cr_PTP_IP.dll','CrAdapter/libusb-1.0.dll','CrAdapter/libssh2.dll')
if($lock.schemaVersion -ne 1 -or $lock.sdkVersion -ne '2.02.00' -or $lock.sdkFiles.Count -ne 7){throw 'Invalid SDK lock'}
$runtime=Join-Path $SonySdkRoot 'external/crsdk'
if(!(Test-Path -LiteralPath (Join-Path $runtime 'Cr_Core.dll'))){
 $runtime=Join-Path $SonySdkRoot 'SimpleCli/external/crsdk'
 if(!(Test-Path -LiteralPath (Join-Path $runtime 'Cr_Core.dll'))){
  if(Test-Path -LiteralPath (Join-Path $SonySdkRoot 'Cr_Core.dll')){$runtime=$SonySdkRoot}
  else{throw '请选择 SDK 解压目录、SimpleCli 目录或包含 Cr_Core.dll 与 CrAdapter 的 crsdk 目录。'}
 }
}
$sdkCache=Join-Path $DataDirectory ('sdk/'+(Hash $lockPath))
$newHost=@()
for($i=0;$i -lt $roles.Count;$i++){
 $entry=@($lock.sdkFiles|Where-Object role -eq $roles[$i])
 if($entry.Count -ne 1 -or $entry[0].path -cne $paths[$i]){throw 'SDK role/path mismatch'}
 $source=Join-Path $runtime $paths[$i]
 if((Hash $source) -ne $entry[0].sha256){throw "SDK 版本或文件不符：$($paths[$i])"}
 CheckPe $source
 if($paths[$i].StartsWith('CrAdapter/')){
  $target=Join-Path $NinaApp $paths[$i];NoLinks $target
  if(Test-Path -LiteralPath $target){if((Hash $target) -ne $entry[0].sha256){throw "拒绝覆盖不同版本的共享适配器：$target"}}
  else{$newHost+=[pscustomobject]@{source=$source;target=$target;sha256=$entry[0].sha256}}
 }
}
NoLinks $sdkCache
if(Test-Path -LiteralPath $sdkCache){
 foreach($entry in $lock.sdkFiles){if((Hash (Join-Path $sdkCache $entry.path)) -ne $entry.sha256){throw '现有 SDK 缓存已改变；未覆盖'}}
}
if($ValidateOnly){Write-Output 'PASS: package, SDK and host adapter compatibility verified. No writes or camera access.';return}
New-Item -ItemType Directory -Path (Join-Path $DataDirectory 'sdk') -Force|Out-Null
$pending=$sdkCache+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
$created=@()
try {
 if(!(Test-Path -LiteralPath $sdkCache)){
  New-Item -ItemType Directory -Path $pending,(Join-Path $pending 'CrAdapter') -Force|Out-Null
  foreach($entry in $lock.sdkFiles){
   $source=Join-Path $runtime $entry.path;$target=Join-Path $pending $entry.path
   # Deny source replacement while copying; verify destination bytes afterwards.
   $inputFile=[IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
   try{$outputFile=[IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$inputFile.CopyTo($outputFile)}finally{$outputFile.Dispose()}}finally{$inputFile.Dispose()}
   if((Hash $target) -ne $entry.sha256){throw 'SDK changed during import'}
  }
 }
 foreach($item in $newHost){
  NoLinks $item.target;New-Item -ItemType Directory -Path (Split-Path $item.target -Parent) -Force|Out-Null
  $adapterTemp=$item.target+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
  try {
   $inputFile=[IO.File]::Open($item.source,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
   try{$outputFile=[IO.File]::Open($adapterTemp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$inputFile.CopyTo($outputFile)}finally{$outputFile.Dispose()}}finally{$inputFile.Dispose()}
   if((Hash $adapterTemp) -ne $item.sha256){throw 'Adapter changed during import'}
   [IO.File]::Move($adapterTemp,$item.target);$created+=$item
  }finally{if(Test-Path -LiteralPath $adapterTemp){Remove-Item -LiteralPath $adapterTemp}}
 }
 # Recheck every adapter before committing cache; concurrent installers may have changed host files.
 foreach($entry in $lock.sdkFiles|Where-Object {$_.path.StartsWith('CrAdapter/')}){if((Hash (Join-Path $NinaApp $entry.path)) -ne $entry.sha256){throw 'Host adapter changed during setup'}}
 if(Test-Path -LiteralPath $pending){[IO.Directory]::Move($pending,$sdkCache)}
 $locationFile=Join-Path $DataDirectory 'sdk-location.txt';NoLinks $locationFile
 [IO.File]::WriteAllText($locationFile,$runtime)
 Write-Output 'SDK configured. Restart NINA before connecting a camera.'
}catch{
 foreach($item in $created){if((Test-Path -LiteralPath $item.target) -and (Hash $item.target) -eq $item.sha256){Remove-Item -LiteralPath $item.target}}
 throw
}finally{
 # Only this invocation's generated staging path is eligible for removal.
 if((Test-Path -LiteralPath $pending) -and $pending.StartsWith($sdkCache+'.',[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $pending -Recurse -Force}
}
