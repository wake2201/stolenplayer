param([switch]$NoRestore)
. "$PSScriptRoot/reverse/common.ps1"
$env:DOTNET_CLI_HOME = Join-Path $script:RepoRoot '.local/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $script:RepoRoot '.local/nuget'
$env:DOTNET_ROOT = Split-Path (Get-LocalDotnet)
$projects = @('src/ThiefSim2Multiplayer/ThiefSim2Multiplayer.csproj',
    'src/ThiefSim2Multiplayer.Diagnostics/ThiefSim2Multiplayer.Diagnostics.csproj',
    'tests/ThiefSim2Multiplayer.Tests/ThiefSim2Multiplayer.Tests.csproj',
    'tools/TransportHarness/TransportHarness.csproj')
foreach ($project in $projects) {
    $arguments = @('build', (Join-Path $script:RepoRoot $project), '-c', 'Release', "-p:ManagedDir=$(Get-ManagedPath)", '--nologo', '-m:1', '-p:UseSharedCompilation=false')
    if ($NoRestore) { $arguments += '--no-restore' }
    & (Get-LocalDotnet) @arguments
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
