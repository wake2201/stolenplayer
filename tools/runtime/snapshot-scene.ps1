param([ValidateSet('all','player','avatars','interactions','inventory','ai','vehicles','managers','avatar-test','avatar-remove','trace-on','trace-off','pickup-test')][string]$Target='all',[string]$Session,[ValidateLength(0,1024)][string]$Scope='')
. "$PSScriptRoot/common.ps1"
$directory=Join-Path $script:RepoRoot '.local/runtime-dumps'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$request=Join-Path $directory 'request.txt'
if(Test-Path -LiteralPath $request) { throw 'A diagnostic request is already pending.' }
Set-Content -LiteralPath (Join-Path $directory 'scope-path.txt') -Value $Scope -Encoding utf8
if($Target -eq 'pickup-test') {
    if(!$Session) {throw 'Pickup experiment requires a launch session with an existing save backup.'}
    $data=Get-RuntimeSession $Session
    $process=Get-SessionProcess $data
    if(!$process -or !(Test-Path -LiteralPath (Join-Path $data.Directory 'save-before/saveData.thief'))) {throw 'Matching running game or save backup missing.'}
    Set-Content -LiteralPath (Join-Path $directory 'pickup-authorization.txt') -Value $data.ProcessId -Encoding ascii
}
# The plugin consumes only a fixed command allowlist. No code or game methods can be injected.
$temporary=Join-Path $directory 'request.tmp'
Set-Content -LiteralPath $temporary -Value $Target -Encoding ascii
Move-Item -LiteralPath $temporary -Destination $request
Write-Output "Queued bounded $Target capture."
