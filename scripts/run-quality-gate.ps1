[CmdletBinding()]
param(
  [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
  [switch]$SkipPython
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'build-common.ps1')
$dotnet = Resolve-ProjectDotnet $repo
$app = Join-Path $repo 'src\VisionWorkbench.App\VisionWorkbench.App.csproj'
$tests = Join-Path $repo 'tests\VisionWorkbench.Tests\VisionWorkbench.Tests.csproj'
$appOutput = Join-Path $repo ".quality-build\app\"
$testOutput = Join-Path $repo ".quality-build\tests\"
Invoke-CheckedNative $dotnet @('build', $app, '-c', $Configuration, '--no-restore', "-p:OutputPath=$appOutput", '-p:AppendTargetFrameworkToOutputPath=false')
Invoke-CheckedNative $dotnet @('build', $tests, '-c', $Configuration, '--no-restore', "-p:OutputPath=$testOutput", '-p:AppendTargetFrameworkToOutputPath=false')
$testDll = Join-Path $testOutput 'VisionWorkbench.Tests.dll'
Invoke-CheckedNative $dotnet @('vstest', $testDll, '--TestCaseFilter:FullyQualifiedName!~UiSmokeTests', '--logger:console;verbosity=minimal')
if (-not $SkipPython) {
  Invoke-CheckedNative 'python' @('-m', 'py_compile', (Join-Path $repo 'workers\atu5\train_worker.py'), (Join-Path $repo 'workers\yolo11\train_worker.py'))
}
Invoke-CheckedNative 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $repo 'scripts\generate-sbom.ps1'))
Write-Host 'VisionWorkbench quality gate passed.'
