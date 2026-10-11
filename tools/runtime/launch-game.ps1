param([ValidateSet('Vanilla','BepOnly','Diagnostics','Multiplayer','Both')][string]$Mode='Both',[switch]$Hidden,
    [ValidateRange(30,1800)][int]$MonitorSeconds=300)
. "$PSScriptRoot/common.ps1"
$game = Get-GamePath
$exe = Join-Path $game 'Thief Simulator 2.exe'
if (Get-Process -Name 'Thief Simulator 2' -ErrorAction SilentlyContinue) { throw 'Game already running.' }
& "$PSScriptRoot/../reverse/fingerprint-game.ps1" -Verify
$session = Join-Path $script:RepoRoot ('.local/runtime-sessions/' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + $Mode)
New-Item -ItemType Directory -Path $session -Force | Out-Null
$ini = Join-Path $game 'doorstop_config.ini'
$added = @(Get-Content (Join-Path $script:RepoRoot '.local/evidence/deployment-files.json') -Raw | ConvertFrom-Json)
if ($ini -notin $added) { throw 'Loader configuration is not a recorded addition.' }
$originals = @(Get-Content (Join-Path $script:RepoRoot '.local/evidence/original-binaries.json') -Raw | ConvertFrom-Json)
$disabled = @()
$restore = [ordered]@{ Doorstop=$ini; Disabled=@(); Restored=$false }
Copy-Item -LiteralPath $ini -Destination (Join-Path $session 'doorstop-original.ini')
# Save backups precede every launch, including baseline runs. Logs are collected separately.
$save = Join-Path $env:USERPROFILE 'AppData/LocalLow/CookieDev/Thief Simulator 2'
if (Test-Path -LiteralPath $save) {
    $saveBackup = Join-Path $session 'save-before'
    New-Item -ItemType Directory -Path $saveBackup | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $save -Recurse -File | Where-Object { $_.Extension -eq '.thief' -or $_.Name -like 'saveInfo*.txt' }) {
        $relative = $file.FullName.Substring($save.Length+1)
        $target = Join-Path $saveBackup $relative
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
try {
    $text = Get-Content -LiteralPath $ini -Raw
    $text = [regex]::Replace($text, '(?m)^enabled\s*=\s*(true|false)\s*$', ('enabled = ' + ($Mode -ne 'Vanilla').ToString().ToLowerInvariant()))
    Set-Content -LiteralPath $ini -Value $text -Encoding utf8
    foreach ($plugin in @('Diagnostics','Multiplayer')) {
        $enable = $Mode -eq 'Both' -or $Mode -eq $plugin
        $folder = 'ThiefSim2Multiplayer' + $(if ($plugin -eq 'Diagnostics') { '.Diagnostics' } else { '' })
        $path = Join-Path $game "BepInEx/plugins/$folder/$folder.dll"
        if (!$enable -and (Test-Path -LiteralPath $path)) {
            if ($path -in $originals.Path) { throw 'Refusing to move an original binary.' }
            $temporary = $path + '.ts2-disabled'
            if (Test-Path -LiteralPath $temporary) { throw 'Unrestored disabled plugin exists.' }
            Move-Item -LiteralPath $path -Destination $temporary
            $disabled += [pscustomobject]@{ Original=$path; Temporary=$temporary }
            $restore.Disabled = $disabled
        }
    }
    $restore | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $session 'restore.json') -Encoding utf8
    # Controlled fixtures use -Hidden; the existing manual launch remains interactive. Helpers stay hidden.
    $process = Start-Process -FilePath $exe -WorkingDirectory $game -WindowStyle $(if($Hidden){'Hidden'}else{'Normal'}) -PassThru
    $data = [ordered]@{ Mode=$Mode; ProcessId=$process.Id; Executable=$exe; StartUtc=$process.StartTime.ToUniversalTime().ToString('o'); Directory=$session }
    $data | ConvertTo-Json | Set-Content (Join-Path $session 'session.json') -Encoding utf8
    $monitor = Join-Path $PSScriptRoot 'monitor-memory.ps1'
    Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @('-NoProfile','-File',('"'+$monitor+'"'),'-Session',('"'+$session+'"'),'-Seconds',$MonitorSeconds) -RedirectStandardOutput (Join-Path $session 'monitor.log') -RedirectStandardError (Join-Path $session 'monitor-error.log') | Out-Null
    Write-Output $session
} catch {
    Copy-Item -LiteralPath (Join-Path $session 'doorstop-original.ini') -Destination $ini -Force
    foreach ($entry in $disabled) { if (Test-Path -LiteralPath $entry.Temporary) { Move-Item -LiteralPath $entry.Temporary -Destination $entry.Original } }
    throw
}
