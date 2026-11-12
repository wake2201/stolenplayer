# N1 tests: release package completeness + integrity, installer (install / update / repair / uninstall, ownership,
# backups, refusals), Host Game / Join Game launches (join code only in the child environment, never on the command
# line or in logs/state/ini), port conflict, save isolation. Uses the staged release from tools\package.ps1 and a fake
# game folder (FakeGame.exe renamed to "Thief Simulator 2.exe"). Never touches the real game or real saves.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$protocolSrc = Get-Content (Join-Path $repo 'src\Shared\Protocol.cs') -Raw
$ver = [regex]::Match($protocolSrc, 'ModVersion = "([^"]+)"').Groups[1].Value
$name = "TS2-Multiplayer-$ver-Windows"
$stage = Join-Path $repo "build\release\$name"
$zip = Join-Path $repo "build\release\$name.zip"
$fakeExe = Join-Path $repo 'build\tests\FakeGame.exe'
$root = Join-Path $env:TEMP ("ts2mp-installer-test-" + (Get-Date -Format yyyyMMdd-HHmmss))
$company = 'TS2MP-InstTest'; $product = 'FakeGame'
$localLow = Join-Path ([Environment]::GetFolderPath('LocalApplicationData') + 'Low') $company
$data = Join-Path $root 'data'; $pkg = Join-Path $root 'pkg'
$script:fail = 0; $script:pass = 0; $script:n = 0

