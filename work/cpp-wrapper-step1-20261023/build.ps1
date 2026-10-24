param(
 [string]$SdkRoot=$env:SONY_SIMPLECLI,
 [string]$BuildDirectory=(Join-Path $PSScriptRoot 'build'),
 [string]$VisualStudioRoot,
 [string]$CMakePath,
 [string]$NinjaPath
)
$ErrorActionPreference='Stop'
if([string]::IsNullOrWhiteSpace($SdkRoot)){throw 'Specify -SdkRoot <official extracted SimpleCli> or set SONY_SIMPLECLI. No developer path is assumed.'}
$sdk=(Resolve-Path -LiteralPath $SdkRoot).Path
foreach($relative in @('app\CRSDK\CameraRemote_SDK.h','external\crsdk\Cr_Core.lib')){
 if(!(Test-Path -LiteralPath (Join-Path $sdk $relative) -PathType Leaf)){throw "SDK file missing: $relative. SdkRoot must point to extracted official SimpleCli."}
}
if(!$VisualStudioRoot){
 $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
 if(Test-Path -LiteralPath $vswhere){$VisualStudioRoot=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath}
}
if($VisualStudioRoot){
 $devcmd=Join-Path $VisualStudioRoot 'Common7\Tools\VsDevCmd.bat'
 if(!(Test-Path -LiteralPath $devcmd)){throw 'VisualStudioRoot does not contain Common7\Tools\VsDevCmd.bat'}
 # Import the selected toolchain environment into this build process only.
 # Reject shell metacharacters before passing the installation path to cmd.
 if($devcmd -match '["&|<>^%!\r\n]'){throw 'Unsupported shell characters in Visual Studio path; use an existing x64 developer shell.'}
 $lines=& $env:ComSpec /d /s /c ('""'+$devcmd+'" -arch=x64 -host_arch=x64 >nul && set"')
 if($LASTEXITCODE -ne 0){throw 'Cannot initialize the Visual Studio x64 toolchain'}
 foreach($line in $lines){$pos=$line.IndexOf('=');if($pos -gt 0){[Environment]::SetEnvironmentVariable($line.Substring(0,$pos),$line.Substring($pos+1),'Process')}}
}
if(!(Get-Command cl.exe -ErrorAction SilentlyContinue)){throw 'MSVC x64 tools not found. Install C++ tools or specify -VisualStudioRoot / use an x64 developer shell.'}
if(!$CMakePath){$found=Get-Command cmake.exe -ErrorAction SilentlyContinue;if($found){$CMakePath=$found.Source}}
if(!$CMakePath -and $VisualStudioRoot){$CMakePath=Join-Path $VisualStudioRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'}
if(!$CMakePath -or !(Test-Path -LiteralPath $CMakePath)){throw 'CMake not found. Install CMake or specify -CMakePath.'}
if(!$NinjaPath){$found=Get-Command ninja.exe -ErrorAction SilentlyContinue;if($found){$NinjaPath=$found.Source}}
if(!$NinjaPath -and $VisualStudioRoot){$NinjaPath=Join-Path $VisualStudioRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'}
if(!$NinjaPath -or !(Test-Path -LiteralPath $NinjaPath)){throw 'Ninja not found. Install Ninja or specify -NinjaPath.'}
$build=[IO.Path]::GetFullPath($BuildDirectory)
& $CMakePath -S $PSScriptRoot -B $build -G Ninja '-DCMAKE_BUILD_TYPE=Release' '-DCMAKE_TRY_COMPILE_CONFIGURATION=Release' "-DCMAKE_MAKE_PROGRAM=$NinjaPath" "-DSONY_SIMPLECLI=$sdk"
if($LASTEXITCODE -ne 0){throw 'CMake configure failed'}
& $CMakePath --build $build
if($LASTEXITCODE -ne 0){throw 'C++ build failed'}
Write-Output "Build completed: $build. SDK dependencies in this local build directory are not a public distribution package."
