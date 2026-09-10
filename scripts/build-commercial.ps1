[CmdletBinding()]
param(
  [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
  [switch]$SkipTests,
  [string]$SigningKeyPath
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path (Split-Path $repo -Parent) '.dotnet10\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$publish = Join-Path $repo "artifacts\commercial\$Configuration"
New-Item -ItemType Directory -Force -Path $publish | Out-Null
& $dotnet restore (Join-Path $repo 'VisionWorkbench.slnx') -r win-x64
& $dotnet build (Join-Path $repo 'src\VisionWorkbench.App\VisionWorkbench.App.csproj') -c $Configuration --no-restore
if (-not $SkipTests) { & $dotnet test (Join-Path $repo 'tests\VisionWorkbench.Tests\VisionWorkbench.Tests.csproj') -c $Configuration --no-restore --filter 'FullyQualifiedName!~UiSmokeTests' }
& $dotnet publish (Join-Path $repo 'src\VisionWorkbench.App\VisionWorkbench.App.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -o $publish
$files = Get-ChildItem $publish -File -Recurse | Where-Object Name -ne 'release-manifest.json' | ForEach-Object {
  [pscustomobject]@{ path = $_.FullName.Substring($publish.Length + 1).Replace('\','/'); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); size = $_.Length }
}
$manifest = [ordered]@{ product='VisionWorkbench'; version='1.0.0'; build=(Get-Date -Format 'yyyyMMdd.HHmmss'); runtime='win-x64'; databaseTargetVersion='20260906-commercial-foundation'; files=$files }
if ($SigningKeyPath) {
  if (-not (Test-Path -LiteralPath $SigningKeyPath)) { throw "Signing key not found: $SigningKeyPath" }
  $options = [System.Text.Json.JsonSerializerOptions]::new()
  $options.WriteIndented = $false
  $options.PropertyNamingPolicy = [System.Text.Json.JsonNamingPolicy]::CamelCase
  $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
  try {
    $ecdsa.ImportFromPem((Get-Content -LiteralPath $SigningKeyPath -Raw))
    $canonical = [System.Text.Json.JsonSerializer]::Serialize($manifest, $options)
    $signature = $ecdsa.SignData([System.Text.Encoding]::UTF8.GetBytes($canonical), [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $manifest.signatureAlgorithm = 'ECDSA_P256_SHA256'
    $manifest.signature = [Convert]::ToBase64String($signature)
  } finally { $ecdsa.Dispose() }
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $publish 'release-manifest.json') -Encoding UTF8
Write-Host "Commercial publish completed: $publish"
