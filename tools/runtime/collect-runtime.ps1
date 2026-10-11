param([Parameter(Mandatory)][string]$Session)
. "$PSScriptRoot/common.ps1"
$data=Get-RuntimeSession $Session
$started=([DateTime]$data.StartUtc).ToUniversalTime()
$sources=@{ Player='AppData/LocalLow/CookieDev/Thief Simulator 2/Player.log'; Previous='AppData/LocalLow/CookieDev/Thief Simulator 2/Player-prev.log' }
foreach($key in $sources.Keys) {
    $file=Join-Path $env:USERPROFILE $sources[$key]
    if(Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $data.Directory ($key+'.log')) -Force }
}
$bep=Join-Path (Get-GamePath) 'BepInEx/LogOutput.log'
if((Test-Path -LiteralPath $bep) -and (Get-Item -LiteralPath $bep).LastWriteTimeUtc -ge $started.AddSeconds(-2)) {
    Copy-Item -LiteralPath $bep -Destination (Join-Path $data.Directory 'BepInEx.log') -Force
}
$crashes=Join-Path $env:TEMP 'CookieDev/Thief Simulator 2/Crashes'
if(Test-Path -LiteralPath $crashes) {
    foreach($file in Get-ChildItem -LiteralPath $crashes -Recurse -File | Where-Object { $_.LastWriteTimeUtc -ge $started -and $_.Extension -in '.log','.txt' -and $_.Length -le 4MB }) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $data.Directory ('crash-'+$file.Directory.Name+'-'+$file.Name)) -Force
    }
}
$dump=Join-Path $script:RepoRoot '.local/runtime-dumps'
if(Test-Path -LiteralPath $dump) {
    foreach($file in Get-ChildItem -LiteralPath $dump -Filter '*.ndjson' | Where-Object { $_.CreationTimeUtc -ge $started.AddSeconds(-2) }) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $data.Directory $file.Name) -Force
    }
}
& "$PSScriptRoot/../reverse/fingerprint-game.ps1" -Verify
Write-Output "Collected logs into $($data.Directory)"
