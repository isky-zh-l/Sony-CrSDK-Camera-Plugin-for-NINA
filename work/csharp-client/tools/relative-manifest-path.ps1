# Windows PowerShell 5.1-compatible path conversion for optional portable configs.
function Get-RelativeManifestPath([string]$ConfigurationPath,[string]$ManifestPath){
 $config=[IO.Path]::GetFullPath($ConfigurationPath)
 $manifest=[IO.Path]::GetFullPath($ManifestPath)
 $base=[IO.Path]::GetDirectoryName($config)
 if([IO.Path]::GetPathRoot($base) -ine [IO.Path]::GetPathRoot($manifest)){
  throw 'Relative manifest paths require configuration and package on the same volume. Move them together or omit -RelativeManifestPath.'
 }
 $baseUri=[Uri]($base.TrimEnd('\')+'\')
 $targetUri=[Uri]$manifest
 $relative=$baseUri.MakeRelativeUri($targetUri)
 if($relative.IsAbsoluteUri){throw 'Cannot express this manifest as a relative file path'}
 return [Uri]::UnescapeDataString($relative.ToString()).Replace('/',[IO.Path]::DirectorySeparatorChar)
}
