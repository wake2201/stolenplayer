param([Parameter(Mandatory)][string]$Session,
    [ValidateSet('host','client')][string]$Role='client',
    [ValidateRange(10,900)][int]$Seconds=90)
. "$PSScriptRoot/common.ps1"
$data = Get-RuntimeSession $Session
if (!(Get-SessionProcess $data) -or !(Test-Path -LiteralPath (Join-Path $data.Directory 'save-before'))) { throw 'Matching live game and existing save backup required.' }
$directory = Join-Path $script:RepoRoot '.local/evidence/milestone-e'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$manifest = Join-Path $directory 'world-manifest.tsv'
$target = Join-Path $script:RepoRoot '.local/runtime-dumps/me-target.txt'
$catalog = Join-Path $script:RepoRoot '.local/runtime-dumps/me-catalog.tsv'
if (!(Test-Path -LiteralPath $catalog)) { throw 'Capture the real native loot catalog first.' }
if ($Role -eq 'client' -and (!(Test-Path -LiteralPath $target) -or (Get-Item -LiteralPath $target).LastWriteTimeUtc -lt ([DateTime]$data.StartUtc).ToUniversalTime())) { throw 'Run a successful original-range loot-probe fixture in this session first.' }
if ($Role -eq 'host' -and !(Test-Path -LiteralPath $manifest)) { throw 'Capture a real host manifest with the client harness first.' }
$label = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + $Role
$harness = Join-Path $script:RepoRoot 'tools/TransportHarness/bin/Release/net9.0/TransportHarness.dll'
$hash = (Get-FileHash -LiteralPath (Join-Path (Get-ManagedPath) 'Assembly-CSharp.dll') -Algorithm SHA256).Hash
$arguments = @(('"' + $harness + '"'), $Role)
if ($Role -eq 'client') { $arguments += '127.0.0.1' }
$arguments += @('9050',$hash,$Seconds,'MadisonSt|MainScene','inventory','4','0',('"' + $manifest + '"'),('"' + $target + '"'),('"' + $catalog + '"'))
$process = Start-Process -FilePath (Get-LocalDotnet) -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $directory "$label.log") -RedirectStandardError (Join-Path $directory "$label-error.log")
[ordered]@{ ProcessId=$process.Id; Role=$Role; Log=(Join-Path $directory "$label.log"); ErrorLog=(Join-Path $directory "$label-error.log"); Evidence='Synthetic canonical actor and protocol harness; native game executes only its own original paths.' } | ConvertTo-Json
