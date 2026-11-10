# Installs or removes the pinned BepInEx 5 loader in the game folder.
#   tools\bepinex.ps1 -Install   [-GamePath <folder>]
#   tools\bepinex.ps1 -Uninstall [-GamePath <folder>]
# Install never overwrites existing files. Uninstall removes only the loader files and the BepInEx folder.
param([switch]$Install, [switch]$Uninstall, [string]$GamePath)
. "$PSScriptRoot\common.ps1"
$game = Resolve-GamePath $GamePath
$loaderFiles = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')

if ($Install) {
    $src = (Get-BepInExZip).Dir
    $conflicts = @($loaderFiles + 'BepInEx' | Where-Object { Test-Path (Join-Path $game $_) })
    if ($conflicts.Count) { throw "Refusing to overwrite existing files in ${game}: $($conflicts -join ', ')" }
    foreach ($f in $loaderFiles) { Copy-Item (Join-Path $src $f) $game }
    Copy-Item (Join-Path $src 'BepInEx') $game -Recurse
    New-Item -ItemType Directory -Force (Join-Path $game 'BepInEx\plugins') | Out-Null
    Write-Host "BepInEx $BepInExVersion installed into $game"
}
elseif ($Uninstall) {
    foreach ($f in $loaderFiles) { $p = Join-Path $game $f; if (Test-Path $p) { Remove-Item $p -Force; Write-Host "removed $p" } }
    $b = Join-Path $game 'BepInEx'
    if (Test-Path $b) { Remove-Item $b -Recurse -Force; Write-Host "removed $b" }
}
else { Write-Host 'Specify -Install or -Uninstall' }
