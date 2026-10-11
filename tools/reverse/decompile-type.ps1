param([Parameter(Mandatory)][string]$Type, [string]$Assembly='Assembly-CSharp.dll')
. "$PSScriptRoot/common.ps1"
if ([IO.Path]::GetFileName($Assembly) -ne $Assembly) { throw 'Assembly must be a filename in Managed.' }
$path = Join-Path (Get-ManagedPath) $Assembly
$out = Assert-LocalOutput (Join-Path $script:RepoRoot ('.local/decompiled/' + $Assembly + '/' + ($Type -replace '[^a-zA-Z0-9_.+-]','_') + '.cs'))
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$env:DOTNET_ROOT = Split-Path (Get-LocalDotnet)
$temporary = $out + '.tmp'
& (Get-Ilspy) -t $Type -r (Get-ManagedPath) $path | Set-Content -LiteralPath $temporary -Encoding utf8
if ($LASTEXITCODE -ne 0) {
    Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue
    throw 'ILSpy failed; no final output replaced.'
}
Move-Item -LiteralPath $temporary -Destination $out -Force
Write-Output $out
