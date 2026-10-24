param([Parameter(Mandatory=$true)][string]$Configuration,[string]$HostDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'tests\SonyCamera.Features\bin\Release\net8.0'),[string]$OutputDirectory=(Join-Path $env:TEMP ('sony-managed-features-'+[guid]::NewGuid().ToString('N'))))
$ErrorActionPreference='Stop'
$exe=Join-Path $HostDirectory 'SonyCamera.Features.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Build and explicitly deploy an isolated approved test package first.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$savedCase=$env:SW_FAKE_CASE
try {
 $results=foreach($case in @('feature-normal','feature-unsupported','feature-set-fail','feature-set-timeout','feature-focus-stop-fail','feature-no-lens','feature-zero-size','feature-wrong-size','feature-wrong-handle','feature-transfer-error','feature-transfer-pending','feature-transfer-timeout')){
  $env:SW_FAKE_CASE=$case
  $dir=Join-Path $OutputDirectory ($case+'-'+[guid]::NewGuid().ToString('N').Substring(0,6))
  New-Item -ItemType Directory -Path $dir|Out-Null
  $output=& $exe ([IO.Path]::GetFullPath($Configuration)) $dir 2>&1
  $code=$LASTEXITCODE
  $output|Set-Content -LiteralPath (Join-Path $dir 'test.log') -Encoding utf8
  [pscustomobject]@{case=$case;exit=$code;directory=$dir}
 }
 $results|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding utf8
 $results|Format-Table
 if($results.Where({$_.exit -ne 0}).Count){throw 'Managed feature tests failed'}
}finally{$env:SW_FAKE_CASE=$savedCase}
