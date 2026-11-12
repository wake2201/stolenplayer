# Developer launcher tests (N1, kept for the developer two-PC / local workflow in N1.2): fixed-role host / join launches
# (join code only in the child environment, never on the command line or in logs/state/ini), refusals, port conflict,
# save backup before a separate-PC join, test saves untouched. Fake game folder (FakeGame.exe renamed to
# "Thief Simulator 2.exe" + placeholder mod files). Never touches the real game or real saves.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$protocolSrc = Get-Content (Join-Path $repo 'src\Shared\Protocol.cs') -Raw
$ver = [regex]::Match($protocolSrc, 'ModVersion = "([^"]+)"').Groups[1].Value
$launcherExe = Join-Path $repo 'build\TS2-Multiplayer-Launcher.exe'
$fakeExe = Join-Path $repo 'build\tests\FakeGame.exe'
$root = Join-Path $env:TEMP ("ts2mp-launcher-dev-test-" + (Get-Date -Format yyyyMMdd-HHmmss))
$company = 'TS2MP-InstTest'; $product = 'FakeGame'
$localLow = Join-Path ([Environment]::GetFolderPath('LocalApplicationData') + 'Low') $company
$data = Join-Path $root 'data'
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
    New-Item -ItemType Directory -Force "$dir\BepInEx\core", "$dir\BepInEx\plugins\TS2MP" | Out-Null
    'x' | Set-Content "$dir\winhttp.dll"; 'x' | Set-Content "$dir\BepInEx\core\BepInEx.dll"; 'x' | Set-Content "$dir\BepInEx\plugins\TS2MP\TS2MP.Plugin.dll"
}
# Runs the launcher; returns the exit code. Output in $script:out.
function L([string]$game, [string[]]$launcherArgs, [string]$stdin = $null, [string]$exe = $launcherExe) {
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

if (Fakes) { Write-Host 'A process named "Thief Simulator 2" is running; close it before running launcher tests.'; exit 2 }
if (Test-Path $localLow) { Write-Host "Test save folder $localLow already exists; aborting to avoid touching it."; exit 2 }

try {
    New-Item -ItemType Directory -Force $root, $data | Out-Null
    New-Item -ItemType Directory -Force "$localLow\$product" | Out-Null
    'save-a' | Set-Content "$localLow\$product\saveData.thief"
    $saveHash = Sha "$localLow\$product\saveData.thief"

    # ---------------- Host Game / Join Game ----------------
    $g1 = Join-Path $root 'game1'; NewGame $g1
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
    $nb = @(Get-ChildItem "$data\backups" -Directory | Where-Object { $_.Name -notlike 'install-*' -and $_.Name -notlike 'uninstall-*' }).Count
    $c = L $g4 @('join', '--host', '127.0.0.1', '--port', '37730', '--code-stdin') $codeShown
    $a = @(ArgLines $g4)
    Check 'Join on a separate PC -> client started with the sandbox data dir, own saves backed up first' ($c -eq 0 -and $a[-1] -match '-ts2mp-role client' -and @(Get-ChildItem "$data\backups" -Directory | Where-Object { $_.Name -notlike 'install-*' -and $_.Name -notlike 'uninstall-*' }).Count -eq $nb + 1)
    KillFakes
    Check 'save isolation: the test saves are byte-identical after every launch' ((Sha "$localLow\$product\saveData.thief") -eq $saveHash)

}
finally {
    KillFakes
    if (Test-Path $localLow) { Remove-Item $localLow -Recurse -Force }
    Write-Host "Launcher dev test data kept in $root"
}
Write-Host "RESULT: $script:pass passed, $script:fail failed"
if ($script:fail) { exit 1 } else { exit 0 }
