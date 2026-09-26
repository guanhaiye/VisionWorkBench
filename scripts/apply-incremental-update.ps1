[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][string]$PackagePath,
  [Parameter(Mandatory=$true)][string]$InstallDirectory,
  [ValidateRange(5, 3600)][int]$EnvironmentCheckTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'

function Assert-SafeRelativePath {
  param([string]$Path)
  if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path.Contains('\')) {
    throw "Unsafe update path: $Path"
  }
  if ($Path.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_.EndsWith('.') -or $_.EndsWith(' ') -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 }) {
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
  for ($current = $target; $current; $current = [IO.Path]::GetDirectoryName($current)) {
    if (Test-Path -LiteralPath $current) {
      if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Update target contains a filesystem link: $RelativePath"
      }
    }
  }
  return $target
}

function Assert-ComponentPath {
  param([string]$Path, [string]$Component)
  $p = $Path.ToLowerInvariant()
  if ($p -match '(^|/)(data|config|logs|datasets|recordings|backups|custom-models|__pycache__|\.venv)(/|$)' -or
      $p -match '\.(db|db-wal|db-shm|sqlite|sqlite3|vwlicense|vwbackup|log)$' -or
      $p -match '(^|/)(appsettings|settings)(\.[^/]*)?\.json$' -or
      $p -like '.update-*' -or $p -eq 'update-manifest.json' -or $p -like 'unins*' -or $p -like 'environment-check.*') {
    throw "Update cannot modify customer data or updater metadata: $Path"
  }
  $model = $p -like 'plugins/*/models/*' -or $p -eq 'model-manifest.json'
  $runtime = $p -like 'runtime/python/*' -or $p -like 'runtime/mvs/*'
  if (($Component -eq 'application' -and ($model -or $runtime)) -or
      ($Component -eq 'models' -and -not $model) -or
      ($Component -eq 'runtime' -and -not $runtime)) {
    throw "Path is outside the selected update component: $Path"
  }
}

function Get-CustomerDataRoot {
  param([string]$Installation)
  $settings = Get-SafeTarget $Installation 'appsettings.json'
  $directory = ''
  if (Test-Path -LiteralPath $settings -PathType Leaf) {
    $document = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
    $directory = [string]$document.App.DataDirectory
  }
  if ([string]::IsNullOrWhiteSpace($directory)) {
    $directory = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'VisionWorkbench'
  } elseif (-not [IO.Path]::IsPathRooted($directory)) {
    $directory = Join-Path $Installation $directory
  }
  $resolved = [IO.Path]::GetFullPath($directory)
  # Inspect even an empty/new data root before any application is allowed to use it.
  [void](Get-SafeTarget $resolved 'Config/visionworkbench.db')
  return $resolved
}

function Get-CustomerStateFiles {
  param([string]$DataRoot)
  $paths = @()
  $config = Get-SafeTarget $DataRoot 'Config'
  if (Test-Path -LiteralPath $config -PathType Container) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($config)
    while ($pending.Count -gt 0) {
      $directory = $pending.Pop()
      foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Customer state contains a filesystem link: $($item.FullName)" }
        if ($item.PSIsContainer) { $pending.Push($item.FullName) } else { $paths += $item.FullName }
      }
    }
  }
  foreach ($name in @('settings.json','visionworkbench.db','visionworkbench.db-wal','visionworkbench.db-shm','datasets.json','tcp-communication.json','license.json','license-clock.dat','license-clock.v2','license-clock.json')) {
    $path = Get-SafeTarget $DataRoot $name
    if (Test-Path -LiteralPath $path -PathType Leaf) { $paths += $path }
  }
  return @($paths)
}

