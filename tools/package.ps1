# N1 release package. Run after tools\build.ps1 (called by build.ps1 -Package / -Test).
# Stages build\release\TS2-Multiplayer-<ver>-Windows\, writes manifest.sha256 (sha256sum format, every file except the
# manifest itself), packs a ZIP with sorted entries and fixed timestamps, then re-opens the ZIP and verifies that it
# contains exactly the manifest files with matching hashes and every required file.
# Reproducibility: the same build outputs always give a byte-identical ZIP. The legacy .NET Framework csc (C# 5) has no
# /deterministic switch, so recompiling changes the hashes of our own two binaries (PE timestamp / MVID); the BepInEx,
# LiteNetLib, license and document files are byte-identical in every build.
. "$PSScriptRoot\common.ps1"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$protocolSrc = Get-Content (Join-Path $RepoRoot 'src\Shared\Protocol.cs') -Raw
$ver   = [regex]::Match($protocolSrc, 'ModVersion = "([^"]+)"').Groups[1].Value
$proto = [regex]::Match($protocolSrc, 'ProtocolVersion = (\d+);').Groups[1].Value
if (-not $ver -or -not $proto) { throw 'cannot read ModVersion / ProtocolVersion from Protocol.cs' }
$launcherSrc = Get-Content (Join-Path $RepoRoot 'src\Launcher\Launcher.cs') -Raw
if ($launcherSrc -notmatch "ModVersion = `"$([regex]::Escape($ver))`"") { throw "launcher version (ProtocolText.ModVersion) differs from plugin version $ver" }

$name   = "TS2-Multiplayer-$ver-Windows"
$relDir = Join-Path $BuildDir 'release'
$stage  = Join-Path $relDir $name
$zip    = Join-Path $relDir "$name.zip"
$bep    = (Get-BepInExZip).Dir

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, "$stage\payload\bepinex", "$stage\payload\plugin", "$stage\licenses" | Out-Null

Copy-Item (Join-Path $BuildDir 'TS2-Multiplayer-Launcher.exe') $stage
foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt')) { Copy-Item (Join-Path $bep $f) "$stage\payload\bepinex\" }
New-Item -ItemType Directory -Force "$stage\payload\bepinex\BepInEx\core" | Out-Null
Copy-Item "$bep\BepInEx\core\*" "$stage\payload\bepinex\BepInEx\core\"
Copy-Item (Join-Path $BuildDir 'plugin\TS2MP.Plugin.dll'), (Join-Path $BuildDir 'plugin\LiteNetLib.dll') "$stage\payload\plugin\"
Copy-Item (Join-Path $RepoRoot 'licenses\*') "$stage\licenses\"
Copy-Item (Join-Path $RepoRoot 'lib\LICENSE-LiteNetLib.txt') "$stage\licenses\LiteNetLib-LICENSE.txt"
Copy-Item (Join-Path $RepoRoot 'release\SCHNELLSTART.txt'), (Join-Path $RepoRoot 'release\THIRD-PARTY-NOTICES.txt') $stage
Copy-Item (Join-Path $RepoRoot 'docs\DIRECT_CONNECT_SETUP_DE.md') $stage

# Manifest (ordinal sort, forward slashes, LF line ends)
$files = Get-ChildItem $stage -Recurse -File | ForEach-Object { $_.FullName.Substring($stage.Length + 1).Replace('\', '/') } | Sort-Object -CaseSensitive
$lines = @("# TS2-Multiplayer version=$ver protocol=$proto bepinex=$BepInExVersion files=$($files.Count)",
           "# SHA-256 of every file in this package except manifest.sha256 (check: launcher 'status' or sha256sum -c)")
foreach ($rel in $files) { $lines += ((Get-FileHash (Join-Path $stage $rel) -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $rel) }
[IO.File]::WriteAllText("$stage\manifest.sha256", ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))

# Deterministic ZIP
if (Test-Path $zip) { Remove-Item $zip -Force }
$stamp = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$all = @($files + 'manifest.sha256') | Sort-Object -CaseSensitive
$fs = [IO.File]::Open($zip, 'CreateNew')
try {
    $za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create, $false)
    foreach ($rel in $all) {
        $e = $za.CreateEntry("$name/$rel", [IO.Compression.CompressionLevel]::Optimal)
        $e.LastWriteTime = $stamp
        $out = $e.Open(); $in = [IO.File]::OpenRead((Join-Path $stage $rel))
        try { $in.CopyTo($out) } finally { $in.Dispose(); $out.Dispose() }
    }
    $za.Dispose()
} finally { $fs.Dispose() }

# Verify the ZIP against the manifest
$required = @('TS2-Multiplayer-Launcher.exe', 'manifest.sha256', 'SCHNELLSTART.txt', 'DIRECT_CONNECT_SETUP_DE.md', 'THIRD-PARTY-NOTICES.txt',
    'payload/plugin/TS2MP.Plugin.dll', 'payload/plugin/LiteNetLib.dll', 'payload/bepinex/winhttp.dll', 'payload/bepinex/doorstop_config.ini',
    'payload/bepinex/.doorstop_version', 'payload/bepinex/BepInEx/core/BepInEx.dll', 'payload/bepinex/BepInEx/core/BepInEx.Preloader.dll',
    'payload/bepinex/BepInEx/core/0Harmony.dll', 'licenses/LiteNetLib-LICENSE.txt', 'licenses/BepInEx-LICENSE.txt', 'licenses/HarmonyX-LICENSE.txt',
    'licenses/MonoMod-LICENSE.txt', 'licenses/Mono.Cecil-LICENSE.txt', 'licenses/UnityDoorstop-LICENSE.txt')
$expect = @{}
foreach ($l in $lines | Where-Object { $_ -notmatch '^#' }) { $expect[$l.Substring(66)] = $l.Substring(0, 64) }
$sha = [Security.Cryptography.SHA256]::Create()
$za = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $seen = @{}
    foreach ($e in $za.Entries) {
        if (-not $e.FullName.StartsWith("$name/")) { throw "ZIP entry outside the package folder: $($e.FullName)" }
        $rel = $e.FullName.Substring($name.Length + 1)
        $s = $e.Open(); try { $h = (-join ($sha.ComputeHash($s) | ForEach-Object { $_.ToString('x2') })) } finally { $s.Dispose() }
        if ($rel -eq 'manifest.sha256') { $seen[$rel] = $true; continue }
        if (-not $expect.ContainsKey($rel)) { throw "ZIP contains a file not in the manifest: $rel" }
        if ($expect[$rel] -ne $h) { throw "ZIP hash mismatch: $rel" }
        $seen[$rel] = $true
    }
    foreach ($k in $expect.Keys) { if (-not $seen[$k]) { throw "ZIP lacks manifest file $k" } }
    foreach ($r in $required) { if (-not $seen[$r]) { throw "ZIP lacks required file $r" } }
} finally { $za.Dispose() }

$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$zipHash  $name.zip`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "PACKAGE OK $zip"
Write-Host "  $($files.Count + 1) files, manifest verified inside the ZIP, sha256 $zipHash"
Write-Host "  plugin sha256 $($expect['payload/plugin/TS2MP.Plugin.dll'])"
exit 0
