param([Parameter(Mandatory)][string]$Session,
    [ValidateSet('audit','host','join','disconnect','input','input-stop','world-probe','world-audit','world-open','world-close','loot-probe','loot-audit','loot-pickup','loot-drop','loot-catalog','vehicle-bootstrap','vehicle-passenger-cycle','vehicle-audit','vehicle-vanilla-audit','vehicle-native-exit','vehicle-drive-cycle','vehicle-economy-cycle','vehicle-hideout','vehicle-passenger-preview','vehicle-coop-cycle','vehicle-mission-prepare','vehicle-mission-drive-cycle','vehicle-variant-cycle','vehicle-stability-cycle','vehicle-interaction-cycle','vehicle-recovery-cycle','vehicle-geometry','vehicle-profile-reset','vehicle-profile-summary')][string]$Action='audit',
    [ValidateRange(0.1,60)][double]$Seconds=1,
    [ValidateRange(-1,1)][double]$Forward=0,[ValidateRange(-1,1)][double]$Right=0,
    [ValidateRange(-100,100)][double]$LookX=0,[ValidateRange(-100,100)][double]$LookY=0,
    [switch]$Run,[ValidateSet('none','crouch','jump','use_item','interact','run')][string]$Button='none')
. "$PSScriptRoot/common.ps1"
$data = Get-RuntimeSession $Session
$process = Get-SessionProcess $data
if (!$process -or !(Test-Path -LiteralPath (Join-Path $data.Directory 'save-before'))) { throw 'Matching live process and backed-up save required.' }
$directory = Join-Path $script:RepoRoot '.local/runtime-dumps'
$request = Join-Path $directory 'request.txt'
if (Test-Path -LiteralPath $request) { throw 'A diagnostic request is already pending.' }
if ($Action -eq 'input') {
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $values = @($Seconds,$Forward,$Right,$LookX,$LookY) | ForEach-Object { $_.ToString($culture) }
    $values += @($Run.IsPresent.ToString().ToLowerInvariant(),$Button,$process.Id.ToString())
    Set-Content -LiteralPath (Join-Path $directory 'mb-input.txt') -Value ($values -join ' ') -Encoding ascii
}
$temporary = Join-Path $directory 'request.tmp'
Set-Content -LiteralPath $temporary -Value "mb-$Action" -Encoding ascii
Move-Item -LiteralPath $temporary -Destination $request
Write-Output "Queued DEVELOPMENT acceptance $Action; input evidence must be labelled DEVELOPMENT INPUT FIXTURE."
