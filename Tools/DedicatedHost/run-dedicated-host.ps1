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

    This window shows the mod's log as the game writes it (logins, kicks, warnings, errors), and
    takes server commands: type one and press Enter, e.g. /help, /save, /give Bob EOTLantern,
    /restore Bob, /kick Bob. They run as the server's owner, and the replies show here. A line
    without a slash is said in chat as the server.

    The server has its own folder (-ServerDir, default %LOCALAPPDATA%\dv-multiplayer\dedicated-host\server)
    and is started from it. Unity Mod Manager loads mods from the Mods folder of the folder the game
    starts in, so the server has its own mods, and each mod keeps its settings (the Multiplayer
    server name, password, admins) inside its folder, apart from your own game's:
      - The first run copies every mod of your game into the server's Mods folder.
      - Every start refreshes each of them from your game's copy (matched by mod ID), except its
        Settings.xml, so versions always match what players have.
      - To keep a mod off the server, delete its folder from the server's Mods. To add one, copy its
        folder in. Players must have the same mods as the server to join.
      - -SharedMods starts the game from its own folder instead, with your game's mods and settings.
    Whether a mod is enabled is kept by Unity Mod Manager for the whole install, so it's shared;
    leaving a mod out of the server's folder is what keeps it off the server.

    Steam must be running and logged in to an account that owns Derail Valley.

.EXAMPLE
    .\run-dedicated-host.ps1 -Save "My Career"

.EXAMPLE
    .\run-dedicated-host.ps1 -Save continue -Port 7777 -Visibility friends -DailyRestart 05:00
#>
param(
    # A save name or UID, a career (session) name for its latest save, or "continue".
    [Parameter(Mandatory = $true)][string]$Save,
    # The game's folder. Found from this script's place when it's in the game's Mods folder.
    [string]$GameDir = "",
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
    [switch]$Visible,
    # Show the mod's debug lines here too (they're in the game's log either way).
    [switch]$VerboseLog,
    # The server's own folder, with its own Mods (see above).
    [string]$ServerDir = "",
    # Start from the game's folder, sharing its mods and settings, as before.
    [switch]$SharedMods
)

$ErrorActionPreference = "Stop"
if ($GameDir -eq "") {
    # Shipped in the mod's folder: <game>\Mods\<Multiplayer>\run-dedicated-host.ps1.
    $fromHere = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $GameDir = if (Test-Path (Join-Path $fromHere "DerailValley.exe")) { $fromHere }
               else { "C:\Program Files (x86)\Steam\steamapps\common\Derail Valley" }
}
if ($ServerDir -eq "") { $ServerDir = Join-Path $DataDir "server" }
$exe = Join-Path $GameDir "DerailValley.exe"
if (-not (Test-Path $exe)) { throw "Derail Valley not found at $exe (pass -GameDir)" }
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir "logs") | Out-Null
$heartbeat = Join-Path $DataDir "heartbeat.json"
$stopRequest = "$heartbeat.stop"
# Commands typed here, one per line; the mod reads and runs them (DedicatedHost.RunConsoleCommands).
$commandFile = "$heartbeat.cmd"
$scriptLog = Join-Path $DataDir "watchdog.log"

# What's been typed at the prompt so far, kept on screen below anything printed meanwhile.
$script:typed = ""
$script:logPath = $null
$script:logReader = $null
$script:logRest = ""

function Write-Out([string]$text, [ConsoleColor]$colour = [ConsoleColor]::Gray) {
    if ($script:typed.Length -gt 0) { [Console]::Write("`r" + (" " * ($script:typed.Length + 2)) + "`r") }
    Write-Host $text -ForegroundColor $colour
    if ($script:typed.Length -gt 0) { [Console]::Write("> " + $script:typed) }
}

function Say([string]$message) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $message
    Write-Out $line White
    Add-Content -Path $scriptLog -Value $line
}

function Read-Heartbeat {
    try { return Get-Content -Raw -Path $heartbeat | ConvertFrom-Json } catch { return $null }
}

# The mod's lines from the game's log as they're written. Only whole lines: the rest waits for the
# next read.
function Show-NewLogLines {
    if (-not $script:logPath) { return }
    if (-not $script:logReader) {
        if (-not (Test-Path $script:logPath)) { return }
        try {
            $stream = [IO.File]::Open($script:logPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            $script:logReader = New-Object IO.StreamReader($stream)
        } catch { return }
    }
    $chunk = $script:logReader.ReadToEnd()
    if ($chunk.Length -eq 0) { return }
    $lines = ($script:logRest + $chunk) -split "`n"
    $script:logRest = $lines[-1]
    if ($lines.Length -lt 2) { return } # (0..-1 would count down, not be empty)
    foreach ($raw in $lines[0..($lines.Length - 2)]) {
        $line = $raw.TrimEnd("`r")
        if (-not $line.StartsWith("[Multiplayer] ")) { continue }
        if (-not $VerboseLog -and $line.Contains("] [Debug] ")) { continue }
        $text = $line.Substring(14)
        $colour = if ($text.Contains("[Error]")) { [ConsoleColor]::Red }
                  elseif ($text.Contains("[Warning]")) { [ConsoleColor]::Yellow }
                  elseif ($text.Contains("[Console]")) { [ConsoleColor]::Cyan }
                  else { [ConsoleColor]::Gray }
        Write-Out $text $colour
    }
}

# Keys typed here: Enter sends the line to the game as a command.
function Read-Typed {
    try { if (-not [Console]::KeyAvailable) { return } } catch { return } # no console to read
    while ([Console]::KeyAvailable) {
        $key = [Console]::ReadKey($true)
        if ($key.Key -eq [ConsoleKey]::Enter) {
            $line = $script:typed.Trim()
            if ($script:typed.Length -gt 0) { [Console]::WriteLine() }
            $script:typed = ""
            if ($line.Length -gt 0) {
                Add-Content -Path $commandFile -Value $line
                Add-Content -Path $scriptLog -Value ("{0:yyyy-MM-dd HH:mm:ss} > {1}" -f (Get-Date), $line)
            }
        } elseif ($key.Key -eq [ConsoleKey]::Backspace) {
            if ($script:typed.Length -gt 0) {
                $script:typed = $script:typed.Substring(0, $script:typed.Length - 1)
                [Console]::Write("`b `b")
            }
        } elseif ($key.KeyChar -ge [char]' ') {
            if ($script:typed.Length -eq 0) { [Console]::Write("> ") }
            $script:typed += $key.KeyChar
            [Console]::Write($key.KeyChar)
        }
    }
}

# A mod folder's ID, from its info.json (Unity Mod Manager's).
function Get-ModId([string]$folder) {
    $info = Get-ChildItem -LiteralPath $folder -Filter "info.json" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $info) { return $null }
    try { return (Get-Content -Raw -LiteralPath $info.FullName | ConvertFrom-Json).Id } catch { return $null }
}

