$ErrorActionPreference='Stop'
if(!(Get-Command cl.exe -ErrorAction SilentlyContinue)){throw 'Use an x64 MSVC developer PowerShell or run root build.ps1 -Native.'}
$root=Split-Path $PSScriptRoot -Parent
$source=Join-Path $root 'third_party\LibRaw'
$lock=Get-Content -LiteralPath (Join-Path $root 'src\nina\dependencies\libraw-source-lock.json') -Raw -Encoding utf8|ConvertFrom-Json
foreach($entry in $lock.files){
 if((Get-FileHash -LiteralPath (Join-Path $source $entry.path)).Hash -ne $entry.sha256){throw "LibRaw source identity mismatch: $($entry.path)"}
}
$build=Join-Path $root 'artifacts\decoder\LibRaw'
$output=Join-Path $root 'artifacts\decoder'
if(!(Test-Path -LiteralPath $build)){New-Item -ItemType Directory -Path $build -Force|Out-Null; Copy-Item -Path (Join-Path $source '*') -Destination $build -Recurse}
# Upstream Makefile expects output directories to exist.
New-Item -ItemType Directory -Path (Join-Path $build 'object'),(Join-Path $build 'lib'),(Join-Path $build 'bin') -Force|Out-Null
Push-Location $build
try {
 & nmake.exe /f Makefile.msvc 'bin\libraw.dll'
 if($LASTEXITCODE -ne 0){throw 'LibRaw DLL build failed.'}
} finally {Pop-Location}
Copy-Item -LiteralPath (Join-Path $build 'bin\libraw.dll') -Destination $output -Force
$shim=Join-Path $root 'src\nina\native\SonyArw6.cpp'
Push-Location $output
try {
 & cl.exe /nologo /LD /MD /EHsc /std:c++17 /O2 "/I$build" $shim (Join-Path $build 'lib\libraw.lib') /link /OUT:SonyArw6.dll
 if($LASTEXITCODE -ne 0){throw 'SonyArw6 bridge build failed.'}
} finally {Pop-Location}
$identity=[ordered]@{}
foreach($name in @('SonyArw6.dll','libraw.dll')){$identity[$name]=(Get-FileHash -LiteralPath (Join-Path $output $name)).Hash}
$identity|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $output 'identity.json') -Encoding utf8
Write-Output 'Private decoder built from locked source. New binaries require hardware regression.'
