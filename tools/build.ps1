# Builds launcher, plugin and tests with the .NET Framework C# compiler (no .NET SDK needed).
#   tools\build.ps1 [-GamePath <folder>] [-Deploy] [-Test]
#   -Deploy  copies plugin + LiteNetLib into <game>\BepInEx\plugins\TS2MP\ (requires BepInEx installed)
#   -Test    runs the offline protocol tests and launcher tests afterwards
param([string]$GamePath, [switch]$Deploy, [switch]$Test)
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

# Launcher: plain .NET Framework 4.x console exe.
Invoke-Csc 'TS2-Multiplayer-Launcher.exe' @('/target:exe', '/platform:anycpu', "/out:$BuildDir\TS2-Multiplayer-Launcher.exe", "$src\Launcher\Launcher.cs")

# Tests (offline)
Invoke-Csc 'TS2MP.ProtocolTests.exe' (@('/target:exe', "/out:$outTests\TS2MP.ProtocolTests.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\ProtocolTests.cs"))
Copy-Item $lnl $outTests -Force
Invoke-Csc 'TS2MP.SyncTests.exe' (@('/target:exe', "/out:$outTests\TS2MP.SyncTests.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\SyncTests.cs"))
Invoke-Csc 'TS2MP.WorldTests.exe' (@('/target:exe', "/out:$outTests\TS2MP.WorldTests.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\WorldTests.cs"))
Invoke-Csc 'TS2MP.VehicleTests.exe' (@('/target:exe', "/out:$outTests\TS2MP.VehicleTests.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\VehicleTests.cs"))
Invoke-Csc 'TS2MP.TravelTests.exe' (@('/target:exe', "/out:$outTests\TS2MP.TravelTests.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\TravelTests.cs"))
Invoke-Csc 'TS2MP.HandoffTests.exe' (@('/target:exe', "/out:$outTests\TS2MP.HandoffTests.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\HandoffTests.cs"))
Invoke-Csc 'TS2MP.PassengerTests.exe' (@('/target:exe', "/out:$outTests\TS2MP.PassengerTests.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\PassengerTests.cs"))
Invoke-Csc 'TS2MP.InteractionTests.exe' (@('/target:exe', "/out:$outTests\TS2MP.InteractionTests.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\InteractionTests.cs"))
Invoke-Csc 'TS2MP.CarBoxTests.exe' (@('/target:exe', "/out:$outTests\TS2MP.CarBoxTests.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\CarBoxTests.cs"))
Invoke-Csc 'TS2MP.Player2Tests.exe' (@('/target:exe', "/out:$outTests\TS2MP.Player2Tests.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\Player2Tests.cs"))
Invoke-Csc 'TS2MP.Player2LifecycleTests.exe' (@('/target:exe', "/out:$outTests\TS2MP.Player2LifecycleTests.exe", "/r:$lnl") + $shared + @("$RepoRoot\tests\Player2LifecycleTests.cs"))
Invoke-Csc 'FakeGame.exe' @('/target:exe', "/out:$outTests\FakeGame.exe", "$RepoRoot\tests\FakeGame.cs")
Invoke-Csc 'TS2MP.RedirectTest.exe' (@('/target:exe', '/platform:x64', "/out:$outTests\TS2MP.RedirectTest.exe", "/r:$core\0Harmony.dll", "/r:$lnl") + $shared + @("$src\Plugin\PathRedirect.cs", "$RepoRoot\tests\RedirectTest.cs"))
Copy-Item "$core\*.dll" $outTests -Force

$ini = Join-Path $BuildDir 'TS2-Multiplayer-Launcher.ini'
if (-not (Test-Path $ini)) {
    Set-Content -Encoding ASCII $ini "; TS2 Multiplayer launcher settings`r`n; Folder that contains 'Thief Simulator 2.exe'`r`nGamePath=$game`r`n; Logs, backups, session state, client save sandbox`r`nDataDir=%LOCALAPPDATA%\TS2MP`r`n; First UDP port tried on 127.0.0.1`r`nBasePort=27960"
    Write-Host "Created $ini"
}

if ($Deploy) {
    if (-not (Test-Path (Join-Path $game 'BepInEx\core\BepInEx.dll'))) { throw "BepInEx not installed in $game. Run tools\bepinex.ps1 -Install first." }
    $dest = Join-Path $game 'BepInEx\plugins\TS2MP'
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item "$outPlugin\TS2MP.Plugin.dll", "$outPlugin\LiteNetLib.dll" $dest -Force
    Write-Host "Deployed plugin to $dest"
}

if ($Test) {
    Write-Host "== protocol tests"
    & "$outTests\TS2MP.ProtocolTests.exe"
    if ($LASTEXITCODE -ne 0) { throw "protocol tests failed" }
    Write-Host "== sync tests"
    & "$outTests\TS2MP.SyncTests.exe"
    if ($LASTEXITCODE -ne 0) { throw "sync tests failed" }
    Write-Host "== world tests"
    & "$outTests\TS2MP.WorldTests.exe"
    if ($LASTEXITCODE -ne 0) { throw "world tests failed" }
    Write-Host "== vehicle tests"
    & "$outTests\TS2MP.VehicleTests.exe"
    if ($LASTEXITCODE -ne 0) { throw "vehicle tests failed" }
    Write-Host "== travel tests"
    & "$outTests\TS2MP.TravelTests.exe"
    if ($LASTEXITCODE -ne 0) { throw "travel tests failed" }
    Write-Host "== handoff tests (M4.2.2)"
    & "$outTests\TS2MP.HandoffTests.exe"
    if ($LASTEXITCODE -ne 0) { throw "handoff tests failed" }
    Write-Host "== passenger tests (M4.3)"
    & "$outTests\TS2MP.PassengerTests.exe"
    if ($LASTEXITCODE -ne 0) { throw "passenger tests failed" }
    Write-Host "== interaction tests (M4.3.1)"
    & "$outTests\TS2MP.InteractionTests.exe"
    if ($LASTEXITCODE -ne 0) { throw "interaction tests failed" }
    Write-Host "== carbox tests (M4.3.2b)"
    & "$outTests\TS2MP.CarBoxTests.exe"
    if ($LASTEXITCODE -ne 0) { throw "carbox tests failed" }
    Write-Host "== player2 tests (M4.3.3)"
    & "$outTests\TS2MP.Player2Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw "player2 tests failed" }
    Write-Host "== player2 lifecycle integration tests (M4.3.3 hotfix, message sequencing)"
    & "$outTests\TS2MP.Player2LifecycleTests.exe"
    if ($LASTEXITCODE -ne 0) { throw "player2 lifecycle tests failed" }
    Write-Host "== save redirect test (real game IL, outside Unity)"
    & "$outTests\TS2MP.RedirectTest.exe" $managed
    if ($LASTEXITCODE -ne 0) { throw "redirect test failed" }
    Write-Host "== launcher tests"
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$RepoRoot\tests\launcher-test.ps1"
    if ($LASTEXITCODE -ne 0) { throw "launcher tests failed" }
}