function Check($name, $cond) { if ($cond) { $script:pass++; Write-Host "PASS  $name" } else { $script:fail++; Write-Host "FAIL  $name" } }
function Sha($f) { (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant() }
function ShaText($s) { $h = [Security.Cryptography.SHA256]::Create(); -join ($h.ComputeHash([Text.Encoding]::ASCII.GetBytes($s)) | ForEach-Object { $_.ToString('x2') }) }
function Fakes { @(Get-Process -Name 'Thief Simulator 2' -ErrorAction SilentlyContinue) }
function KillFakes { Fakes | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 400 }
function NewGame($dir) {
    New-Item -ItemType Directory -Force "$dir\Thief Simulator 2_Data" | Out-Null
    Copy-Item $fakeExe "$dir\Thief Simulator 2.exe"
    "$company`n$product" | Set-Content "$dir\Thief Simulator 2_Data\app.info"
    'original game data' | Set-Content "$dir\Thief Simulator 2_Data\data.unity3d"
}
# Runs the launcher; returns the exit code. Output in $script:out.
function L([string]$game, [string[]]$launcherArgs, [string]$stdin = $null, [string]$exe = "$pkg\TS2-Multiplayer-Launcher.exe") {
    $script:n++
    $o = Join-Path $root "out-$($script:n).txt"
    $a = @($launcherArgs) + @('--no-pause', '--config', "`"$root\launcher.ini`"", '--game', "`"$game`"", '--data-dir', "`"$data`"")
    $sp = @{ FilePath = $exe; ArgumentList = $a; PassThru = $true; NoNewWindow = $true; RedirectStandardOutput = $o }
    # stdin is always redirected (empty unless given) so no prompt can ever block the test
    $i = Join-Path $root "in-$($script:n).txt"; Set-Content -Encoding ASCII $i $(if ($stdin -ne $null) { $stdin } else { '' }); $sp.RedirectStandardInput = $i
    $p = Start-Process @sp
    $null = $p.Handle   # keeps the process handle so ExitCode is available after redirected runs
    $p.WaitForExit()
    Start-Sleep -Milliseconds 200
    $script:out = (Get-Content $o -Raw -ErrorAction SilentlyContinue)
    # (the input file stays: a started fake game inherits the handle)
    return $p.ExitCode
}
function ArgLines($game) { if (Test-Path "$game\fakegame-args.txt") { @(Get-Content "$game\fakegame-args.txt") } else { @() } }
function EnvLines($game) { if (Test-Path "$game\fakegame-env.txt") { @(Get-Content "$game\fakegame-env.txt") } else { @() } }
function Manifest($dir) {
    $m = @{}
    foreach ($l in Get-Content "$dir\manifest.sha256") { if ($l -and -not $l.StartsWith('#')) { $m[$l.Substring(66)] = $l.Substring(0, 64) } }
    return $m
}

if (Fakes) { Write-Host 'A process named "Thief Simulator 2" is running; close it before running installer tests.'; exit 2 }
if (Test-Path $localLow) { Write-Host "Test save folder $localLow already exists; aborting to avoid touching it."; exit 2 }
if (-not (Test-Path "$stage\manifest.sha256") -or -not (Test-Path $zip)) { Write-Host "Release package missing; run tools\build.ps1 -Package first."; exit 2 }

try {
    New-Item -ItemType Directory -Force $root, $data | Out-Null
    New-Item -ItemType Directory -Force "$localLow\$product" | Out-Null
    'save-a' | Set-Content "$localLow\$product\saveData.thief"
    $saveHash = Sha "$localLow\$product\saveData.thief"

    # ---------------- package ----------------
    $zipHash = Sha $zip
    Check 'ZIP sha256 file matches the ZIP' ((Get-Content "$zip.sha256" -Raw).StartsWith($zipHash))
    Expand-Archive -Path $zip -DestinationPath "$root\unzipped"
    Copy-Item "$root\unzipped\$name" $pkg -Recurse
    $m = Manifest $pkg
    $files = @(Get-ChildItem $pkg -Recurse -File | ForEach-Object { $_.FullName.Substring($pkg.Length + 1).Replace('\', '/') } | Where-Object { $_ -ne 'manifest.sha256' })
    $bad = @($files | Where-Object { -not $m.ContainsKey($_) -or $m[$_] -ne (Sha "$pkg\$_") })
    Check "ZIP content = manifest ($($files.Count) files, all SHA-256 match, none extra)" ($bad.Count -eq 0 -and $files.Count -eq $m.Count)
    $required = @('TS2-Multiplayer-Launcher.exe', 'SCHNELLSTART.txt', 'DIRECT_CONNECT_SETUP_DE.md', 'THIRD-PARTY-NOTICES.txt', 'payload/plugin/TS2MP.Plugin.dll',
        'payload/plugin/LiteNetLib.dll', 'payload/bepinex/winhttp.dll', 'payload/bepinex/doorstop_config.ini', 'payload/bepinex/BepInEx/core/BepInEx.dll',
        'payload/bepinex/BepInEx/core/BepInEx.Preloader.dll', 'payload/bepinex/BepInEx/core/0Harmony.dll', 'licenses/LiteNetLib-LICENSE.txt',
        'licenses/BepInEx-LICENSE.txt', 'licenses/UnityDoorstop-LICENSE.txt', 'licenses/HarmonyX-LICENSE.txt', 'licenses/MonoMod-LICENSE.txt', 'licenses/Mono.Cecil-LICENSE.txt')
    $missing = @($required | Where-Object { -not $m.ContainsKey($_) })
    Check 'package complete: launcher, plugin, LiteNetLib, BepInEx 5.4.23.5, licenses, German quick start + setup guide' ($missing.Count -eq 0)
    Check 'payload plugin = freshly built plugin' ($m['payload/plugin/TS2MP.Plugin.dll'] -eq (Sha "$repo\build\plugin\TS2MP.Plugin.dll"))
    Check 'payload BepInEx = official 5.4.23.5 files' ($m['payload/bepinex/BepInEx/core/BepInEx.dll'] -eq (Sha "$repo\build\deps\BepInEx_5.4.23.5\BepInEx\core\BepInEx.dll") -and $m['payload/bepinex/winhttp.dll'] -eq (Sha "$repo\build\deps\BepInEx_5.4.23.5\winhttp.dll"))
    $hdr = (Get-Content "$pkg\manifest.sha256" -TotalCount 1)
    $launcherSrc = Get-Content (Join-Path $repo 'src\Launcher\Launcher.cs') -Raw
    Check "versions pinned: manifest, launcher and plugin all $ver / protocol 21" ($hdr -match "version=$([regex]::Escape($ver)) protocol=21 " -and $launcherSrc -match "ModVersion = `"$([regex]::Escape($ver))`"" -and $ver -eq '0.11.1')
    $q = (Get-Content "$pkg\SCHNELLSTART.txt" -Raw -Encoding UTF8)
    Check 'German quick start mentions port forwarding, join code, unencrypted transport, CGNAT/DS-Lite' ($q -match 'UDP 27960' -and $q -match 'Beitrittscode' -and $q -match 'NICHT verschl' -and $q -match 'DS-Lite')
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$repo\tools\package.ps1" | Out-Null
    Check 'reproducible packaging: rebuilding the ZIP from the same build gives the same bytes' ($LASTEXITCODE -eq 0 -and (Sha $zip) -eq $zipHash)

    # ---------------- installer ----------------
    $g1 = Join-Path $root 'game1'; NewGame $g1
    $dataHash = Sha "$g1\Thief Simulator 2_Data\data.unity3d"; $exeHash = Sha "$g1\Thief Simulator 2.exe"
    $c = L $g1 @('status')
    Check 'status before install -> not installed (exit 12), package verified' ($c -eq 12 -and $script:out -match 'files verified' -and $script:out -match 'not installed')
    $c = L $g1 @('host', '--port', '37700', '--bind', '127.0.0.1')
    Check 'Host Game without installation -> refused (exit 2), nothing started' ($c -eq 2 -and (ArgLines $g1).Count -eq 0)
    $c = L $g1 @('install')
    $payload = @($m.Keys | Where-Object { $_.StartsWith('payload/') })
    $wrong = @($payload | ForEach-Object {
        $rel = $_.Replace('/', '\'); $dest = if ($rel.StartsWith('payload\bepinex\')) { $rel.Substring(16) } else { 'BepInEx\plugins\TS2MP\' + $rel.Substring(15) }
        if (-not (Test-Path "$g1\$dest") -or (Sha "$g1\$dest") -ne $m[$_]) { $dest } })
    Check "install -> exit 0, all $($payload.Count) payload files placed with matching SHA-256" ($c -eq 0 -and $wrong.Count -eq 0)
    $rec = Get-Content "$g1\BepInEx\plugins\TS2MP\install-record.txt"
    Check 'install record lists owned files, BepInEx owned' (($rec -contains 'bepinex=owned') -and @($rec | Where-Object { $_ -like 'owned *' }).Count -eq $payload.Count -and ($rec -contains "version=$ver"))
    Check 'game exe, game data and saves untouched by install' ((Sha "$g1\Thief Simulator 2.exe") -eq $exeHash -and (Sha "$g1\Thief Simulator 2_Data\data.unity3d") -eq $dataHash -and (Sha "$localLow\$product\saveData.thief") -eq $saveHash)
    $c = L $g1 @('status')
    Check 'status after install -> installed (exit 0)' ($c -eq 0 -and $script:out -match 'all \d+ files verified')
    $c = L $g1 @('install')
    Check 'second install -> nothing to do (exit 0)' ($c -eq 0 -and $script:out -match 'Nothing to do')

    # repair
    Remove-Item "$g1\BepInEx\core\BepInEx.dll"
    [IO.File]::WriteAllBytes("$g1\BepInEx\plugins\TS2MP\TS2MP.Plugin.dll", [byte[]](1, 2, 3))
    $c = L $g1 @('status')
    Check 'damaged installation detected (exit 12)' ($c -eq 12 -and $script:out -match 'missing')
    $before = @(Get-ChildItem "$data\backups" -Directory -Filter 'install-*' -ErrorAction SilentlyContinue).Count
    $c = L $g1 @('repair')
    $bk = @(Get-ChildItem "$data\backups" -Directory -Filter 'install-*' | Sort-Object Name)
    Check 'repair -> exit 0, missing + damaged files restored' ($c -eq 0 -and (Sha "$g1\BepInEx\core\BepInEx.dll") -eq $m['payload/bepinex/BepInEx/core/BepInEx.dll'] -and (Sha "$g1\BepInEx\plugins\TS2MP\TS2MP.Plugin.dll") -eq $m['payload/plugin/TS2MP.Plugin.dll'])
    Check 'repair backed up the damaged file before replacing it' ($bk.Count -eq $before + 1 -and (Get-Content -Encoding Byte "$($bk[-1].FullName)\game\BepInEx\plugins\TS2MP\TS2MP.Plugin.dll").Count -eq 3)

    # update (an older plugin build in place)
    [IO.File]::WriteAllText("$g1\BepInEx\plugins\TS2MP\TS2MP.Plugin.dll", 'older plugin build')
    $c = L $g1 @('host', '--port', '37700', '--bind', '127.0.0.1')
    Check 'Host Game with a plugin that differs from the package -> refused (exit 2)' ($c -eq 2 -and $script:out -match 'Update')
    $c = L $g1 @('update')
    Check 'update -> exit 0, plugin replaced, old one backed up' ($c -eq 0 -and (Sha "$g1\BepInEx\plugins\TS2MP\TS2MP.Plugin.dll") -eq $m['payload/plugin/TS2MP.Plugin.dll'] -and
        @(Get-ChildItem "$data\backups" -Recurse -File -Filter 'TS2MP.Plugin.dll' | Where-Object { (Get-Content $_.FullName -Raw) -eq 'older plugin build' }).Count -eq 1)

    # damaged package: nothing in the game folder changes
    $g2 = Join-Path $root 'game2'; NewGame $g2
    Copy-Item $pkg "$root\pkgbad" -Recurse
    $b = [IO.File]::ReadAllBytes("$root\pkgbad\payload\plugin\TS2MP.Plugin.dll"); $b[100] = $b[100] -bxor 0xFF; [IO.File]::WriteAllBytes("$root\pkgbad\payload\plugin\TS2MP.Plugin.dll", $b)
    $c = L $g2 @('install', '--package', "`"$root\pkgbad`"") -exe "$root\pkgbad\TS2-Multiplayer-Launcher.exe"
    Check 'tampered package file -> install refused (exit 12), game folder unchanged' ($c -eq 12 -and $script:out -match 'damaged' -and -not (Test-Path "$g2\BepInEx") -and -not (Test-Path "$g2\winhttp.dll"))
    Copy-Item "$pkg\payload\plugin\TS2MP.Plugin.dll" "$root\pkgbad\payload\plugin\TS2MP.Plugin.dll" -Force
    'evil' | Set-Content "$root\pkgbad\payload\plugin\extra.dll"
    $c = L $g2 @('install') -exe "$root\pkgbad\TS2-Multiplayer-Launcher.exe"
    Check 'unlisted file in the package payload -> install refused (exit 12)' ($c -eq 12 -and $script:out -match 'unlisted' -and -not (Test-Path "$g2\BepInEx"))

    # foreign BepInEx: never overwritten
    New-Item -ItemType Directory -Force "$g2\BepInEx\core" | Out-Null
    'someone elses bepinex' | Set-Content "$g2\BepInEx\core\BepInEx.dll"
    $c = L $g2 @('install')
    Check 'different BepInEx already installed -> refused (exit 12), its files untouched' ($c -eq 12 -and (Get-Content "$g2\BepInEx\core\BepInEx.dll") -eq 'someone elses bepinex' -and -not (Test-Path "$g2\BepInEx\plugins\TS2MP"))

    # pre-existing identical BepInEx (developer install without record): adopted as pre-existing, kept on uninstall
    $g3 = Join-Path $root 'game3'; NewGame $g3
    Copy-Item "$pkg\payload\bepinex\*" $g3 -Recurse -Force
    $c = L $g3 @('install')
    Check 'identical pre-existing BepInEx -> install adds only the plugin, records BepInEx as pre-existing' ($c -eq 0 -and ((Get-Content "$g3\BepInEx\plugins\TS2MP\install-record.txt") -contains 'bepinex=preexisting'))
    $c = L $g3 @('uninstall', '--yes')
    Check 'uninstall with pre-existing BepInEx -> plugin removed, BepInEx left in place' ($c -eq 0 -and -not (Test-Path "$g3\BepInEx\plugins\TS2MP") -and (Test-Path "$g3\BepInEx\core\BepInEx.dll") -and (Test-Path "$g3\winhttp.dll"))

    # install refused while the game runs
    $run = Start-Process "$g2\Thief Simulator 2.exe" -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 400
    $c = L $g3 @('install')
    Check 'install while the game is running -> refused (exit 12)' ($c -eq 12 -and $script:out -match 'running')
    KillFakes

    # ---------------- Host Game / Join Game ----------------
    $port = 37710
    $c = L $g1 @('host', '--port', "$port", '--bind', '127.0.0.1')
    $hostOut = $script:out
    $codeShown = [regex]::Match($hostOut, 'Join code: ([0-9A-Z]{5}(-[0-9A-Z]{5}){4})').Groups[1].Value
    $codeNorm = $codeShown.Replace('-', '')
    $a = @(ArgLines $g1); $e = @(EnvLines $g1)
    Check 'Host Game -> exit 0, host role, configured port and bind passed' ($c -eq 0 -and $a.Count -eq 1 -and $a[0] -match '-ts2mp-role host' -and $a[0] -match "-ts2mp-port $port " -and $a[0] -match '-ts2mp-bind 127\.0\.0\.1 ')
    Check 'join code shown to the host (25 chars) and passed only via the child environment' ($codeNorm.Length -eq 25 -and $e.Count -eq 1 -and $e[0].EndsWith(' ' + (ShaText $codeNorm)))
    $leak = @($a[0], (Get-Content "$data\logs\launcher.log" -Raw), (Get-Content "$data\session.state" -Raw), (Get-Content "$root\launcher.ini" -Raw)) | Where-Object { $_ -match $codeNorm -or $_ -match [regex]::Escape($codeShown) }
    Check 'join code not on the command line, not in launcher.log, session.state or the ini' ($leak.Count -eq 0)
    Check 'host made a verified save backup first' (@(Get-ChildItem "$data\backups" -Recurse -File -Filter 'saveData.thief').Count -ge 1)
    $c = L $g1 @('host', '--port', "$port", '--bind', '127.0.0.1')
    Check 'second Host Game while one runs -> refused (exit 4)' ($c -eq 4)
    $c = L $g1 @('local')
    Check 'Local Test while a Host Game runs -> refused (exit 4)' ($c -eq 4)
    $c = L $g1 @('join', '--host', '127.0.0.1', '--port', "$port", '--code-stdin') 'not-a-code'
    Check 'Join with an invalid code -> refused (exit 2), nothing started' ($c -eq 2 -and (ArgLines $g1).Count -eq 1)
    $c = L $g1 @('join', '--host', '999.1.1.1', '--port', "$port", '--code-stdin') $codeShown
    Check 'Join with an invalid address -> refused (exit 2)' ($c -eq 2)
    $c = L $g1 @('join', '--host', 'nonexistent-host.invalid', '--port', "$port", '--code-stdin') $codeShown
    Check 'Join with an unresolvable host name -> refused (exit 2)' ($c -eq 2 -and $script:out -match 'resolved')
    $c = L $g1 @('join', '--host', '127.0.0.1', '--port', "$port", '--code-stdin') $codeShown.ToLowerInvariant()
    $a = @(ArgLines $g1); $e = @(EnvLines $g1)
    Check 'Join (same PC as the host) -> exit 0, client role, target + port, session "join", data sandbox dir' ($c -eq 0 -and $a.Count -eq 2 -and $a[1] -match '-ts2mp-role client' -and $a[1] -match '-ts2mp-session join ' -and $a[1] -match "-ts2mp-port $port " -and $a[1] -match '-ts2mp-connect 127\.0\.0\.1' -and $a[1] -match [regex]::Escape("-ts2mp-data $data "))
    Check 'client got the same join code via its environment (normalised), not via argv' ($e.Count -eq 2 -and $e[1].EndsWith(' ' + (ShaText $codeNorm)) -and $a[1] -notmatch $codeNorm)
    Check 'ini remembers the host address and port, never the code' ((Get-Content "$root\launcher.ini" -Raw) -match 'JoinAddress=127\.0\.0\.1' -and (Get-Content "$root\launcher.ini" -Raw) -notmatch $codeNorm)
    $c = L $g1 @('join', '--host', '127.0.0.1', '--port', "$port", '--code-stdin') $codeShown
    Check 'second Join while Player 2 runs -> refused (exit 4)' ($c -eq 4)
    KillFakes

    # port conflict
    $blocker = New-Object Net.Sockets.UdpClient([Net.IPEndPoint]::new([Net.IPAddress]::Any, 37720))
    try {
        $c = L $g1 @('host', '--port', '37720')
        Check 'Host Game on a UDP port already in use -> refused (exit 11), nothing started' ($c -eq 11 -and $script:out -match 'already in use' -and (ArgLines $g1).Count -eq 2)
    } finally { $blocker.Close() }
    $c = L $g1 @('host', '--port', '37720', '--bind', '127.0.0.1')
    Check 'same port once free -> host starts' ($c -eq 0)
    KillFakes
    $c = L $g1 @('host', '--port', '80')
    Check 'invalid port -> refused (exit 2)' ($c -eq 2)

    # Join from a separate PC (no local host): own saves backed up, never written
    $g4 = Join-Path $root 'game4'; NewGame $g4
    $c = L $g4 @('install'); $nb = @(Get-ChildItem "$data\backups" -Directory | Where-Object { $_.Name -notlike 'install-*' -and $_.Name -notlike 'uninstall-*' }).Count
    $c = L $g4 @('join', '--host', '127.0.0.1', '--port', '37730', '--code-stdin') $codeShown
    $a = @(ArgLines $g4)
    Check 'Join on a separate PC -> client started with the sandbox data dir, own saves backed up first' ($c -eq 0 -and $a[-1] -match '-ts2mp-role client' -and @(Get-ChildItem "$data\backups" -Directory | Where-Object { $_.Name -notlike 'install-*' -and $_.Name -notlike 'uninstall-*' }).Count -eq $nb + 1)
    KillFakes
    Check 'save isolation: the test saves are byte-identical after every launch' ((Sha "$localLow\$product\saveData.thief") -eq $saveHash)

    # ---------------- uninstall (owned BepInEx) ----------------
    New-Item -ItemType Directory -Force "$g1\BepInEx\config", "$g1\BepInEx\cache", "$g1\BepInEx\plugins\Other" | Out-Null
    'log' | Set-Content "$g1\BepInEx\LogOutput.log"; 'cfg' | Set-Content "$g1\BepInEx\config\BepInEx.cfg"
    'cfg' | Set-Content "$g1\BepInEx\config\com.ts2mp.multiplayer.cfg"; 'c' | Set-Content "$g1\BepInEx\cache\chainloader_typeloader.dat"
    'other mod' | Set-Content "$g1\BepInEx\plugins\Other\other.dll"
    'user edit' | Add-Content "$g1\doorstop_config.ini"
    $c = L $g1 @('uninstall')
    Check 'uninstall without --yes in a non-interactive run -> cancelled (exit 13), nothing removed' ($c -eq 13 -and (Test-Path "$g1\winhttp.dll"))
    $c = L $g1 @('uninstall', '--yes')
    $left = @(Get-ChildItem $g1 -Recurse -File | ForEach-Object { $_.FullName.Substring($g1.Length + 1) } | Sort-Object)
    $expectLeft = @('BepInEx\plugins\Other\other.dll', 'doorstop_config.ini', 'fakegame-args.txt', 'fakegame-env.txt', 'Thief Simulator 2.exe', 'Thief Simulator 2_Data\app.info', 'Thief Simulator 2_Data\data.unity3d') | Sort-Object
    Check 'uninstall -> exit 0; only mod-owned files removed; other mods, user-edited file and game files kept' ($c -eq 0 -and (($left -join '|') -eq ($expectLeft -join '|')))
    $ub = @(Get-ChildItem "$data\backups" -Directory -Filter 'uninstall-*' | Sort-Object Name)[-1].FullName
    Check 'uninstall backed up every removed file (plugin, BepInEx core, configs)' ((Test-Path "$ub\game\BepInEx\plugins\TS2MP\TS2MP.Plugin.dll") -and (Test-Path "$ub\game\BepInEx\core\BepInEx.dll") -and (Test-Path "$ub\game\BepInEx\config\com.ts2mp.multiplayer.cfg") -and (Test-Path "$ub\game\winhttp.dll"))
    Check 'game exe + data untouched after uninstall' ((Sha "$g1\Thief Simulator 2.exe") -eq $exeHash -and (Sha "$g1\Thief Simulator 2_Data\data.unity3d") -eq $dataHash)
    $c = L $g1 @('host', '--port', '37740', '--bind', '127.0.0.1')
    Check 'Host Game after uninstall -> refused (exit 2)' ($c -eq 2)
}
finally {
    KillFakes
    if (Test-Path $localLow) { Remove-Item $localLow -Recurse -Force }
    Write-Host "Installer test data kept in $root"
}
Write-Host "RESULT: $script:pass passed, $script:fail failed"
if ($script:fail) { exit 1 } else { exit 0 }
