function Resolve-ProjectDotnet {
  param([string]$Repository)
  foreach ($candidate in @(
    (Join-Path $Repository '.dotnet-sdk-10b\dotnet.exe'),
    (Join-Path (Split-Path -Parent $Repository) '.dotnet10\dotnet.exe')
  )) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
  }
  $candidate = (Get-Command dotnet -ErrorAction Stop).Source
  $version = & $candidate --version
  if ($LASTEXITCODE -ne 0 -or $version -notmatch '^10\.') { throw '.NET 10 SDK is required.' }
  return $candidate
}

function Invoke-CheckedNative {
  param([string]$FilePath, [string[]]$Arguments)
  & $FilePath @Arguments
  if ($LASTEXITCODE -ne 0) { throw "Command failed: $FilePath (exit code $LASTEXITCODE)" }
}
function Clear-ProjectBuildOutput {
  param([string]$Repository, [string]$Directory, [string[]]$KeepNames = @())
  $target = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
  $artifacts = [IO.Path]::GetFullPath((Join-Path $Repository 'artifacts')).TrimEnd('\') + '\'
  if (-not $target.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear a directory outside project artifacts: $target"
  }
  if (-not (Test-Path -LiteralPath $target)) { return }
  for ($parent = $target; $parent; $parent = [IO.Path]::GetDirectoryName($parent)) {
    if (((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
      throw "Refusing to clear an output reached through a filesystem link: $target"
    }
  }
  Get-ChildItem -LiteralPath $target -Force | Where-Object { $_.Name -notin $KeepNames } | ForEach-Object {
    $child = [IO.Path]::GetFullPath($_.FullName)
    if (-not $child.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe output child path.' }
    Remove-Item -LiteralPath $child -Recurse -Force
  }
}