# Local PostgreSQL for development and integration tests, run as a normal user process (no service, no admin).
# Uses the PostgreSQL Windows binaries zip (https://www.enterprisedb.com/download-postgresql-binaries) extracted
# anywhere; this script never downloads anything.
#
#   .\scripts\dev\postgres.ps1 -Action Init  -PgHome D:\PulsarDev\pg18\pgsql -Root D:\PulsarDev\postgres
#   .\scripts\dev\postgres.ps1 -Action Start -PgHome ... -Root ...
#   .\scripts\dev\postgres.ps1 -Action Stop  -PgHome ... -Root ...
#
# Init creates a cluster that only listens on localhost, a superuser "postgres" and a "pulsar_dev" role with
# CREATEDB (integration tests create one database per test run). Random passwords are written to
# <Root>\connection.txt, which is the only place they are stored.
param(
    [Parameter(Mandatory)] [ValidateSet('Init', 'Start', 'Stop', 'Status')] [string] $Action,
    [Parameter(Mandatory)] [string] $PgHome,
    [Parameter(Mandatory)] [string] $Root,
    [int] $Port = 5432
)
$ErrorActionPreference = 'Stop'

$bin = Join-Path $PgHome 'bin'
$data = Join-Path $Root 'data'
$logFile = Join-Path $Root 'postgres.log'
$connectionFile = Join-Path $Root 'connection.txt'

function Invoke-Native([string] $exe, [string[]] $arguments) {
    & (Join-Path $bin $exe) @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

# The server started by pg_ctl inherits every inheritable handle of pg_ctl. When pg_ctl runs as a normal native
# command, those include the pipes PowerShell (or a terminal host, or CI) reads, which then never close and the
# caller hangs. Start-Process without redirection uses ShellExecute, which passes no handles. pg_ctl's own output
# goes to NUL: a file would stay open in the server and block the next call; the server logs to postgres.log.
function Invoke-PgCtl([string[]] $arguments) {
    $quoted = ($arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
    $process = Start-Process -FilePath $env:ComSpec -WindowStyle Hidden -PassThru `
        -ArgumentList "/d /c `"`"$(Join-Path $bin 'pg_ctl.exe')`" $quoted > NUL 2>&1 < NUL`""
    $null = $process.Handle  # keeps the handle so ExitCode is available after exit
    $process.WaitForExit()   # not Start-Process -Wait: it would also wait for the server it starts
    if ($process.ExitCode -ne 0) { throw "pg_ctl $($arguments[0]) failed with exit code $($process.ExitCode); see $logFile" }
    Write-Host "pg_ctl $($arguments[0]): OK"
}

function New-Password {
    $bytes = [byte[]]::new(24)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()  # Fill() does not exist on Windows PowerShell 5.1
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    # URL-safe base64 without padding: no characters that need quoting in a connection string.
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

switch ($Action) {
    'Init' {
        if (Test-Path $data) { throw "$data already exists; refusing to overwrite an existing cluster" }
        New-Item -ItemType Directory -Force $Root | Out-Null
        # connection.txt and initdb.pw hold the passwords: nobody but this user (and SYSTEM) may open the folder, or
        # what is created in it. Done before anything is written there.
        $private = New-Object Security.AccessControl.DirectorySecurity
        $private.SetAccessRuleProtection($true, $false)
        $owners = @([Security.Principal.WindowsIdentity]::GetCurrent().User, (New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'))
        foreach ($sid in $owners) {
            $private.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        }
        Set-Acl -LiteralPath $Root -AclObject $private

        $superPassword = New-Password
        $devPassword = New-Password
        # Written first so a failure later in Init never loses the passwords.
        Set-Content -Path $connectionFile -Encoding utf8 -Value @"
# Local development PostgreSQL created by scripts/dev/postgres.ps1. Do not commit.
Pulsar (panel):        Host=localhost;Port=$Port;Database=pulsar;Username=pulsar_dev;Password=$devPassword
PULSAR_TEST_POSTGRES:  Host=localhost;Port=$Port;Username=pulsar_dev;Password=$devPassword
Superuser:             Host=localhost;Port=$Port;Username=postgres;Password=$superPassword
"@
        $pwFile = Join-Path $Root 'initdb.pw'
        try {
            Set-Content -Path $pwFile -Value $superPassword -NoNewline -Encoding ascii
            Invoke-Native 'initdb.exe' @('-D', $data, '-U', 'postgres', '--pwfile', $pwFile, '--auth', 'scram-sha-256',
                '--encoding', 'UTF8', '--locale-provider', 'builtin', '--builtin-locale', 'C.UTF-8')
        }
        finally {
            Remove-Item $pwFile -ErrorAction SilentlyContinue
        }

        # No BOM: Windows PowerShell's "-Encoding utf8" would write one in the middle of the file.
        [IO.File]::AppendAllText((Join-Path $data 'postgresql.conf'),
            "`n# Added by scripts/dev/postgres.ps1`nlisten_addresses = 'localhost'`nport = $Port`n",
            [Text.UTF8Encoding]::new($false))

        Invoke-PgCtl @('start', '-D', $data, '-l', $logFile, '-w')
        $env:PGPASSWORD = $superPassword
        try {
            Invoke-Native 'psql.exe' @('-h', 'localhost', '-p', $Port, '-U', 'postgres', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1',
                '-c', "CREATE ROLE pulsar_dev LOGIN CREATEDB PASSWORD '$devPassword';",
                '-c', 'CREATE DATABASE pulsar OWNER pulsar_dev;')
        }
        finally {
            Remove-Item Env:PGPASSWORD
        }

        Write-Host "Cluster ready on localhost:$Port. Connection strings: $connectionFile"
    }
    'Start' { Invoke-PgCtl @('start', '-D', $data, '-l', $logFile, '-w') }
    'Stop' { Invoke-PgCtl @('stop', '-D', $data, '-m', 'fast', '-w') }
    'Status' { & (Join-Path $bin 'pg_ctl.exe') status -D $data }
}
