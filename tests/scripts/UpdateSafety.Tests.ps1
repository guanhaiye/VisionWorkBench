$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$root = Join-Path (Join-Path $repo 'artifacts\update-safety-tests') ('update-fixture-' + [guid]::NewGuid().ToString('N'))
$base = Join-Path $root 'base'; $next = Join-Path $root 'next'; $output = Join-Path $root 'packages'; $installed = Join-Path $root 'installed'
New-Item -ItemType Directory -Force -Path $base,$next,$output,$installed | Out-Null
function Assert-Equal($expected, $actual) { if ($expected -ne $actual) { throw "Expected $expected, actual $actual" } }
foreach ($dir in @($base,$installed)) {
  '{"product":"VisionWorkbench","version":"1.0"}' | Set-Content -LiteralPath (Join-Path $dir 'release-manifest.json')
  'old-program' | Set-Content -LiteralPath (Join-Path $dir 'program.dll')
  New-Item -ItemType Directory -Path (Join-Path $dir 'Config') -Force | Out-Null
  'customer' | Set-Content -LiteralPath (Join-Path $dir 'Config\user.json')
}
'{"product":"VisionWorkbench","version":"1.1"}' | Set-Content -LiteralPath (Join-Path $next 'release-manifest.json')
'new-program' | Set-Content -LiteralPath (Join-Path $next 'program.dll')
New-Item -ItemType Directory -Path (Join-Path $next 'Config') -Force | Out-Null
'overwrite-attempt' | Set-Content -LiteralPath (Join-Path $next 'Config\user.json')
'staging database' | Set-Content -LiteralPath (Join-Path $next 'customer.db')
& (Join-Path $repo 'scripts\build-incremental-update.ps1') -BaseReleaseDirectory $base -CurrentReleaseDirectory $next -OutputDirectory $output
$package = (Get-ChildItem -LiteralPath $output -Filter '*.zip').FullName
$data = Join-Path $root 'customer-data'
New-Item -ItemType Directory -Force -Path (Join-Path $data 'Config') | Out-Null
'original customer DB bytes' | Set-Content -LiteralPath (Join-Path $data 'Config\visionworkbench.db')
'original settings' | Set-Content -LiteralPath (Join-Path $data 'Config\settings.json')
@{ App=@{ DataDirectory=$data } } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $installed 'appsettings.json')
'fixture executable' | Set-Content -LiteralPath (Join-Path $installed 'VisionWorkbench.exe')
$global:vwUpdateTestExitCode = 0
$global:vwUpdateTestTimeout = $false
# The simulated process changes only this isolated fixture's database/configuration.
# Production updater still always launches the real --environment-check process.
function Start-Process {
  param($FilePath, $ArgumentList, $WorkingDirectory, [switch]$PassThru, $WindowStyle)
  Assert-Equal (Join-Path $installed 'VisionWorkbench.exe') $FilePath
  Assert-Equal '--environment-check' $ArgumentList
  Assert-Equal 'Hidden' $WindowStyle
  'migrated database bytes' | Set-Content -LiteralPath (Join-Path $data 'Config\visionworkbench.db')
  'generated during migration' | Set-Content -LiteralPath (Join-Path $data 'Config\migration-new.json')
  $process = [pscustomobject]@{ ExitCode=$global:vwUpdateTestExitCode; Killed=$false }
  $process | Add-Member ScriptMethod WaitForExit { param($milliseconds) if ($null -eq $milliseconds) { return }; return (-not $global:vwUpdateTestTimeout) }
  $process | Add-Member ScriptMethod Kill { $this.Killed=$true; $global:vwUpdateTestTimeout=$false }
  $process | Add-Member ScriptMethod Dispose { }
  return $process
}
# Bypass only the external running-process probe; real application data is never used.
function Get-Process { param($Name) return @() }
& (Join-Path $repo 'scripts\apply-incremental-update.ps1') -PackagePath $package -InstallDirectory $installed
Assert-Equal 'new-program' (Get-Content -LiteralPath (Join-Path $installed 'program.dll'))
Assert-Equal 'customer' (Get-Content -LiteralPath (Join-Path $installed 'Config\user.json'))
Assert-Equal $false (Test-Path -LiteralPath (Join-Path $installed 'customer.db'))
Write-Output 'PASS update preserves customer data and updates program'
Assert-Equal 'migrated database bytes' (Get-Content -LiteralPath (Join-Path $data 'Config\visionworkbench.db'))
Write-Output 'PASS successful environment check executes migration'
$malicious = Join-Path $root 'bad-payload'; New-Item -ItemType Directory -Path (Join-Path $malicious 'Config') -Force | Out-Null
'bad' | Set-Content -LiteralPath (Join-Path $malicious 'Config\user.json')
$badManifest = @{ product='VisionWorkbench';kind='incremental-update';fromVersion='1.1';toVersion='1.2';component='application';files=@(@{path='Config/user.json';sha256=(Get-FileHash -LiteralPath (Join-Path $malicious 'Config\user.json')).Hash});deletedFiles=@() }
$badManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $malicious 'update-manifest.json')
$badPackage = Join-Path $root 'bad.zip'; Compress-Archive -Path (Join-Path $malicious '*') -DestinationPath $badPackage
$failed=$false
try { & (Join-Path $repo 'scripts\apply-incremental-update.ps1') -PackagePath $badPackage -InstallDirectory $installed } catch { $failed=$true; Write-Output $_.Exception.Message }
Assert-Equal $true $failed
Assert-Equal 'customer' (Get-Content -LiteralPath (Join-Path $installed 'Config\user.json'))
Write-Output 'PASS crafted update cannot overwrite customer data'
# Build the next version, then simulate migration failure and timeout separately.
'{"product":"VisionWorkbench","version":"1.2"}' | Set-Content -LiteralPath (Join-Path $next 'release-manifest.json')
Copy-Item -LiteralPath (Join-Path $installed 'VisionWorkbench.exe') -Destination (Join-Path $next 'VisionWorkbench.exe')
'bad-new-program' | Set-Content -LiteralPath (Join-Path $next 'program.dll')
& (Join-Path $repo 'scripts\build-incremental-update.ps1') -BaseReleaseDirectory $installed -CurrentReleaseDirectory $next -OutputDirectory $output
$failedPackage = (Get-ChildItem -LiteralPath $output -Filter '*1.1-to-1.2.zip').FullName
foreach ($mode in @('failure','timeout')) {
  'pre-update database bytes' | Set-Content -LiteralPath (Join-Path $data 'Config\visionworkbench.db')
  Remove-Item -LiteralPath (Join-Path $data 'Config\migration-new.json') -Force -ErrorAction SilentlyContinue
  $global:vwUpdateTestExitCode = if ($mode -eq 'failure') { 1 } else { 0 }
  $global:vwUpdateTestTimeout = $mode -eq 'timeout'
  $failed=$false
  try { & (Join-Path $repo 'scripts\apply-incremental-update.ps1') -PackagePath $failedPackage -InstallDirectory $installed -EnvironmentCheckTimeoutSeconds 5 } catch { $failed=$true; Write-Output $_.Exception.Message }
  Assert-Equal $true $failed
  Assert-Equal 'new-program' (Get-Content -LiteralPath (Join-Path $installed 'program.dll'))
  Assert-Equal '1.1' ((Get-Content -LiteralPath (Join-Path $installed 'release-manifest.json') -Raw | ConvertFrom-Json).version)
  Assert-Equal 'pre-update database bytes' (Get-Content -LiteralPath (Join-Path $data 'Config\visionworkbench.db'))
  Assert-Equal 'original settings' (Get-Content -LiteralPath (Join-Path $data 'Config\settings.json'))
  Assert-Equal $false (Test-Path -LiteralPath (Join-Path $data 'Config\migration-new.json'))
  Write-Output "PASS $mode restores application and exact pre-migration customer state"
}
. (Join-Path $repo 'scripts\build-common.ps1')
$failed=$false
try { Invoke-CheckedNative $env:COMSPEC @('/c','exit','7') } catch { $failed=$true }
Assert-Equal $true $failed
Write-Output 'PASS native command failure stops release gate'
'All update regression cases passed.'