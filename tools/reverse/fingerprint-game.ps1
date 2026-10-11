param([switch]$Verify)
. "$PSScriptRoot/common.ps1"
$baseline=Join-Path $script:RepoRoot '.local/evidence/original-binaries.json'
if ($Verify) {
    if (!(Test-Path -LiteralPath $baseline)) { throw 'Baseline missing.' }
    $mismatch=@(foreach($row in (Get-Content -LiteralPath $baseline -Raw | ConvertFrom-Json)) {
        if (!(Test-Path -LiteralPath $row.Path)) { "Missing: $($row.Path)" }
        elseif ((Get-FileHash -LiteralPath $row.Path -Algorithm SHA256).Hash -ne $row.SHA256) { "Changed: $($row.Path)" }
    })
    if ($mismatch.Count) { $mismatch | Write-Output; throw 'Original binary verification failed.' }
    Write-Output 'All original DLL/EXE fingerprints unchanged.'
} else {
    if (Test-Path -LiteralPath $baseline) { throw 'Baseline exists; refusing to replace it.' }
    $rows=@(Get-ChildItem -LiteralPath (Get-GamePath) -Recurse -File | Where-Object { $_.Extension -in '.dll','.exe' -and $_.FullName -notmatch '\\BepInEx\\' } | ForEach-Object {
        [pscustomobject]@{ Path=$_.FullName; Bytes=$_.Length; SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $baseline -Encoding utf8
    Write-Output "Fingerprinted $($rows.Count) original DLL/EXE files."
}
