<#
.SYNOPSIS
Runs a command as SYSTEM in session 0 (what the agent service looks like) through a temporary scheduled task, prints
its output and exits with its exit code.

.DESCRIPTION
Used by CI for the tests that need SYSTEM: starting a process as another account (CreateProcessAsUser) needs
privileges an elevated administrator does not have. Run it only on a disposable, elevated machine (a CI runner,
Windows Sandbox).

.EXAMPLE
.\scripts\ci\Invoke-AsSystem.ps1 -FilePath tests\...\Pulsar.Agent.Windows.Tests.exe -Environment @{ PULSAR_ACCOUNT_TESTS = '1' }
#>
param(
    [Parameter(Mandatory)][string]$FilePath,
    [string[]]$ArgumentList = @(),
    [hashtable]$Environment = @{},
    [int]$TimeoutMinutes = 20
)

$ErrorActionPreference = 'Stop'
$FilePath = (Resolve-Path $FilePath).Path
$id = [guid]::NewGuid().ToString('N')
# A folder both this (elevated) user and SYSTEM can write to (SYSTEM's own temp is not readable here), and nobody
# else: run.cmd in it runs as SYSTEM, so a folder every user can write to would let any of them run code as SYSTEM.
# Created with those rules, not changed after: the name never exists with the drive's inherited ones. A name of
# its own (no shared parent folder a user could have made first).
$work = Join-Path $env:SystemDrive "PulsarAsSystem-$id"
$private = New-Object Security.AccessControl.DirectorySecurity
$private.SetAccessRuleProtection($true, $false)
foreach ($sidText in 'S-1-5-18', 'S-1-5-32-544') {
    $private.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sidText),
        'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
if ($PSVersionTable.PSEdition -eq 'Core') { [IO.FileSystemAclExtensions]::Create((New-Object IO.DirectoryInfo $work), $private) }
else { (New-Object IO.DirectoryInfo $work).Create($private) }
$log = Join-Path $work 'output.log'
$exitFile = Join-Path $work 'exit.txt'

$lines = @('@echo off', "cd /d `"$(Split-Path $FilePath)`"")
foreach ($name in $Environment.Keys) {
    $lines += "set `"$name=$($Environment[$name])`""
}
$arguments = ($ArgumentList | ForEach-Object { "`"$_`"" }) -join ' '
$lines += "`"$FilePath`" $arguments > `"$log`" 2>&1"
# Redirection first: "echo 0> file" would redirect handle 0 and leave the file empty.
$lines += "> `"$exitFile`" echo %ERRORLEVEL%"
Set-Content -Path (Join-Path $work 'run.cmd') -Value $lines -Encoding Ascii

$taskName = "PulsarAsSystem-$id"
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$(Join-Path $work 'run.cmd')`""
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes $TimeoutMinutes)
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
try {
    Start-ScheduledTask -TaskName $taskName
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    # The file can exist a moment before cmd.exe has written the code into it.
    $code = $null
    while ($null -eq $code -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $text = Get-Content $exitFile -Raw -ErrorAction SilentlyContinue
        if ($text -and $text.Trim() -match '^-?\d+$') {
            $code = [int]$text.Trim()
        }
    }

    if (Test-Path $log) {
        Get-Content $log
    }

    if ($null -eq $code) {
        # What is still running from the command, for diagnosis.
        Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*$(Split-Path $FilePath -Leaf)*" -or $_.CommandLine -like "*$id*" } |
            Format-Table ProcessId, ParentProcessId, Name, CommandLine -AutoSize | Out-String -Width 400 | Write-Host
        Write-Error "Timed out after $TimeoutMinutes minutes."
        exit 1
    }

    Write-Host "Exit code (as SYSTEM): $code"
    exit $code
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
