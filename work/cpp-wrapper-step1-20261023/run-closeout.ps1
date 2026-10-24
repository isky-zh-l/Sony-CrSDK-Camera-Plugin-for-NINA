$ErrorActionPreference='Stop'
$moduleRoot=$PSScriptRoot
if([Environment]::GetEnvironmentVariable('SW_FAKE_CASE')){throw 'Unset SW_FAKE_CASE before real-camera test'}
$stamp=[TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow,'China Standard Time').ToString('yyyyMMdd-HHmmss-fff')
$runDir=Join-Path $moduleRoot "logs\closeout-real-$stamp"
New-Item -ItemType Directory -Path $runDir | Out-Null
$candidates=@(Get-Process | Where-Object {$_.ProcessName -match 'Sony|Imaging|^Remote$|RemoteCli|RemoteTransfer|phase1|NINA|getSet|ContentsTransfer|Capture|Lightroom|wrapper_test|wrapper_parameters_test|wrapper_capture_test|wrapper_closeout_test'})
$loaded=@();$unreadable=0
foreach($proc in Get-Process){try{foreach($mod in $proc.Modules){if($mod.ModuleName -eq 'Cr_Core.dll'){$loaded+="$($proc.Id) $($proc.ProcessName) $($mod.FileName)"}}}catch{$unreadable++}}
@("Beijing=$stamp",'Candidate processes:',($candidates|Format-Table Id,ProcessName,Path|Out-String),'Loaded Cr_Core.dll:',$loaded,"Unreadable=$unreadable",'Visible-process check does not enumerate all device handles.') | Set-Content -LiteralPath (Join-Path $runDir 'preflight.txt') -Encoding utf8
if($candidates.Count -or $loaded.Count){throw "Potential camera user; no process stopped. See $runDir"}
$receive=Join-Path $runDir 'receive';New-Item -ItemType Directory -Path $receive|Out-Null
$hash=(Get-FileHash -LiteralPath (Join-Path $moduleRoot 'build\sony_wrapper.dll')).Hash
"WRAPPER_SHA256=$hash"|Set-Content -LiteralPath (Join-Path $runDir 'wrapper-sha256.txt')
$child=Start-Process -FilePath (Join-Path $moduleRoot 'build\wrapper_closeout_test.exe') -ArgumentList @('--hardware',('"'+$receive+'"')) -WorkingDirectory (Join-Path $moduleRoot 'build') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runDir 'stdout.txt') -RedirectStandardError (Join-Path $runDir 'stderr.txt')
$clock=[Diagnostics.Stopwatch]::StartNew();$done=$false
while($clock.Elapsed.TotalSeconds -lt 180){if($child.WaitForExit(1000)){$done=$true;break}}
if(!$done){Stop-Process -Id $child.Id;'WATCHDOG_TIMEOUT=180s; camera/restore state UNCONFIRMED'|Set-Content -LiteralPath (Join-Path $runDir 'result.txt');throw "Inspect camera and logs: $runDir"}
$child.Refresh();$rc=$child.ExitCode;"EXIT_CODE=$rc"|Set-Content -LiteralPath (Join-Path $runDir 'result.txt')
Get-Content -LiteralPath (Join-Path $runDir 'stdout.txt') -Encoding utf8
$paths=@(Get-Content -LiteralPath (Join-Path $runDir 'stdout.txt') -Encoding utf8 | Where-Object {$_.StartsWith('SAMPLE_PATH=')} | ForEach-Object {$_.Substring(12)})
$files=@(foreach($path in $paths){[ordered]@{path=$path;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path).Hash}})
[ordered]@{wrapper_sha256=$hash;files=$files;exit_code=$rc}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $runDir 'samples.json') -Encoding utf8
Write-Output "Logs: $runDir"
if($rc -ne 0){throw "Hardware acceptance failed; see $runDir"}
