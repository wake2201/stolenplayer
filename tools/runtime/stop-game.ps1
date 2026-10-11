param([Parameter(Mandatory)][string]$Session,[switch]$Force)
. "$PSScriptRoot/common.ps1"
$data=Get-RuntimeSession $Session
$process=Get-SessionProcess $data
if($process) {
    if($Force) { Stop-Process -Id $process.Id; if(!$process.WaitForExit(10000)) { throw 'Owned fixture process did not exit; do not restore files yet.' } }
    else { [void]$process.CloseMainWindow(); if(!$process.WaitForExit(10000)) { throw 'Game did not close within 10 seconds; use -Force only for this verified process if required.' } }
}
& "$PSScriptRoot/collect-runtime.ps1" -Session $Session
$restoreFile=Join-Path $data.Directory 'restore.json'
$restore=Get-Content -LiteralPath $restoreFile -Raw | ConvertFrom-Json
if(!$restore.Restored) {
    $game=Get-GamePath
    if($restore.Doorstop -ne (Join-Path $game 'doorstop_config.ini')) { throw 'Unexpected loader path.' }
    Copy-Item -LiteralPath (Join-Path $data.Directory 'doorstop-original.ini') -Destination $restore.Doorstop -Force
    foreach($entry in $restore.Disabled) {
        $allowed=[IO.Path]::GetFullPath((Join-Path $game 'BepInEx/plugins'))+[IO.Path]::DirectorySeparatorChar
        if(!$entry.Original.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or $entry.Temporary -ne ($entry.Original+'.ts2-disabled')) { throw 'Unexpected restoration path.' }
        if(Test-Path -LiteralPath $entry.Original) { throw 'Restoration destination already exists.' }
        Move-Item -LiteralPath $entry.Temporary -Destination $entry.Original
    }
    $restore.Restored=$true
    $restore | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $restoreFile -Encoding utf8
}
