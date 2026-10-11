param([ValidateSet('host','client')][string]$Role='client',
    [ValidateRange(1,65535)][int]$Port=9050,
    [ValidateRange(10,1800)][int]$Seconds=60,
    [ValidateRange(2,30)][int]$PhaseSeconds=4,
    [ValidateSet('avatar','equipment','soak','world','s1')][string]$Sequence='avatar',
    [ValidateRange(0,1)][int]$AvatarId=0,
    [string]$SceneKey='IntroHouse|MainScene',
    [ValidatePattern('^[a-z0-9-]{1,48}$')][string]$Label='avatar-client')
. "$PSScriptRoot/common.ps1"
$output = Join-Path $script:RepoRoot '.local/evidence/avatar'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$harness = Join-Path $script:RepoRoot 'tools/TransportHarness/bin/Release/net9.0/TransportHarness.dll'
$hash = (Get-FileHash -LiteralPath (Join-Path (Get-ManagedPath) 'Assembly-CSharp.dll') -Algorithm SHA256).Hash
$arguments = @(('"'+$harness+'"'),$Role)
if ($Role -eq 'client') { $arguments += '127.0.0.1' }
$arguments += @($Port,$hash,$Seconds,('"'+$SceneKey+'"'),$Sequence,$PhaseSeconds,$AvatarId)
$process = Start-Process -FilePath (Get-LocalDotnet) -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output "$Label.log") -RedirectStandardError (Join-Path $output "$Label-error.log")
Write-Output "Avatar harness PID=$($process.Id); role=$Role; port=$Port; label=$Label"
