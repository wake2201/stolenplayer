param([Parameter(Mandatory)][string]$Session,
    [ValidateRange(10,20)][int]$Cycles=10,
    [ValidateRange(180,180)][int]$CycleSeconds=180)
. "$PSScriptRoot/common.ps1"
$data = Get-RuntimeSession $Session
if (!(Get-SessionProcess $data)) { throw 'Matching live game required.' }
$output = Join-Path $script:RepoRoot '.local/evidence/avatar'
$records = Join-Path $output ('soak-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.ndjson')
$hash = (Get-FileHash -LiteralPath (Join-Path (Get-ManagedPath) 'Assembly-CSharp.dll')).Hash
$harness = Join-Path $script:RepoRoot 'tools/TransportHarness/bin/Release/net9.0/TransportHarness.dll'
$process = $null
$completed = $false
function Request-SoakAudit([string]$Action) {
    $request = Join-Path $script:RepoRoot '.local/runtime-dumps/request.txt'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (Test-Path -LiteralPath $request) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Diagnostic request stalled.' }
        Start-Sleep -Milliseconds 250
    }
    & "$PSScriptRoot/milestone-b-fixture.ps1" -Session $Session -Action $Action | Out-Null
}
try {
for ($cycle=1; $cycle -le $Cycles; $cycle++) {
    if (!(Get-SessionProcess $data)) { throw 'Game ended during soak.' }
    Request-SoakAudit host
    Start-Sleep -Seconds 2
    $log = Join-Path $output "mb-soak-cycle-$cycle.log"
    $process = Start-Process -FilePath (Get-LocalDotnet) -ArgumentList @(('"'+$harness+'"'),'client','127.0.0.1',9050,$hash,$CycleSeconds,'MainScene|MadisonSt','soak',5,0) -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError (Join-Path $output "mb-soak-cycle-$cycle-error.log")
    $timer = [Diagnostics.Stopwatch]::StartNew()
    foreach ($second in @(5,32,38,80,132,175)) {
        while ($timer.Elapsed.TotalSeconds -lt $second) {
            if ($process.HasExited -or !(Get-SessionProcess $data)) { throw 'Game/harness ended before bounded cycle completed.' }
            Start-Sleep -Seconds 1
        }
        Request-SoakAudit audit
        $gameProcess = Get-SessionProcess $data
        [ordered]@{ utc=[DateTime]::UtcNow.ToString('o'); cycle=$cycle; second=$second; workingSetBytes=$gameProcess.WorkingSet64; privateBytes=$gameProcess.PrivateMemorySize64 } | ConvertTo-Json -Compress | Add-Content -LiteralPath $records
    }
    if (!$process.WaitForExit(15000)) { throw 'Harness did not exit within its bound.' }
    if ($process.ExitCode -ne 0) { throw "Harness cycle $cycle failed; inspect $log" }
    Start-Sleep -Seconds 2
    Request-SoakAudit audit
    Write-Output "Avatar soak cycle $cycle/$Cycles complete; bounded synthetic incoming states, genuine local player retained."
}
$completed = $true
Write-Output "Avatar soak complete; evidence=$records"
} finally {
    # Only terminate the hidden helper created by this run; never the game.
    if ($process) { $process.Refresh(); if (!$process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) } }
    if (!$completed -and (Get-SessionProcess $data)) { Request-SoakAudit disconnect }
}
