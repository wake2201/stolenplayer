# N1 local two-process smoke test: a host process and a separate client process (TS2MP.NetSmoke.exe, the real
# SessionNode + join-code handshake over UDP on 127.0.0.1) each get the code through their process environment,
# exactly like the launcher passes it to the game. Also checks that a second client with a wrong code is refused.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'build\tests\TS2MP.NetSmoke.exe'
$root = Join-Path $env:TEMP ("ts2mp-n1-smoke-" + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Force $root | Out-Null
$script:fail = 0; $script:pass = 0
function Check($name, $cond) { if ($cond) { $script:pass++; Write-Host "PASS  $name" } else { $script:fail++; Write-Host "FAIL  $name" } }
function StartWithCode($code, [string[]]$a, $tag) {
    $psi = New-Object Diagnostics.ProcessStartInfo($exe, ($a -join ' '))
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['TS2MP_JOIN_CODE'] = $code
    return [Diagnostics.Process]::Start($psi)
}

$code = (& $exe newcode).Trim()
$port = 37800
$h = StartWithCode $code @('host', '127.0.0.1', "$port", "`"$root`"") 'host'
Start-Sleep -Milliseconds 500
$c = StartWithCode $code @('client', '127.0.0.1', "$port", "`"$root`"") 'client'
$c.WaitForExit(25000) | Out-Null; $h.WaitForExit(25000) | Out-Null
$co = $c.StandardOutput.ReadToEnd(); $ho = $h.StandardOutput.ReadToEnd()
Write-Host "   host  : $($ho.Trim())"; Write-Host "   client: $($co.Trim())"
Check 'host process + client process: join-code handshake, message both ways, clean exit' ($h.ExitCode -eq 0 -and $c.ExitCode -eq 0 -and $co -match 'SMOKE_OK' -and $ho -match 'SMOKE_OK host authenticated 1')

$port = 37801
$h = StartWithCode $code @('host', '127.0.0.1', "$port", "`"$root\b`"") 'host'
Start-Sleep -Milliseconds 500
$wrong = (& $exe newcode).Trim()
$c = StartWithCode $wrong @('client', '127.0.0.1', "$port", "`"$root\b`"") 'client'
$c.WaitForExit(25000) | Out-Null
$co = $c.StandardOutput.ReadToEnd()
Write-Host "   client (wrong code): $($co.Trim())"
Check 'client process with a wrong code is refused (AuthFailed, no retry)' ($c.ExitCode -eq 5 -and $co -match 'AuthFailed')
if (-not $h.HasExited) { $h.Kill() }
$logs = (Get-ChildItem $root -Recurse -Filter *.log | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
Check 'neither process wrote a join code to its log' ($logs -notmatch [regex]::Escape($code) -and $logs -notmatch $code.Replace('-', '') -and $logs -notmatch $wrong.Replace('-', ''))
Write-Host "RESULT: $script:pass passed, $script:fail failed (logs: $root)"
if ($script:fail) { exit 1 } else { exit 0 }
