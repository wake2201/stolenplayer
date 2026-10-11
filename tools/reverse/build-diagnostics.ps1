. "$PSScriptRoot/common.ps1"
$env:DOTNET_CLI_HOME=Join-Path $script:RepoRoot '.local/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:NUGET_PACKAGES=Join-Path $script:RepoRoot '.local/nuget'
$env:DOTNET_ROOT=Split-Path (Get-LocalDotnet)
$project=Join-Path $script:RepoRoot 'src/ThiefSim2Multiplayer.Diagnostics/ThiefSim2Multiplayer.Diagnostics.csproj'
& (Get-LocalDotnet) build $project -c Release "-p:ManagedDir=$(Get-ManagedPath)" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Diagnostics build failed.' }
