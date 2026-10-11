Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
function Get-GamePath {
    $file = Join-Path $script:RepoRoot '.local/game-path.txt'
    if (!(Test-Path -LiteralPath $file)) { throw 'Run tools/reverse/find-game.ps1 first.' }
    $path = (Get-Content -LiteralPath $file -Raw).Trim()
    if (!(Test-Path -LiteralPath $path -PathType Container)) { throw "Game directory missing: $path" }
    return $path
}
function Get-ManagedPath {
    $dirs = @(Get-ChildItem -LiteralPath (Get-GamePath) -Directory -Filter '*_Data' | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Managed/Assembly-CSharp.dll') })
    if ($dirs.Count -ne 1) { throw 'Expected exactly one Mono Managed directory.' }
    return (Join-Path $dirs[0].FullName 'Managed')
}
function Get-LocalDotnet {
    $local = Join-Path $script:RepoRoot '.local/dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    return (Get-Command dotnet -ErrorAction Stop).Source
}
function Get-Ilspy {
    $local = Join-Path $script:RepoRoot '.local/tools/ilspy/ilspycmd.exe'
    if (Test-Path -LiteralPath $local) { return $local }
    return (Get-Command ilspycmd -ErrorAction Stop).Source
}
function Assert-LocalOutput([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $allowed = [IO.Path]::GetFullPath((Join-Path $script:RepoRoot '.local')) + [IO.Path]::DirectorySeparatorChar
    if (!$absolute.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Proprietary analysis output must stay inside .local.' }
    return $absolute
}
