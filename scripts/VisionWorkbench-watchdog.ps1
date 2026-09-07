[CmdletBinding()]
param(
  [Parameter(Mandatory=$true)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$Executable,
  [int]$PollSeconds = 10,
  [switch]$Once
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Executable).Path
$workDir = Split-Path -Parent $exe
do {
  $process = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exe)) -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exe } | Select-Object -First 1
  if ($null -eq $process) {
    Start-Process -FilePath $exe -WorkingDirectory $workDir | Out-Null
  }
  if ($Once) { break }
  Start-Sleep -Seconds ([Math]::Max(2, $PollSeconds))
} while ($true)
