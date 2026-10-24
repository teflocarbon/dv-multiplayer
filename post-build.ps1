param
(   
    [switch]$NoArchive,
    [string]$GameDir,
    [string]$Target,
	[string]$Ver,
	# Debug builds only: the GameNetworkingSockets DLL for the test-only GNS transport.
	[string]$GnsDll
)

# The GNS transport lets the mod run without Steam, so its DLL never goes in build/ (which a
# release zips) and never ships: a Debug build installs it straight into the game, and a release
# removes any left from an earlier Debug build (docs/testing.md, "GNS pairs").
$gnsDllName = 'GameNetworkingSockets.dll'
if (!$NoArchive -and $GnsDll) {
	throw "A release must not install $gnsDllName"
}
Remove-Item -LiteralPath ($PSScriptRoot + "/build/$gnsDllName") -ErrorAction SilentlyContinue

Write-Host "Root: $PSScriptRoot"
Write-Host "No Archive: $NoArchive"
Write-Host "Target: $Target"
Write-Host "Game Dir: $GameDir"
Write-Host "Version: $Ver"

$compress

#Update the JSON. The build gets this build's version; the repo's info.json keeps only releases',
#so a Debug build's dev version never ends up in it.
$json = Get-Content ($PSScriptRoot + '/info.json') -raw | ConvertFrom-Json
$json.Version = $Ver
$modId = $json.Id
New-Item -ItemType Directory -Force ($PSScriptRoot + '/build') | Out-Null
$json | ConvertTo-Json -depth 32| set-content ($PSScriptRoot + '/build/info.json')
if (!$NoArchive) {
	Copy-Item ($PSScriptRoot + '/build/info.json') -Destination ($PSScriptRoot + '/info.json')
}

# Unity Mod Manager folders are user-named and may include the release version.
# If the configured conventional path is not itself the installed mod, locate the
# one enabled installation by its stable info.json Id.
$configuredInfo = Join-Path $GameDir 'info.json'
$configuredMatches = $false
if (Test-Path -LiteralPath $configuredInfo) {
	try {
		$configuredJson = Get-Content -LiteralPath $configuredInfo -Raw | ConvertFrom-Json
		$configuredMatches = $configuredJson.Id -eq $modId
	} catch {
		$configuredMatches = $false
	}
}

if (!$configuredMatches) {
	$modsRoot = Split-Path -Parent $GameDir
	$matchingInstalls = @(
		Get-ChildItem -LiteralPath $modsRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
			$candidateInfo = Join-Path $_.FullName 'info.json'
			if (!(Test-Path -LiteralPath $candidateInfo)) { return }
			try {
				$candidateJson = Get-Content -LiteralPath $candidateInfo -Raw | ConvertFrom-Json
				if ($candidateJson.Id -eq $modId) { $_.FullName }
			} catch { }
		}
	)
	if ($matchingInstalls.Count -eq 1) {
		$GameDir = $matchingInstalls[0]
		Write-Host "Resolved installed mod directory: $GameDir"
	} elseif ($matchingInstalls.Count -gt 1) {
		throw "Multiple installed mod directories with Id '$modId' were found: $($matchingInstalls -join ', ')"
	}
}

#Copy files to Build Dir
Copy-Item ($Target) -Destination ("$PSScriptRoot/build/")
Copy-Item ($PSScriptRoot + '/LICENSE') -Destination ("$PSScriptRoot/build/")
# The dedicated server's watchdog and its double-click launcher ship with the mod.
Copy-Item ($PSScriptRoot + '/Tools/DedicatedHost/run-dedicated-host.ps1') -Destination ("$PSScriptRoot/build/")
Copy-Item ($PSScriptRoot + '/Tools/DedicatedHost/Start Dedicated Server.cmd') -Destination ("$PSScriptRoot/build/")
# So does the server app (Tools/DedicatedHost/ServerApp), which Multiplayer.csproj builds first in the
# same configuration: -NoArchive is a Debug build.
$serverAppConfig = if ($NoArchive) { 'Debug' } else { 'Release' }
$serverApp = "$PSScriptRoot/Tools/DedicatedHost/ServerApp/bin/$serverAppConfig/net48/DV Multiplayer Server.exe"
if (Test-Path -LiteralPath $serverApp) {
	Copy-Item -LiteralPath $serverApp -Destination ("$PSScriptRoot/build/")
} else {
	Write-Warning "The server app isn't built ($serverApp), so the mod goes without it"
}

#Copy files to Game Dir
if (!(Test-Path ($GameDir))) {
	New-Item -ItemType Directory -Path $GameDir
}
Copy-Item ("$PSScriptRoot/build/*") -Destination ($GameDir)
if ($NoArchive -and $GnsDll) {
	if (Test-Path -LiteralPath $GnsDll) {
		Copy-Item -LiteralPath $GnsDll -Destination ($GameDir)
	} else {
		Write-Host "No $GnsDll, so -dvmp-transport gns won't work in this install (DevTools/Gns/build-gns.ps1 builds it)"
	}
} elseif (!$NoArchive) {
	Remove-Item -LiteralPath (Join-Path $GameDir $gnsDllName) -ErrorAction SilentlyContinue
}


#Files to be compressed if we make a zip
$compress = @{
	Path = ($PSScriptRoot + "/build/*")
	CompressionLevel = "Fastest"
	DestinationPath = ($PSScriptRoot + "/Releases/$modId $Ver.zip")
}

#Are we building a release or debug?
if (!$NoArchive){
	if (!(Test-Path ($PSScriptRoot + "/releases"))) {
		New-Item -ItemType Directory -Path ($PSScriptRoot + "/releases")
	}
	
	Write-Host "Zip Path: " $compress.DestinationPath
    Compress-Archive @compress -Force

	Add-Type -AssemblyName System.IO.Compression.FileSystem
	$zip = [System.IO.Compression.ZipFile]::OpenRead($compress.DestinationPath)
	try {
		$leaked = @($zip.Entries | Where-Object { $_.Name -eq $gnsDllName })
	} finally {
		$zip.Dispose()
	}
	if ($leaked.Count -gt 0) {
		Remove-Item -LiteralPath $compress.DestinationPath
		throw "The release zip contained $gnsDllName; deleted it"
	}
}
