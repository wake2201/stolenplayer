. "$PSScriptRoot/common.ps1"
$game=Get-GamePath
$managed=Get-ManagedPath
$data=Split-Path $managed
$player=Join-Path $game 'UnityPlayer.dll'
$header=[IO.File]::ReadAllBytes((Join-Path $data 'globalgamemanagers'))
$version=[regex]::Match([Text.Encoding]::ASCII.GetString($header,0,[Math]::Min(256,$header.Length)),'20\d\d\.\d+\.\d+[abfp]\d+').Value
$exe=Get-ChildItem -LiteralPath $game -Filter '*.exe' | Where-Object Name -notmatch 'CrashHandler' | Select-Object -First 1
$stream=[IO.File]::OpenRead($exe.FullName)
try {
    $reader=[IO.BinaryReader]::new($stream)
    $stream.Position=0x3c; $pe=$reader.ReadInt32(); $stream.Position=$pe+4; $machine=$reader.ReadUInt16()
} finally { $stream.Dispose() }
$snapshot=[pscustomobject]@{
    CapturedUtc=[DateTime]::UtcNow.ToString('o'); GamePath=$game; ManagedPath=$managed
    UnitySerializedVersion=$version; UnityPlayerVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($player).FileVersion
    PEMachine=('0x{0:X4}' -f $machine)
    MonoRuntimePresent=(Test-Path -LiteralPath (Join-Path $game 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll'))
    GameAssemblyPresent=(Test-Path -LiteralPath (Join-Path $game 'GameAssembly.dll'))
    Il2cppMetadataPresent=(Test-Path -LiteralPath (Join-Path $data 'il2cpp_data/Metadata/global-metadata.dat'))
    AssemblyCSharpPresent=(Test-Path -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll'))
    NativeSteamFiles=@(Get-ChildItem -LiteralPath (Join-Path $data 'Plugins') -Recurse -Filter '*steam*' | ForEach-Object FullName)
    BepInExPresent=(Test-Path -LiteralPath (Join-Path $game 'BepInEx/core/BepInEx.dll'))
}
$snapshot | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $script:RepoRoot '.local/evidence/runtime.json') -Encoding utf8
$snapshot | ConvertTo-Json -Depth 5
