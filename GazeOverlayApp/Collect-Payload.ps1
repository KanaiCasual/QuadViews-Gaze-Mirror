# Gathers the files the MSI installs next to the settings app. Run after v2\Build-Dev.ps1 (which builds them all).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$payload = Join-Path $PSScriptRoot 'Payload'
$v2 = Join-Path $root 'v2'

$files = @(
    @("$v2\bin\layer\XR_APILAYER_NOVENDOR_gaze_mirror.dll", 'Layer'),
    @("$v2\bin\layer\XR_APILAYER_NOVENDOR_gaze_mirror.json", 'Layer'),
    @("$v2\bin\obs-plugin\gaze-mirror-capture.dll", 'OBSPlugin\obs-plugins\64bit'),
    @("$v2\bin\obs-plugin\data\obs-plugins\gaze-mirror-capture\locale\en-US.ini", 'OBSPlugin\data\obs-plugins\gaze-mirror-capture\locale'),
    @("$v2\external\qvf\Quad-Views-Foveated-1.1.3.msi", 'Quad-Views-Foveated'),
    @("$v2\external\qvf\LICENSE", 'Quad-Views-Foveated')
)

if (Test-Path $payload) { Get-ChildItem $payload -Recurse -File | Remove-Item -Force }
foreach ($f in $files) {
    if (-not (Test-Path $f[0])) { throw "Payload source missing: $($f[0]) (run v2\Build-Dev.ps1 and v2\Get-External.ps1 first)" }
    $dest = Join-Path $payload $f[1]
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $f[0] $dest -Force
}
# The optional VRCFaceTracking module as the zip VRCFT installs ("Install from file"): the DLL and its module.json,
# with the same version as the app.
$moduleDir = Join-Path $payload 'VRCFT-module'
New-Item -ItemType Directory -Force $moduleDir | Out-Null
$staging = Join-Path $env:TEMP 'gaze-vrcft-module'
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item "$v2\bin\vrcft-module\GazeMirror.VRCFT.dll" $staging
$moduleJson = Get-Content "$v2\vrcft-module\module.json" -Raw
$appVersion = ([xml](Get-Content "$PSScriptRoot\GazeOverlayApp.csproj")).Project.PropertyGroup.Version | Select-Object -First 1
if ($appVersion) { $moduleJson = $moduleJson -replace '"Version": "[^"]*"', ('"Version": "' + [string]$appVersion + '"') }
[IO.File]::WriteAllText((Join-Path $staging 'module.json'), $moduleJson)
$zip = Join-Path $moduleDir 'VR-Gaze-Mirror-VRCFT-module.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Copy-Item "$v2\external\vrcft\LICENSE" (Join-Path $moduleDir 'LICENSE-VRCFaceTracking.txt') -Force
# Licence texts, named for what they belong to.
$licences = Join-Path $payload 'Licences'
New-Item -ItemType Directory -Force $licences | Out-Null
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $licences 'LICENSE-VR-Gaze-Mirror.txt') -Force
Copy-Item (Join-Path $v2 'external\libobs\COPYING') (Join-Path $licences 'LICENSE-OBS-plugin-GPL-2.txt') -Force
Copy-Item (Join-Path $v2 'external\openvr\LICENSE') (Join-Path $licences 'LICENSE-OpenVR.txt') -Force
Get-ChildItem $payload -Recurse -File | ForEach-Object { '{0,9}  {1}' -f $_.Length, $_.FullName.Substring($payload.Length + 1) }
