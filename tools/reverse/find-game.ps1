param([string]$GamePath)
. "$PSScriptRoot/common.ps1"
$steamRoots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($key in @('HKCU:\Software\Valve\Steam','HKLM:\SOFTWARE\WOW6432Node\Valve\Steam','HKLM:\SOFTWARE\Valve\Steam')) {
    if (Test-Path $key) {
        $value = Get-ItemProperty $key
        foreach ($property in @('SteamPath','InstallPath')) {
            if ($value.PSObject.Properties[$property] -and $value.$property) { [void]$steamRoots.Add($value.$property) }
        }
    }
}
foreach ($process in @(Get-Process steam -ErrorAction SilentlyContinue)) {
    try { if ($process.Path) { [void]$steamRoots.Add((Split-Path $process.Path)) } } catch { }
}
foreach ($base in @(${env:ProgramFiles},${env:ProgramFiles(x86)})) {
    if ($base -and (Test-Path -LiteralPath (Join-Path $base 'Steam'))) { [void]$steamRoots.Add((Join-Path $base 'Steam')) }
}
$libraries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($root in $steamRoots) {
    [void]$libraries.Add($root)
    $vdf = Join-Path $root 'steamapps/libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        $content = Get-Content -LiteralPath $vdf -Raw
        foreach ($match in [regex]::Matches($content, '"path"\s*"([^"]+)"')) { [void]$libraries.Add($match.Groups[1].Value.Replace('\\','\')) }
        # Legacy Steam libraryfolders.vdf uses numeric keys with direct paths.
        foreach ($match in [regex]::Matches($content, '"\d+"\s*"([A-Za-z]:\\[^\"]*(?:\\.[^\"]*)*)"')) { [void]$libraries.Add($match.Groups[1].Value.Replace('\\','\')) }
    }
}
foreach ($drive in @(Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Name -match '^[A-Z]$' })) {
    foreach ($name in @('Steam','SteamLibrary')) {
        $candidate = Join-Path $drive.Root $name
        if (Test-Path -LiteralPath (Join-Path $candidate 'steamapps')) { [void]$libraries.Add($candidate) }
    }
}
$found = @()
foreach ($library in $libraries) {
    $manifest = Join-Path $library 'steamapps/appmanifest_1332720.acf'
    if (Test-Path -LiteralPath $manifest) {
        $text = Get-Content -LiteralPath $manifest -Raw
        if ($text -match '"installdir"\s*"([^"]+)"') {
            $candidate = Join-Path $library ('steamapps/common/' + $Matches[1])
            if (Test-Path -LiteralPath $candidate) { $found += [pscustomobject]@{ Path=$candidate; Source='SteamManifest'; Manifest=$manifest } }
        }
    }
}
if ($GamePath) { $found = @([pscustomobject]@{ Path=(Resolve-Path -LiteralPath $GamePath).Path; Source='ExplicitOverride'; Manifest=$null }) }
if (!$found.Count) {
    # Bounded fallback, never recurse through an entire disk or unrelated user data.
    foreach ($drive in @(Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Name -match '^[A-Z]$' })) {
        $parents = @($drive.Root)
        foreach ($name in @('games','Games','SteamLibrary/steamapps/common','Steam/steamapps/common')) {
            $parent = Join-Path $drive.Root $name
            if (Test-Path -LiteralPath $parent) { $parents += $parent }
        }
        foreach ($parent in ($parents | Select-Object -Unique)) {
            foreach ($dir in @(Get-ChildItem -LiteralPath $parent -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'Thief[ -]?Simulator[ -]?2' })) {
                foreach ($candidate in @($dir.FullName,(Join-Path $dir.FullName 'Thief Simulator 2'))) {
                    if (Test-Path -LiteralPath (Join-Path $candidate 'Thief Simulator 2.exe')) { $found += [pscustomobject]@{ Path=$candidate; Source='NonSteamFallback'; Manifest=$null } }
                }
            }
        }
    }
}
$found = @($found | Sort-Object Path -Unique)
$local = Join-Path $script:RepoRoot '.local'
New-Item -ItemType Directory -Force -Path $local | Out-Null
[pscustomobject]@{ AppId=1332720; SteamRoots=@($steamRoots); Libraries=@($libraries); Candidates=$found; CapturedUtc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $local 'discovery.json') -Encoding utf8
if ($found.Count -ne 1) { throw "Found $($found.Count) candidates. See .local/discovery.json; use -GamePath to resolve ambiguity." }
$found[0].Path | Set-Content -LiteralPath (Join-Path $local 'game-path.txt') -Encoding utf8
$found[0] | Format-List
