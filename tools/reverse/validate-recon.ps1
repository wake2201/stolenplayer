. "$PSScriptRoot/common.ps1"
$failures=@()
foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..') -Recurse -Filter '*.ps1') {
    $tokens=$null; $parseErrors=$null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$parseErrors)
    if($parseErrors.Count) { $failures+="$($file.Name): $($parseErrors.Message -join '; ')" }
}
$samples=@('.local/game-path.txt','game-ref/Assembly-CSharp.dll','reverse-output/index.json','decompiled/PlayerScript.cs',
    'runtime-dumps/recon.ndjson','.local/runtime-sessions/test/save-before/saveData.thief','.local/runtime-dumps/avatar-visible.jpg','Assembly-CSharp.dll','UnityEngine.dll','steamapps/appmanifest_1332720.acf',
    'Thief Simulator 2_Data/Managed/Assembly-CSharp.dll','Thief Simulator 2_Data/level0','saveData.thief','BepInEx/core/BepInEx.dll')
foreach($sample in $samples) {
    & git -C $script:RepoRoot check-ignore --no-index --quiet -- $sample
    if($LASTEXITCODE -ne 0) { $failures+="Unprotected path: $sample" }
}
$tracked=@(& git -C $script:RepoRoot ls-files)
foreach($file in $tracked) {
    if($file -match '(?i)(^|/)(\.local|game-ref|reverse-output|decompiled|runtime-dumps|steamapps|BepInEx|[^/]*_Data)/|\.(dll|exe|pdb|assets|bundle|thief|sav|save|vdf|acf)$') {
        $failures+="Forbidden tracked file: $file"
    }
}
foreach($name in @('runtime-analysis.md','game-architecture.md','hook-candidates.md','multiplayer-feasibility.md','multiplayer-architecture.md','phase-1-plan.md',
    'avatar-candidates.md','avatar-catalog.md','player-animation-inventory.md','world-object-identity.md','world-interactions.md','pickup-transaction.md',
    'npc-multiplayer-analysis.md','vehicle-multiplayer-analysis.md','scene-sync-analysis.md','runtime-managers.md',
    'steam-transport-feasibility.md','full-runtime-recon.md','full-sync-roadmap.md','runtime-tooling.md')) {
    if(!(Test-Path -LiteralPath (Join-Path $script:RepoRoot "docs/$name"))) { $failures+="Missing report: $name" }
}
$plugin=Join-Path $script:RepoRoot 'src/ThiefSim2Multiplayer.Diagnostics/bin/Release/net46/ThiefSim2Multiplayer.Diagnostics.dll'
if(!(Test-Path -LiteralPath $plugin)) { $failures+='Built diagnostics missing.' }
if($failures.Count) { $failures | Write-Output; throw 'Recon validation failed.' }
& "$PSScriptRoot/fingerprint-game.ps1" -Verify
Write-Output 'PowerShell parsing, required reports, Git protections and tracked-file checks passed.'
& git -C $script:RepoRoot status --short
