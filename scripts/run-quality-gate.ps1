[CmdletBinding()]
param(
  [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
  [switch]$SkipPython
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path (Split-Path $repo -Parent) '.dotnet10\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$app = Join-Path $repo 'src\VisionWorkbench.App\VisionWorkbench.App.csproj'
$tests = Join-Path $repo 'tests\VisionWorkbench.Tests\VisionWorkbench.Tests.csproj'
$appOutput = Join-Path $repo ".quality-build\app\"
$testOutput = Join-Path $repo ".quality-build\tests\"
& $dotnet build $app -c $Configuration --no-restore "-p:OutputPath=$appOutput"
& $dotnet build $tests -c $Configuration --no-restore "-p:OutputPath=$testOutput"
$testDll = Join-Path $testOutput 'VisionWorkbench.Tests.dll'
& $dotnet vstest $testDll '--TestCaseFilter:FullyQualifiedName!~UiSmokeTests' '--logger:console;verbosity=minimal'
if (-not $SkipPython) {
  & python -m py_compile (Join-Path $repo 'workers\atu5\train_worker.py') (Join-Path $repo 'workers\yolo11\train_worker.py')
}
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts\generate-sbom.ps1')
Write-Host 'VisionWorkbench quality gate passed.'
