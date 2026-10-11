. "$PSScriptRoot/reverse/common.ps1"
$failures = @()
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -Filter '*.ps1') {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)
    if ($errors.Count) { $failures += "$($file.Name): $($errors.Message -join '; ')" }
}
$pluginBuild = Join-Path $script:RepoRoot 'src/ThiefSim2Multiplayer/bin/Release/net46'
$allowed = @('ThiefSim2Multiplayer.dll','ThiefSim2Multiplayer.Core.dll','LiteNetLib.dll')
foreach ($name in $allowed) { if (!(Test-Path -LiteralPath (Join-Path $pluginBuild $name))) { $failures += "Missing output: $name" } }
foreach ($file in Get-ChildItem -LiteralPath $pluginBuild -Filter '*.dll') {
    if ($file.Name -notin $allowed) { $failures += "Unexpected redistributed DLL: $($file.Name)" }
}
foreach ($file in @(& git -C $script:RepoRoot ls-files --cached --others --exclude-standard)) {
    if ($file -match '(?i)(^|/)(\.local|decompiled|game-ref|reverse-output|runtime-dumps|bin|obj|BepInEx|[^/]*_Data)/|\.(dll|exe|pdb|assets|bundle|sav|save|thief|log|ndjson|png|jpe?g|webp|gif|bmp)$') {
        $failures += "Forbidden tracked material: $file"
    }
    if ($file -match '\.(md|ps1|cs|csproj|json)$') {
        $text = Get-Content -LiteralPath (Join-Path $script:RepoRoot $file) -Raw
        if ($text -match '(?i)(?<![A-Z0-9_])[A-Z]:[\\/]') { $failures += "Tracked machine path: $file" }
    }
}
foreach ($name in @('loot-inventory-sync.md','milestone-e-acceptance.md','world-interaction-sync.md','milestone-d-acceptance.md','network-entity-registry.md','world-snapshot.md','coop-gameplay-rules.md','milestone-c-acceptance.md','network-avatar.md','milestone-b-acceptance.md','two-game-acceptance-plan.md','phase-1-implementation.md','phase-1-acceptance.md','live-player-hierarchy.md','recon-report.md','multiplayer-feasibility.md')) {
    if (!(Test-Path -LiteralPath (Join-Path $script:RepoRoot "docs/$name"))) { $failures += "Missing document: $name" }
}
foreach ($name in @('playability-stabilization-s1.md','manual-runtime-testing.md','vehicle-passenger-sync.md','scene-continuity-acceptance.md','scene-continuity.md')) {
    if (!(Test-Path -LiteralPath (Join-Path $script:RepoRoot "docs/$name"))) { $failures += "Missing S1 document: $name" }
}
if (!(Test-Path -LiteralPath (Join-Path $script:RepoRoot 'third_party/LiteNetLib/LICENSE.txt'))) { $failures += 'LiteNetLib MIT license missing.' }
$upstream = Join-Path $script:RepoRoot '.local/dependencies/LiteNetLib-1.3.5/LiteNetLib'
function ConvertTo-NormalizedSource([string]$Text) {
    return [regex]::Replace($Text.Replace("`r`n","`n"), '(?m)[ \t]+$', '')
}
if (Test-Path -LiteralPath $upstream) {
    foreach ($file in Get-ChildItem -LiteralPath $upstream -Recurse -Filter '*.cs') {
        $relative = $file.FullName.Substring($upstream.Length+1)
        $vendored = Join-Path $script:RepoRoot "third_party/LiteNetLib/$relative"
        if (!(Test-Path -LiteralPath $vendored)) { $failures += "Missing vendored source: $relative"; continue }
        if ($relative -eq 'NetPeer.cs') {
            $original = Get-Content -LiteralPath $file.FullName -Raw
            $actual = Get-Content -LiteralPath $vendored -Raw
            $expected = $original.Replace('                    (_unreliableChannel, _unreliableSecondQueue) = (_unreliableSecondQueue, _unreliableChannel);', @'
#if NET46
                    var previousChannel = _unreliableChannel;
                    _unreliableChannel = _unreliableSecondQueue;
                    _unreliableSecondQueue = previousChannel;
#else
                    (_unreliableChannel, _unreliableSecondQueue) = (_unreliableSecondQueue, _unreliableChannel);
#endif
'@)
            if ((ConvertTo-NormalizedSource $expected) -ne (ConvertTo-NormalizedSource $actual)) { $failures += 'Unexpected NetPeer source adaptation.' }
        } elseif($relative -eq 'NetManager.cs') {
            $expected=ConvertTo-NormalizedSource (Get-Content -LiteralPath $file.FullName -Raw)
            $patches=Get-Content -LiteralPath (Join-Path $script:RepoRoot 'third_party/LiteNetLib/NetManager.patch.json') -Raw | ConvertFrom-Json
            foreach($patch in $patches) {
                $occurrences=[regex]::Matches($expected,[regex]::Escape($patch.before)).Count
                if($occurrences -ne $patch.count) { $failures+='Queue-budget patch context mismatch.'; break }
                $expected=$expected.Replace($patch.before,$patch.after)
            }
            if($expected -ne (ConvertTo-NormalizedSource (Get-Content -LiteralPath $vendored -Raw))) { $failures+='Unexpected NetManager queue-budget adaptation.' }
        } elseif ((ConvertTo-NormalizedSource (Get-Content -LiteralPath $file.FullName -Raw)) -ne (ConvertTo-NormalizedSource (Get-Content -LiteralPath $vendored -Raw))) { $failures += "Upstream source changed: $relative" }
    }
    Write-Output 'Checked vendored source against the pinned local upstream archive.'
}
if ($failures.Count) { $failures | Write-Output; throw 'Multiplayer validation failed.' }
& "$PSScriptRoot/reverse/validate-recon.ps1"
& git -C $script:RepoRoot diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
& git -C $script:RepoRoot diff --cached --check
if ($LASTEXITCODE -ne 0) { throw 'Staged whitespace validation failed.' }
Write-Output 'Multiplayer validation passed: scripts, output allowlist, tracking/paths, license, provenance and original binaries.'
