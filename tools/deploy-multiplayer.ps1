. "$PSScriptRoot/reverse/common.ps1"
$game = Get-GamePath
if (Get-Process -Name 'Thief Simulator 2' -ErrorAction SilentlyContinue) { throw 'Close the game before deployment.' }
if (!(Test-Path -LiteralPath (Join-Path $game 'BepInEx/core/BepInEx.dll'))) { throw 'Deploy the existing diagnostics/BepInEx loader first.' }
$build = Join-Path $script:RepoRoot 'src/ThiefSim2Multiplayer/bin/Release/net46'
$pluginDirectory = Join-Path $game 'BepInEx/plugins/ThiefSim2Multiplayer'
$dependencyDirectory = Join-Path $game 'BepInEx/plugins/ThiefSim2Multiplayer.Dependencies'
$files = @('ThiefSim2Multiplayer.dll','ThiefSim2Multiplayer.Core.dll','LiteNetLib.dll')
foreach ($name in $files) { if (!(Test-Path -LiteralPath (Join-Path $build $name))) { throw "Build output missing: $name" } }
& "$PSScriptRoot/reverse/fingerprint-game.ps1" -Verify
New-Item -ItemType Directory -Force -Path $pluginDirectory,$dependencyDirectory | Out-Null
$additions = @()
foreach ($name in $files) {
    $targetDirectory = if ($name -eq 'LiteNetLib.dll') { $dependencyDirectory } else { $pluginDirectory }
    $destination = Join-Path $targetDirectory $name
    Copy-Item -LiteralPath (Join-Path $build $name) -Destination $destination -Force
    if ((Get-FileHash -LiteralPath (Join-Path $build $name)).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw "Installed DLL hash mismatch: $name" }
    $additions += $destination
}
Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'third_party/LiteNetLib/LICENSE.txt') -Destination (Join-Path $dependencyDirectory 'LiteNetLib-LICENSE.txt') -Force
$additions += Join-Path $dependencyDirectory 'LiteNetLib-LICENSE.txt'
$manifest = Join-Path $script:RepoRoot '.local/evidence/multiplayer-deployment.json'
$additions | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
& "$PSScriptRoot/reverse/fingerprint-game.ps1" -Verify
$installedVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $pluginDirectory 'ThiefSim2Multiplayer.dll')).Version.ToString(3)
Write-Output "Deployed multiplayer $installedVersion, core, LiteNetLib and MIT license; installed DLL hashes match the build."
