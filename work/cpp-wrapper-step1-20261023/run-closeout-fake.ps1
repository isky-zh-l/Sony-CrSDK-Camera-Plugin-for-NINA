$ErrorActionPreference='Stop'
$moduleRoot=$PSScriptRoot
$stamp=[TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow,'China Standard Time').ToString('yyyyMMdd-HHmmss-fff')
$runDir=Join-Path $moduleRoot "logs\closeout-fake-$stamp"
New-Item -ItemType Directory -Path $runDir | Out-Null
# Refresh only the project DLL and client; the isolated Cr_Core.dll remains the fake backend.
# An unchanged import library can let Ninja skip the client's POST_BUILD copy.
foreach($name in @('sony_wrapper.dll','wrapper_closeout_test.exe')) {
 Copy-Item -LiteralPath (Join-Path $moduleRoot "build\$name") -Destination (Join-Path $moduleRoot "build\fake\$name") -Force
}
$realHash=(Get-FileHash -LiteralPath (Join-Path $moduleRoot 'build\sony_wrapper.dll')).Hash
$fakeCopyHash=(Get-FileHash -LiteralPath (Join-Path $moduleRoot 'build\fake\sony_wrapper.dll')).Hash
if($realHash -ne $fakeCopyHash){throw 'Production wrapper and isolated test copy differ'}
"WRAPPER_SHA256=$realHash" | Set-Content -LiteralPath (Join-Path $runDir 'wrapper-sha256.txt')
$previous=[Environment]::GetEnvironmentVariable('SW_FAKE_CASE')
$allPassed=$true
try {
 & (Join-Path $moduleRoot 'build\mailbox_test.exe') *> (Join-Path $runDir 'mailbox.txt')
 if($LASTEXITCODE){throw 'Mailbox unit test failed'}
 foreach($scenario in @('late','queue','preview','unknown','overflow','upfail','upfail_both','chunks','recovery')) {
  [Environment]::SetEnvironmentVariable('SW_FAKE_CASE',$scenario)
  $receive=Join-Path $runDir $scenario; New-Item -ItemType Directory -Path $receive | Out-Null
  $child=Start-Process -FilePath (Join-Path $moduleRoot 'build\fake\wrapper_closeout_test.exe') -ArgumentList @('--fake',('"'+$receive+'"')) -WorkingDirectory (Join-Path $moduleRoot 'build\fake') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runDir "$scenario.stdout.txt") -RedirectStandardError (Join-Path $runDir "$scenario.stderr.txt")
  $clock=[Diagnostics.Stopwatch]::StartNew();$done=$false
  while($clock.Elapsed.TotalSeconds -lt 40){if($child.WaitForExit(1000)){$done=$true;break}}
  if(!$done){Stop-Process -Id $child.Id;$allPassed=$false;"WATCHDOG_TIMEOUT" | Set-Content -LiteralPath (Join-Path $runDir "$scenario.result.txt")}
  else{$child.Refresh();$rc=$child.ExitCode;"EXIT_CODE=$rc" | Set-Content -LiteralPath (Join-Path $runDir "$scenario.result.txt");if($rc -ne 0){$allPassed=$false}}
  Get-Content -LiteralPath (Join-Path $runDir "$scenario.stdout.txt") -Encoding utf8 | Select-Object -Last 8
 }
}finally{[Environment]::SetEnvironmentVariable('SW_FAKE_CASE',$previous)}
Write-Output "Logs: $runDir"
if(!$allPassed){throw 'One or more targeted fake tests failed'}

