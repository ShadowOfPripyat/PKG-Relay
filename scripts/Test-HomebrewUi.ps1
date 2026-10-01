param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The native WinUI smoke check requires Windows.'
}
$project = Join-Path $PSScriptRoot 'HomebrewUiSmoke\HomebrewUiSmoke.csproj'
dotnet build $project -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'UI smoke check build failed.' }
$output = Join-Path $PSScriptRoot "HomebrewUiSmoke\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64"
$process = Start-Process -FilePath (Join-Path $output 'HomebrewUiSmoke.exe') -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(30000)) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw 'UI smoke check timed out.'
}
$log = Get-Content -LiteralPath (Join-Path $output 'smoke.log') -Raw
Write-Output $log
if ($process.ExitCode -ne 0 -or $log -notmatch '(?m)^PASS:') {
    throw "UI smoke check failed with exit code $($process.ExitCode)."
}
