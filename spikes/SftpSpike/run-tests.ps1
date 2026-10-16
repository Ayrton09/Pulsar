# Throwaway: drives the Windows OpenSSH client (sftp.exe / ssh.exe) against the spike server on 127.0.0.1 and
# writes every output next to the data folder. Usage: powershell -File run-tests.ps1 -Data <folder> [-Port 2222]
param([string]$Data = (Join-Path $env:TEMP 'sftpspike'), [int]$Port = 2222)
$ErrorActionPreference = 'Stop'
$sftp = 'C:\Windows\System32\OpenSSH\sftp.exe'
$ssh = 'C:\Windows\System32\OpenSSH\ssh.exe'
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0\SftpSpike.exe'
New-Item -ItemType Directory -Force $Data | Out-Null
$out = Join-Path $Data 'results.txt'
Set-Content $out "run $(Get-Date -Format s)"
function Say($t) { Write-Host $t; Add-Content $out $t }

# askpass: password prompts get SPIKE_PW, "Verification code" prompts get SPIKE_CODE
$askpass = Join-Path $Data 'askpass.cmd'
Set-Content -Encoding ascii $askpass "@echo off`r`necho %~1| findstr /i `"verification`" >nul`r`nif %errorlevel%==0 (echo %SPIKE_CODE%) else (echo %SPIKE_PW%)`r`n"
Set-Content (Join-Path $Data 'ssh_config') ''
$env:SSH_ASKPASS = $askpass; $env:SSH_ASKPASS_REQUIRE = 'force'; $env:SPIKE_CODE = '123456'
$common = "-F `"$Data\ssh_config`" -o StrictHostKeyChecking=no -o UserKnownHostsFile=`"$Data\known_hosts`" -o PubkeyAuthentication=no -o ConnectTimeout=10 -o NumberOfPasswordPrompts=1 -o BatchMode=no"

