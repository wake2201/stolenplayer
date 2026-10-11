param([switch]$Decompile)
. "$PSScriptRoot/common.ps1"
& "$PSScriptRoot/find-game.ps1"
& "$PSScriptRoot/inspect-runtime.ps1"
& "$PSScriptRoot/list-assemblies.ps1" | Out-File (Join-Path $script:RepoRoot '.local/evidence/assembly-table.txt')
& "$PSScriptRoot/index-assemblies.ps1"
& "$PSScriptRoot/scan-systems.ps1"
if($Decompile) {
    foreach($type in @('PlayerScript','PlayerMotorBehavior','PlayerCameraBehavior','GameManager','Inventory','ItemID','Pickupable','Door',
        'WindowUse','Lockpick','LockedWindow','AI','Police_Manager','PolicemanAI','NPCHabits','SidewalkSpawner','CarController',
        'VehiclesManager','VanHeavyItems','EVP.VehicleController','EVP.VehicleStandardInput','StoryManager','StoryScript','Mission_Item',
        'Mission_Manager','SelectResidence','Skill_Manager','SaveGame','SPrefs','InitializePrefs','MainMenu')) {
        & "$PSScriptRoot/decompile-type.ps1" -Type $type
    }
    foreach($type in @('FBPP','FBPPConfig','FBPPFileModel')) { & "$PSScriptRoot/decompile-type.ps1" -Type $type -Assembly 'FileBasedPrefs.dll' }
}
