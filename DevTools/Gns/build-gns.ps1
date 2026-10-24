<#
.SYNOPSIS
Builds Valve's GameNetworkingSockets for the Debug-only GNS transport (docs/testing.md, "GNS pairs").

.DESCRIPTION
Valve publishes no GameNetworkingSockets binaries, so this builds one from pinned sources:
GameNetworkingSockets at its v1.6.1 tag and vcpkg at the baseline that tag's vcpkg.json names,
which supplies protobuf. The result is one self-contained GameNetworkingSockets.dll: the C runtime
and protobuf are linked statically and Windows' own BCrypt does the crypto, so it needs nothing
else on a PC or under Wine on a pod.

A Debug build of Multiplayer copies the DLL into the game's mod folder when it is in OutDir.
Release builds never do, and post-build.ps1 refuses to package it.

Needs Visual Studio 2022 with the C++ workload (for cl, CMake and Ninja) and git. It downloads
the GNS source (~5 MB), vcpkg (~30 MB plus its ~7 MB tool) and, through vcpkg, protobuf and
abseil (~15 MB), all from GitHub. A first build takes about 15 minutes; later runs reuse WorkDir.

.PARAMETER OutDir
Where the DLL goes. The Multiplayer build looks here by default.

.PARAMETER WorkDir
Sources and intermediate files.
#>
param(
    [string]$OutDir = "$env:LOCALAPPDATA\dvmp\gns\1.6.1",
    [string]$WorkDir = "$env:LOCALAPPDATA\dvmp\gns\work"
)

$ErrorActionPreference = 'Stop'

# The pins. GNS v1.6.1 is the tag's commit, not the tag, so a moved tag can't change the build.
$GnsRepo = 'https://github.com/ValveSoftware/GameNetworkingSockets.git'
$GnsCommit = 'd534c19aa760df3fb75fd20db13ba1932b8a5463'   # v1.6.1, 2026-10-03
$VcpkgRepo = 'https://github.com/microsoft/vcpkg.git'
$VcpkgCommit = '522253caf47268c1724f486a035e927a42a90092' # builtin-baseline of GNS v1.6.1's vcpkg.json
$Triplet = 'x64-windows-static'                            # static libraries, static CRT (/MT)

# Windows PowerShell turns a native tool's stderr into a terminating error under Stop, and git and
# vcpkg write progress there: judge native tools by their exit code alone.
function Invoke-Checked {
    param([string]$File, [string[]]$Arguments)
    $ErrorActionPreference = 'Continue'
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

# A shallow checkout of exactly one commit.
function Get-PinnedSource {
    param([string]$Repo, [string]$Commit, [string]$Dir)
    if (!(Test-Path "$Dir\.git")) {
        New-Item -ItemType Directory -Force $Dir | Out-Null
        Invoke-Checked git @('-C', $Dir, 'init', '-q')
        Invoke-Checked git @('-C', $Dir, 'remote', 'add', 'origin', $Repo)
    }
    $ErrorActionPreference = 'Continue'
    $head = (& git -C $Dir rev-parse HEAD 2>$null)
    if ($head -ne $Commit) {
        Invoke-Checked git @('-C', $Dir, 'fetch', '-q', '--depth', '1', 'origin', $Commit)
        Invoke-Checked git @('-C', $Dir, 'checkout', '-q', '--force', $Commit)
    }
    $head = (& git -C $Dir rev-parse HEAD)
    if ($head -ne $Commit) { throw "$Dir is at $head, not the pinned $Commit" }
}

# Runs the rest in the VS 2022 x64 developer environment, so cl, CMake and Ninja are found.
function Import-VsDevEnvironment {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vs = & $vswhere -latest -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) { throw 'Visual Studio 2022 with the C++ workload is needed' }
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    $env:PATH = "$(Split-Path $vswhere);$env:PATH"
    $ErrorActionPreference = 'Continue'
    cmd /c "`"$vcvars`" >nul && set" | ForEach-Object {
        if ($_ -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2]) }
    }
    $cmakeDir = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake'
    $env:PATH = "$cmakeDir\CMake\bin;$cmakeDir\Ninja;$env:PATH"
}

$gnsDir = Join-Path $WorkDir 'GameNetworkingSockets'
$vcpkgDir = Join-Path $WorkDir 'vcpkg'
$buildDir = Join-Path $WorkDir 'build'

Write-Host "Fetching GameNetworkingSockets $GnsCommit and vcpkg $VcpkgCommit"
Get-PinnedSource $GnsRepo $GnsCommit $gnsDir
Get-PinnedSource $VcpkgRepo $VcpkgCommit $vcpkgDir

Import-VsDevEnvironment

if (!(Test-Path "$vcpkgDir\vcpkg.exe")) {
    Invoke-Checked "$vcpkgDir\bootstrap-vcpkg.bat" @('-disableMetrics')
}
$env:VCPKG_DISABLE_METRICS = '1'
Write-Host "Building protobuf ($Triplet)"
Invoke-Checked "$vcpkgDir\vcpkg.exe" @('install', "protobuf:$Triplet", '--classic', "--vcpkg-root=$vcpkgDir")

Write-Host 'Building GameNetworkingSockets'
Invoke-Checked cmake @(
    '-S', $gnsDir, '-B', $buildDir, '-G', 'Ninja',
    '-DCMAKE_BUILD_TYPE=Release',
    "-DCMAKE_TOOLCHAIN_FILE=$vcpkgDir\scripts\buildsystems\vcpkg.cmake",
    '-DVCPKG_MANIFEST_MODE=OFF',
    "-DVCPKG_TARGET_TRIPLET=$Triplet",
    '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded',
    '-DProtobuf_USE_STATIC_LIBS=ON',
    '-DBUILD_SHARED_LIB=ON', '-DBUILD_STATIC_LIB=OFF',
    '-DUSE_CRYPTO=BCrypt', '-DUSE_CRYPTO25519=Reference')
Invoke-Checked cmake @('--build', $buildDir, '--target', 'GameNetworkingSockets')

$dll = Get-ChildItem -Path $buildDir -Recurse -Filter 'GameNetworkingSockets.dll' | Select-Object -First 1
if (!$dll) { throw "No GameNetworkingSockets.dll under $buildDir" }

# The offsets and constants GnsNative.cs relies on, checked against this source's headers.
Write-Host 'Checking struct layouts'
$checkExe = Join-Path $buildDir 'layout-check.exe'
Invoke-Checked cl @('/nologo', '/EHsc', '/std:c++17', "/I$gnsDir\include",
    "/Fe$checkExe", "/Fo$buildDir\\", "$PSScriptRoot\layout-check.cpp")
Invoke-Checked $checkExe @()

New-Item -ItemType Directory -Force $OutDir | Out-Null
Copy-Item $dll.FullName -Destination $OutDir -Force
Copy-Item "$gnsDir\LICENSE" -Destination "$OutDir\LICENSE-GameNetworkingSockets.txt" -Force
$hash = (Get-FileHash "$OutDir\GameNetworkingSockets.dll" -Algorithm SHA256).Hash
@(
    "GameNetworkingSockets $GnsCommit (v1.6.1)",
    "vcpkg $VcpkgCommit, triplet $Triplet",
    "USE_CRYPTO=BCrypt USE_CRYPTO25519=Reference, static CRT",
    "SHA256 $hash"
) | Set-Content "$OutDir\BUILD-INFO.txt"
Write-Host "Built $OutDir\GameNetworkingSockets.dll (SHA256 $hash)"
