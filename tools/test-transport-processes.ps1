param([switch]$AbruptHostExit, [switch]$AbruptClientExit,
    [ValidateSet('circle','world')][string]$Mode = 'circle',
    [ValidateRange(0,1000)][int]$LatencyMilliseconds = 0,
    [ValidateRange(0,1000)][int]$JitterMilliseconds = 0,
    [ValidateRange(0,50)][int]$LossPercent = 0)
if ($AbruptHostExit -and $AbruptClientExit) { throw 'Choose one process termination case.' }
. "$PSScriptRoot/reverse/common.ps1"
$dotnet = Get-LocalDotnet
$harness = Join-Path $script:RepoRoot 'tools/TransportHarness/bin/Release/net9.0/TransportHarness.dll'
$output = Join-Path $script:RepoRoot '.local/evidence/transport'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$socket = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
$port = ([Net.IPEndPoint]$socket.Client.LocalEndPoint).Port
$socket.Dispose()
$linkSocket = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
$linkPort = ([Net.IPEndPoint]$linkSocket.Client.LocalEndPoint).Port
$linkSocket.Dispose()
$label = if ($AbruptHostExit) { 'abrupt-host' } elseif ($AbruptClientExit) { 'abrupt-client' } else { "latency$LatencyMilliseconds-jitter$JitterMilliseconds-loss$LossPercent" }
if ($Mode -eq 'world') { $label = 'world-' + $label }
$hostLog = Join-Path $output "$label-host.log"
$clientLog = Join-Path $output "$label-client.log"
$hostError = Join-Path $output "$label-host-error.log"
$clientError = Join-Path $output "$label-client-error.log"
$hostProcess = $null
$clientProcess = $null
$linkProcess = $null
try {
    # Start-Process joins arguments; quote the observed absolute harness path explicitly.
    $quotedHarness = '"' + $harness + '"'
    $hostDuration = if ($AbruptHostExit -or $AbruptClientExit) { 10 } else { 8 }
    $clientDuration = if ($AbruptHostExit -or $AbruptClientExit) { 12 } else { 6 }
    $joinPort = $port
    if ($LatencyMilliseconds -gt 0 -or $JitterMilliseconds -gt 0 -or $LossPercent -gt 0) {
        $joinPort = $linkPort
        $linkProcess = Start-Process -FilePath $dotnet -ArgumentList @($quotedHarness,'link',$linkPort,$port,($hostDuration+1),$LatencyMilliseconds,$JitterMilliseconds,$LossPercent) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output "$label-link.log") -RedirectStandardError (Join-Path $output "$label-link-error.log")
    }
    $hostProcess = Start-Process -FilePath $dotnet -ArgumentList @($quotedHarness,'host',$port,'transport-test-build',$hostDuration,'MainScene',$Mode) -WindowStyle Hidden -PassThru -RedirectStandardOutput $hostLog -RedirectStandardError $hostError
    $clientProcess = Start-Process -FilePath $dotnet -ArgumentList @($quotedHarness,'client','127.0.0.1',$joinPort,'transport-test-build',$clientDuration,'MainScene',$Mode) -WindowStyle Hidden -PassThru -RedirectStandardOutput $clientLog -RedirectStandardError $clientError
    if ($AbruptHostExit -or $AbruptClientExit) {
        $deadline = [DateTime]::UtcNow.AddSeconds(4)
        do { Start-Sleep -Milliseconds 100; $text = Get-Content -LiteralPath $clientLog -Raw -ErrorAction SilentlyContinue } while ($text -notmatch 'Handshake accepted' -and [DateTime]::UtcNow -lt $deadline)
        if ($text -notmatch 'Handshake accepted') { throw 'Harness client never connected.' }
        Start-Sleep -Milliseconds 500
        # Only the exact hidden helper process created above is terminated.
        if ($AbruptHostExit) {
            $hostProcess.Kill()
            if (!$clientProcess.WaitForExit(15000)) { throw 'Abrupt host timeout did not finish.' }
        } else { $clientProcess.Kill() }
    }
    if (!$hostProcess.WaitForExit(15000) -or !$clientProcess.WaitForExit(1000)) { throw 'Harness processes timed out.' }
    if ($null -ne $linkProcess) {
        if (!$linkProcess.WaitForExit(5000)) { throw 'Test condition link did not finish.' }
        $linkProcess.Refresh(); Get-Content -LiteralPath (Join-Path $output "$label-link.log")
        if ($linkProcess.ExitCode -ne 0) { Get-Content -LiteralPath (Join-Path $output "$label-link-error.log"); throw 'Test condition link failed.' }
    }
    $hostProcess.Refresh(); $clientProcess.Refresh()
    Get-Content -LiteralPath $hostLog,$clientLog
    if ((!$AbruptHostExit -and $hostProcess.ExitCode -ne 0) -or (!$AbruptClientExit -and $clientProcess.ExitCode -ne 0)) {
        Get-Content -LiteralPath $hostError,$clientError
        throw 'Independent host/client process test failed.'
    }
    if ($AbruptHostExit -and (Get-Content -LiteralPath $clientLog -Raw) -notmatch 'Peer timed out') { throw 'Abrupt host loss was not detected as a timeout.' }
    if ($AbruptClientExit -and ((Get-Content -LiteralPath $hostLog -Raw) -notmatch 'Peer timed out' -or (Get-Content -LiteralPath $hostLog -Raw) -notmatch '"state":"Hosting"')) { throw 'Abrupt client loss did not return host to Hosting.' }
    Write-Output 'AUTOMATED VERIFIED: two independent transport processes exchanged synthetic position/yaw updates.'
} finally {
    foreach ($taskProcess in @($hostProcess,$clientProcess,$linkProcess)) {
        if ($null -ne $taskProcess) { $taskProcess.Refresh(); if (!$taskProcess.HasExited) { $taskProcess.Kill() } }
    }
}
