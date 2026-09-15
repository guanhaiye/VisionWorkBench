[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$PackagePath,
  [Parameter(Mandatory=$true)][string]$InstallDirectory
)

$ErrorActionPreference = 'Stop'

function Assert-SafeRelativePath {
  param([string]$Path)
  if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path.Contains('\')) {
    throw "Unsafe update path: $Path"
  }
  if ($Path.Split('/') | Where-Object { $_ -in @('', '.', '..') }) {
    throw "Unsafe update path: $Path"
  }
}

function Get-ManifestVersion {
  param([string]$Root)
  $path = Join-Path $Root 'release-manifest.json'
  if (-not (Test-Path -LiteralPath $path)) { throw "Installed release manifest not found: $path" }
  $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
  if (-not $manifest.version) { throw 'Installed release version is missing.' }
  return [string]$manifest.version
}

function Get-SafeTarget {
  param([string]$Root, [string]$RelativePath)
  Assert-SafeRelativePath $RelativePath
  $target = [IO.Path]::GetFullPath((Join-Path $Root ($RelativePath -replace '/', '\')))
  $rootWithSlash = ([IO.Path]::GetFullPath($Root)).TrimEnd('\') + '\'
  if (-not $target.StartsWith($rootWithSlash, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Update path escapes installation directory: $RelativePath"
  }
  return $target
}

$package = [IO.Path]::GetFullPath($PackagePath)
$install = [IO.Path]::GetFullPath($InstallDirectory)
if (-not (Test-Path -LiteralPath $package)) { throw "Update package not found: $package" }
if (-not (Test-Path -LiteralPath $install)) { throw "Install directory not found: $install" }
if (Get-Process -Name 'VisionWorkbench' -ErrorAction SilentlyContinue) {
  throw 'VisionWorkbench is still running. Close it before applying the update.'
}

$extractRoot = Join-Path ([IO.Path]::GetTempPath()) ('VisionWorkbench-update-' + [guid]::NewGuid().ToString('N'))
$backupRoot = Join-Path $install ('.update-backups\' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $extractRoot, $backupRoot | Out-Null
$backups = @{}
$created = @()

try {
  Expand-Archive -LiteralPath $package -DestinationPath $extractRoot -Force
  $updateManifestPath = Join-Path $extractRoot 'update-manifest.json'
  if (-not (Test-Path -LiteralPath $updateManifestPath)) { throw 'Update manifest is missing.' }
  $manifest = Get-Content -LiteralPath $updateManifestPath -Raw | ConvertFrom-Json
  if ($manifest.product -ne 'VisionWorkbench' -or $manifest.kind -ne 'incremental-update') {
    throw 'Update package product or type is invalid.'
  }
  $currentVersion = Get-ManifestVersion $install
  if ([string]$manifest.fromVersion -ne $currentVersion) {
    throw "Update baseline mismatch: installed $currentVersion, package requires $($manifest.fromVersion)."
  }

  foreach ($item in @($manifest.files)) {
    $target = Get-SafeTarget $install ([string]$item.path)
    $payload = Get-SafeTarget $extractRoot ([string]$item.path)
    if (-not (Test-Path -LiteralPath $payload)) { throw "Update payload file is missing: $($item.path)" }
    $actualHash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$item.sha256).ToLowerInvariant()) { throw "Update payload hash mismatch: $($item.path)" }
    if (Test-Path -LiteralPath $target) {
      $backup = Join-Path $backupRoot ([string]$item.path -replace '/', '\')
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
      Copy-Item -LiteralPath $target -Destination $backup -Force
      $backups[$target] = $backup
    } else {
      $created += $target
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $payload -Destination $target -Force
  }

  foreach ($relativePath in @($manifest.deletedFiles)) {
    $target = Get-SafeTarget $install ([string]$relativePath)
    if (Test-Path -LiteralPath $target) {
      $backup = Join-Path $backupRoot ([string]$relativePath -replace '/', '\')
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
      Copy-Item -LiteralPath $target -Destination $backup -Force
      $backups[$target] = $backup
      Remove-Item -LiteralPath $target -Force
    }
  }

  foreach ($item in @($manifest.files)) {
    $target = Get-SafeTarget $install ([string]$item.path)
    $actualHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$item.sha256).ToLowerInvariant()) { throw "Installed file verification failed: $($item.path)" }
  }
  $marker = [ordered]@{
    product = 'VisionWorkbench'
    fromVersion = $currentVersion
    toVersion = [string]$manifest.toVersion
    backupDirectory = $backupRoot
    completedAtUtc = [DateTime]::UtcNow.ToString('o')
  }
  $marker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $install '.update-last-success.json') -Encoding UTF8
  Write-Host "Incremental update applied: $currentVersion -> $($manifest.toVersion)"
  Write-Host "Rollback backup: $backupRoot"
} catch {
  foreach ($target in $created) { Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue }
  foreach ($target in $backups.Keys) {
    $backup = $backups[$target]
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $backup -Destination $target -Force
  }
  throw
} finally {
  Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
}
