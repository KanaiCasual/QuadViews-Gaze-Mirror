# Builds dist\OpenXR-Gaze-Overlay-<version>.msi: both OpenXR layers, the OBS plugin and the settings app.
# Prerequisites: both layers already built (Build-Layers.ps1), the .NET 10 SDK, and internet access the first time (the
# WiX toolset comes from nuget.org as part of the build).
#
#   .\Build-Installer.ps1 -Version 0.7.0                 a normal release
#   .\Build-Installer.ps1 -Version 0.4.1 -Label beta.1   a beta: the app calls itself 0.4.1-beta.1, the MSI is 0.4.1
#
# Every published build - beta or not - needs its own x.y.z: Windows Installer only upgrades to a higher number, and the
# app's update check compares those numbers. Whether a release counts as a beta is decided by GitHub's "pre-release" tick.
param(
    [string]$Version = '0.7.0',
    [string]$Label = '',
    # owner/name of the repository whose releases the app's update check looks at. '' = build without update checks.
    [string]$GitHubRepository = 'KanaiCasual/OpenXR-Gaze-Overlay'
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "-Version must be x.y.z (got '$Version'). Put 'beta.1' etc. in -Label." }
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$appOut = Join-Path $dist 'app'
$appVersion = if ($Label) { "$Version-$Label" } else { $Version }

# 1) Layer DLLs, manifests, licences and the OBS plugin -> GazeOverlayApp\Payload
& (Join-Path $root 'GazeOverlayApp\Collect-Payload.ps1') | Out-Null

# 2) Settings app, self-contained, as a plain folder (the MSI installs it).
if (Test-Path $appOut) { Remove-Item $appOut -Recurse -Force }
dotnet publish (Join-Path $root 'GazeOverlayApp\GazeOverlayApp.csproj') -c Release -o $appOut -v q --nologo `
    "-p:Version=$appVersion" "-p:GitHubRepository=$GitHubRepository"
if ($LASTEXITCODE -ne 0) { throw "Publishing the settings app failed." }

# 3) The MSI.
$payload = Join-Path $root 'GazeOverlayApp\Payload'
dotnet build (Join-Path $root 'Installer\GazeOverlay.Installer.wixproj') -c Release -v q --nologo `
    "-p:ProductVersion=$Version" "-p:PayloadDir=$payload" "-p:AppDir=$appOut"
if ($LASTEXITCODE -ne 0) { throw "Building the MSI failed." }

$msi = Get-ChildItem (Join-Path $root 'Installer\bin') -Recurse -Filter 'OpenXR-Gaze-Overlay.msi' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$target = Join-Path $dist "OpenXR-Gaze-Overlay-$appVersion.msi"
Copy-Item $msi.FullName $target -Force
'{0}  ({1:N1} MB)' -f $target, ((Get-Item $target).Length / 1MB)
