# Gathers the files that get embedded into GazeOverlay.exe. Run before building/publishing the app.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$payload = Join-Path $PSScriptRoot 'Payload'

$qvf = Join-Path $root 'Quad-Views-Foveated'
$obsm = Join-Path $root 'OpenXR-Layer-OBSMirror'
# The OBS plugin is unmodified upstream code; take the prebuilt one from an existing OBSMirror install.
$plugin = 'C:\Program Files\OpenXR-Layer-OBSMirror\OBS_Plugin'

$files = @(
    @("$qvf\bin\x64\Release\XR_APILAYER_MBUCCHIA_quad_views_foveated.dll", 'QuadViewsFoveated'),
    @("$qvf\bin\x64\Release\openxr-api-layer.json", 'QuadViewsFoveated'),
    @("$qvf\bin\x64\Release\settings.cfg", 'QuadViewsFoveated'),
    @("$qvf\LICENSE", 'QuadViewsFoveated'),
    @("$qvf\THIRD_PARTY", 'QuadViewsFoveated'),
    @("$obsm\bin\x64\Release\XR_APILAYER_NOVENDOR_OBSMirror.dll", 'OBSMirror'),
    @("$obsm\bin\x64\Release\XR_APILAYER_NOVENDOR_OBSMirror.json", 'OBSMirror'),
    @("$obsm\LICENSE", 'OBSMirror'),
    @("$obsm\THIRD_PARTY", 'OBSMirror'),
    @("$plugin\obs-plugins\64bit\win-openxr.dll", 'OBSPlugin\obs-plugins\64bit'),
    @("$plugin\data\obs-plugins\win-openxr\win_openxrmirror-presets.ini", 'OBSPlugin\data\obs-plugins\win-openxr'),
    @("$plugin\data\obs-plugins\win-openxr\locale\en-US.ini", 'OBSPlugin\data\obs-plugins\win-openxr\locale'),
    @("$plugin\data\obs-plugins\win-openxr\locale\fi-FI.ini", 'OBSPlugin\data\obs-plugins\win-openxr\locale')
)

if (Test-Path $payload) { Get-ChildItem $payload -Recurse -File | Remove-Item -Force }
foreach ($f in $files) {
    if (-not (Test-Path $f[0])) { throw "Payload source missing: $($f[0])" }
    $dest = Join-Path $payload $f[1]
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item $f[0] $dest -Force
}
Get-ChildItem $payload -Recurse -File | ForEach-Object { '{0,9}  {1}' -f $_.Length, $_.FullName.Substring($payload.Length + 1) }
