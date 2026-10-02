# Runs inside the sandbox prepared by prepare.ps1: the real game tests as SYSTEM (through a temporary scheduled task),
# with the game mapped at C:\game. Writes report\tests.txt and report\summary.txt (PASS/FAIL, then "done").
param([Parameter(Mandatory)] [string] $Bundle)
$ErrorActionPreference = 'Stop'
$report = New-Item -ItemType Directory -Force (Join-Path $Bundle 'report')
$summary = Join-Path $report 'summary.txt'
Set-Content $summary "real game check $(Get-Date -Format o) on $([Environment]::OSVersion.VersionString)"
try {
    # Keep the output whatever happens (a timeout is reported with Write-Error).
    $ErrorActionPreference = 'Continue'
    $output = & (Join-Path $Bundle 'Invoke-AsSystem.ps1') -FilePath (Join-Path $Bundle 'tests\Pulsar.Agent.Windows.Tests.exe') `
        -ArgumentList '-class', 'Pulsar.Agent.Windows.Tests.RealGameTests', '-diagnostics', '-noColor' `
        -Environment @{ PULSAR_ACCOUNT_TESTS = '1'; PULSAR_SRCDS_DIR = 'C:\game' } -TimeoutMinutes 30 2>&1
    $code = $LASTEXITCODE
    $output | Out-File (Join-Path $report 'tests.txt') -Encoding utf8
    $text = $output -join "`n"
    $ran = $text -match 'Total: 1, Errors: 0, Failed: 0, Skipped: 0'
    Add-Content $summary ("{0} CS:S (srcds.exe) runs as its own account from SYSTEM" -f $(if ($code -eq 0 -and $ran) { 'PASS' } else { 'FAIL' }))
}
catch {
    Add-Content $summary "FAIL script - $(($_ | Out-String).Trim())"
}
finally {
    Add-Content $summary 'done'
}
