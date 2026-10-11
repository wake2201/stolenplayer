param([Parameter(Mandatory)][ValidateSet('Backup','Restore')][string]$Action, [Parameter(Mandatory)][string]$Directory,[string]$ExpectedCurrentDirectory)
. "$PSScriptRoot/common.ps1"
$s1Dir = Assert-LocalOutput ([IO.Path]::GetFullPath($Directory))
$s1PrefsFile = Join-Path $s1Dir 'playerprefs-before.clixml'
$s1KeyName = 'Software\CookieDev\Thief Simulator 2'
# Unity can store eight-byte floating-point data under REG_DWORD. .NET reads it as
# Int64 but SetValue(DWord) insists on Int32. Preserve both the original kind and width.
if (!('S1RegistryRaw' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class S1RegistryRaw {
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, EntryPoint="RegSetValueExW")]
    public static extern int Set(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name, int reserved, uint kind, byte[] data, int bytes);
}
'@
}
function Read-S1Prefs {
    $s1Key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($s1KeyName,$false)
    if (!$s1Key) { return [pscustomobject]@{ Exists=$false; Values=@() } }
    try {
        if ($s1Key.GetSubKeyNames().Count) { throw 'Unexpected game preference subkeys; inspect before proceeding.' }
        $s1Values = @($s1Key.GetValueNames() | Sort-Object | ForEach-Object { [pscustomobject]@{ Name=$_; Kind=[int]$s1Key.GetValueKind($_); Value=$s1Key.GetValue($_,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } })
        return [pscustomobject]@{ Exists=$true; Values=$s1Values }
    } finally { $s1Key.Dispose() }
}
if ($Action -eq 'Backup') { if (Test-Path -LiteralPath $s1PrefsFile) { throw 'PlayerPrefs backup already exists.' }; Read-S1Prefs | Export-Clixml -LiteralPath $s1PrefsFile; Write-Output 'Backed up game PlayerPrefs only.'; return }
if (Get-Process -Name 'Thief Simulator 2' -ErrorAction SilentlyContinue) { throw 'Close game before PlayerPrefs restoration.' }
$s1Before = Import-Clixml -LiteralPath $s1PrefsFile
$s1Current=Read-S1Prefs
if($ExpectedCurrentDirectory) {
    $s1ExpectedDir=Assert-LocalOutput ([IO.Path]::GetFullPath($ExpectedCurrentDirectory))
    $s1Expected=Import-Clixml -LiteralPath (Join-Path $s1ExpectedDir 'playerprefs-before.clixml')
    if(($s1Expected | ConvertTo-Json -Depth 5 -Compress) -ne ($s1Current | ConvertTo-Json -Depth 5 -Compress)) { throw 'Current PlayerPrefs changed since the reviewed post-test snapshot; refusing restoration.' }
}
$s1Key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($s1KeyName,$true)
if (!$s1Key -and $s1Before.Exists) { $s1Key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($s1KeyName) }
if ($s1Key) {
    try {
        $s1NamesBefore = @($s1Before.Values | ForEach-Object { $_.Name })
        foreach ($s1Name in $s1Key.GetValueNames()) { if ($s1Name -notin $s1NamesBefore) { $s1Key.DeleteValue($s1Name,$false) } }
        foreach ($s1Value in $s1Before.Values) {
            if($ExpectedCurrentDirectory) {
                $s1Live=@($s1Current.Values | Where-Object Name -eq $s1Value.Name)
                if($s1Live.Count -eq 1 -and ($s1Live[0] | ConvertTo-Json -Depth 5 -Compress) -eq ($s1Value | ConvertTo-Json -Depth 5 -Compress)) { continue }
            }
            $s1Data = $s1Value.Value
            if ($s1Value.Kind -eq 4 -and $s1Data -is [long]) {
                $s1Raw = [BitConverter]::GetBytes([long]$s1Data)
                $s1Error = [S1RegistryRaw]::Set($s1Key.Handle,$s1Value.Name,0,4,$s1Raw,$s1Raw.Length)
                if ($s1Error) { throw "Raw Unity preference restoration failed: $s1Error" }
                continue
            }
            if ($s1Value.Kind -eq 3) { $s1Data = [byte[]]$s1Data } elseif ($s1Value.Kind -eq 4) { $s1Data=[int]$s1Data } elseif ($s1Value.Kind -eq 11) { $s1Data=[long]$s1Data } elseif ($s1Value.Kind -eq 7) { $s1Data=[string[]]$s1Data }
            $s1Key.SetValue($s1Value.Name,$s1Data,[Microsoft.Win32.RegistryValueKind]$s1Value.Kind)
        }
    } finally { $s1Key.Dispose() }
}
$s1After = Read-S1Prefs
if (($s1Before.Values | ConvertTo-Json -Depth 5 -Compress) -ne ($s1After.Values | ConvertTo-Json -Depth 5 -Compress)) { throw 'PlayerPrefs restoration mismatch.' }
Write-Output 'Game PlayerPrefs restored and all value names, types and data verified.'
