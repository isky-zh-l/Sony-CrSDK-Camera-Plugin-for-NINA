param([Parameter(Mandatory=$true)][string]$ReceiptPath)
$ErrorActionPreference='Stop'
foreach($module in @('Microsoft.PowerShell.Management','Microsoft.PowerShell.Utility')){Import-Module (Join-Path $PSHOME ('Modules/'+$module+'/'+$module+'.psd1'))}
$receipt=Get-Content -LiteralPath $ReceiptPath -Raw -Encoding utf8|ConvertFrom-Json
if($receipt.schemaVersion -ne 1){throw '未知安装记录版本'}
$plugin=[IO.Path]::GetFullPath($receipt.pluginDirectory).TrimEnd('\')+'\'
$adapters=[IO.Path]::GetFullPath((Join-Path $receipt.hostDirectory 'CrAdapter')).TrimEnd('\')+'\'
$backups=[IO.Path]::GetFullPath($receipt.backupDirectory).TrimEnd('\')+'\'
foreach($process in @(Get-Process -Name NINA -ErrorAction SilentlyContinue)){
 if(!$process.Path -or $process.Path -eq (Join-Path $receipt.hostDirectory 'NINA.exe')){throw '请先退出目标 NINA'}
}
# Validate all targets before changing any file; only files from this receipt are handled.
foreach($entry in $receipt.files){
 $target=[IO.Path]::GetFullPath($entry.target)
 if(!$target.StartsWith($plugin,[StringComparison]::OrdinalIgnoreCase) -and !$target.StartsWith($adapters,[StringComparison]::OrdinalIgnoreCase)){throw "安装记录目标越界：$target"}
 if($entry.existed){
  $backup=[IO.Path]::GetFullPath($entry.backup)
  if(!$backup.StartsWith($backups,[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $backup).Hash -ne $entry.previousSha256){throw '备份文件身份不符，停止卸载'}
 }
}
foreach($entry in $receipt.files){
 if(!(Test-Path -LiteralPath $entry.target)){continue}
 if((Split-Path $entry.target -Leaf) -eq 'settings.json'){Write-Output "保留用户配置：$($entry.target)";continue}
 if((Get-FileHash -LiteralPath $entry.target).Hash -ne $entry.installedSha256){Write-Warning "文件已变化，保留不动：$($entry.target)";continue}
 if($entry.existed){Copy-Item -LiteralPath $entry.backup -Destination $entry.target -Force}
 elseif($entry.target.StartsWith($adapters,[StringComparison]::OrdinalIgnoreCase)){Write-Output "保留可能被其他插件共用的 CrAdapter：$($entry.target)"}
 else {Remove-Item -LiteralPath $entry.target -Force}
}
Write-Output '卸载/恢复完成。用户设置、共享适配器、图像、NINA 配置及设备驱动均未删除。'
