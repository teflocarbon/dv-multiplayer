param
(   
    [switch]$NoArchive,
    [string]$GameDir,
    [string]$Target,
	[string]$Ver
)

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

#Copy files to Game Dir
if (!(Test-Path ($GameDir))) {
	New-Item -ItemType Directory -Path $GameDir
}
Copy-Item ("$PSScriptRoot/build/*") -Destination ($GameDir)


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
}
