# Launcher tests against a fake game folder (FakeGame.exe renamed to "Thief Simulator 2.exe"). Never touches the real game.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $repo 'build\TS2-Multiplayer-Launcher.exe'
$fakeExe = Join-Path $repo 'build\tests\FakeGame.exe'
$root = Join-Path $env:TEMP ("ts2mp-launcher-test-" + (Get-Date -Format yyyyMMdd-HHmmss))
$game = Join-Path $root 'game'; $data = Join-Path $root 'data'
$company = 'TS2MP-Test'; $product = 'FakeGame'
$localLow = Join-Path ([Environment]::GetFolderPath('LocalApplicationData') + 'Low') $company
$script:fail = 0; $script:pass = 0

function Check($name, $cond) { if ($cond) { $script:pass++; Write-Host "PASS  $name" } else { $script:fail++; Write-Host "FAIL  $name" } }
function Launch { $p = Start-Process -FilePath $launcher -ArgumentList @('--no-pause', '--game', "`"$game`"", '--data-dir', "`"$data`"") -PassThru -WindowStyle Hidden; $p.WaitForExit(); return $p.ExitCode }
function Fakes { @(Get-Process -Name 'Thief Simulator 2' -ErrorAction SilentlyContinue) }
function ArgLines { if (Test-Path "$game\fakegame-args.txt") { @(Get-Content "$game\fakegame-args.txt") } else { @() } }
function RoleOf($line) { if ($line -match '-ts2mp-role (\w+)') { $Matches[1] } }
function KillPid($id) { Stop-Process -Id $id -Force; Start-Sleep -Milliseconds 500 }

if (Fakes) { Write-Host 'A process named "Thief Simulator 2" is running; close it before running launcher tests.'; exit 2 }
if (Test-Path $localLow) { Write-Host "Test save folder $localLow already exists; aborting to avoid touching it."; exit 2 }
try {
    New-Item -ItemType Directory -Force "$game\Thief Simulator 2_Data", "$game\BepInEx\core", "$game\BepInEx\plugins\TS2MP", $data | Out-Null
    Copy-Item $fakeExe "$game\Thief Simulator 2.exe"
    'x' | Set-Content "$game\winhttp.dll"; 'x' | Set-Content "$game\BepInEx\core\BepInEx.dll"; 'x' | Set-Content "$game\BepInEx\plugins\TS2MP\TS2MP.Plugin.dll"
    "$company`n$product" | Set-Content "$game\Thief Simulator 2_Data\app.info"
    New-Item -ItemType Directory -Force "$localLow\$product\backups" | Out-Null
    'save-a' | Set-Content "$localLow\$product\saveData.thief"; 'save-b' | Set-Content "$localLow\$product\backups\saveData_backup1.thief"

    $c = Launch; $l = @(ArgLines)
    Check 'launch 1 -> exit 0, role host' ($c -eq 0 -and $l.Count -eq 1 -and (RoleOf $l[0]) -eq 'host')
    $bk = @(Get-ChildItem "$data\backups" -Recurse -File -ErrorAction SilentlyContinue)
    Check 'host launch made verified save backup (2 files)' ($bk.Count -eq 2)
    $c = Launch; $l = @(ArgLines)
    Check 'launch 2 -> exit 0, role client' ($c -eq 0 -and $l.Count -eq 2 -and (RoleOf $l[1]) -eq 'client')
    $s0 = [regex]::Match($l[0], '-ts2mp-session (\w+).*-ts2mp-port (\d+)'); $s1 = [regex]::Match($l[1], '-ts2mp-session (\w+).*-ts2mp-port (\d+)')
    Check 'client got same session id and port' ($s0.Success -and $s0.Groups[1].Value -eq $s1.Groups[1].Value -and $s0.Groups[2].Value -eq $s1.Groups[2].Value)
    Check 'host and client have different instance ids and log dirs' (([regex]::Match($l[0], '-ts2mp-instance (\w+)').Groups[1].Value) -ne ([regex]::Match($l[1], '-ts2mp-instance (\w+)').Groups[1].Value))
    $c = Launch
    Check 'launch 3 -> refused (session full, exit 4), no third process' ($c -eq 4 -and (Fakes).Count -eq 2)

    $clientPid = [int]($l[1] -split ' ')[0]; $hostPid = [int]($l[0] -split ' ')[0]
    KillPid $clientPid
    $c = Launch; $l = @(ArgLines)
    Check 'client restart -> role client, same session' ($c -eq 0 -and (RoleOf $l[2]) -eq 'client' -and $l[2] -match "-ts2mp-session $($s0.Groups[1].Value)")
    $clientPid = [int]($l[2] -split ' ')[0]
    KillPid $hostPid
    $c = Launch
    Check 'host gone but client alive -> refused (exit 9)' ($c -eq 9)
    KillPid $clientPid

    $c = Launch; $l = @(ArgLines)
    Check 'after full shutdown -> new host session' ($c -eq 0 -and (RoleOf $l[3]) -eq 'host' -and $l[3] -notmatch "-ts2mp-session $($s0.Groups[1].Value)")
    Fakes | Stop-Process -Force; Start-Sleep -Milliseconds 500

    $u = Start-Process "$game\Thief Simulator 2.exe" -PassThru
    Start-Sleep -Milliseconds 500
    $c = Launch
    Check 'unmanaged game instance running -> refused (exit 3)' ($c -eq 3)
    KillPid $u.Id

    # rapid double launch
    $before = (ArgLines).Count
    $a1 = Start-Process -FilePath $launcher -ArgumentList @('--no-pause', '--game', "`"$game`"", '--data-dir', "`"$data`"") -PassThru -WindowStyle Hidden
    $a2 = Start-Process -FilePath $launcher -ArgumentList @('--no-pause', '--game', "`"$game`"", '--data-dir', "`"$data`"") -PassThru -WindowStyle Hidden
    $a1.WaitForExit(); $a2.WaitForExit()
    $new = @(ArgLines | Select-Object -Skip $before)
    $roles = @($new | ForEach-Object { RoleOf $_ }) | Sort-Object
    Check 'two simultaneous launches -> exactly one host + one client' ($a1.ExitCode -eq 0 -and $a2.ExitCode -eq 0 -and ($roles -join ',') -eq 'client,host')
    Fakes | Stop-Process -Force; Start-Sleep -Milliseconds 500

    # N1: Local Test stays loopback-only without a join code; the M2 LAN keys are refused with a pointer to Host/Join
    function LaunchIni($ini) { Set-Content -Encoding ASCII "$root\test.ini" $ini; $p = Start-Process -FilePath $launcher -ArgumentList @('--no-pause', '--config', "`"$root\test.ini`"", '--game', "`"$game`"", '--data-dir', "`"$data`"") -PassThru -WindowStyle Hidden; $p.WaitForExit(); return $p.ExitCode }
    function EnvLines { if (Test-Path "$game\fakegame-env.txt") { @(Get-Content "$game\fakegame-env.txt") } else { @() } }
    $before = (ArgLines).Count
    $c = LaunchIni "Role=banana"
    Check 'legacy Role key -> refused (exit 2), nothing started' ($c -eq 2 -and (ArgLines).Count -eq $before)
    $c = LaunchIni "HostAddress=10.0.0.5"
    Check 'legacy HostAddress key -> refused (exit 2)' ($c -eq 2 -and (ArgLines).Count -eq $before)
    $c = LaunchIni "SessionKey=test-key"
    Check 'legacy SessionKey -> refused (exit 2)' ($c -eq 2 -and (ArgLines).Count -eq $before)
    $c = LaunchIni "BindAddress=192.168.1.50"; $l = @(ArgLines); $e = @(EnvLines)
    Check 'Local Test ignores the Host Game bind address: loopback bind/connect' ($c -eq 0 -and $l[-1] -match '-ts2mp-bind 127\.0\.0\.1 -ts2mp-connect 127\.0\.0\.1')
    Check 'Local Test passes no join code to the game' ($e.Count -gt 0 -and $e[-1] -match ' none$')
    $c = LaunchIni "BindAddress=192.168.1.50"; $l = @(ArgLines)
    Check 'second Local Test launch -> client, same session' ($c -eq 0 -and (RoleOf $l[-1]) -eq 'client' -and (RoleOf $l[-2]) -eq 'host')
    Fakes | Stop-Process -Force; Start-Sleep -Milliseconds 500

    $p = Start-Process -FilePath $launcher -ArgumentList @('--no-pause', '--game', "`"$root\nope`"", '--data-dir', "`"$data`"") -PassThru -WindowStyle Hidden; $p.WaitForExit()
    Check 'missing game folder -> config error (exit 2)' ($p.ExitCode -eq 2)
    Remove-Item "$game\BepInEx\plugins\TS2MP\TS2MP.Plugin.dll"
    $c = Launch
    Check 'missing plugin -> config error (exit 2)' ($c -eq 2)
    $orig = Get-Content "$localLow\$product\saveData.thief"
    Check 'real (test) save files untouched by launcher' ($orig -eq 'save-a')
    Check 'launcher.log written' (Test-Path "$data\logs\launcher.log")
}
finally {
    Fakes | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
    if (Test-Path $localLow) { Remove-Item $localLow -Recurse -Force }
    Write-Host "Launcher test data kept in $root"
}
Write-Host "RESULT: $script:pass passed, $script:fail failed"
if ($script:fail) { exit 1 } else { exit 0 }
