<#
.SYNOPSIS
    Runs Derail Valley as a dv-multiplayer dedicated host and keeps it running.

.DESCRIPTION
    Starts the game with -mp-dedicated (see docs/design/dedicated-host.md) and watches the
    heartbeat file the mod rewrites every 5 seconds:
      - the game exits without having been told to stop (a crash): restart it;
      - the heartbeat goes stale (a hang): kill the game and restart it;
      - /stop in chat, or Ctrl+C here: the game saves and quits, and so does this script;
      - -DailyRestart HH:mm: once a day, ask the game to save and quit, then start it again.
    The mod itself saves every -AutosaveMinutes while anyone is online and when the last player
    leaves, and keeps backup copies in the career's DedicatedBackups folder.

    Steam must be running and logged in to an account that owns Derail Valley.

.EXAMPLE
    .\run-dedicated-host.ps1 -Save "My Career"

.EXAMPLE
    .\run-dedicated-host.ps1 -Save continue -Port 7777 -Visibility friends -DailyRestart 05:00
#>
param(
    # A save name or UID, a career (session) name for its latest save, or "continue".
    [Parameter(Mandatory = $true)][string]$Save,
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Derail Valley",
    [int]$Port = 0,
    [ValidateSet("", "private", "friends", "public")][string]$Visibility = "",
    [int]$AutosaveMinutes = 10,
    [int]$Fps = 30,
    # Heartbeat, logs and this script's own log.
    [string]$DataDir = "$env:LOCALAPPDATA\dv-multiplayer\dedicated-host",
    # How long the heartbeat may go unchanged before the game counts as hung.
    [int]$StaleSeconds = 120,
    # Grace for the first heartbeat after a start (loading the world takes a while).
    [int]$StartupSeconds = 600,
    # Optional daily restart, local time, e.g. "05:00".
    [string]$DailyRestart = "",
    # Show the game window instead of starting it minimised.
    [switch]$Visible
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $GameDir "DerailValley.exe"
if (-not (Test-Path $exe)) { throw "Derail Valley not found at $exe (pass -GameDir)" }
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir "logs") | Out-Null
$heartbeat = Join-Path $DataDir "heartbeat.json"
$stopRequest = "$heartbeat.stop"
$scriptLog = Join-Path $DataDir "watchdog.log"

function Say([string]$message) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $message
    Write-Host $line
    Add-Content -Path $scriptLog -Value $line
}

function Read-Heartbeat {
    try { return Get-Content -Raw -Path $heartbeat | ConvertFrom-Json } catch { return $null }
}

function Start-Game {
    Remove-Item -Force -ErrorAction SilentlyContinue $heartbeat, $stopRequest
    $log = Join-Path $DataDir ("logs\{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
    $arguments = @("-nonvr", "-mp-dedicated", "-mp-save", "`"$Save`"", "-mp-heartbeat", "`"$heartbeat`"",
                   "-mp-autosave", $AutosaveMinutes, "-mp-fps", $Fps, "-logFile", "`"$log`"")
    if ($Port -gt 0) { $arguments += @("-mp-port", $Port) }
    if ($Visibility -ne "") { $arguments += @("-mp-visibility", $Visibility) }
    $style = if ($Visible) { "Normal" } else { "Minimized" }
    $process = Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList ($arguments -join " ") -WindowStyle $style -PassThru
    Say "started the game (pid $($process.Id)), log $log"
    return $process
}

# Asks the game to save and quit (the mod polls for the stop file), then waits for it.
function Stop-Game($process, [int]$waitSeconds = 120) {
    if ($process.HasExited) { return }
    Set-Content -Path $stopRequest -Value "stop"
    if (-not $process.WaitForExit($waitSeconds * 1000)) {
        Say "the game didn't quit within $waitSeconds s; killing it"
        $process.Kill()
    }
}

$crashes = New-Object System.Collections.Generic.List[datetime]
$lastDaily = (Get-Date).Date
$game = $null
try {
    while ($true) {
        $game = Start-Game
        $started = Get-Date
        $restart = $false
        while (-not $game.HasExited) {
            Start-Sleep -Seconds 5
            $beat = Read-Heartbeat
            $age = if (Test-Path $heartbeat) { ((Get-Date) - (Get-Item $heartbeat).LastWriteTime).TotalSeconds } else { [double]::MaxValue }
            $grace = ((Get-Date) - $started).TotalSeconds -lt $StartupSeconds -and -not $beat
            if (-not $grace -and $age -gt $StaleSeconds) {
                Say ("the heartbeat is {0:N0} s old; the game looks hung, killing it" -f $age)
                $game.Kill()
                break
            }
            if ($DailyRestart -ne "") {
                $due = [datetime]::ParseExact($DailyRestart, "HH:mm", $null)
                if ((Get-Date) -ge $due -and $lastDaily -lt (Get-Date).Date) {
                    $lastDaily = (Get-Date).Date
                    Say "daily restart"
                    $restart = $true
                    Stop-Game $game
                }
            }
        }
        $beat = Read-Heartbeat
        if (-not $restart -and $beat -and $beat.stopping) {
            Say "the game was stopped (/stop or a stop request); not restarting"
            break
        }
        if (-not $restart) {
            Say "the game exited unexpectedly (code $($game.ExitCode))"
            $crashes.Add((Get-Date))
            $recent = @($crashes | Where-Object { $_ -gt (Get-Date).AddMinutes(-10) })
            if ($recent.Count -ge 3) {
                Say "three exits within 10 minutes; waiting 5 minutes before trying again"
                Start-Sleep -Seconds 300
            } else {
                Start-Sleep -Seconds 15
            }
        }
    }
} finally {
    # Ctrl+C: let the game save and quit rather than leaving it running unwatched.
    if ($game -and -not $game.HasExited) {
        Say "stopping the game"
        Stop-Game $game
    }
}
