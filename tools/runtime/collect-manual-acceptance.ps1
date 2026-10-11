param([Parameter(Mandatory)][string]$AcceptanceSession, [switch]$Restore)
. "$PSScriptRoot/common.ps1"
$s1Dir = Assert-LocalOutput ([IO.Path]::GetFullPath($AcceptanceSession))
$s1File = Join-Path $s1Dir 'acceptance.json'
$s1State = Get-Content -LiteralPath $s1File -Raw | ConvertFrom-Json
if ($s1State.Directory -ne $s1Dir) { throw 'Acceptance session identity mismatch.' }
$s1Log = ''
if ($s1State.RuntimeSession) {
    & "$PSScriptRoot/collect-runtime.ps1" -Session $s1State.RuntimeSession
    $s1Bep = Join-Path $s1State.RuntimeSession 'BepInEx.log'
    if (Test-Path -LiteralPath $s1Bep) { $s1Log = Get-Content -LiteralPath $s1Bep -Raw; Copy-Item -LiteralPath $s1Bep -Destination (Join-Path $s1Dir 'BepInEx.log') -Force }
}
$s1Lines = @($s1Log -split '\r?\n' | Where-Object { $_ -match 'MANUAL_CHECKPOINT|MANUAL_PROFILE|MANUAL_SUBSYSTEM|LEGACY_F8|ONE_SHOT_|SCENE |World sync:|Integration stopped' })
$s1Lines | Set-Content -LiteralPath (Join-Path $s1Dir 'evidence.txt') -Encoding utf8
$s1Tests = Get-Content -LiteralPath (Join-Path $s1Dir 'tests.json') -Raw | ConvertFrom-Json
$s1Observations = Get-Content -LiteralPath (Join-Path $s1Dir 'observations.json') -Raw | ConvertFrom-Json
$s1Result = @($s1Tests | ForEach-Object {
    $s1Case = $_; $s1Human = @($s1Observations.Cases | Where-Object { $_.Id -eq $s1Case.Id })
    [pscustomobject]@{ Id=$s1Case.Id; Class='MANUAL'; Status='MANUAL_REQUIRED'; HumanEvidence=$s1Human; Reason='Human observations and corresponding logs require review; collection does not auto-accept.' }
})
$s1Result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $s1Dir 'results.json') -Encoding utf8
if ($Restore) {
    if (Get-Process -Name 'Thief Simulator 2' -ErrorAction SilentlyContinue) { throw 'Close the game before restoration.' }
    if ($s1State.RuntimeSession) { & "$PSScriptRoot/stop-game.ps1" -Session $s1State.RuntimeSession; & "$PSScriptRoot/restore-save.ps1" -Session $s1State.RuntimeSession -RestoreFileSet }
    if (Test-Path -LiteralPath (Join-Path $s1Dir 'playerprefs-before.clixml')) { & "$PSScriptRoot/playerprefs.ps1" -Action Restore -Directory $s1Dir }
    $s1ExpectedConfig = Join-Path (Get-GamePath) 'BepInEx/config/local.thiefsim2.multiplayer.cfg'
    if ($s1State.Config -ne $s1ExpectedConfig) { throw 'Unexpected config restore path.' }
    if (!$s1State.ConfigRestored) {
        if ($s1State.HadConfig) { Copy-Item -LiteralPath (Join-Path $s1Dir 'multiplayer-before.cfg') -Destination $s1ExpectedConfig -Force; if ((Get-FileHash -LiteralPath $s1ExpectedConfig).Hash -ne (Get-FileHash -LiteralPath (Join-Path $s1Dir 'multiplayer-before.cfg')).Hash) { throw 'Config restoration hash mismatch.' } }
        elseif (Test-Path -LiteralPath $s1ExpectedConfig) { Remove-Item -LiteralPath $s1ExpectedConfig }
        $s1State.ConfigRestored=$true; $s1State | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $s1File -Encoding utf8
    }
}
& "$PSScriptRoot/../reverse/fingerprint-game.ps1" -Verify
Write-Output "Collected $($s1Lines.Count) evidence lines; all manual gates require review. Results: $(Join-Path $s1Dir 'results.json')"
