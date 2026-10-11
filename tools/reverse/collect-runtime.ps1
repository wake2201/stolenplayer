. "$PSScriptRoot/common.ps1"
$out=Join-Path $script:RepoRoot '.local/runtime-dumps'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$bepLog=Join-Path (Get-GamePath) 'BepInEx/LogOutput.log'
if (Test-Path -LiteralPath $bepLog) { Copy-Item -LiteralPath $bepLog -Destination (Join-Path $out 'BepInEx-LogOutput.log') -Force }
$files=@(Get-ChildItem -LiteralPath $out -Filter '*.ndjson')
if (!$files.Count) { throw 'No runtime snapshots yet. Launch the game with the deployed plugin.' }
$latest=$files | Sort-Object CreationTimeUtc -Descending | Select-Object -First 1
Write-Output "Reading $($latest.FullName)"
$rows=@(Get-Content -LiteralPath $latest.FullName | ForEach-Object { $_ | ConvertFrom-Json })
$rows | Group-Object event | Select-Object Name,Count | Format-Table -AutoSize
$players=@($rows | Where-Object event -eq 'player_candidate')
Write-Output "Player snapshots: $($players.Count)"
& "$PSScriptRoot/fingerprint-game.ps1" -Verify
