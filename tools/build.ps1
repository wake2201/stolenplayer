# Builds launcher, plugin and tests with the .NET Framework C# compiler (no .NET SDK needed).
#   tools\build.ps1 [-GamePath <folder>] [-Deploy] [-Test] [-Package]
#   -Deploy   copies plugin + LiteNetLib into <game>\BepInEx\plugins\TS2MP\ (requires BepInEx installed)
#   -Package  stages build\release\TS2-Multiplayer-<ver>-Windows\ and writes the verified release ZIP (tools\package.ps1)
#   -Test     runs all offline suites (implies -Package: the installer / package tests use the staged release)
param([string]$GamePath, [switch]$Deploy, [switch]$Test, [switch]$Package)
. "$PSScriptRoot\common.ps1"

$game    = Resolve-GamePath $GamePath
$managed = Join-Path $game 'Thief Simulator 2_Data\Managed'
$bep     = (Get-BepInExZip).Dir
$core    = Join-Path $bep 'BepInEx\core'
$csc     = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$lnl     = Join-Path $RepoRoot 'lib\LiteNetLib.dll'
$src     = Join-Path $RepoRoot 'src'
$shared  = @(Get-ChildItem "$src\Shared\*.cs" | ForEach-Object FullName)

$outPlugin = Join-Path $BuildDir 'plugin'
$outTests  = Join-Path $BuildDir 'tests'
New-Item -ItemType Directory -Force $outPlugin, $outTests | Out-Null

function Invoke-Csc([string]$name, [string[]]$cscArgs) {
    Write-Host "== csc $name"
    $out = & $csc /nologo /optimize+ /warn:4 /nowarn:1701,1702 @cscArgs 2>&1
    $out | ForEach-Object { Write-Host "   $_" }
    if ($LASTEXITCODE -ne 0) { throw "$name build failed (exit $LASTEXITCODE)" }
    $warn = @($out | Where-Object { $_ -match 'warning CS' }).Count
    Write-Host "   OK ($warn warning(s))"
}

# Plugin: compiled against the game's own Mono class libraries (Unity 2020.3, .NET 4.x profile).
Invoke-Csc 'TS2MP.Plugin.dll' (@('/target:library', '/noconfig', '/nostdlib+', "/out:$outPlugin\TS2MP.Plugin.dll",
    "/r:$managed\mscorlib.dll", "/r:$managed\System.dll", "/r:$managed\System.Core.dll",
    "/r:$managed\UnityEngine.dll", "/r:$managed\UnityEngine.CoreModule.dll", "/r:$managed\UnityEngine.IMGUIModule.dll",
    "/r:$managed\UnityEngine.AnimationModule.dll", "/r:$managed\UnityEngine.PhysicsModule.dll", "/r:$managed\UnityEngine.VehiclesModule.dll", "/r:$managed\UnityEngine.InputLegacyModule.dll", "/r:$managed\UnityEngine.TextRenderingModule.dll", "/r:$managed\UnityEngine.UIModule.dll", "/r:$managed\UnityEngine.AudioModule.dll",
    "/r:$core\BepInEx.dll", "/r:$core\0Harmony.dll", "/r:$lnl") + $shared + @(Get-ChildItem "$src\Plugin\*.cs" | ForEach-Object FullName))
Copy-Item $lnl $outPlugin -Force

# Developer launcher (local two-instance tests; not part of the player release): plain .NET Framework 4.x console exe,
# shares join-code / address validation with the plugin (src\Shared\JoinAuth.cs has no LiteNetLib dependency).
Invoke-Csc 'TS2-Multiplayer-Launcher.exe' @('/target:exe', '/platform:anycpu', "/out:$BuildDir\TS2-Multiplayer-Launcher.exe",
    "$src\Launcher\Launcher.cs", "$src\Shared\JoinAuth.cs")

