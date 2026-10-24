param([switch]$Contract)
$ErrorActionPreference='Stop'
$moduleRoot=if($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$stamp=[TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([DateTime]::UtcNow,'China Standard Time').ToString('yyyyMMdd-HHmmss-fff')
$runDir=Join-Path $moduleRoot "logs\step3-run-$stamp"
New-Item -ItemType Directory -Path $runDir | Out-Null
$candidates=@(Get-Process | Where-Object {$_.ProcessName -match 'Sony|Imaging|^Remote$|RemoteCli|RemoteTransfer|phase1|NINA|getSet|ContentsTransfer|Capture|Lightroom|wrapper_test|wrapper_parameters_test|wrapper_capture_test'})
$loaded=@(); $unreadable=0
foreach($proc in Get-Process) {
    try { foreach($mod in $proc.Modules) {
        if($mod.ModuleName -eq 'Cr_Core.dll') { $loaded += "$($proc.Id) $($proc.ProcessName) $($mod.FileName)" }
    } } catch { $unreadable++ }
}
@("Beijing timestamp: $stamp",'Camera application candidates:',($candidates | Format-Table Id,ProcessName,Path | Out-String),
    'Cr_Core.dll loaded in:',$loaded,"Unreadable module lists: $unreadable",
    'Inspection covers known application names and visible SDK modules; it cannot prove absence of every device handle.') |
    Set-Content -LiteralPath (Join-Path $runDir 'preflight.txt') -Encoding utf8
if($candidates.Count -or $loaded.Count) {
    Get-Content -LiteralPath (Join-Path $runDir 'preflight.txt')
    throw 'Potential camera user detected; close that camera application, then run again. No processes were stopped.'
}
$startArgs=@{ FilePath=(Join-Path $moduleRoot 'build\wrapper_capture_test.exe'); WorkingDirectory=(Join-Path $moduleRoot 'build');
    WindowStyle='Hidden'; PassThru=$true; RedirectStandardOutput=(Join-Path $runDir 'stdout.txt');
    RedirectStandardError=(Join-Path $runDir 'stderr.txt') }
if($Contract) { $startArgs.ArgumentList='--contract' }
else {
    $receiveRoot=Join-Path $runDir 'receive'
    New-Item -ItemType Directory -Path $receiveRoot | Out-Null
    $startArgs.ArgumentList='"' + $receiveRoot + '"'
}
$child=Start-Process @startArgs
$watch=[System.Diagnostics.Stopwatch]::StartNew()
$exited=$false
while($watch.Elapsed.TotalSeconds -lt 110) {
    if($child.WaitForExit(1000)) { $exited=$true; break }
}
if(-not $exited) {
    Stop-Process -Id $child.Id
    'WATCHDOG_TIMEOUT=110s; child process terminated; camera state UNCONFIRMED. Do not immediately retry.' |
        Set-Content -LiteralPath (Join-Path $runDir 'result.txt')
    throw "Watchdog timeout; inspect camera and logs: $runDir"
}
$child.Refresh(); $rc=$child.ExitCode
"EXIT_CODE=$rc" | Set-Content -LiteralPath (Join-Path $runDir 'result.txt')
Get-Content -LiteralPath (Join-Path $runDir 'stdout.txt')
Write-Output "Logs: $runDir"
Write-Output "EXIT_CODE=$rc"
if($rc -ne 0) { throw "Test failed; see $runDir" }

