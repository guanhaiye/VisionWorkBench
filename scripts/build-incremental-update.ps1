[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$BaseReleaseDirectory,
  [string]$CurrentReleaseDirectory = '',
  [string]$OutputDirectory = '',
  [string]$Version = '',
  [ValidateSet('application','models','runtime','all')][string]$Component = 'application'
)

$ErrorActionPreference = 'Stop'

function Get-ReleaseVersion {
  param([string]$Root, [string]$Fallback)
  if (-not [string]::IsNullOrWhiteSpace($Fallback)) { return $Fallback.Trim() }
  $manifestPath = Join-Path $Root 'release-manifest.json'
  if (-not (Test-Path -LiteralPath $manifestPath)) { return 'unknown' }
  try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.version) { return [string]$manifest.version }
    if ($manifest.Version) { return [string]$manifest.Version }
  } catch { }
  return 'unknown'
}

function Get-FileIndex {
  param([string]$Root)
  $index = @{}
  if (-not (Test-Path -LiteralPath $Root)) { throw "Release directory not found: $Root" }
  Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($Root.Length).TrimStart('\').Replace('\','/')
    if (-not (Test-InScopePath $relative)) { return }
    $index[$relative] = [ordered]@{
      path = $relative
      size = $_.Length
      sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
  }
  return $index
}

function Test-InScopePath {
  param([string]$RelativePath)
  $p = $RelativePath.ToLowerInvariant()
  if ($p -match '(^|/)(data|config|logs|datasets|recordings|backups|custom-models|__pycache__|\.venv)(/|$)' -or
      $p -match '\.(db|db-wal|db-shm|sqlite|sqlite3|vwlicense|vwbackup|log)$' -or
      $p -match '(^|/)(appsettings|settings)(\.[^/]*)?\.json$') { return $false }
  if ($p -eq 'update-manifest.json' -or $p -eq '.update-last-success.json' -or $p -like '.update-backups/*') { return $false }
  switch ($Component) {
    'models' { return $p -like 'plugins/*/models/*' -or $p -eq 'model-manifest.json' }
    'runtime' { return $p -like 'runtime/python/*' -or $p -like 'runtime/mvs/*' }
    'all' { return $p -notlike 'data/*' -and $p -notlike 'config/*' -and $p -notlike 'logs/*' -and $p -notlike 'unins*' -and $p -notlike 'environment-check.*' }
    default {
      return $p -notlike 'runtime/python/*' -and
        $p -notlike 'runtime/mvs/*' -and
        $p -notlike 'plugins/*/models/*' -and
        $p -ne 'model-manifest.json' -and
        $p -notlike 'data/*' -and
        $p -notlike 'config/*' -and
        $p -notlike 'logs/*' -and
        $p -notlike 'unins*' -and
        $p -notlike 'environment-check.*'
    }
  }
}

function Get-ArchiveRelativePath {
  param([string]$RelativePath)
  return $RelativePath.Replace('/','\')
}

$baseRoot = [IO.Path]::GetFullPath($BaseReleaseDirectory)
if ([string]::IsNullOrWhiteSpace($CurrentReleaseDirectory)) {
  $CurrentReleaseDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\installer-staging'
}
$currentRoot = [IO.Path]::GetFullPath($CurrentReleaseDirectory)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $outputName = ([char]0x0041)+([char]0x0049)+([char]0x6267)+([char]0x884c)+([char]0x8f6f)+([char]0x4ef6)+([char]0x5b89)+([char]0x88c5)+([char]0x5305)
  $OutputDirectory = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) $outputName
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)

$fromVersion = Get-ReleaseVersion $baseRoot ''
$toVersion = Get-ReleaseVersion $currentRoot $Version
if ($fromVersion -eq 'unknown') { throw 'Baseline release version is missing.' }
if ($toVersion -eq 'unknown') { throw 'Target release version is missing. Use -Version or provide release-manifest.json.' }
$safeFrom = $fromVersion -replace '[^0-9A-Za-z._-]', '_'
$safeTo = $toVersion -replace '[^0-9A-Za-z._-]', '_'
$packageName = "VisionWorkbench-Update-$Component-$safeFrom-to-$safeTo"
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ($packageName + '-' + [guid]::NewGuid().ToString('N'))
$payloadRoot = Join-Path $workRoot 'payload'
New-Item -ItemType Directory -Force -Path $payloadRoot, $outputRoot | Out-Null

try {
  $base = Get-FileIndex $baseRoot
  $current = Get-FileIndex $currentRoot
  $changed = @()
  $deleted = @()
  foreach ($path in $current.Keys) {
    if (-not (Test-InScopePath $path)) { continue }
    if (-not $base.ContainsKey($path) -or $base[$path].sha256 -ne $current[$path].sha256) {
      $changed += $current[$path]
      $source = Join-Path $currentRoot (Get-ArchiveRelativePath $path)
      $target = Join-Path $payloadRoot (Get-ArchiveRelativePath $path)
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
      Copy-Item -LiteralPath $source -Destination $target -Force
    }
  }
  foreach ($path in $base.Keys) {
    if (-not (Test-InScopePath $path)) { continue }
    if (-not $current.ContainsKey($path)) { $deleted += $path }
  }

  $manifest = [ordered]@{
    product = 'VisionWorkbench'
    kind = 'incremental-update'
    fromVersion = $fromVersion
    toVersion = $toVersion
    generatedAtUtc = [DateTime]::UtcNow.ToString('o')
    component = $Component
    excludedComponents = if ($Component -eq 'application') { @('runtime/python', 'runtime/mvs', 'plugins/*/models', 'customer-data') } else { @('customer-data') }
    files = @($changed | Sort-Object path)
    deletedFiles = @($deleted | Sort-Object)
    codeSigned = $false
    unsignedReason = 'No code-signing certificate was provided.'
  }
  $manifestPath = Join-Path $payloadRoot 'update-manifest.json'
  $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
  $archivePath = Join-Path $outputRoot ($packageName + '.zip')
  Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
  Compress-Archive -Path (Join-Path $payloadRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal
  $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
  Set-Content -LiteralPath ($archivePath + '.sha256') -Value "$hash  $(Split-Path -Leaf $archivePath)" -Encoding ASCII
  $summary = [ordered]@{
    package = $archivePath
    fromVersion = $fromVersion
    toVersion = $toVersion
    changedFiles = @($changed).Count
    deletedFiles = @($deleted).Count
    sizeBytes = (Get-Item -LiteralPath $archivePath).Length
    sha256 = $hash
    manifest = 'update-manifest.json (inside package)'
  }
  $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot ($packageName + '.json')) -Encoding UTF8
  Write-Host "Incremental update: $archivePath"
  Write-Host "Changed files: $(@($changed).Count); deleted files: $(@($deleted).Count)"
  Write-Host "SHA-256: $hash"
} finally {
  $resolvedWorkRoot = [IO.Path]::GetFullPath($workRoot)
  $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
  if ($resolvedWorkRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
      [IO.Path]::GetFileName($resolvedWorkRoot).StartsWith($packageName + '-')) {
    Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force -ErrorAction SilentlyContinue
  }
}
