# Spike 1 with real game servers: SteamCMD bootstrap/progress and console capture of srcds (CS:S, GMod)
# and optionally cs2.exe, in pty and pipes mode. Servers bind to 127.0.0.1 with sv_lan 1.
#
#   .\spikes\ConsoleCapture\run-spike1-games.ps1 -Root D:\PulsarSpike1            # SteamCMD + CS:S + GMod (a few GB)
#   .\spikes\ConsoleCapture\run-spike1-games.ps1 -Root D:\PulsarSpike1 -IncludeCs2 # + CS2 (~60 GB)
#
# Downloads SteamCMD from Valve's official URL. Captures go to <repo>\artifacts\spike1.
param(
    [Parameter(Mandatory)] [string] $Root,
    [switch] $IncludeCs2,
    [switch] $SkipInstall
)
$ErrorActionPreference = 'Stop'

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$out = Join-Path $repo 'artifacts\spike1'
New-Item -ItemType Directory -Force $out, $Root | Out-Null

dotnet build $PSScriptRoot -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'build ConsoleCapture failed' }
$capture = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\ConsoleCapture.exe'

function Invoke-Capture([string] $Name, [string] $Mode, [string] $Exe, [int] $Duration, [string[]] $Actions, [string[]] $Arguments) {
    $captureArgs = @('--mode', $Mode, '--exe', $Exe, '--out', (Join-Path $out "$Name-$Mode.jsonl"), '--duration', $Duration)
    if ($Mode -eq 'pty') { $captureArgs += '--usestdhandles' }
    foreach ($action in $Actions) {
        # Windows PowerShell 5.1 has no ternary operator.
        if ($action -like 'ctrlc:*') { $captureArgs += @('--ctrlc', $action.Split(':')[1]) }
        else { $captureArgs += @('--send', $action) }
    }
    $captureArgs += '--'
    $captureArgs += $Arguments
    Write-Host "=== $Name ($Mode)"
    & $capture @captureArgs | Select-Object -Last 20 | Out-String | Add-Content (Join-Path $out 'summary.txt')
}

# --- SteamCMD -----------------------------------------------------------------------------------
$steamDir = Join-Path $Root 'steamcmd'
$steamExe = Join-Path $steamDir 'steamcmd.exe'
if (-not (Test-Path $steamExe)) {
    New-Item -ItemType Directory -Force $steamDir | Out-Null
    $zip = Join-Path $Root 'steamcmd.zip'
    Invoke-WebRequest 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip' -OutFile $zip
    Expand-Archive $zip $steamDir -Force
    Remove-Item $zip
}

Get-AuthenticodeSignature $steamExe | Select-Object Status, StatusMessage, @{n = 'Signer'; e = { $_.SignerCertificate.Subject } } |
    Format-List | Out-String | Set-Content (Join-Path $out 'steamcmd-signature.txt')

# Bootstrap (first run self-updates) and an idle run, in both modes, to compare output buffering.
Invoke-Capture 'steamcmd-bootstrap' 'pipes' $steamExe 600 @() @('+quit')
Invoke-Capture 'steamcmd-idle' 'pty' $steamExe 300 @() @('+quit')

# --- Installs (progress format and exit codes) ---------------------------------------------------
$games = [ordered]@{
    css  = @{ AppId = 232330; Mode = 'pty' }
    gmod = @{ AppId = 4020; Mode = 'pipes' }
}
if ($IncludeCs2) { $games['cs2'] = @{ AppId = 730; Mode = 'pty' } }

if (-not $SkipInstall) {
    foreach ($game in $games.Keys) {
        $dir = Join-Path $Root $game
        Invoke-Capture "install-$game" $games[$game].Mode $steamExe 7200 @() @(
            '+force_install_dir', $dir, '+login', 'anonymous', '+app_update', $games[$game].AppId, 'validate', '+quit')
    }
}

# --- Running the servers ---------------------------------------------------------------------------
$commonActions = @('40:status', '45:echo pulsar-console-input-test', '50:quit')
$srcdsArgs = @{
    css  = @('-game', 'cstrike', '-console', '+ip', '127.0.0.1', '-port', '27115', '+sv_lan', '1', '+maxplayers', '8', '+map', 'de_dust2')
    gmod = @('-game', 'garrysmod', '-console', '+ip', '127.0.0.1', '-port', '27116', '+sv_lan', '1', '+maxplayers', '8', '+gamemode', 'sandbox', '+map', 'gm_construct')
}

foreach ($game in @('css', 'gmod')) {
    $exe = Join-Path $Root "$game\srcds.exe"
    foreach ($mode in @('pty', 'pipes')) {
        Invoke-Capture "run-$game" $mode $exe 90 $commonActions $srcdsArgs[$game]
    }
    # Ctrl+C handling (pty only).
    Invoke-Capture "ctrlc-$game" 'pty' $exe 70 @('ctrlc:40') $srcdsArgs[$game]
}

if ($IncludeCs2) {
    $cs2 = Join-Path $Root 'cs2\game\bin\win64\cs2.exe'
    $cs2Args = @('-dedicated', '-ip', '127.0.0.1', '-port', '27117', '+sv_lan', '1', '-maxplayers', '10', '+map', 'de_dust2')
    foreach ($mode in @('pty', 'pipes')) {
        Invoke-Capture 'run-cs2' $mode $cs2 150 @('90:status', '95:echo pulsar-console-input-test', '100:quit') $cs2Args
    }
}

Write-Host "Captures in $out"
