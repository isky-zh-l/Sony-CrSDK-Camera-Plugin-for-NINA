param([switch]$Desktop)
$ErrorActionPreference='Stop'
if(![Environment]::Is64BitOperatingSystem -or ![Environment]::Is64BitProcess){throw 'Use Windows x64 and the x64 PowerShell/dotnet host.'}
$dotnet=Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if(!(Test-Path -LiteralPath $dotnet)){throw '.NET x64 runtime is missing. Install official .NET 8 Desktop Runtime for the GUI, or .NET 8 Runtime for a console host.'}
$runtimes=@(& $dotnet --list-runtimes)
if($LASTEXITCODE -ne 0){throw 'Cannot query installed .NET runtimes.'}
if(!($runtimes -match '^Microsoft.NETCore.App 8\.')){throw '.NET 8 x64 Runtime is required. SDK 10 alone does not replace this target runtime.'}
if($Desktop -and !($runtimes -match '^Microsoft.WindowsDesktop.App 8\.')){throw '.NET 8 x64 Desktop Runtime is required by the development GUI.'}
foreach($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')){
 $path=Join-Path ([Environment]::SystemDirectory) $name
 if(!(Test-Path -LiteralPath $path)){throw "Microsoft Visual C++ x64 Runtime dependency is missing: $name. Install the official runtime; do not change camera drivers or security settings."}
}
Write-Output 'Windows x64, .NET 8 and visible Visual C++ runtime prerequisites found. Native load-time checks still apply.'
