# Runs elevated (from Install-/Uninstall-DevObsPlugin.cmd): copies the built 2.0 OBS plugin into OBS Studio, or removes it.
param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '..\bin\obs-plugin'
$obs = 'C:\Program Files\obs-studio'
$dll = Join-Path $obs 'obs-plugins\64bit\gaze-mirror-capture.dll'
$data = Join-Path $obs 'data\obs-plugins\gaze-mirror-capture'
if ($Remove) {
    if (Test-Path $dll) { Remove-Item $dll -Force }
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    'Removed.'
} else {
    Copy-Item (Join-Path $source 'gaze-mirror-capture.dll') $dll -Force
    New-Item -ItemType Directory -Force (Join-Path $data 'locale') | Out-Null
    Copy-Item (Join-Path $source 'data\obs-plugins\gaze-mirror-capture\locale\*') (Join-Path $data 'locale') -Force
    'Installed.'
}
