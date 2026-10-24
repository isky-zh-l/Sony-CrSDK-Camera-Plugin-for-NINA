param([switch]$Fake,[switch]$Inspect)
$ErrorActionPreference='Stop'
$moduleRoot=$PSScriptRoot
if(!$Fake -and [Environment]::GetEnvironmentVariable('SW_FAKE_CASE')){throw 'Unset SW_FAKE_CASE before camera use'}
$stamp=[TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow,'China Standard Time').ToString('yyyyMMdd-HHmmss-fff')
$kind=if($Fake){'fake'}elseif($Inspect){'inspect'}else{'real'}
$runDir=Join-Path $moduleRoot "logs\bulb-$kind-$stamp"
New-Item -ItemType Directory -Path $runDir|Out-Null
$hash=(Get-FileHash -LiteralPath (Join-Path $moduleRoot 'build\sony_wrapper.dll')).Hash
"WRAPPER_SHA256=$hash"|Set-Content -LiteralPath (Join-Path $runDir 'wrapper-sha256.txt')
if(!$Fake){
 $candidates=@(Get-Process|Where-Object {$_.ProcessName -match 'Sony|Imaging|^Remote$|RemoteCli|RemoteTransfer|phase1|NINA|getSet|ContentsTransfer|Capture|Lightroom|wrapper_test|wrapper_parameters_test|wrapper_capture_test|wrapper_closeout_test'})
 $loaded=@();$unreadable=0
 foreach($proc in Get-Process){try{foreach($mod in $proc.Modules){if($mod.ModuleName -eq 'Cr_Core.dll'){$loaded+="$($proc.Id) $($proc.ProcessName) $($mod.FileName)"}}}catch{$unreadable++}}
 @("Beijing=$stamp",'Candidate camera programs:',($candidates|Format-Table Id,ProcessName,Path|Out-String),'Loaded Cr_Core.dll:',$loaded,"Unreadable=$unreadable",'Visible-process inspection does not prove absence of every device handle.')|Set-Content -LiteralPath (Join-Path $runDir 'preflight.txt') -Encoding utf8
 if($candidates.Count -or $loaded.Count){throw "Potential camera user; nothing stopped. Inspect $runDir"}
}else{
 foreach($file in @('sony_wrapper.dll','wrapper_closeout_test.exe')){Copy-Item -LiteralPath (Join-Path $moduleRoot "build\$file") -Destination (Join-Path $moduleRoot "build\fake\$file") -Force}
 if((Get-FileHash -LiteralPath (Join-Path $moduleRoot 'build\fake\sony_wrapper.dll')).Hash -ne $hash){throw 'Isolated copy mismatch'}
}
$scenarios=if($Fake){@('bulb-timer','bulb-late','bulb-upfail','bulb-upfail-both','bulb-restorefail','bulb-early','bulb-exposure-timeout','bulb-download-timeout','bulb-disconnect')}else{@($kind)}
$previous=[Environment]::GetEnvironmentVariable('SW_FAKE_CASE');$allPassed=$true
try{
 foreach($scenario in $scenarios){
  if($Fake){[Environment]::SetEnvironmentVariable('SW_FAKE_CASE',$scenario)}
  $receive=Join-Path $runDir "$scenario-receive";New-Item -ItemType Directory -Path $receive|Out-Null
  $workDir=Join-Path $moduleRoot $(if($Fake){'build\fake'}else{'build'})
  $mode=if($Fake){'--bulb-fake'}elseif($Inspect){'--inspect'}else{'--bulb-hardware'}
  $stdout=Join-Path $runDir "$scenario.stdout.txt";$stderr=Join-Path $runDir "$scenario.stderr.txt"
  $child=Start-Process -FilePath (Join-Path $workDir 'wrapper_closeout_test.exe') -ArgumentList @($mode,('"'+$receive+'"')) -WorkingDirectory $workDir -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
  $watch=[Diagnostics.Stopwatch]::StartNew();$done=$false;$budget=if($Fake){55}else{300}
  while($watch.Elapsed.TotalSeconds -lt $budget){if($child.WaitForExit(1000)){$done=$true;break}}
  if(!$done){Stop-Process -Id $child.Id;$allPassed=$false;"WATCHDOG_TIMEOUT=$budget; camera and restoration UNCONFIRMED"|Set-Content -LiteralPath (Join-Path $runDir "$scenario.result.txt")}
  else{$child.Refresh();$rc=$child.ExitCode;"EXIT_CODE=$rc"|Set-Content -LiteralPath (Join-Path $runDir "$scenario.result.txt");if($rc -ne 0){$allPassed=$false}}
  $lines=@(Get-Content -LiteralPath $stdout -Encoding utf8)
  if($Fake){$lines|Select-Object -Last 12}else{$lines}
  if(!$Fake -and !$Inspect){
   $files=@(foreach($line in $lines){if($line.StartsWith('SAMPLE_PATH=')){$p=$line.Substring(12);[ordered]@{path=$p;bytes=(Get-Item -LiteralPath $p).Length;sha256=(Get-FileHash -LiteralPath $p).Hash}}})
   $bulbs=@();$id='';$seconds=0
   foreach($line in $lines){if($line.StartsWith('BULB_SAMPLE_ID=')){$id=$line.Substring(15)}elseif($line.StartsWith('BULB_SAMPLE_SECONDS=')){$seconds=[double]::Parse($line.Substring(20),[Globalization.CultureInfo]::InvariantCulture)}elseif($line.StartsWith('BULB_SAMPLE_PATH=')){$bulbs+=[ordered]@{capture_id=$id;requested_seconds=$seconds;path=$line.Substring(17)}}}
   [ordered]@{wrapper_sha256=$hash;files=$files;bulb_samples=$bulbs;exit_code=$rc}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $runDir 'samples.json') -Encoding utf8
  }
 }
}finally{[Environment]::SetEnvironmentVariable('SW_FAKE_CASE',$previous)}
Write-Output "Logs: $runDir"
if(!$allPassed){throw "One or more Bulb checks failed; inspect $runDir"}
