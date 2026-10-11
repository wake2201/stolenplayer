. "$PSScriptRoot/reverse/common.ps1"
$env:DOTNET_ROOT = Split-Path (Get-LocalDotnet)
$previousTiering = $env:DOTNET_TieredCompilation
try {
    # Tiered JIT work can enter the measured allocation window even after warmup.
    # Disable tiering for this test process; preserve the zero-allocation assertion.
    $env:DOTNET_TieredCompilation = '0'
    & (Get-LocalDotnet) (Join-Path $script:RepoRoot 'tests/ThiefSim2Multiplayer.Tests/bin/Release/net9.0/ThiefSim2Multiplayer.Tests.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Multiplayer tests failed.' }
} finally { $env:DOTNET_TieredCompilation = $previousTiering }
