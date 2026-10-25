param(
 [Parameter(Mandatory=$true)][string]$ManifestPath,
 [Parameter(Mandatory=$true)][string]$ExpectedManifestSha256,
 [Parameter(Mandatory=$true)][string]$TrustSource,
 [string]$Configuration=(Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SonyNinaCrSdk\client.json'),
 [switch]$RelativeManifestPath
)
$ErrorActionPreference='Stop'
if($ExpectedManifestSha256 -notmatch '^[0-9A-Fa-f]{64}$'){throw 'ExpectedManifestSha256 must be supplied from an independently reviewed maintainer channel or local preparation receipt.'}
if([string]::IsNullOrWhiteSpace($TrustSource)){throw 'Record who/what approved the package. A hash is not origin authentication.'}
$manifest=[IO.Path]::GetFullPath($ManifestPath)
$hash=(Get-FileHash -LiteralPath $manifest).Hash
if($hash -ne $ExpectedManifestSha256){throw 'Manifest does not match the separately supplied approved digest.'}
$package=Get-Content -LiteralPath $manifest -Raw -Encoding utf8|ConvertFrom-Json
if($package.backend -ne 'production'){throw 'This approval command does not approve isolated test packages.'}
$config=[IO.Path]::GetFullPath($Configuration)
New-Item -ItemType Directory -Path (Split-Path $config -Parent) -Force|Out-Null
if(Test-Path -LiteralPath $config){Copy-Item -LiteralPath $config -Destination ($config+'.previous-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmssfff'))}
$configuredManifest=$manifest
if($RelativeManifestPath){. (Join-Path $PSScriptRoot 'relative-manifest-path.ps1');$configuredManifest=Get-RelativeManifestPath $config $manifest}
[ordered]@{manifestPath=$configuredManifest;approvedManifestSha256=$hash;allowTestPackage=$false}|ConvertTo-Json|Set-Content -LiteralPath $config -Encoding utf8
[ordered]@{trustSource=$TrustSource;manifestPath=$manifest;expectedManifestSha256=$hash;approvedUtc=[DateTimeOffset]::UtcNow.ToString('O');limitation='This local approval pins bytes. It is not a Sony signature and grants no redistribution license.'}|ConvertTo-Json|Set-Content -LiteralPath ($config+'.approval-receipt.json') -Encoding utf8
Write-Output "Approved configuration written: $config. Validate the complete native package before use."