function Backup-CustomerState {
  param([string]$DataRoot, [string]$BackupDirectory)
  $locks = @{}
  $records = @()
  try {
    # Hold all source handles exclusively until the complete snapshot exists, so
    # an active SQLite writer causes an abort instead of an inconsistent DB/WAL copy.
    foreach ($path in @(Get-CustomerStateFiles $DataRoot)) {
      $locks[$path] = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    }
    foreach ($path in $locks.Keys) {
      $relative = $path.Substring($DataRoot.TrimEnd('\').Length).TrimStart('\').Replace('\','/')
      $backup = Get-SafeTarget $BackupDirectory $relative
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backup) | Out-Null
      $output = [IO.File]::Create($backup)
      try { $locks[$path].CopyTo($output) } finally { $output.Dispose() }
      $records += [pscustomobject]@{ path=$relative; sha256=(Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash }
    }
  } finally { foreach ($handle in $locks.Values) { $handle.Dispose() } }
  $records | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $BackupDirectory 'customer-state-manifest.json') -Encoding UTF8
  return @($records)
}

function Restore-CustomerState {
  param([string]$DataRoot, [string]$BackupDirectory, [object[]]$Records)
  if (Get-Process -Name 'VisionWorkbench' -ErrorAction SilentlyContinue) { throw 'A VisionWorkbench process is running; customer rollback is blocked to protect live data.' }
  $original = @{}
  foreach ($record in $Records) {
    $source = Get-SafeTarget $BackupDirectory ([string]$record.path)
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne [string]$record.sha256) { throw "Customer rollback snapshot is corrupt: $($record.path)" }
    $original[[string]$record.path] = $true
  }
  $locks = @{}
  try {
    foreach ($path in @(Get-CustomerStateFiles $DataRoot)) {
      $locks[$path] = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    foreach ($record in $Records) {
      $target = Get-SafeTarget $DataRoot ([string]$record.path)
      $source = Get-SafeTarget $BackupDirectory ([string]$record.path)
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
      if (-not $locks.ContainsKey($target)) {
        $locks[$target] = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
      }
      $input = [IO.File]::OpenRead($source)
      try { $locks[$target].SetLength(0); $input.CopyTo($locks[$target]); $locks[$target].Flush($true) } finally { $input.Dispose() }
    }
  } finally { foreach ($handle in $locks.Values) { $handle.Dispose() } }
  foreach ($path in @(Get-CustomerStateFiles $DataRoot)) {
    $relative = $path.Substring($DataRoot.TrimEnd('\').Length).TrimStart('\').Replace('\','/')
    if (-not $original.ContainsKey($relative)) { Remove-Item -LiteralPath (Get-SafeTarget $DataRoot $relative) -Force }
  }
  foreach ($record in $Records) {
    $target = Get-SafeTarget $DataRoot ([string]$record.path)
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne [string]$record.sha256) { throw "Customer rollback verification failed: $($record.path)" }
  }
}

function Invoke-UpdatedEnvironmentCheck {
  param([string]$Installation, [int]$TimeoutSeconds)
  $executable = Get-SafeTarget $Installation 'VisionWorkbench.exe'
  if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Updated application executable is missing.' }
  $process = Start-Process -FilePath $executable -ArgumentList '--environment-check' -WorkingDirectory $Installation -PassThru -WindowStyle Hidden
  try {
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
      $process.Kill()
      $process.WaitForExit()
      throw 'Updated application environment check timed out.'
    }
    if ($process.ExitCode -ne 0) { throw "Updated application migration/environment check failed (exit code $($process.ExitCode))." }
  } finally { $process.Dispose() }
}
$package = [IO.Path]::GetFullPath($PackagePath)
$install = [IO.Path]::GetFullPath($InstallDirectory)
if (-not (Test-Path -LiteralPath $package)) { throw "Update package not found: $package" }
if (-not (Test-Path -LiteralPath $install)) { throw "Install directory not found: $install" }
if (Get-Process -Name 'VisionWorkbench' -ErrorAction SilentlyContinue) {
  throw 'VisionWorkbench is still running. Close it before applying the update.'
}

$extractRoot = Join-Path ([IO.Path]::GetTempPath()) ('VisionWorkbench-update-' + [guid]::NewGuid().ToString('N'))
$backupRoot = Get-SafeTarget $install ('.update-backups/' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $extractRoot, $backupRoot | Out-Null
$backups = @{}
$created = @()
$customerState = @()
$customerCheckStarted = $false

try {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive = [IO.Compression.ZipFile]::OpenRead($package)
  try {
    $entryNames = @{}
    foreach ($entry in $archive.Entries) {
      $entryName = $entry.FullName.TrimEnd('/')
      Assert-SafeRelativePath $entryName
      if ($entryNames.ContainsKey($entryName)) { throw "Duplicate archive path: $entryName" }
      $entryNames[$entryName] = $true
    }
  } finally { $archive.Dispose() }
  Expand-Archive -LiteralPath $package -DestinationPath $extractRoot -Force
  $updateManifestPath = Join-Path $extractRoot 'update-manifest.json'
  if (-not (Test-Path -LiteralPath $updateManifestPath)) { throw 'Update manifest is missing.' }
  $manifest = Get-Content -LiteralPath $updateManifestPath -Raw | ConvertFrom-Json
  if ($manifest.product -ne 'VisionWorkbench' -or $manifest.kind -ne 'incremental-update') {
    throw 'Update package product or type is invalid.'
  }
  $component = [string]$manifest.component
  if ($component -notin @('application', 'models', 'runtime', 'all') -or [string]::IsNullOrWhiteSpace([string]$manifest.toVersion)) {
    throw 'Update component or target version is missing.'
  }
  $currentVersion = Get-ManifestVersion $install
  if ([string]$manifest.fromVersion -ne $currentVersion -and -not ($component -in @('models', 'runtime') -and [string]$manifest.toVersion -eq $currentVersion)) {
    throw "Update baseline mismatch: installed $currentVersion, package requires $($manifest.fromVersion)."
  }

  # Validate the complete operation before backing up or overwriting any file.
  $paths = @{}
  foreach ($item in @($manifest.files)) {
    $relative = [string]$item.path
    Assert-ComponentPath $relative $component
    $target = Get-SafeTarget $install $relative
    $payload = Get-SafeTarget $extractRoot $relative
    if ($paths.ContainsKey($relative)) { throw "Duplicate update operation: $relative" }
    $paths[$relative] = $true
    if (-not (Test-Path -LiteralPath $payload -PathType Leaf)) { throw "Update payload file is missing: $relative" }
    if ((Test-Path -LiteralPath $target) -and -not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Update target is not a file: $relative" }
    if ([string]$item.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or
        (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash -ne [string]$item.sha256) {
      throw "Update payload hash mismatch: $relative"
    }
  }
  foreach ($relative in @($manifest.deletedFiles)) {
    Assert-ComponentPath ([string]$relative) $component
    $target = Get-SafeTarget $install ([string]$relative)
    if ($paths.ContainsKey([string]$relative)) { throw "Duplicate update operation: $relative" }
    $paths[[string]$relative] = $true
    if ((Test-Path -LiteralPath $target) -and -not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Delete target is not a file: $relative" }
  }
  $dataRoot = Get-CustomerDataRoot $install
  $stateBackup = Join-Path $backupRoot 'customer-state'
  New-Item -ItemType Directory -Force -Path $stateBackup | Out-Null
  $customerState = @(Backup-CustomerState $dataRoot $stateBackup)
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
  $customerCheckStarted = $true
  Invoke-UpdatedEnvironmentCheck $install $EnvironmentCheckTimeoutSeconds
  $marker = [ordered]@{
    product = 'VisionWorkbench'
    fromVersion = $currentVersion
    toVersion = [string]$manifest.toVersion
    backupDirectory = $backupRoot
    customerStateBackup = $stateBackup
    environmentCheck = 'passed'
    completedAtUtc = [DateTime]::UtcNow.ToString('o')
  }
  $marker | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $install '.update-last-success.json') -Encoding UTF8
  Write-Host "Incremental update applied: $currentVersion -> $($manifest.toVersion)"
  Write-Host "Rollback backup: $backupRoot"
} catch {
  $updateError = $_
  $rollbackErrors = @()
  if ($customerCheckStarted) {
    try { Restore-CustomerState $dataRoot $stateBackup $customerState } catch { $rollbackErrors += $_.Exception.Message }
  }
  foreach ($target in $created) {
    try { Remove-Item -LiteralPath $target -Force -ErrorAction Stop } catch { $rollbackErrors += $_.Exception.Message }
  }
  foreach ($target in $backups.Keys) {
    try {
      $backup = $backups[$target]
      New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
      Copy-Item -LiteralPath $backup -Destination $target -Force
    } catch { $rollbackErrors += $_.Exception.Message }
  }
  if ($rollbackErrors.Count -gt 0) {
    throw "Update failed: $($updateError.Exception.Message). Rollback needs recovery from ${backupRoot}: $($rollbackErrors -join '; ')"
  }
  throw $updateError} finally {
  $resolvedExtractRoot = [IO.Path]::GetFullPath($extractRoot)
  $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
  if ($resolvedExtractRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
      [IO.Path]::GetFileName($resolvedExtractRoot).StartsWith('VisionWorkbench-update-')) {
    Remove-Item -LiteralPath $resolvedExtractRoot -Recurse -Force -ErrorAction SilentlyContinue
  }
}
