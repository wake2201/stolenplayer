param([Parameter(Mandatory)][string]$Type,[Parameter(Mandatory)][string]$Method,[string]$Assembly='Assembly-CSharp.dll')
. "$PSScriptRoot/common.ps1"
Add-Type -Path (Join-Path $script:RepoRoot '.local/tools/cecil/lib/netstandard2.0/Mono.Cecil.dll')
if ([IO.Path]::GetFileName($Assembly) -ne $Assembly) { throw 'Use a Managed filename.' }
$asm=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path (Get-ManagedPath) $Assembly))
try {
    $typeDefinition=$asm.MainModule.GetType($Type)
    if (!$typeDefinition) { throw "Type not found: $Type" }
    $methods=@($typeDefinition.Methods | Where-Object Name -eq $Method)
    if (!$methods.Count) { throw "Method not found: $Type::$Method" }
    foreach ($candidate in $methods) {
        $candidate.FullName
        if ($candidate.HasBody) { $candidate.Body.Instructions | ForEach-Object ToString }
    }
} finally { $asm.Dispose() }
