#Requires -RunAsAdministrator
# Runs spike 2 as SYSTEM in session 0 (like the real agent service) through a temporary scheduled task.
# ONLY run this inside a disposable environment (Windows Sandbox or a VM): it creates local users and
# changes user rights. It removes what it creates, but do not run it on a machine you care about.
param(
    [Parameter(Mandatory)] [string] $Bundle,
    [string] $WorkRoot = 'C:\PulsarSpike2'
)
$ErrorActionPreference = 'Stop'

$report = Join-Path $Bundle 'report'
New-Item -ItemType Directory -Force $report | Out-Null
$transcript = Join-Path $report 'run-spike2.txt'
Start-Transcript -Path $transcript -Force | Out-Null

try {
    Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber | Format-List | Out-String | Write-Host

    $exe = Join-Path $Bundle 'IsolatedLaunch\IsolatedLaunch.exe'
    $probe = Join-Path $Bundle 'ConsoleProbe'
    $taskName = 'PulsarSpike2'

    $action = New-ScheduledTaskAction -Execute $exe -Argument "--probe-dir `"$probe`" --root `"$WorkRoot`" --report `"$report`""
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null

    try {
        Start-ScheduledTask -TaskName $taskName
        $deadline = (Get-Date).AddMinutes(5)
        do {
            Start-Sleep -Seconds 2
            $state = (Get-ScheduledTask -TaskName $taskName).State
        } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)

        $info = Get-ScheduledTaskInfo -TaskName $taskName
        Write-Host "task finished: state=$state lastResult=$($info.LastTaskResult)"
    }
    finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }

    # Leftover check: nothing created by the spike should remain.
    Write-Host 'leftover accounts:' (@(Get-LocalUser | Where-Object Name -like 'pulsar_spk_*').Name -join ', ')
    Write-Host 'leftover work dir:' (Test-Path $WorkRoot)
    Write-Host 'leftover probe processes:' @(Get-Process ConsoleProbe -ErrorAction SilentlyContinue).Count

    Get-Content (Join-Path $report 'spike2.log') | Write-Host
}
finally {
    Stop-Transcript | Out-Null
}
