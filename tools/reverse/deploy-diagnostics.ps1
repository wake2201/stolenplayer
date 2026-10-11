param([switch]$VerifyRuntime)
. "$PSScriptRoot/common.ps1"
$game=Get-GamePath
if (Get-Process -Name 'Thief Simulator 2' -ErrorAction SilentlyContinue) { throw 'Close the game before deployment.' }
$plugin=Join-Path $script:RepoRoot 'src/ThiefSim2Multiplayer.Diagnostics/bin/Release/net46/ThiefSim2Multiplayer.Diagnostics.dll'
if (!(Test-Path -LiteralPath $plugin)) { throw 'Build diagnostics first.' }
if (!(Test-Path -LiteralPath (Join-Path $script:RepoRoot '.local/evidence/original-binaries.json'))) { & "$PSScriptRoot/fingerprint-game.ps1" }
$source=Join-Path $script:RepoRoot '.local/bepinex'
$manifest=Join-Path $script:RepoRoot '.local/evidence/deployment-files.json'
$additions=@()
if (Test-Path -LiteralPath $manifest) { $additions=@(Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json) }
if (!(Test-Path -LiteralPath (Join-Path $game 'BepInEx/core/BepInEx.dll'))) {
    $files=@(Get-ChildItem -LiteralPath $source -Recurse -File)
    # Validate every destination before adding loader files. Never overwrite originals or another loader.
    foreach($file in $files) {
        $relative=$file.FullName.Substring($source.Length+1)
        $destination=Join-Path $game $relative
        if (Test-Path -LiteralPath $destination) { throw "Loader destination already exists: $destination" }
    }
    foreach($file in $files) {
        $relative=$file.FullName.Substring($source.Length+1)
        $destination=Join-Path $game $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
        $additions+=$destination
    }
}
$destination=Join-Path $game 'BepInEx/plugins/ThiefSim2Multiplayer.Diagnostics/ThiefSim2Multiplayer.Diagnostics.dll'
New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
Copy-Item -LiteralPath $plugin -Destination $destination -Force
$dependencies = Join-Path $game 'BepInEx/plugins/ThiefSim2Multiplayer.Dependencies'
New-Item -ItemType Directory -Force -Path $dependencies | Out-Null
foreach ($name in @('LiteNetLib.dll')) {
    Copy-Item -LiteralPath (Join-Path (Split-Path $plugin) $name) -Destination (Join-Path $dependencies $name) -Force
    $additions += Join-Path $dependencies $name
}
Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'third_party/LiteNetLib/LICENSE.txt') -Destination (Join-Path $dependencies 'LiteNetLib-LICENSE.txt') -Force
$additions += Join-Path $dependencies 'LiteNetLib-LICENSE.txt'
$additions+=$destination
$output=Join-Path $script:RepoRoot '.local/runtime-dumps'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$config=Join-Path $game 'BepInEx/config/local.thiefsim2.multiplayer.diagnostics.cfg'
New-Item -ItemType Directory -Force -Path (Split-Path $config) | Out-Null
@("[Capture]","OutputDirectory = $output",'ObjectLimit = 12000','RecordLimit = 300', '[Verification]', "Enabled = $($VerifyRuntime.IsPresent)") | Set-Content -LiteralPath $config -Encoding utf8
$additions+=$config
$additions | Where-Object { Test-Path -LiteralPath $_ } | Sort-Object -Unique | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
& "$PSScriptRoot/fingerprint-game.ps1" -Verify
Write-Output "Deployed diagnostics to $destination"
