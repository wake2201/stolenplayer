. "$PSScriptRoot/common.ps1"
$local=Join-Path $script:RepoRoot '.local'
New-Item -ItemType Directory -Force -Path (Join-Path $local 'tools') | Out-Null
$env:DOTNET_CLI_HOME=Join-Path $local 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:NUGET_PACKAGES=Join-Path $local 'nuget'
$env:DOTNET_ROOT=Join-Path $local 'dotnet'
if (!(Test-Path -LiteralPath (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
    $installer=Join-Path $local 'tools/dotnet-install.ps1'
    Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Version '9.0.318' -InstallDir $env:DOTNET_ROOT -NoPath
    if (!$?) { throw 'SDK bootstrap failed.' }
}
$ilspyDir=Join-Path $local 'tools/ilspy'
if (!(Test-Path -LiteralPath (Join-Path $ilspyDir 'ilspycmd.exe'))) {
    & (Get-LocalDotnet) tool install ilspycmd --version 9.1.0.7988 --tool-path $ilspyDir --allow-roll-forward
    if ($LASTEXITCODE -ne 0) { throw 'ILSpy bootstrap failed.' }
}
foreach($dependency in @(
    @{Name='bepinex'; Url='https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip'; Archive='tools/bepinex.zip'; Destination='bepinex'; Marker='bepinex/BepInEx/core/BepInEx.dll'},
    @{Name='cecil'; Url='https://api.nuget.org/v3-flatcontainer/mono.cecil/0.11.6/mono.cecil.0.11.6.nupkg'; Archive='tools/cecil.zip'; Destination='tools/cecil'; Marker='tools/cecil/lib/netstandard2.0/Mono.Cecil.dll'}
)) {
    if (!(Test-Path -LiteralPath (Join-Path $local $dependency.Marker))) {
        Invoke-WebRequest $dependency.Url -OutFile (Join-Path $local $dependency.Archive)
        Expand-Archive -LiteralPath (Join-Path $local $dependency.Archive) -DestinationPath (Join-Path $local $dependency.Destination) -Force
    }
}
& (Get-Ilspy) --version
if ($LASTEXITCODE -ne 0) { throw 'ILSpy version check failed.' }