function Start-Server($ki) {
    Remove-Item (Join-Path $Data 'stop') -ErrorAction SilentlyContinue
    $script:srv = Start-Process $exe -ArgumentList "server --port $Port --data `"$Data`" --ki $ki" -RedirectStandardOutput (Join-Path $Data "server-$ki.log") -RedirectStandardError (Join-Path $Data "server-$ki.err") -PassThru -WindowStyle Hidden; $null = $p.Handle
    Start-Sleep 2
}
function Stop-Server { Set-Content (Join-Path $Data 'stop') 'x'; Start-Sleep 1.5; if (-not $script:srv.HasExited) { $script:srv.Kill() } }

function Run($name, $file, $argString, $timeoutSec = 60) {
    $o = Join-Path $Data "$name.out"; $e = Join-Path $Data "$name.err"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $file -ArgumentList $argString -RedirectStandardOutput $o -RedirectStandardError $e -PassThru -WindowStyle Hidden; $null = $p.Handle
    $done = $p.WaitForExit($timeoutSec * 1000)
    if (-not $done) { $p.Kill(); $code = 'TIMEOUT' } else { $code = $p.ExitCode }
    Say "=== $name  exit=$code  $([int]$sw.ElapsedMilliseconds) ms"
    Say "> $([IO.Path]::GetFileName($file)) $argString"
    foreach ($f in $o, $e) { if (Test-Path $f) { $t = (Get-Content $f -Raw); if ($t) { Say $t.TrimEnd() } } }
    return $sw.Elapsed
}

# local files
$small = Join-Path $Data 'small.txt'; Set-Content $small 'hello sftp'
$big = Join-Path $Data 'big50.bin'
if (-not (Test-Path $big)) { $b = New-Object byte[] (50MB); (New-Object Random 7).NextBytes($b); [IO.File]::WriteAllBytes($big, $b) }
$dl = Join-Path $Data 'dl'; New-Item -ItemType Directory -Force $dl | Out-Null; Remove-Item "$dl\*" -Force -ErrorAction SilentlyContinue
$fw = $Data -replace '\\', '/'

$batch = Join-Path $Data 'batch1.txt'
Set-Content -Encoding ascii $batch @"
pwd
mkdir d1
put "$fw/small.txt" d1/small.txt
ls -l d1
rename d1/small.txt d1/renamed.txt
get d1/renamed.txt "$fw/dl/got.txt"
rm d1/renamed.txt
rmdir d1
ls -l
-get ../../x "$fw/dl/x1"
-get /../x "$fw/dl/x2"
-get ..\..\x "$fw/dl/x3"
-put "$fw/small.txt" ../../escaped.txt
-ls ../..
"@

foreach ($ki in 'two', 'single') {
    Say "################ server --ki $ki ################"
    Start-Server $ki
    try {
        $env:SPIKE_PW = 'pw-alice'
        Run "alice-batch-$ki" $sftp "$common -P $Port -b `"$batch`" alice@127.0.0.1" | Out-Null
        if ($ki -eq 'two') {
            Say "dl/got.txt: $(Get-Content "$dl\got.txt" -ErrorAction SilentlyContinue)  (x1/x2/x3 exist? $((Test-Path "$dl\x1") -or (Test-Path "$dl\x2") -or (Test-Path "$dl\x3")))"
            # -v for negotiated algorithms, with SendEnv LANG to check that a refused env request is harmless
            $env:LANG = 'es_ES.UTF-8'
            $b2 = Join-Path $Data 'batch2.txt'; Set-Content -Encoding ascii $b2 "pwd`r`nls -l"
            Run 'alice-verbose-env' $sftp "$common -v -o SendEnv=LANG -P $Port -b `"$b2`" alice@127.0.0.1" | Out-Null
            Remove-Item Env:\LANG
            # wrong password
            $env:SPIKE_PW = 'wrong'; Run 'alice-wrong-password' $sftp "$common -P $Port -b `"$b2`" alice@127.0.0.1" | Out-Null
            $env:SPIKE_PW = 'pw-alice'
        }
        # bob: password + code (two prompts or two rounds)
        $env:SPIKE_PW = 'pw-bob'
        Run "bob-2fa-$ki" $sftp "$common -v -P $Port -b `"$($Data)\batch2.txt`" bob@127.0.0.1" | Out-Null
        if ($ki -eq 'two') {
            $env:SPIKE_CODE = '000000'; Run 'bob-wrong-code' $sftp "$common -P $Port -b `"$($Data)\batch2.txt`" bob@127.0.0.1" | Out-Null; $env:SPIKE_CODE = '123456'
            Run 'bob-srv1' $sftp "$common -P $Port -b `"$($Data)\batch2.txt`" bob.srv1@127.0.0.1" | Out-Null
            Run 'bob-unknown-server' $sftp "$common -P $Port -b `"$($Data)\batch2.txt`" bob.nope@127.0.0.1" | Out-Null

            # no shell / exec / pty / forwarding
            $env:SPIKE_PW = 'pw-alice'
            Run 'ssh-shell' $ssh "$common -v -p $Port alice@127.0.0.1" 20 | Out-Null
            Run 'ssh-shell-pty' $ssh "$common -tt -p $Port alice@127.0.0.1" 20 | Out-Null
            Run 'ssh-exec' $ssh "$common -p $Port alice@127.0.0.1 whoami" 20 | Out-Null
            Run 'ssh-x11-agent' $ssh "$common -X -A -v -p $Port alice@127.0.0.1 whoami" 20 | Out-Null
            # local forward: start, connect to the local port, look at the verbose log
            $fo = Join-Path $Data 'ssh-L.out'; $fe = Join-Path $Data 'ssh-L.err'
            $pl = Start-Process $ssh -ArgumentList "$common -v -N -L 127.0.0.1:19999:127.0.0.1:$Port -p $Port alice@127.0.0.1" -RedirectStandardOutput $fo -RedirectStandardError $fe -PassThru -WindowStyle Hidden; $null = $p.Handle
            Start-Sleep 4
            try { $tc = New-Object Net.Sockets.TcpClient('127.0.0.1', 19999); Start-Sleep 2; $tc.Close() } catch { Say "ssh -L: connect to the local port failed: $_" }
            Start-Sleep 1; if (-not $pl.HasExited) { $pl.Kill() }
            Say '=== ssh -L (verbose, filtered)'; Say ((Get-Content $fe | Select-String -Pattern 'channel|forward|prohibited|refused|failed|Authenticat' | ForEach-Object { $_.Line }) -join "`n")
            # remote forward
            $ro = Join-Path $Data 'ssh-R.out'; $re = Join-Path $Data 'ssh-R.err'
            $pr = Start-Process $ssh -ArgumentList "$common -v -N -o ExitOnForwardFailure=yes -R 19998:127.0.0.1:$Port -p $Port alice@127.0.0.1" -RedirectStandardOutput $ro -RedirectStandardError $re -PassThru -WindowStyle Hidden; $null = $p.Handle
            Start-Sleep 5; $exited = $pr.HasExited; if (-not $exited) { $pr.Kill() }
            Say "=== ssh -R (exited by itself because the forward failed: $exited)"; Say ((Get-Content $re | Select-String -Pattern 'forward|prohibited|refused|failed|Authenticat|remote port' | ForEach-Object { $_.Line }) -join "`n")

            # throughput: 50 MB put and get
            $bb = Join-Path $Data 'batch-big-put.txt'; Set-Content -Encoding ascii $bb "put `"$fw/big50.bin`" big50.bin"
            $t1 = Run 'big-put' $sftp "$common -P $Port -b `"$bb`" alice@127.0.0.1" 180
            $bg = Join-Path $Data 'batch-big-get.txt'; Set-Content -Encoding ascii $bg "get big50.bin `"$fw/dl/big50.back`""
            $t2 = Run 'big-get' $sftp "$common -P $Port -b `"$bg`" alice@127.0.0.1" 180
            Say ("50 MB put: {0:F2} s ({1:F0} MB/s); get: {2:F2} s ({3:F0} MB/s)  [includes connect and sign-in]" -f $t1.TotalSeconds, (50 / $t1.TotalSeconds), $t2.TotalSeconds, (50 / $t2.TotalSeconds))
            Say ("hash equal after put+get: " + ((Get-FileHash $big).Hash -eq (Get-FileHash "$dl\big50.back").Hash))
        }
    } finally { Stop-Server }
}
# server logs, filtered
foreach ($ki in 'two', 'single') { Say "---- server-$ki.log (filtered)"; Say ((Get-Content (Join-Path $Data "server-$ki.log") | Where-Object { $_ -match '^\d\d:' } | ForEach-Object { if ($_.Length -gt 170) { $_.Substring(0, 170) } else { $_ } }) -join "`n") }
Remove-Item (Join-Path $Data 'jail\big50.bin'), (Join-Path $Data 'big50.bin') -ErrorAction SilentlyContinue
