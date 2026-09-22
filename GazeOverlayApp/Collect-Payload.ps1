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
# Licence texts, named for what they belong to.
$licences = Join-Path $payload 'Licences'
New-Item -ItemType Directory -Force $licences | Out-Null
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $licences 'LICENSE-VR-Gaze-Mirror.txt') -Force
Copy-Item (Join-Path $v2 'external\libobs\COPYING') (Join-Path $licences 'LICENSE-OBS-plugin-GPL-2.txt') -Force
Copy-Item (Join-Path $v2 'external\openvr\LICENSE') (Join-Path $licences 'LICENSE-OpenVR.txt') -Force
Get-ChildItem $payload -Recurse -File | ForEach-Object { '{0,9}  {1}' -f $_.Length, $_.FullName.Substring($payload.Length + 1) }