# Tests (offline). Each entry: exe name, test source.
$suites = @(
    @('TS2MP.ProtocolTests', 'ProtocolTests.cs', 'protocol tests'),
    @('TS2MP.SyncTests', 'SyncTests.cs', 'sync tests'),
    @('TS2MP.WorldTests', 'WorldTests.cs', 'world tests'),
    @('TS2MP.VehicleTests', 'VehicleTests.cs', 'vehicle tests'),
    @('TS2MP.TravelTests', 'TravelTests.cs', 'travel tests'),
    @('TS2MP.HandoffTests', 'HandoffTests.cs', 'handoff tests (M4.2.2)'),
    @('TS2MP.PassengerTests', 'PassengerTests.cs', 'passenger tests (M4.3)'),
    @('TS2MP.InteractionTests', 'InteractionTests.cs', 'interaction tests (M4.3.1)'),
    @('TS2MP.CarBoxTests', 'CarBoxTests.cs', 'carbox tests (M4.3.2b)'),
    @('TS2MP.Player2Tests', 'Player2Tests.cs', 'player2 tests (M4.3.3)'),
    @('TS2MP.Player2LifecycleTests', 'Player2LifecycleTests.cs', 'player2 lifecycle integration tests (M4.3.3 hotfix, message sequencing)'),
    @('TS2MP.JointTravelTests', 'JointTravelTests.cs', 'joint travel tests (M4.3 Phase B)'),
    @('TS2MP.WorldIdentityTests', 'WorldIdentityTests.cs', 'world identity tests (M5 W1)'),
    @('TS2MP.WorldMirrorTests', 'WorldMirrorTests.cs', 'world-key mirror tests (M5 W2)'),
    @('TS2MP.W3Tests', 'W3Tests.cs', 'shared locker / dropped items / gear tests (M5 W3)'),
    @('TS2MP.DurabilityTests', 'DurabilityTests.cs', 'storage durability tests (M5 W3.4)'),
    @('TS2MP.WorldLootTests', 'WorldLootTests.cs', 'shared world loot tests (M5 W4.1)'),
    @('TS2MP.NetAuthTests', 'NetAuthTests.cs', 'direct connect / join-code auth tests (N1)'),
    @('TS2MP.LifecycleTests', 'LifecycleTests.cs', 'F8 session lifecycle / host gate / ClientReady / save isolation rules (N1.2)'),
    @('TS2MP.W42Tests', 'W42Tests.cs', 'host campaign bootstrap / Player-2 spawn (W4.2 Phase A)'),
    @('TS2MP.W42BTests', 'W42BTests.cs', 'shared doors / locks / windows, region epochs, initialization, reservations (W4.2B)')
)
foreach ($s in $suites) {
    Invoke-Csc "$($s[0]).exe" (@('/target:exe', "/out:$outTests\$($s[0]).exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\$($s[1])"))
}
Copy-Item $lnl $outTests -Force
Invoke-Csc 'TS2MP.NetSmoke.exe' (@('/target:exe', "/out:$outTests\TS2MP.NetSmoke.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\NetSmoke.cs"))
Invoke-Csc 'FakeGame.exe' @('/target:exe', "/out:$outTests\FakeGame.exe", "$RepoRoot\tests\FakeGame.cs")
Invoke-Csc 'TS2MP.RedirectTest.exe' (@('/target:exe', '/platform:x64', "/out:$outTests\TS2MP.RedirectTest.exe", "/r:$core\0Harmony.dll", "/r:$lnl") + $shared + @("$src\Plugin\PathRedirect.cs", "$RepoRoot\tests\RedirectTest.cs"))
Copy-Item "$core\*.dll" $outTests -Force

$ini = Join-Path $BuildDir 'TS2-Multiplayer-Launcher.ini'
if (-not (Test-Path $ini)) {
    Set-Content -Encoding ASCII $ini "; TS2 Multiplayer launcher settings`r`n; Folder that contains 'Thief Simulator 2.exe'`r`nGamePath=$game`r`n; Logs, backups, session state, client save sandbox`r`nDataDir=%LOCALAPPDATA%\TS2MP`r`n; First UDP port tried on 127.0.0.1`r`nBasePort=27960"
    Write-Host "Created $ini"
}

if ($Package -or $Test) {
    & "$PSScriptRoot\package.ps1"
    if ($LASTEXITCODE -ne 0) { throw "release packaging failed" }
}

if ($Deploy) {
    if (-not (Test-Path (Join-Path $game 'BepInEx\core\BepInEx.dll'))) { throw "BepInEx not installed in $game. Run tools\bepinex.ps1 -Install first." }
    $dest = Join-Path $game 'BepInEx\plugins\TS2MP'
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item "$outPlugin\TS2MP.Plugin.dll", "$outPlugin\LiteNetLib.dll" $dest -Force
    Write-Host "Deployed plugin to $dest"
}

if ($Test) {
    $ran = 0
    foreach ($s in $suites) {
        Write-Host "== $($s[2])"
        & "$outTests\$($s[0]).exe"
        if ($LASTEXITCODE -ne 0) { throw "$($s[2]) failed" }
        $ran++
    }
    Write-Host "== save redirect test (real game IL, outside Unity)"
    & "$outTests\TS2MP.RedirectTest.exe" $managed
    if ($LASTEXITCODE -ne 0) { throw "redirect test failed" }
    $ran++
    foreach ($t in @(@('launcher-test.ps1', 'launcher tests'), @('launcher-dev-test.ps1', 'developer launcher host/join tests (N1)'), @('package-test.ps1', 'drag-and-drop release package tests (N1.2)'), @('n1-smoke.ps1', 'two-process join-code smoke test (N1)'))) {
        Write-Host "== $($t[1])"
        & powershell -NoProfile -ExecutionPolicy Bypass -File "$RepoRoot\tests\$($t[0])"
        if ($LASTEXITCODE -ne 0) { throw "$($t[1]) failed" }
        $ran++
    }
    Write-Host "ALL $ran SUITES PASSED"
}
