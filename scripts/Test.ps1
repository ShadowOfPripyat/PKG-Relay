$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tests\PkgRelay.Core.Tests\PkgRelay.Core.Tests.csproj'
$dotnet = Get-Command dotnet -ErrorAction Stop
& $dotnet.Source test $project -c Release
if ($LASTEXITCODE -ne 0) { throw 'Core regression tests failed.' }
