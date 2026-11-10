# Shared helpers for build.ps1 / bepinex.ps1 (dot-sourced).
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$BuildDir = Join-Path $RepoRoot 'build'
$DepsDir  = Join-Path $BuildDir 'deps'

# Pinned BepInEx 5 (Unity Mono, x64). SHA-256 recorded in M1.
$BepInExVersion = '5.4.23.5'
$BepInExUrl     = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"
$BepInExSha256  = '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4'

function Resolve-GamePath([string]$GamePath) {
    if (-not $GamePath) {
        $f = Join-Path $RepoRoot 'game-path.txt'
        if (Test-Path $f) { $GamePath = (Get-Content $f -TotalCount 1).Trim() }
    }
    if (-not $GamePath -or -not (Test-Path (Join-Path $GamePath 'Thief Simulator 2.exe'))) {
        throw "Game folder not found. Pass -GamePath or put the folder path in $RepoRoot\game-path.txt"
    }
    return (Resolve-Path $GamePath).Path
}

function Get-BepInExZip {
    New-Item -ItemType Directory -Force $DepsDir | Out-Null
    $zip = Join-Path $DepsDir "BepInEx_win_x64_$BepInExVersion.zip"
    if (-not (Test-Path $zip)) {
        Write-Host "Downloading $BepInExUrl"
        Invoke-WebRequest -UseBasicParsing -Uri $BepInExUrl -OutFile $zip
    }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $BepInExSha256) { Remove-Item $zip; throw "BepInEx zip hash mismatch ($hash)" }
    $extract = Join-Path $DepsDir "BepInEx_$BepInExVersion"
    if (-not (Test-Path (Join-Path $extract 'BepInEx\core\BepInEx.dll'))) {
        Expand-Archive -Path $zip -DestinationPath $extract -Force
    }
    return @{ Zip = $zip; Dir = $extract }
}
