param([string]$Destination)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$Destination){$Destination=Join-Path $root 'artifacts\sony-a7v-source.zip'}
$Destination=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $Destination){throw 'Destination exists; preserve previous exports.'}
$files=@(& git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard)
if($LASTEXITCODE -ne 0){throw 'Run git init in the repository first.'}
$files=@($files|Sort-Object -Unique)
foreach($file in $files){
 if($file -match '(?i)(^|/)(deps|artifacts|bin|obj|\.packages|\.dotnet-home)/|\.(dll|lib|exe|pdb|arw|fits|xisf|zip)$|(^|/)(CameraRemote_SDK|CrDeviceProperty|CrDefines|CrTypes)\.h$'){throw "Private/generated/SDK file in Git-visible set: $file"}
}
New-Item -ItemType Directory -Path (Split-Path $Destination -Parent) -Force|Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::Open($Destination,[IO.Compression.ZipArchiveMode]::Create)
try {foreach($file in $files){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $root $file),$file,[IO.Compression.CompressionLevel]::Optimal)|Out-Null}}
finally {$archive.Dispose()}
Write-Output "Exported $($files.Count) source files: $Destination"
Get-FileHash -LiteralPath $Destination
