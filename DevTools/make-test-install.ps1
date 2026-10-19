# Makes (or refreshes) a test copy of Derail Valley that test builds install into, so they never
# touch the game the user plays or runs their own servers from. Nothing in the real install is
# written to: big folders are directory junctions to it, the two mods the harness installs
# (Multiplayer, MultiplayerDevTools) are real copies, every other mod is a junction.
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Derail Valley',
    [string]$TestDir = (Join-Path $env:LOCALAPPDATA 'dvmp\test-install'),
    [string[]]$OwnMods = @('Multiplayer', 'MultiplayerDevTools')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $GameDir 'DerailValley.exe'))) { throw "no game at $GameDir" }
New-Item -ItemType Directory -Force $TestDir | Out-Null

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

$mods = Join-Path $TestDir 'Mods'
New-Item -ItemType Directory -Force $mods | Out-Null
foreach ($mod in Get-ChildItem (Join-Path $GameDir 'Mods') -Directory) {
    $dest = Join-Path $mods $mod.Name
    if ($OwnMods -contains $mod.Name) {
        # The harness's own mods: real copies (only on first creation; builds keep them current).
        if (-not (Test-Path $dest)) { Copy-Item $mod.FullName $dest -Recurse }
    } elseif (-not (Test-Path $dest)) {
        New-Item -ItemType Junction -Path $dest -Target $mod.FullName | Out-Null
    }
}
Get-ChildItem $TestDir | Select-Object Name, LinkType, Target | Format-Table -AutoSize | Out-String -Width 200
Get-ChildItem $mods | Where-Object { $OwnMods -contains $_.Name } | Select-Object Name, LinkType | Format-Table -AutoSize | Out-String
