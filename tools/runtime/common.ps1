. "$PSScriptRoot/../reverse/common.ps1"
function Get-RuntimeSession([string]$Session) {
    $path = Assert-LocalOutput ([IO.Path]::GetFullPath($Session))
    return Get-Content -LiteralPath (Join-Path $path 'session.json') -Raw | ConvertFrom-Json
}
function Get-SessionProcess($Session) {
    $process = Get-Process -Id $Session.ProcessId -ErrorAction SilentlyContinue
    if (!$process) { return $null }
    if ($process.Path -ne $Session.Executable -or $process.StartTime.ToUniversalTime().Ticks -ne ([DateTime]$Session.StartUtc).ToUniversalTime().Ticks) {
        throw 'Process identity changed; refusing to operate on a reused PID.'
    }
    return $process
}
