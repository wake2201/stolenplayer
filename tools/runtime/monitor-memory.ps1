param([Parameter(Mandatory)][string]$Session,[ValidateRange(30,3600)][int]$Seconds=300,
    [ValidatePattern('^[a-z0-9-]+\.ndjson$')][string]$OutputName='memory.ndjson')
. "$PSScriptRoot/common.ps1"
$data=Get-RuntimeSession $Session
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ReconMemory {
 [StructLayout(LayoutKind.Sequential)] public struct MEMORYSTATUSEX {
  public uint length, load; public ulong totalPhys, availPhys, totalPage, availPage, totalVirtual, availVirtual, availExtended;
 }
 [StructLayout(LayoutKind.Sequential)] public struct PERFORMANCE_INFORMATION {
  public uint size; public UIntPtr commitTotal, commitLimit, commitPeak, physicalTotal, physicalAvailable, systemCache, kernelTotal, kernelPaged, kernelNonpaged, pageSize; public uint handles, processes, threads;
 }
 [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX state);
 [DllImport("psapi.dll", SetLastError=true)] public static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, uint size);
}
'@
$writer = New-Object IO.StreamWriter (Join-Path $data.Directory $OutputName),$false
try {
    for ($i=0; $i -lt $Seconds; $i++) {
        $process=Get-SessionProcess $data
        $memory=New-Object ReconMemory+MEMORYSTATUSEX
        $memory.length=[Runtime.InteropServices.Marshal]::SizeOf($memory)
        $perf=New-Object ReconMemory+PERFORMANCE_INFORMATION
        $perf.size=[Runtime.InteropServices.Marshal]::SizeOf($perf)
        $memoryOk=[ReconMemory]::GlobalMemoryStatusEx([ref]$memory)
        $perfOk=[ReconMemory]::GetPerformanceInfo([ref]$perf,$perf.size)
        $row=[ordered]@{ utc=[DateTime]::UtcNow.ToString('o'); alive=($null -ne $process); availableBytes=$(if($memoryOk){$memory.availPhys}else{$null}); commitBytes=$(if($perfOk){$perf.commitTotal.ToUInt64()*$perf.pageSize.ToUInt64()}else{$null}); commitLimitBytes=$(if($perfOk){$perf.commitLimit.ToUInt64()*$perf.pageSize.ToUInt64()}else{$null}); workingSetBytes=$(if($process){$process.WorkingSet64}else{$null}); privateBytes=$(if($process){$process.PrivateMemorySize64}else{$null}); }
        $writer.WriteLine(($row | ConvertTo-Json -Compress)); $writer.Flush()
        if (!$process) { break }
        Start-Sleep -Seconds 1
    }
} finally { $writer.Dispose() }
