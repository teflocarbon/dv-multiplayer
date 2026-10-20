# Makes (or refreshes) a test copy of Derail Valley that test builds install into, so they never
# touch the game the user plays or runs their own servers from. Nothing in the real install is
# written to: big folders are directory junctions to it, and the two mods the harness installs
# (Multiplayer, MultiplayerDevTools) are real copies. No other mod is loaded: Unity Mod Manager
# took 32 s per launch with the user's 21 mods against 10 s with these two (docs/research/
# harness-time-budget.md), and tests should see the mod alone. -AllMods links every other mod
# too, for a mod-compatibility pass; without it, a refresh removes those links again. An installed
# copy of the harness's own mods (Info.json Id Multiplayer, in any folder) is never linked.
# Run as admin, it also adds a Windows Firewall inbound allow rule for the install's own
# DerailValley.exe, so its first launch doesn't prompt. -FirewallOnly only does that, for every
# %LOCALAPPDATA%\dvmp\test-install* install.
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Derail Valley',
    [string]$TestDir = (Join-Path $env:LOCALAPPDATA 'dvmp\test-install'),
    [string[]]$OwnMods = @('Multiplayer', 'MultiplayerDevTools'),
    [switch]$AllMods,
    [switch]$FirewallOnly
)
$ErrorActionPreference = 'Stop'

# Windows Firewall keys its rules on the program path, so each install's copied exe prompts once.
# Cancel on that prompt adds Block rules; they go, and an Allow rule is added unless one exists.
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
function AllowFirewall($dir) {
    $exe = Join-Path $dir 'DerailValley.exe'
    $name = "Derail Valley test ($(Split-Path $dir -Leaf))"
    if (-not $admin) {
        Write-Host "Firewall: no rule added (not admin). Re-run this script as admin, or in an admin PowerShell:"
        Write-Host "  New-NetFirewallRule -DisplayName '$name' -Direction Inbound -Program '$exe' -Action Allow -Profile Private"
        return
    }
    # -Program matches case-insensitively, like the lower-case paths the prompt's own rules use.
    $rules = @(Get-NetFirewallApplicationFilter -Program $exe -ErrorAction SilentlyContinue | Get-NetFirewallRule | Where-Object Direction -eq Inbound)
    $rules | Where-Object Action -eq Block | Remove-NetFirewallRule
    if ($rules | Where-Object Action -eq Allow) { Write-Host "Firewall: $exe already allowed"; return }
    New-NetFirewallRule -DisplayName $name -Direction Inbound -Program $exe -Action Allow -Profile Private | Out-Null
    Write-Host "Firewall: allowed $exe ($name)"
}
if ($FirewallOnly) {
    foreach ($dir in Get-ChildItem (Join-Path $env:LOCALAPPDATA 'dvmp') -Directory -Filter 'test-install*') {
        if (Test-Path (Join-Path $dir.FullName 'DerailValley.exe')) { AllowFirewall $dir.FullName }
    }
    return
}

if (-not (Test-Path (Join-Path $GameDir 'DerailValley.exe'))) { throw "no game at $GameDir" }
$TestDir = (New-Item -ItemType Directory -Force $TestDir).FullName

function Link($name) {
    $link = Join-Path $TestDir $name
    if (Test-Path $link) { return }
    New-Item -ItemType Junction -Path $link -Target (Join-Path $GameDir $name) | Out-Null
}
Link 'DerailValley_Data'
Link 'MonoBleedingEdge'

# The launcher and Unity Mod Manager's loader (doorstop): small, copied.
foreach ($f in 'DerailValley.exe', 'UnityPlayer.dll', 'UnityCrashHandler64.exe', 'winhttp.dll', 'doorstop_config.ini') {
    $src = Join-Path $GameDir $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $TestDir $f) -Force }
}
# Started outside Steam's folder, the game finds its Steam app id here.
Set-Content -Path (Join-Path $TestDir 'steam_appid.txt') -Value '588030' -NoNewline -Encoding ascii
AllowFirewall $TestDir

$mods = Join-Path $TestDir 'Mods'
New-Item -ItemType Directory -Force $mods | Out-Null
# One of the harness's own mods, by folder name or by its Info.json Id: the user's installed
# release lives in a versioned folder ("Multiplayer 2026.10.20-dev.0945 ..."). Linked, the test
# game would load two Multiplayer mods, and a build into the test install would write through the
# link into the real one.
function IsOwnMod($dir) {
    if ($OwnMods -contains $dir.Name) { return $true }
    $info = Join-Path $dir.FullName 'Info.json'
    if (-not (Test-Path $info)) { return $false }
    try { $id = (Get-Content $info -Raw | ConvertFrom-Json).Id } catch { return $false }
    return [bool]($OwnMods | Where-Object { $_ -ieq $id })
}
$realMods = (Resolve-Path (Join-Path $GameDir 'Mods')).Path.TrimEnd('\') + '\'
foreach ($mod in Get-ChildItem $realMods -Directory) {
    $dest = Join-Path $mods $mod.Name
    if ($OwnMods -contains $mod.Name) {
        # The harness's own mods: real copies (only on first creation; builds keep them current).
        if (-not (Test-Path $dest)) { Copy-Item $mod.FullName $dest -Recurse }
    } elseif ($AllMods -and -not (Test-Path $dest) -and -not (IsOwnMod $mod)) {
        New-Item -ItemType Junction -Path $dest -Target $mod.FullName | Out-Null
    }
}
# Links left by an earlier run: to other mods unless -AllMods, and to an own mod in the real
# install always. Only junctions go: Directory.Delete without recursion removes the link itself,
# never what it points to. A real folder is left alone.
foreach ($link in Get-ChildItem $mods -Directory | Where-Object LinkType -eq 'Junction') {
    $target = "$($link.Target)"
    $own = IsOwnMod $link
    if (($own -and $target.StartsWith($realMods, [StringComparison]::OrdinalIgnoreCase)) -or (-not $own -and -not $AllMods)) {
        [System.IO.Directory]::Delete($link.FullName, $false)
        Write-Host "Removed link $($link.Name) -> $target"
    }
}
Get-ChildItem $TestDir | Select-Object Name, LinkType, Target | Format-Table -AutoSize | Out-String -Width 200
Get-ChildItem $mods | Select-Object Name, LinkType, Target | Format-Table -AutoSize | Out-String -Width 200
