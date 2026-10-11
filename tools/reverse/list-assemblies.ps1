. "$PSScriptRoot/common.ps1"
$managed = Get-ManagedPath
$rows = foreach ($file in Get-ChildItem -LiteralPath $managed -Filter '*.dll') {
    try { $identity = [Reflection.AssemblyName]::GetAssemblyName($file.FullName).FullName } catch { $identity='Unreadable: ' + $_.Exception.Message }
    [pscustomobject]@{ Name=$file.Name; Path=$file.FullName; Bytes=$file.Length; Identity=$identity; SHA256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
}
$out = Join-Path $script:RepoRoot '.local/assembly-list.json'
$rows | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $out -Encoding utf8
$rows | Select-Object Name,Bytes,Identity | Format-Table -AutoSize
