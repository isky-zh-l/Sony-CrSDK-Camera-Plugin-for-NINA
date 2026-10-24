param(
 [Parameter(Mandatory=$true)][string]$HostDirectory,
 [Parameter(Mandatory=$true)][string]$Configuration,
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [switch]$SkipAbi
)
# Fault injection only. Case-specific re-pinning below is NOT production approval.
# Never modifies the input package, configuration, or existing evidence.
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$utf8=New-Object System.Text.UTF8Encoding($false)
function Write-Json($Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30),$utf8)}
function Read-Json($Path){Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json}
function Hash($Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Absolute($Path,$Base){if([IO.Path]::IsPathRooted($Path)){[IO.Path]::GetFullPath($Path)}else{[IO.Path]::GetFullPath((Join-Path $Base $Path))}}
function Entry($Manifest,$Role){@($Manifest.files|Where-Object {$_.role -ceq $Role})[0]}
function Case-File($CaseRoot,$Item){$base=if($Item.base -ceq 'native'){Join-Path $CaseRoot 'native'}else{$CaseRoot};Join-Path $base ($Item.path.Replace('/',[IO.Path]::DirectorySeparatorChar))}
function Assert-Plain($Path){$part=[IO.Path]::GetFullPath($Path);while($part){if((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Input must not contain reparse points: $part"};$parent=[IO.Path]::GetDirectoryName($part);if($parent -eq $part){break};$part=$parent}}
$HostDirectory=[IO.Path]::GetFullPath($HostDirectory)
$Configuration=[IO.Path]::GetFullPath($Configuration)
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'OutputDirectory must be a new, nonexistent directory; existing evidence is never overwritten.'}
Assert-Plain $HostDirectory
foreach($name in @('SonyCamera.Acceptance.exe','SonyCamera.Acceptance.dll','SonyCamera.Acceptance.runtimeconfig.json','SonyCamera.Acceptance.deps.json','SonyCamera.Client.dll')){if(!(Test-Path -LiteralPath (Join-Path $HostDirectory $name) -PathType Leaf)){throw "Standalone host is missing $name"}}
if(@(Get-ChildItem -LiteralPath $HostDirectory -Filter 'SonyCamera.Desktop*').Count){throw 'HostDirectory must not contain Desktop binaries.'}
foreach($name in @('src','tests','.git')){if(Test-Path -LiteralPath (Join-Path $HostDirectory $name)){throw 'Use a prepared standalone runtime directory, not the source/module directory.'}}
$originalConfig=Read-Json $Configuration
$sourceManifest=Absolute $originalConfig.manifestPath ([IO.Path]::GetDirectoryName($Configuration))
Assert-Plain $sourceManifest
if((Hash $sourceManifest) -cne ([string]$originalConfig.approvedManifestSha256).ToUpperInvariant()){throw 'Original manifest does not match its externally approved pin.'}
if($originalConfig.allowTestPackage){throw 'The baseline must be a production-approved configuration.'}
$originalManifest=Read-Json $sourceManifest
if($originalManifest.backend -cne 'production'){throw 'The baseline package must be production.'}
$sourceNative=[IO.Path]::GetDirectoryName($sourceManifest)
$expected=@{
 wrapper=@('native','sony_wrapper.dll');core=@('native','Cr_Core.dll');monitor=@('native','monitor_protocol.dll');monitorPf=@('native','monitor_protocol_pf.dll');
 usbAdapter=@('host','CrAdapter/Cr_PTP_USB.dll');ipAdapter=@('host','CrAdapter/Cr_PTP_IP.dll');libusb=@('host','CrAdapter/libusb-1.0.dll');libssh=@('host','CrAdapter/libssh2.dll')
}
if(@($originalManifest.files).Count -ne 8){throw 'Baseline must contain the fixed eight roles.'}
$seen=@{}
foreach($file in $originalManifest.files){
 if(!$expected.ContainsKey($file.role) -or $seen.ContainsKey($file.role)){throw 'Baseline contains unknown or duplicate role.'}
 $seen[$file.role]=$true;$rule=$expected[$file.role]
 if($file.base -cne $rule[0] -or $file.path -cne $rule[1]){throw 'Baseline paths must match fixed role paths.'}
 $source=if($file.base -ceq 'native'){Join-Path $sourceNative $file.path}else{Join-Path $HostDirectory $file.path}
 Assert-Plain $source
 if((Hash $source) -cne ([string]$file.sha256).ToUpperInvariant()){throw "Baseline dependency hash mismatch: $($file.role)"}
}
New-Item -ItemType Directory -Path $OutputDirectory|Out-Null
$baseline=Join-Path $OutputDirectory 'baseline-package'
New-Item -ItemType Directory -Path $baseline|Out-Null
# Only runtime root files and the fixed eight dependencies enter the new host.
Get-ChildItem -LiteralPath $HostDirectory -File|Where-Object {$_.Extension -in @('.exe','.dll','.json','.pdb')}|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $baseline}
foreach($file in $originalManifest.files){
 $source=if($file.base -ceq 'native'){Join-Path $sourceNative $file.path}else{Join-Path $HostDirectory $file.path}
 $target=Case-File $baseline $file
 New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force|Out-Null
 Copy-Item -LiteralPath $source -Destination $target -Force
}
Copy-Item -LiteralPath $sourceManifest -Destination (Join-Path $baseline 'native/manifest.json')
$binaryHashes=@(Get-ChildItem -LiteralPath $baseline -File -Recurse|Where-Object {$_.Extension -in @('.dll','.exe')}|ForEach-Object {[ordered]@{path=$_.FullName.Substring($baseline.Length+1);sha256=Hash $_.FullName}})
Write-Json (Join-Path $OutputDirectory 'baseline-binary-hashes.json') $binaryHashes
Write-Json (Join-Path $OutputDirectory 'trust-boundary.json') ([ordered]@{purpose='Fault injection only; per-case derived pins are NOT a production approval process.';originalManifest=$sourceManifest;originalApprovedPin=$originalConfig.approvedManifestSha256;originalClientSha256=Hash (Join-Path $baseline 'SonyCamera.Client.dll');independentHost=$baseline;workingDirectoryPolicy='Fresh empty directory per case';pathPolicy='Only System32 and dotnet host directory';sdkActions='validate: none; abi: initialize/shutdown only, no enumerate/connect/capture'})
$dotnet=Get-Command dotnet -ErrorAction Stop
$cleanPath=(Join-Path $env:SystemRoot 'System32')+';'+[IO.Path]::GetDirectoryName($dotnet.Source)
$results=New-Object System.Collections.Generic.List[object]
$diagnostics=@{
 '04-wrong-approved-pin'='清单未获批准|批准值不一致';'05-wrong-file-hash'='文件哈希不匹配|哈希不匹配';
 '06-missing-core'='Cr_Core|role=core';'07-missing-adapter'='Cr_PTP_USB|usbAdapter';
 '08-wrong-schema'='schemaVersion';'09-wrong-abi'='abiVersion';'10-wrong-structure-size'='结构大小|ABI.*布局';
 '11-path-escape'='固定为|路径逃逸';'12-duplicate-role'='角色.*重复|重复.*角色';
 '13-missing-role'='8项固定|必需项';'14-x86-pe-with-repinned-file'='AMD64|machine=0x014C';
 '15-test-backend-in-production'='拒绝测试包|AllowTestPackage';'16-absolute-path'='固定为|路径逃逸';
 '17-duplicate-json-property'='JSON重复属性|重复属性'
}
function Run-Case([string]$Name,[string]$Mode,[bool]$ExpectSuccess,[scriptblock]$Mutation,[bool]$KeepOldPin=$false,[scriptblock]$RawMutation=$null){
 $case=Join-Path $OutputDirectory $Name
 New-Item -ItemType Directory -Path $case|Out-Null
 Get-ChildItem -LiteralPath $baseline -Force|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $case -Recurse -Force}
 $manifestPath=Join-Path $case 'native/manifest.json'
 $manifest=Read-Json $manifestPath
 if($Mutation){& $Mutation $manifest $case;Write-Json $manifestPath $manifest}
 if($RawMutation){$raw=[IO.File]::ReadAllText($manifestPath);[IO.File]::WriteAllText($manifestPath,(& $RawMutation $raw),$utf8)}
 $approved=if($KeepOldPin){'0'*64}else{Hash $manifestPath}
 $configPath=Join-Path $case 'fault-injection-config.json'
 Write-Json $configPath ([ordered]@{manifestPath='native/manifest.json';approvedManifestSha256=$approved;allowTestPackage=$false})
 $cwd=Join-Path $case 'empty-cwd';New-Item -ItemType Directory -Path $cwd|Out-Null
 $logs=Join-Path $case 'logs';New-Item -ItemType Directory -Path $logs|Out-Null
 $arguments=@('--mode',$Mode,'--config',$configPath,'--run-dir',$logs)
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=Join-Path $case 'SonyCamera.Acceptance.exe'
 $info.Arguments=($arguments|ForEach-Object {'"'+$_+'"'}) -join ' '
 $info.WorkingDirectory=$cwd;$info.UseShellExecute=$false;$info.CreateNoWindow=$true
 $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.EnvironmentVariables['PATH']=$cleanPath
 $info.EnvironmentVariables.Remove('SONY_CRSDK_RUNTIME');$info.EnvironmentVariables.Remove('SW_FAKE_CASE')
 $process=New-Object Diagnostics.Process;$process.StartInfo=$info
 $started=[DateTimeOffset]::UtcNow;$null=$process.Start()
 $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
 $timedOut=!$process.WaitForExit(30000)
 if($timedOut){$process.Kill();$process.WaitForExit()}
 [IO.File]::WriteAllText((Join-Path $case 'stdout.txt'),$stdout.GetAwaiter().GetResult(),$utf8)
 [IO.File]::WriteAllText((Join-Path $case 'stderr.txt'),$stderr.GetAwaiter().GetResult(),$utf8)
 $exitCode=$process.ExitCode;$process.Dispose()
 $diagnosticText=[IO.File]::ReadAllText((Join-Path $case 'stdout.txt'))+[IO.File]::ReadAllText((Join-Path $case 'stderr.txt'))
 foreach($log in Get-ChildItem -LiteralPath $logs -File -Recurse|Where-Object {$_.Extension -in @('.json','.txt','.log')}){$diagnosticText+=[IO.File]::ReadAllText($log.FullName)}
 $expectedDiagnostic=if($diagnostics.ContainsKey($Name)){$diagnostics[$Name]}else{''}
 $reasonMatched=(!$expectedDiagnostic -or $diagnosticText -match $expectedDiagnostic)
 $clientHash=Hash (Join-Path $case 'SonyCamera.Client.dll')
 $sameClient=$clientHash -ceq (Hash (Join-Path $baseline 'SonyCamera.Client.dll'))
 $row=[ordered]@{case=$Name;mode=$Mode;expectedSuccess=$ExpectSuccess;exitCode=$exitCode;timedOut=$timedOut;expectedDiagnostic=$expectedDiagnostic;reasonMatched=$reasonMatched;passed=(!$timedOut -and (($exitCode -eq 0) -eq $ExpectSuccess) -and $sameClient -and $reasonMatched);clientUnchanged=$sameClient;clientSha256=$clientHash;manifestSha256=Hash $manifestPath;derivedApprovedPin=$approved;faultInjectionApprovalOnly=$true;startedUtc=$started.ToString('o');caseDirectory=$case}
 $results.Add([pscustomobject]$row);Write-Json (Join-Path $case 'case-result.json') $row
 Write-Json (Join-Path $OutputDirectory 'summary.json') @($results.ToArray())
 Write-Output ("{0}: {1} (exit {2})" -f $Name,$row.passed,$exitCode)
}
Run-Case '01-independent-static' 'validate' $true $null
if(!$SkipAbi){Run-Case '02-independent-abi' 'abi' $true $null}
Run-Case '03-package-id-update-no-recompile' 'validate' $true {param($m,$c) $m.packageId='compatibility-update-'+[Guid]::NewGuid().ToString('N')}
if(!$SkipAbi){Run-Case '03b-package-id-update-runtime-abi' 'abi' $true {param($m,$c) $m.packageId='compatibility-update-'+[Guid]::NewGuid().ToString('N')}}
Run-Case '04-wrong-approved-pin' 'validate' $false $null $true
Run-Case '05-wrong-file-hash' 'validate' $false {param($m,$c) (Entry $m 'monitor').sha256='0'*64}
Run-Case '06-missing-core' 'validate' $false {param($m,$c) $f=Case-File $c (Entry $m 'core');Remove-Item -LiteralPath $f}
Run-Case '07-missing-adapter' 'validate' $false {param($m,$c) $f=Case-File $c (Entry $m 'usbAdapter');Remove-Item -LiteralPath $f}
Run-Case '08-wrong-schema' 'validate' $false {param($m,$c) $m.schemaVersion=99}
Run-Case '09-wrong-abi' 'validate' $false {param($m,$c) $m.abiVersion=99}
Run-Case '10-wrong-structure-size' 'validate' $false {param($m,$c) $m.structureSizes.SwExposureInfo=463}
Run-Case '11-path-escape' 'validate' $false {param($m,$c) (Entry $m 'monitor').path='../monitor_protocol.dll'}
Run-Case '12-duplicate-role' 'validate' $false {param($m,$c) (Entry $m 'monitor').role='core'}
Run-Case '13-missing-role' 'validate' $false {param($m,$c) $m.files=@($m.files|Where-Object {$_.role -cne 'monitor'})}
Run-Case '14-x86-pe-with-repinned-file' 'validate' $false {
 param($m,$c) $item=Entry $m 'monitor';$path=Case-File $c $item;$bytes=[IO.File]::ReadAllBytes($path)
 $offset=[BitConverter]::ToInt32($bytes,0x3c);$bytes[$offset+4]=0x4c;$bytes[$offset+5]=0x01
 [IO.File]::WriteAllBytes($path,$bytes);$item.sha256=Hash $path
}
Run-Case '15-test-backend-in-production' 'validate' $false {param($m,$c) $m.backend='test'}
Run-Case '16-absolute-path' 'validate' $false {param($m,$c) (Entry $m 'monitor').path=[IO.Path]::GetFullPath((Join-Path $c 'native/monitor_protocol.dll'))}
Run-Case '17-duplicate-json-property' 'validate' $false $null $false {param($raw) $raw -replace '"schemaVersion"\s*:\s*1\s*,','"schemaVersion":1,"schemaVersion":1,'}
if(@($results|Where-Object {!$_.passed}).Count){throw "Deployment matrix failed; inspect $OutputDirectory/summary.json"}
Write-Output "Deployment matrix passed. Evidence: $OutputDirectory/summary.json"
