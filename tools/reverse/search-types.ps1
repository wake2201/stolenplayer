param([Parameter(Mandatory)][string]$Pattern, [switch]$Members)
. "$PSScriptRoot/common.ps1"
$file = Join-Path $script:RepoRoot '.local/type-index.json'
if (!(Test-Path -LiteralPath $file)) { throw 'Run index-assemblies.ps1 first.' }
$index = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
foreach ($assembly in $index) {
    foreach ($type in $assembly.Types) {
        if ($type.Name -match $Pattern) { $type | Select-Object @{n='Assembly';e={$assembly.Name}},Name,Kind,BaseType }
        if ($Members) {
            foreach ($member in @($type.Fields)+@($type.Properties)+@($type.Methods)) {
                if ($member -match $Pattern) { [pscustomobject]@{ Assembly=$assembly.Name; Type=$type.Name; Member=$member } }
            }
        }
    }
}
