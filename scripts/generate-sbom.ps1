[CmdletBinding()]
param([string]$Output = 'artifacts/commercial/sbom.json')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$items = Get-ChildItem (Join-Path $repo 'src') -Filter '*.csproj' -Recurse | ForEach-Object {
  $matches = @(Select-String -Path $_.FullName -Pattern 'PackageReference Include="([^"]+)"' -AllMatches)
  $packages = @($matches | ForEach-Object { $_.Matches | ForEach-Object { $_.Groups[1].Value } } | Sort-Object -Unique)
  [pscustomobject]@{
    name = $_.BaseName
    project = $_.FullName.Substring($repo.Length + 1).Replace('\','/')
    packages = $packages
  }
}
$target = Join-Path $repo $Output
New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
@{
  format = 'VisionWorkbench-SBOM-1'
  generatedAtUtc = (Get-Date).ToUniversalTime().ToString('O')
  components = $items
} | ConvertTo-Json -Depth 6 | Set-Content $target -Encoding UTF8
Write-Host "SBOM written: $target"
