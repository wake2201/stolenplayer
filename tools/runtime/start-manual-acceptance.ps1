param([switch]$SkipBuild, [switch]$PrepareOnly)
. "$PSScriptRoot/common.ps1"
if (Get-Process -Name 'Thief Simulator 2' -ErrorAction SilentlyContinue) { throw 'Close the game before preparing/deploying manual acceptance.' }
if (!$SkipBuild) { & "$PSScriptRoot/../build-multiplayer.ps1" -NoRestore }
& "$PSScriptRoot/../validate-multiplayer.ps1"
& "$PSScriptRoot/../deploy-multiplayer.ps1"
$s1Id = 's1-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$s1Dir = Assert-LocalOutput (Join-Path $script:RepoRoot ('.local/manual-acceptance/'+$s1Id))
New-Item -ItemType Directory -Path $s1Dir | Out-Null
$s1Config = Join-Path (Get-GamePath) 'BepInEx/config/local.thiefsim2.multiplayer.cfg'
$s1HadConfig = Test-Path -LiteralPath $s1Config
if ($s1HadConfig) { Copy-Item -LiteralPath $s1Config -Destination (Join-Path $s1Dir 'multiplayer-before.cfg') }
# Keep config changes inside the reversible session. No automatic gameplay/navigation.
$s1Text = if ($s1HadConfig) { Get-Content -LiteralPath $s1Config -Raw } else { '' }
if ($s1Text -notmatch '(?m)^\[Diagnostics\]') { $s1Text += "`n[Diagnostics]`n" }
foreach ($s1Pair in @(@('ManualSessionId',$s1Id), @('DevelopmentControls','false'), @('VerboseTransforms','false'))) {
    $s1Pattern = '(?m)^' + $s1Pair[0] + '\s*=.*$'
    if ($s1Text -match $s1Pattern) { $s1Text = [regex]::Replace($s1Text,$s1Pattern,($s1Pair[0]+' = '+$s1Pair[1])) }
    else { $s1Text = [regex]::Replace($s1Text,'(?m)^\[Diagnostics\]',("[Diagnostics]`n"+$s1Pair[0]+' = '+$s1Pair[1])) }
}
$s1State = [ordered]@{ TestSessionId=$s1Id; Directory=$s1Dir; Config=$s1Config; HadConfig=$s1HadConfig; ConfigRestored=$false; RuntimeSession=$null; Status='MANUAL_REQUIRED'; GameLaunches=0 }
Copy-Item -LiteralPath (Join-Path $script:RepoRoot 'tools/runtime/manual-tests-s1.json') -Destination (Join-Path $s1Dir 'tests.json')
$s1Instructions = @'
MANUAL_REQUIRED. No gameplay actions are automated.
Use your usual initialized save; load one gameplay map. F8 > Host (9050), then close F8.
From another terminal start: ./tools/runtime/start-avatar-harness.ps1 -Sequence s1 -Seconds 900 -Label s1-manual
Harness is a synthetic observer/echo, not a second native game. It echoes crouch/vehicle state.
Ctrl+F10 = cheap MARK. Ctrl+F7 = one-shot SNAPSHOT (do outside timed windows).
Ctrl+F6 = 10-second performance window; keep scene, camera, pause, graphics and F8 constant.
PERF: fixed camera, F8 OFF, Ctrl+F6, wait for PROFILE_END. F8 ON, Ctrl+F6, wait. Repeat OFF/ON.
CROUCH: MARK standing, crouch idle, move briefly crouched, stand, walk; MARK at each state. SNAPSHOT each idle state.
VEHICLE: walk to a supported car yourself; MARK, enter, MARK/SNAPSHOT, exit, MARK/SNAPSHOT.
SCENE: MARK/SNAPSHOT; host chooses another supported area; wait for WorldReady; MARK/SNAPSHOT. Never reconnect manually.
JOIN: requires another legitimate native client or a captured-manifest harness host; prove automatic native client follow separately.
Write visible observations and frame/camera context in observations.json beside tests.json. Missing observations stay MANUAL_REQUIRED.
Close game, then collect with ./tools/runtime/collect-manual-acceptance.ps1 -AcceptanceSession <this directory> -Restore
Do not claim vehicle physics/passengers, full crouch animation, objectives/rewards or two-game acceptance.
'@
$s1Instructions | Set-Content -LiteralPath (Join-Path $s1Dir 'INSTRUCTIONS.txt') -Encoding utf8
@{ Tester=''; Cases=@(); BeforePerformance=$null; Notes='' } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $s1Dir 'observations.json') -Encoding utf8
try {
    if (!$PrepareOnly) { & "$PSScriptRoot/playerprefs.ps1" -Action Backup -Directory $s1Dir }
    if (!$PrepareOnly) { Set-Content -LiteralPath $s1Config -Value $s1Text -Encoding utf8; $s1Launch = @(& "$PSScriptRoot/launch-game.ps1" -Mode Multiplayer -MonitorSeconds 900); $s1State.RuntimeSession = [string]$s1Launch[-1]; $s1State.GameLaunches=1 }
    else { $s1State.ConfigRestored=$true }
    $s1State | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $s1Dir 'acceptance.json') -Encoding utf8
} catch {
    if ($s1HadConfig) { Copy-Item -LiteralPath (Join-Path $s1Dir 'multiplayer-before.cfg') -Destination $s1Config -Force }
    elseif (Test-Path -LiteralPath $s1Config) { Remove-Item -LiteralPath $s1Config }
    if (Test-Path -LiteralPath (Join-Path $s1Dir 'playerprefs-before.clixml')) { & "$PSScriptRoot/playerprefs.ps1" -Action Restore -Directory $s1Dir }
    throw
}
Write-Output "MANUAL_REQUIRED: $s1Dir"
Write-Output "Instructions: $(Join-Path $s1Dir 'INSTRUCTIONS.txt')"
if ($PrepareOnly) { Write-Output 'Prepared/deployed, no game launched. Run the start script again without -PrepareOnly when ready.' }
