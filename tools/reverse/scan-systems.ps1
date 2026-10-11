. "$PSScriptRoot/common.ps1"
$index=Get-Content -LiteralPath (Join-Path $script:RepoRoot '.local/type-index.json') -Raw | ConvertFrom-Json
$concepts=[ordered]@{
    player='Player|Controller|Movement|Character|FirstPerson|Camera|Input|Interact|Crouch|Sprint|Run|Jump|Health'
    interactions='Interact|Door|Window|Lock|Container|Open|Close|Switch|Button|Pickup|Item|Loot'
    inventory='Inventory|Backpack|Item|Pickup|Drop|Weight|Capacity|Equipment|Tool'
    npc='NPC|\bAI\b|Citizen|Owner|Guard|Police|Detection|Alert|Wanted|Suspicion|Vision|Hearing|Path|NavMesh|RichAI'
    vehicles='Vehicle|Car|Van|Drive|Driver|Seat|EnterVehicle|ExitVehicle|Trunk'
    progression='Mission|Quest|Objective|Story|Stage|Progress|Experience|XP|Skill|Money'
    saves='Save|Load|Serialization|Persistence|Profile|FBPP|SPrefs'
    scenes='Scene|World|Map|Level|Spawn'
    identity='persistent.*id|save.*id|object.*id|guid|hierarchy|scene.*id|prefName|item_ID|mission_prefs'
}
$result=foreach($system in $concepts.Keys) {
    $matches=@(foreach($assembly in $index) {
        foreach($type in $assembly.Types) {
            $members=@(@($type.Fields)+@($type.Properties)+@($type.Methods) | Where-Object {$_ -match $concepts[$system]})
            if($type.Name -match $concepts[$system] -or $members.Count) {
                [pscustomobject]@{Assembly=$assembly.Name; Type=$type.Name; TypeNameMatched=($type.Name -match $concepts[$system]); Members=$members}
            }
        }
    })
    [pscustomobject]@{System=$system; Pattern=$concepts[$system]; Matches=$matches}
}
$out=Join-Path $script:RepoRoot '.local/system-candidates.json'
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $out -Encoding utf8
$result | ForEach-Object { "$($_.System): $($_.Matches.Count) candidate types (name matches are not implementation evidence)" }
