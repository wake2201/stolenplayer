# N1.2 drag-and-drop release tests: ZIP integrity (manifest + hashes + .sha256), required layout (bootstrap root files,
# BepInEx core, plugin + LiteNetLib in BepInEx\plugins\TS2MP), packaged plugin = tested build, nothing forbidden
# (launcher, scripts, saves, logs, configs), extraction into a fake game folder that already has another mod and a
# BepInEx config: only release files are added, nothing unrelated is overwritten, game files unchanged, reproducible ZIP.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$protocolSrc = Get-Content (Join-Path $repo 'src\Shared\Protocol.cs') -Raw
$ver = [regex]::Match($protocolSrc, 'ModVersion = "([^"]+)"').Groups[1].Value
$proto = [regex]::Match($protocolSrc, 'ProtocolVersion = (\d+);').Groups[1].Value
$name = "TS2-Multiplayer-$ver"
$zip = Join-Path $repo "build\release\$name.zip"
$root = Join-Path $env:TEMP ("ts2mp-package-test-" + (Get-Date -Format yyyyMMdd-HHmmss))
$script:fail = 0; $script:pass = 0
function Check($n, $c) { if ($c) { $script:pass++; Write-Host "PASS  $n" } else { $script:fail++; Write-Host "FAIL  $n" } }
function Sha($f) { (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant() }
if (-not (Test-Path $zip)) { Write-Host "Release ZIP missing; run tools\build.ps1 -Package first."; exit 2 }
Add-Type -AssemblyName System.IO.Compression.FileSystem

try {
    New-Item -ItemType Directory -Force $root | Out-Null
    $zipHash = Sha $zip
    Check 'ZIP sha256 file matches the ZIP' ((Get-Content "$zip.sha256" -Raw).StartsWith("$zipHash  $name.zip"))
    $za = [IO.Compression.ZipFile]::OpenRead($zip)
    $entries = @($za.Entries | ForEach-Object { $_.FullName }); $za.Dispose()
    Check 'ZIP has no wrapping folder (copy the contents into the game root)' (($entries -contains 'winhttp.dll') -and ($entries -contains 'BepInEx/plugins/TS2MP/TS2MP.Plugin.dll'))
    $allowed = @($entries | Where-Object { $_ -in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'TS2-Multiplayer-LIESMICH.txt') -or $_ -like 'BepInEx/core/*.dll' -or $_ -like 'BepInEx/plugins/TS2MP/*' })
    Check "only release paths: bootstrap, BepInEx\core, BepInEx\plugins\TS2MP, LIESMICH ($($entries.Count) files)" ($allowed.Count -eq $entries.Count)
    $forbidden = @($entries | Where-Object { $_ -match '(?i)\.(exe|ps1|bat|cmd|thief|log|cfg|reg)$' -or $_ -match '(?i)launcher|client-sandbox|saveData|p2inv|fleet-' })
    Check 'no launcher, scripts, saves, logs, configs or records in the release' ($forbidden.Count -eq 0)

    $x = Join-Path $root 'unzipped'
    Expand-Archive -Path $zip -DestinationPath $x
    $m = @{}
    foreach ($l in Get-Content "$x\BepInEx\plugins\TS2MP\manifest.sha256") { if ($l -and -not $l.StartsWith('#')) { $m[$l.Substring(66)] = $l.Substring(0, 64) } }
    $files = @(Get-ChildItem $x -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($x.Length + 1).Replace('\', '/') } | Where-Object { $_ -ne 'BepInEx/plugins/TS2MP/manifest.sha256' })
    $bad = @($files | Where-Object { -not $m.ContainsKey($_) -or $m[$_] -ne (Sha "$x\$_") })
    Check "ZIP content = manifest ($($files.Count) files, all SHA-256 match, none extra)" ($bad.Count -eq 0 -and $files.Count -eq $m.Count)
    $hdr = Get-Content "$x\BepInEx\plugins\TS2MP\manifest.sha256" -TotalCount 1
    Check "versions pinned: manifest $ver / protocol $proto, plugin 0.14.2 / 26" ($hdr -match "version=$([regex]::Escape($ver)) protocol=$proto " -and $ver -eq '0.14.2' -and $proto -eq '26')
    $bep = Join-Path $repo 'build\deps\BepInEx_5.4.23.5'
    $bootstrap = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version') | Where-Object { (Sha "$x\$_") -ne (Sha "$bep\$_") }
    $core = @(Get-ChildItem "$bep\BepInEx\core\*.dll" | Where-Object { -not (Test-Path "$x\BepInEx\core\$($_.Name)") -or (Sha "$x\BepInEx\core\$($_.Name)") -ne (Sha $_.FullName) })
    Check 'BepInEx 5.4.23.5 bootstrap root files + all core DLLs present and unmodified' ($bootstrap.Count -eq 0 -and $core.Count -eq 0)
    Check 'packaged plugin + LiteNetLib = the tested build (build\plugin)' ((Sha "$x\BepInEx\plugins\TS2MP\TS2MP.Plugin.dll") -eq (Sha "$repo\build\plugin\TS2MP.Plugin.dll") -and (Sha "$x\BepInEx\plugins\TS2MP\LiteNetLib.dll") -eq (Sha "$repo\lib\LiteNetLib.dll"))
    $lic = @('LiteNetLib-LICENSE.txt', 'BepInEx-LICENSE.txt', 'HarmonyX-LICENSE.txt', 'MonoMod-LICENSE.txt', 'Mono.Cecil-LICENSE.txt', 'UnityDoorstop-LICENSE.txt') | Where-Object { -not (Test-Path "$x\BepInEx\plugins\TS2MP\licenses\$_") }
    Check 'license texts + third-party notice + German setup guide included' ($lic.Count -eq 0 -and (Test-Path "$x\BepInEx\plugins\TS2MP\THIRD-PARTY-NOTICES.txt") -and (Test-Path "$x\BepInEx\plugins\TS2MP\DIRECT_CONNECT_SETUP_DE.md"))
    $readme = Get-Content "$x\TS2-Multiplayer-LIESMICH.txt" -Raw -Encoding UTF8
    Check 'German README: copy into the game folder, Steam start, F8, host from the world, join from the main menu, unencrypted, saves untouched' (
        $readme -match 'Spielordner' -and $readme -match 'Steam' -and $readme -match 'F8' -and $readme -match 'HAUPTMENÜ' -and $readme -match 'NICHT verschl' -and $readme -match 'Einzelspieler-Spielstände werden dabei nicht verändert' -and $readme -match 'DS-Lite')
    $guide = Get-Content "$x\BepInEx\plugins\TS2MP\DIRECT_CONNECT_SETUP_DE.md" -Raw -Encoding UTF8
    Check 'setup guide: UDP 27960 only, DHCP reservation, firewall, CGNAT/DS-Lite, troubleshooting, no launcher references' ($guide -match 'UDP 27960' -and $guide -match 'DHCP-Reservierung' -and $guide -match 'Firewall' -and $guide -match 'CGNAT' -and $guide -match 'Fehlerbehebung' -and $guide -notmatch '(?i)launcher')

    # drag-and-drop into a game folder with an unrelated mod and an existing BepInEx config
    $g = Join-Path $root 'game'
    New-Item -ItemType Directory -Force "$g\Thief Simulator 2_Data", "$g\BepInEx\plugins\OtherMod", "$g\BepInEx\config" | Out-Null
    'game exe' | Set-Content "$g\Thief Simulator 2.exe"; 'data' | Set-Content "$g\Thief Simulator 2_Data\data.unity3d"
    'other mod' | Set-Content "$g\BepInEx\plugins\OtherMod\Other.dll"; 'user cfg' | Set-Content "$g\BepInEx\config\BepInEx.cfg"
    $before = @{}; Get-ChildItem $g -Recurse -File | ForEach-Object { $before[$_.FullName] = Sha $_.FullName }
    Copy-Item "$x\*" $g -Recurse -Force
    Copy-Item "$x\.doorstop_version" $g -Force
    $changed = @($before.Keys | Where-Object { (Sha $_) -ne $before[$_] })
    Check 'extraction adds files only: other mod, user BepInEx config and game files untouched' ($changed.Count -eq 0)
    $placed = @($m.Keys | Where-Object { -not (Test-Path -LiteralPath (Join-Path $g $_.Replace('/', '\'))) -or (Sha (Join-Path $g $_.Replace('/', '\'))) -ne $m[$_] })
    Check 'every release file lands at its expected path in the game folder' ($placed.Count -eq 0 -and (Test-Path "$g\BepInEx\plugins\TS2MP\TS2MP.Plugin.dll") -and (Test-Path "$g\winhttp.dll"))

    & powershell -NoProfile -ExecutionPolicy Bypass -File "$repo\tools\package.ps1" | Out-Null
    Check 'reproducible packaging: rebuilding the ZIP from the same build gives the same bytes' ($LASTEXITCODE -eq 0 -and (Sha $zip) -eq $zipHash)
}
finally { Write-Host "Package test data kept in $root" }
Write-Host "RESULT: $script:pass passed, $script:fail failed"
if ($script:fail) { exit 1 } else { exit 0 }
