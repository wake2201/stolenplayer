# N1.2 drag-and-drop release. Run after tools\build.ps1 (called by build.ps1 -Package / -Test).
# Stages build\release\TS2-Multiplayer-<ver>\ with exactly the files that go into the game folder:
#   winhttp.dll, doorstop_config.ini, .doorstop_version      BepInEx 5.4.23.5 bootstrap (UnityDoorstop), unmodified
#   BepInEx\core\*.dll                                        BepInEx 5.4.23.5 core, unmodified
#   BepInEx\plugins\TS2MP\TS2MP.Plugin.dll, LiteNetLib.dll    the mod (= the tested build in build\plugin)
#   BepInEx\plugins\TS2MP\{manifest.sha256, THIRD-PARTY-NOTICES.txt, DIRECT_CONNECT_SETUP_DE.md, licenses\}
#   TS2-Multiplayer-LIESMICH.txt                              German quick start
# The ZIP has no wrapping folder (extract / copy into the game root). Sorted entries + fixed timestamps: the same build
# outputs always give the same ZIP bytes. The legacy csc has no /deterministic, so recompiling changes the plugin hash.
# No launcher, no PowerShell, no developer files, no saves, logs or secrets are packaged.
. "$PSScriptRoot\common.ps1"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$protocolSrc = Get-Content (Join-Path $RepoRoot 'src\Shared\Protocol.cs') -Raw
$ver   = [regex]::Match($protocolSrc, 'ModVersion = "([^"]+)"').Groups[1].Value
$proto = [regex]::Match($protocolSrc, 'ProtocolVersion = (\d+);').Groups[1].Value
if (-not $ver -or -not $proto) { throw 'cannot read ModVersion / ProtocolVersion from Protocol.cs' }

$name   = "TS2-Multiplayer-$ver"
$relDir = Join-Path $BuildDir 'release'
$stage  = Join-Path $relDir $name
$zip    = Join-Path $relDir "$name.zip"
$bep    = (Get-BepInExZip).Dir
$modRel = 'BepInEx\plugins\TS2MP'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, "$stage\BepInEx\core", "$stage\$modRel\licenses" | Out-Null
foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) { Copy-Item (Join-Path $bep $f) "$stage\" }
Copy-Item "$bep\BepInEx\core\*.dll" "$stage\BepInEx\core\"
Copy-Item (Join-Path $BuildDir 'plugin\TS2MP.Plugin.dll'), (Join-Path $BuildDir 'plugin\LiteNetLib.dll') "$stage\$modRel\"
Copy-Item (Join-Path $RepoRoot 'licenses\*') "$stage\$modRel\licenses\"
Copy-Item (Join-Path $RepoRoot 'lib\LICENSE-LiteNetLib.txt') "$stage\$modRel\licenses\LiteNetLib-LICENSE.txt"
Copy-Item (Join-Path $RepoRoot 'release\THIRD-PARTY-NOTICES.txt') "$stage\$modRel\"
Copy-Item (Join-Path $RepoRoot 'docs\DIRECT_CONNECT_SETUP_DE.md') "$stage\$modRel\"
Copy-Item (Join-Path $RepoRoot 'release\TS2-Multiplayer-LIESMICH.txt') "$stage\"

# Manifest (ordinal sort, forward slashes, LF line ends), stored inside the mod folder
$manifestRel = 'BepInEx/plugins/TS2MP/manifest.sha256'
$files = Get-ChildItem $stage -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($stage.Length + 1).Replace('\', '/') } | Sort-Object -CaseSensitive
$lines = @("# TS2-Multiplayer version=$ver protocol=$proto bepinex=$BepInExVersion files=$($files.Count)",
           "# SHA-256 of every file of the release (paths relative to the game folder), except this manifest")
foreach ($rel in $files) { $lines += ((Get-FileHash (Join-Path $stage $rel) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $rel) }
[IO.File]::WriteAllText((Join-Path $stage $manifestRel), ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))

if (Test-Path $zip) { Remove-Item $zip -Force }
$stamp = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$all = @($files + $manifestRel) | Sort-Object -CaseSensitive
$fs = [IO.File]::Open($zip, 'CreateNew')
try {
    $za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create, $false)
    foreach ($rel in $all) {
        $e = $za.CreateEntry($rel, [IO.Compression.CompressionLevel]::Optimal)
        $e.LastWriteTime = $stamp
        $out = $e.Open(); $in = [IO.File]::OpenRead((Join-Path $stage $rel))
        try { $in.CopyTo($out) } finally { $in.Dispose(); $out.Dispose() }
    }
    $za.Dispose()
} finally { $fs.Dispose() }

# Verify the ZIP against the manifest and the required layout
$required = @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'BepInEx/core/BepInEx.dll', 'BepInEx/core/BepInEx.Preloader.dll',
    'BepInEx/core/0Harmony.dll', 'BepInEx/core/MonoMod.RuntimeDetour.dll', 'BepInEx/core/Mono.Cecil.dll',
    'BepInEx/plugins/TS2MP/TS2MP.Plugin.dll', 'BepInEx/plugins/TS2MP/LiteNetLib.dll', $manifestRel, 'BepInEx/plugins/TS2MP/THIRD-PARTY-NOTICES.txt',
    'BepInEx/plugins/TS2MP/DIRECT_CONNECT_SETUP_DE.md', 'BepInEx/plugins/TS2MP/licenses/LiteNetLib-LICENSE.txt', 'BepInEx/plugins/TS2MP/licenses/BepInEx-LICENSE.txt',
    'BepInEx/plugins/TS2MP/licenses/UnityDoorstop-LICENSE.txt', 'TS2-Multiplayer-LIESMICH.txt')
$expect = @{}
foreach ($l in $lines | Where-Object { $_ -notmatch '^#' }) { $expect[$l.Substring(66)] = $l.Substring(0, 64) }
$sha = [Security.Cryptography.SHA256]::Create()
$za = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $seen = @{}
    foreach ($e in $za.Entries) {
        $rel = $e.FullName
        if ($rel -match '(^|/)\.\.(/|$)' -or $rel.StartsWith('/')) { throw "unsafe ZIP entry $rel" }
        if ($rel -match '(?i)\.(exe|ps1|thief|log|ini\.bak)$' -and $rel -ne 'winhttp.dll') { throw "forbidden file type in the release: $rel" }
        $s = $e.Open(); try { $h = (-join ($sha.ComputeHash($s) | ForEach-Object { $_.ToString('x2') })) } finally { $s.Dispose() }
        $seen[$rel] = $true
        if ($rel -eq $manifestRel) { continue }
        if (-not $expect.ContainsKey($rel)) { throw "ZIP contains a file not in the manifest: $rel" }
        if ($expect[$rel] -ne $h) { throw "ZIP hash mismatch: $rel" }
    }
    foreach ($k in $expect.Keys) { if (-not $seen[$k]) { throw "ZIP lacks manifest file $k" } }
    foreach ($r in $required) { if (-not $seen[$r]) { throw "ZIP lacks required file $r" } }
} finally { $za.Dispose() }
if ($expect['BepInEx/plugins/TS2MP/TS2MP.Plugin.dll'] -ne (Get-FileHash (Join-Path $BuildDir 'plugin\TS2MP.Plugin.dll') -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'packaged plugin differs from build\plugin' }

$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$zipHash  $name.zip`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "PACKAGE OK $zip"
Write-Host "  $($files.Count + 1) files, manifest verified inside the ZIP, sha256 $zipHash"
Write-Host "  plugin sha256 $($expect['BepInEx/plugins/TS2MP/TS2MP.Plugin.dll'])"
exit 0
