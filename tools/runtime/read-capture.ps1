param([string]$Path,[string[]]$Events=@('snapshot_end','avatar_candidate','avatar_test_spawned','avatar_test_audit','avatar_test_blocked'),[ValidateRange(1,2000)][int]$Limit=100)
. "$PSScriptRoot/common.ps1"
if(!$Path) { $Path=(Get-ChildItem (Join-Path $script:RepoRoot '.local/runtime-dumps') -Filter 'recon-*.ndjson' | Sort-Object CreationTimeUtc -Descending | Select-Object -First 1).FullName }
$Path=Assert-LocalOutput ([IO.Path]::GetFullPath($Path))
# Allow a live writer, but read exactly the current file length rather than following appended data.
$stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
$reader=New-Object IO.StreamReader $stream
try {
    $end=$stream.Length; $records=0; [long]$consumed=0
    while($consumed -lt $end -and !$reader.EndOfStream -and $records -lt $Limit) {
        $line=$reader.ReadLine()
        # Recon writer uses Windows CRLF. Count consumed bytes, not FileStream's
        # read-ahead position, so appends cannot extend this snapshot indefinitely.
        $consumed += [Text.Encoding]::UTF8.GetByteCount($line) + 2
        if($consumed -gt $end) { break }
        if($line -match '"event":"([^"]+)"' -and $Matches[1] -in $Events -and $line.EndsWith('}')) {
            Write-Output $line; $records++
        }
    }
} finally {$reader.Dispose()}
