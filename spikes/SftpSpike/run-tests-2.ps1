# Throwaway, part 2: DevTunnels client probe of refused requests, SSH.NET checks (both keyboard-interactive modes), restricted algorithm sets.
param([string]$Data = (Join-Path $env:TEMP 'sftpspike'), [int]$Port = 2222)
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0\SftpSpike.exe'
$sftp = 'C:\Windows\System32\OpenSSH\sftp.exe'
$out = Join-Path $Data 'results2.txt'; Set-Content $out "run $(Get-Date -Format s)"
function Say($t) { Write-Host $t; Add-Content $out $t }
function Start-Server($ki) {
    Remove-Item (Join-Path $Data 'stop') -ErrorAction SilentlyContinue
    $script:srv = Start-Process $exe -ArgumentList "server --port $Port --data `"$Data`" --ki $ki" -RedirectStandardOutput (Join-Path $Data "s2-$ki.log") -RedirectStandardError (Join-Path $Data "s2-$ki.err") -PassThru -WindowStyle Hidden
    Start-Sleep 2
}
function Stop-Server { Set-Content (Join-Path $Data 'stop') 'x'; Start-Sleep 1.5; if (-not $script:srv.HasExited) { $script:srv.Kill() } }
function Client($mode) { Say "--- $mode"; Say ((& $exe $mode --port $Port 2>&1 | Out-String).TrimEnd()) }

foreach ($ki in 'two', 'single') {
    Say "################ server --ki $ki"
    Start-Server $ki
    try { Client 'probe'; Client 'sshnet' } finally { Stop-Server }
    Say "---- server log ($ki), refusals only"
    Say ((Get-Content (Join-Path $Data "s2-$ki.log") | Where-Object { $_ -match 'REFUSED|refused|bad ' } | ForEach-Object { if ($_.Length -gt 150) { $_.Substring(0, 150) } else { $_ } }) -join "`n")
}

# restricted algorithms with the Windows OpenSSH client
$askpass = Join-Path $Data 'askpass.cmd'
$env:SSH_ASKPASS = $askpass; $env:SSH_ASKPASS_REQUIRE = 'force'; $env:SPIKE_PW = 'pw-alice'; $env:SPIKE_CODE = '123456'
$b = Join-Path $Data 'batch2.txt'
$common = "-F `"$Data\ssh_config`" -o StrictHostKeyChecking=no -o UserKnownHostsFile=`"$Data\known_hosts`" -o PubkeyAuthentication=no -o ConnectTimeout=10 -o NumberOfPasswordPrompts=1 -o BatchMode=no -v -P $Port"
foreach ($set in @{ SPIKE_ENC = 'aes256-gcm@openssh.com' }, @{ SPIKE_KEX = 'diffie-hellman-group16-sha512' }, @{ SPIKE_KEX = 'ecdh-sha2-nistp384' }, @{ SPIKE_ENC = 'aes256-cbc' }) {
    $k = ($set.Keys)[0]; Set-Item "Env:$k" $set[$k]
    Start-Server 'two'
    try {
        $o = Join-Path $Data 'alg.out'; $e = Join-Path $Data 'alg.err'
        $p = Start-Process $sftp -ArgumentList "$common -b `"$b`" alice@127.0.0.1" -RedirectStandardOutput $o -RedirectStandardError $e -PassThru -WindowStyle Hidden; $null = $p.Handle
        if (-not $p.WaitForExit(30000)) { $p.Kill() }
        Say "=== $k=$($set[$k])  exit=$($p.ExitCode)"
        Say ((Get-Content $e | Select-String -Pattern '^debug1: kex: (algorithm|host key|server->client)|Permission denied|no matching|Unable to negotiate|Authenticated to' | ForEach-Object { $_.Line }) -join "`n")
    } finally { Stop-Server; Remove-Item "Env:$k" }
}
