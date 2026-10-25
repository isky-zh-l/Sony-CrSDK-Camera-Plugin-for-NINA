param(
 [ValidateSet('validate','abi','inspect','real','fake')][string]$Mode='inspect',
 [string]$Configuration=(Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SonyNinaCrSdk\client.json'),
 [string]$HostDirectory=(Join-Path $PSScriptRoot 'tests\SonyCamera.Acceptance\bin\Release\net8.0'),
 [string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$exe=Join-Path $HostDirectory 'SonyCamera.Acceptance.exe'
$stamp=[DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyyMMdd-HHmmss-fff')
$run=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "SonyNinaCrSdk\regression\$Mode-$stamp"}
if(Test-Path -LiteralPath $run){throw 'Output directory already exists; preserve previous evidence.'}
New-Item -ItemType Directory -Path $run | Out-Null
if($Mode -ne 'fake'){
 if($env:SW_FAKE_CASE){throw '真实运行前请移除SW_FAKE_CASE环境变量'}
 $candidates=@(Get-Process|Where-Object {$_.ProcessName -match 'SonyCamera|Sony|Imaging|^Remote$|RemoteCli|RemoteTransfer|phase1|NINA|getSet|ContentsTransfer|Lightroom|wrapper_'})
 $loaded=@();$unreadable=0
 foreach($process in Get-Process){try{foreach($mod in $process.Modules){if($mod.ModuleName -eq 'Cr_Core.dll'){$loaded+="$($process.Id) $($process.ProcessName) $($mod.FileName)"}}}catch{$unreadable++}}
 @('Candidate camera programs:',($candidates|Format-Table Id,ProcessName,Path|Out-String),'Loaded Cr_Core:',$loaded,"Unreadable=$unreadable; visible-process checks are not a proof of all device handles.")|Set-Content -LiteralPath (Join-Path $run 'preflight.txt') -Encoding utf8
 if($candidates.Count -or $loaded.Count){throw "发现可能占用相机的程序，未停止任何程序。请查看 $run"}
 & $exe --mode $Mode --config $Configuration --run-dir $run
 if($LASTEXITCODE -ne 0){throw "验收未通过，请查看 $run"}
}else{
 # Test host/package must be explicitly prepared with an approved test config.
 # This runner neither copies nor discovers C++ source/build directories.
 $previous=$env:SW_FAKE_CASE
 try{
  foreach($scenario in @('bulb-timer','bulb-late','bulb-upfail','bulb-upfail-both','bulb-restorefail','bulb-early','bulb-exposure-timeout','bulb-download-timeout')){
   $env:SW_FAKE_CASE=$scenario
   $testMode=if($scenario -in @('bulb-timer','bulb-late')){'real'}else{'fault'}
   & $exe --mode $testMode --fake --config $Configuration --run-dir (Join-Path $run $scenario)
   if($LASTEXITCODE -ne 0){throw "替身场景失败：$scenario；日志 $run"}
  }
 }finally{$env:SW_FAKE_CASE=$previous}
}
Write-Output "验收通过：$run"
