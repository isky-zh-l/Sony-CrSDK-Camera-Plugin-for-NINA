param(
 [Parameter(Mandatory=$true)][string]$RuntimePackage,
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [Parameter(Mandatory=$true)][string]$SourceArchive
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$RuntimePackage=(Resolve-Path -LiteralPath $RuntimePackage).Path
$SourceArchive=(Resolve-Path -LiteralPath $SourceArchive).Path
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Output already exists; preserve previous packages.'}
$built=Join-Path $root 'work\nina-plugin\src\SonyA7V.Camera\bin\Release\net8.0-windows'
$version=(Get-Item -LiteralPath (Join-Path $built 'SonyA7V.Camera.dll')).VersionInfo.FileVersion
$runtimePlugin=Join-Path $RuntimePackage 'plugin'
$manifestPath=Join-Path $runtimePlugin 'native\approved-native.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding utf8|ConvertFrom-Json
$deployment=Get-Content -LiteralPath (Join-Path $runtimePlugin 'sony-a7v.json') -Raw -Encoding utf8|ConvertFrom-Json
if((Get-FileHash -LiteralPath $manifestPath).Hash -ne $deployment.approvedManifestSha256 -or $manifest.backend -ne 'production'){throw 'Runtime source must be an explicitly approved production package'}
foreach($entry in $manifest.files){
 $base=if($entry.base -eq 'host'){$RuntimePackage}else{Join-Path $runtimePlugin 'native'}
 if((Get-FileHash -LiteralPath (Join-Path $base $entry.path)).Hash -ne $entry.sha256){throw "Runtime source mismatch: $($entry.path)"}
}
$decoder=Get-Content -LiteralPath (Join-Path $runtimePlugin 'native\identity.json') -Raw -Encoding utf8|ConvertFrom-Json
foreach($name in @('libraw.dll','SonyArw6.dll')){if((Get-FileHash -LiteralPath (Join-Path $runtimePlugin ('native\'+$name))).Hash -ne $decoder.$name){throw "Decoder identity mismatch: $name"}}
# Require corresponding source without SDK headers, imported libraries or binary artifacts.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceZip=[IO.Compression.ZipFile]::OpenRead($SourceArchive)
try {
 if(!($sourceZip.Entries.FullName -contains 'third_party/LibRaw/LICENSE.LGPL')){throw 'Corresponding LibRaw source/license missing'}
 foreach($entry in $sourceZip.Entries){if($entry.FullName -match '(?i)\.(dll|lib|exe|pdb|arw|fits|xisf)$|(^|/)(CameraRemote_SDK|CrDeviceProperty|CrDefines|CrTypes)\.h$'){throw "Forbidden source entry: $($entry.FullName)"}}
} finally {$sourceZip.Dispose()}
New-Item -ItemType Directory -Path (Join-Path $output 'native'),(Join-Path $output 'config'),(Join-Path $output 'licenses') -Force|Out-Null
Copy-Item -LiteralPath (Join-Path $built 'SonyA7V.Camera.dll'),(Join-Path $built 'SonyCamera.Client.dll') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'config\settings.json') -Destination $output
foreach($name in @('sony_wrapper.dll','libraw.dll','SonyArw6.dll','identity.json')){Copy-Item -LiteralPath (Join-Path $runtimePlugin ('native\'+$name)) -Destination (Join-Path $output 'native')}
$manifest.packageId='sony-crsdk-camera-'+$version+'-local-setup'
$manifest|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $output 'config\native-manifest.template.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $root 'config\sdk-runtime-lock.json') -Destination (Join-Path $output 'config')
Copy-Item -LiteralPath (Join-Path $root 'installer\Install.ps1'),(Join-Path $root 'installer\Uninstall.ps1') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'docs\INSTALL.md') -Destination (Join-Path $output 'INSTALL.md')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'assets\icon.png') -Destination $output
foreach($name in @('COPYRIGHT','LICENSE.LGPL','LICENSE.CDDL')){Copy-Item -LiteralPath (Join-Path $root ('third_party\LibRaw\'+$name)) -Destination (Join-Path $output 'licenses')}
Copy-Item -LiteralPath $SourceArchive -Destination (Join-Path $output 'corresponding-source.zip')
@'
# Corresponding source and rebuild information

corresponding-source.zip includes the complete project-owned source and pinned LibRaw source,
copyright notices and licenses. LibRaw source is unchanged; this distribution chooses the
LGPL licensing option for the dynamically linked private libraw.dll. The independent MIT
bridge is work/nina-plugin/native/SonyArw6.cpp. You may rebuild/replace the private decoder;
update native/identity.json with your own reviewed DLL hashes after replacement.

To rebuild only LibRaw and the bridge: extract corresponding-source.zip, open an x64 MSVC
developer PowerShell, then run tools/Build-Decoder.ps1 from its root. .NET and the Sony SDK
are not needed for that decoder-only command. Build flags and source lock are in that script.
This package contains no Sony SDK file. Runtime dependencies are obtained locally during setup.
'@|Set-Content -LiteralPath (Join-Path $output 'SOURCE.md') -Encoding utf8
$files=@(Get-ChildItem -LiteralPath $output -File -Recurse|Sort-Object FullName|ForEach-Object {
 [ordered]@{path=$_.FullName.Substring($output.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
[ordered]@{schemaVersion=1;version=$version;files=$files}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'package-integrity.json') -Encoding utf8
# Verify the only native/managed binaries present belong to the explicit project/LibRaw list.
$allowed=@('SonyA7V.Camera.dll','SonyCamera.Client.dll','native/sony_wrapper.dll','native/libraw.dll','native/SonyArw6.dll')
foreach($entry in $files){if($entry.path -match '(?i)\.(dll|lib|exe)$' -and $entry.path -notin $allowed){throw "Unapproved binary in public package: $($entry.path)"}}
if(Test-Path -LiteralPath ($output+'.zip')){throw 'ZIP destination already exists'}
Compress-Archive -Path (Join-Path $output '*') -DestinationPath ($output+'.zip') -CompressionLevel Optimal
Write-Output "Public candidate prepared: $output.zip. Contains no Sony SDK; hardware validation still required for this build."
Get-FileHash -LiteralPath ($output+'.zip')
