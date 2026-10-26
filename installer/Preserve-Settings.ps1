param(
 [Parameter(Mandatory=$true)][string]$LegacyPluginDirectory,
 [string]$DataDirectory=(Join-Path $env:LOCALAPPDATA 'NINA/PluginData/9c1f3b7e-5a24-4f0c-9a86-2d1e7c4b6a51')
)
# Run before the first plugin-manager update from 0.5.4 or an earlier manual install.
$ErrorActionPreference='Stop'
foreach($module in @('Microsoft.PowerShell.Management','Microsoft.PowerShell.Utility')){Import-Module (Join-Path $PSHOME ('Modules/'+$module+'/'+$module+'.psd1'))}
function NoLinks([string]$Path){
 $current=[IO.Path]::GetFullPath($Path)
 while($current){if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "不接受链接路径：$current"};$current=Split-Path $current -Parent}
}
$LegacyPluginDirectory=(Resolve-Path -LiteralPath $LegacyPluginDirectory).Path
$DataDirectory=[IO.Path]::GetFullPath($DataDirectory)
NoLinks $LegacyPluginDirectory;NoLinks $DataDirectory
if($DataDirectory -eq $LegacyPluginDirectory -or $DataDirectory.StartsWith($LegacyPluginDirectory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'User data must be outside the plugin directory'}
$settings=Join-Path $LegacyPluginDirectory 'settings.json'
NoLinks $settings
$null=Get-Content -LiteralPath $settings -Raw -Encoding utf8|ConvertFrom-Json
$receiveRoot=''
$deployment=Join-Path $LegacyPluginDirectory 'sony-a7v.json'
if(Test-Path -LiteralPath $deployment){
 NoLinks $deployment;$old=Get-Content -LiteralPath $deployment -Raw -Encoding utf8|ConvertFrom-Json
 if($old.PSObject.Properties.Name -contains 'receiveRoot'){
  if($null -ne $old.receiveRoot -and $old.receiveRoot -isnot [string]){throw 'Invalid receiveRoot'}
  $receiveRoot=[string]$old.receiveRoot
  if($receiveRoot -and ($receiveRoot -notmatch '^[A-Za-z]:[\\/]' -or $receiveRoot.IndexOf([char]0) -ge 0)){throw 'receiveRoot must be an absolute local path'}
 }
}
foreach($name in @('settings.json','receive-root.txt')){NoLinks (Join-Path $DataDirectory $name)}
New-Item -ItemType Directory -Path $DataDirectory -Force|Out-Null
$target=Join-Path $DataDirectory 'settings.json'
if(!(Test-Path -LiteralPath $target)){Copy-Item -LiteralPath $settings -Destination $target}
$target=Join-Path $DataDirectory 'receive-root.txt'
if($receiveRoot -and !(Test-Path -LiteralPath $target)){[IO.File]::WriteAllText($target,$receiveRoot)}
Write-Output 'User settings preserved outside the plugin directory. Existing user data was not overwritten.'
