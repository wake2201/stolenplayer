param([Parameter(Mandatory)][string]$Session,[switch]$RestoreFileSet,[string]$ExpectedCurrentDirectory)
. "$PSScriptRoot/common.ps1"
if(Get-Process -Name 'Thief Simulator 2' -ErrorAction SilentlyContinue) { throw 'Close the game before save restoration.' }
$data=Get-RuntimeSession $Session
$backup=Join-Path $data.Directory 'save-before'
if(!(Test-Path -LiteralPath $backup)) { throw 'No save backup for this session.' }
$save=Join-Path $env:USERPROFILE 'AppData/LocalLow/CookieDev/Thief Simulator 2'
$after=Assert-LocalOutput (Join-Path $data.Directory ('save-before-restore-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')))
$expected=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$backupFiles=@(Get-ChildItem -LiteralPath $backup -Recurse -File)
if($ExpectedCurrentDirectory) {
    $expectedDirectory=Assert-LocalOutput ([IO.Path]::GetFullPath($ExpectedCurrentDirectory))
    $currentFiles=@(Get-ChildItem -LiteralPath $save -Recurse -File | Where-Object { $_.Extension -eq '.thief' -or $_.Name -like 'saveInfo*.txt' })
    $reviewedFiles=@(Get-ChildItem -LiteralPath $expectedDirectory -Recurse -File)
    if($currentFiles.Count -ne $reviewedFiles.Count) { throw 'Save file set changed after the reviewed post-test snapshot.' }
    foreach($currentFile in $currentFiles) {
        $relative=$currentFile.FullName.Substring($save.Length+1)
        $reviewed=Join-Path $expectedDirectory $relative
        if(!(Test-Path -LiteralPath $reviewed) -or (Get-FileHash -LiteralPath $reviewed).Hash -ne (Get-FileHash -LiteralPath $currentFile.FullName).Hash) { throw 'Save changed after post-test snapshot; refusing restoration.' }
        $original=Join-Path $backup $relative
        if((!(Test-Path -LiteralPath $original) -or (Get-FileHash -LiteralPath $original).Hash -ne (Get-FileHash -LiteralPath $currentFile.FullName).Hash) -and $currentFile.LastWriteTimeUtc -lt ([DateTime]$data.StartUtc).ToUniversalTime()) { throw 'Changed save predates the owned fixture; refusing restoration.' }
    }
}
foreach($file in $backupFiles) { [void]$expected.Add($file.FullName.Substring($backup.Length+1)) }
$created=@()
if($RestoreFileSet) {
    foreach($file in Get-ChildItem -LiteralPath $save -Recurse -File | Where-Object { $_.Extension -eq '.thief' -or $_.Name -like 'saveInfo*.txt' }) {
        $relative=$file.FullName.Substring($save.Length+1)
        if(!$expected.Contains($relative)) {
            if($file.CreationTimeUtc -lt ([DateTime]$data.StartUtc).ToUniversalTime().AddSeconds(-2)) { throw 'Extra save file predates this test; refusing to relocate it.' }
            $created+=$file
        }
    }
}
New-Item -ItemType Directory -Path $after | Out-Null
foreach($file in $backupFiles) {
    $relative=$file.FullName.Substring($backup.Length+1)
    if($file.Extension -ne '.thief' -and $file.Name -notlike 'saveInfo*.txt') { throw 'Unexpected backup file.' }
    $target=[IO.Path]::GetFullPath((Join-Path $save $relative))
    $allowed=[IO.Path]::GetFullPath($save)+[IO.Path]::DirectorySeparatorChar
    if(!$target.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid restoration target.' }
    if($ExpectedCurrentDirectory -and (Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $file.FullName).Hash) { continue }
    if(Test-Path -LiteralPath $target) {
        $preserved=Join-Path $after $relative
        New-Item -ItemType Directory -Path (Split-Path $preserved) -Force | Out-Null
        Copy-Item -LiteralPath $target -Destination $preserved
    }
    New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    if((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw 'Restored save hash mismatch.' }
}
foreach($file in $created) {
    $source=[IO.Path]::GetFullPath($file.FullName)
    $allowed=[IO.Path]::GetFullPath($save)+[IO.Path]::DirectorySeparatorChar
    if(!$source.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid created-save source.' }
    $relative=$source.Substring($allowed.Length)
    $destination=Assert-LocalOutput (Join-Path (Join-Path $after 'created') $relative)
    if(Test-Path -LiteralPath $destination) { throw 'Created-save preservation destination already exists.' }
    $hash=(Get-FileHash -LiteralPath $source).Hash
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Move-Item -LiteralPath $source -Destination $destination
    if((Get-FileHash -LiteralPath $destination).Hash -ne $hash) { throw 'Preserved created-save hash mismatch.' }
}
if($RestoreFileSet) { Write-Output "Restored original save file set; preserved $($created.Count) test-created files locally." }
Write-Output 'Backed-up save files restored byte-for-byte; post-test files preserved locally.'