# Files of one mod folder into another, except its settings; only what changed. robocopy, as some
# mods' files sit deeper than Windows' 260-character path limit allows Copy-Item.
function Sync-Mod([string]$from, [string]$to) {
    $out = & robocopy $from $to /E /XF Settings.xml /R:1 /W:1 /NJH /NFL /NDL /NP
    if ($LASTEXITCODE -ge 8) { throw "copying $from to $to failed (robocopy $LASTEXITCODE): $($out -join ' ')" }
    $files = $out | Where-Object { $_ -match '^\s*Files\s*:' } | Select-Object -First 1
    if ($files -and $files -match ':\s*\d+\s+(\d+)') { return [int]$Matches[1] }
    return 0
}

# The server's own folder: its Mods, refreshed from the game's, and Steam's app ID for a game
# started outside Steam's own folder.
function Initialize-ServerDir {
    $mods = Join-Path $ServerDir "Mods"
    $gameMods = Join-Path $GameDir "Mods"
    $first = -not (Test-Path $mods)
    New-Item -ItemType Directory -Force -Path $mods | Out-Null
    Set-Content -Path (Join-Path $ServerDir "steam_appid.txt") -Value "588030" -NoNewline
    $gameById = @{}
    foreach ($dir in Get-ChildItem -LiteralPath $gameMods -Directory -ErrorAction SilentlyContinue) {
        $id = Get-ModId $dir.FullName
        if ($id) { $gameById[$id] = $dir.FullName }
    }
    if ($first) {
        foreach ($id in $gameById.Keys) {
            $to = Join-Path $mods (Split-Path $gameById[$id] -Leaf)
            Sync-Mod $gameById[$id] $to | Out-Null
            $settings = Join-Path $gameById[$id] "Settings.xml"
            if (Test-Path $settings) { Copy-Item -LiteralPath $settings -Destination $to }
        }
        Say "made the server's folder $ServerDir with a copy of your $($gameById.Count) mod(s) and their settings; delete a mod's folder from its Mods to keep it off the server"
        return
    }
    $serverIds = @()
    foreach ($dir in Get-ChildItem -LiteralPath $mods -Directory) {
        $id = Get-ModId $dir.FullName
        if (-not $id) { continue }
        $serverIds += $id
        if (-not $gameById.ContainsKey($id)) { Say "the server has mod $id, which your game doesn't; keeping it as it is"; continue }
        $copied = Sync-Mod $gameById[$id] $dir.FullName
        if ($copied -gt 0) { Say "updated the server's $id from your game ($copied file(s))" }
    }
    if (-not ($serverIds -contains "Multiplayer")) { throw "the server's Mods folder ($mods) has no Multiplayer mod: copy it in from $gameMods" }
    Say ("server mods: " + (($serverIds | Sort-Object) -join ", "))
}

function Start-Game {
    Remove-Item -Force -ErrorAction SilentlyContinue $heartbeat, $stopRequest, $commandFile
    $log = Join-Path $DataDir ("logs\{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
    $arguments = @("-nonvr", "-mp-dedicated", "-mp-save", "`"$Save`"", "-mp-heartbeat", "`"$heartbeat`"",
                   "-mp-autosave", $AutosaveMinutes, "-mp-fps", $Fps, "-logFile", "`"$log`"")
    if ($Port -gt 0) { $arguments += @("-mp-port", $Port) }
    if ($Visibility -ne "") { $arguments += @("-mp-visibility", $Visibility) }
    $style = if ($Visible) { "Normal" } else { "Minimized" }
    $workingDir = if ($SharedMods) { $GameDir } else { $ServerDir }
    $process = Start-Process -FilePath $exe -WorkingDirectory $workingDir -ArgumentList ($arguments -join " ") -WindowStyle $style -PassThru
    if ($script:logReader) { $script:logReader.Dispose() }
    $script:logReader = $null
    $script:logRest = ""
    $script:logPath = $log
    Say "started the game (pid $($process.Id)), log $log"
    Say "type a command and press Enter (/help lists them); Ctrl+C saves and stops the server"
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
        if (-not $SharedMods) { Initialize-ServerDir }
        $game = Start-Game
        $started = Get-Date
        $restart = $false
        while (-not $game.HasExited) {
            # Keys and the log every quarter second, the heartbeat every 5.
            for ($tick = 0; $tick -lt 20 -and -not $game.HasExited; $tick++) {
                Start-Sleep -Milliseconds 250
                Read-Typed
                Show-NewLogLines
            }
            if ($game.HasExited) { break }
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
        Show-NewLogLines
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
    if ($script:logReader) { $script:logReader.Dispose() }
}
