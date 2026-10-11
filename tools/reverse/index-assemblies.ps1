param([string[]]$Assemblies=@('Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','com.rlabrecque.steamworks.net.dll','FileBasedPrefs.dll'))
. "$PSScriptRoot/common.ps1"
$cecil = Join-Path $script:RepoRoot '.local/tools/cecil/lib/netstandard2.0/Mono.Cecil.dll'
if (!(Test-Path -LiteralPath $cecil)) { throw 'Bootstrap Mono.Cecil first.' }
Add-Type -Path $cecil
function Get-TypeRows($Types) {
    foreach ($type in $Types) {
        $kind = if ($type.IsEnum) { 'enum' } elseif ($type.IsInterface) { 'interface' } elseif ($type.IsValueType) { 'struct' } else { 'class' }
        [pscustomobject]@{
            Name=$type.FullName; Namespace=$type.Namespace; Kind=$kind
            BaseType=$(if ($type.BaseType) {$type.BaseType.FullName} else {$null})
            Interfaces=@($type.Interfaces | ForEach-Object { $_.InterfaceType.FullName })
            Fields=@($type.Fields | ForEach-Object { "$(if($_.IsStatic){'static '})$($_.FullName)" })
            Properties=@($type.Properties | ForEach-Object FullName)
            Methods=@($type.Methods | ForEach-Object { "$(if($_.IsStatic){'static '})$($_.FullName)" })
            Attributes=@($type.CustomAttributes | ForEach-Object { $_.AttributeType.FullName })
        }
        if ($type.HasNestedTypes) { Get-TypeRows $type.NestedTypes }
    }
}
$managed = Get-ManagedPath
$rows = foreach ($name in $Assemblies) {
    if ([IO.Path]::GetFileName($name) -ne $name) { throw 'Use Managed filenames.' }
    $path=Join-Path $managed $name
    $assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($path)
    try {
        [pscustomobject]@{ Name=$name; Identity=$assembly.Name.FullName; Runtime=$assembly.MainModule.RuntimeVersion
            SHA256=(Get-FileHash -LiteralPath $path).Hash
            References=@($assembly.MainModule.AssemblyReferences | ForEach-Object FullName)
            Attributes=@($assembly.CustomAttributes | ForEach-Object { [pscustomobject]@{Type=$_.AttributeType.FullName; Arguments=@($_.ConstructorArguments | ForEach-Object {$_.Value.ToString()})} })
            Types=@(Get-TypeRows $assembly.MainModule.Types)
        }
    } finally { $assembly.Dispose() }
}
$out=Join-Path $script:RepoRoot '.local/type-index.json'
$rows | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $out -Encoding utf8
$rows | ForEach-Object { "$($_.Name): $($_.Types.Count) types; runtime $($_.Runtime)" }
Write-Output $out
