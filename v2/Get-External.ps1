# Fetches the third-party files the 2.0 build needs but does not keep in the repository (only their licences are kept):
#   external\libobs\      OBS Studio's public headers, tag 32.1.2 (GPL-2)   - to build the OBS plugin
#   external\libobs-lib\  an import library made from the INSTALLED obs.dll  - no download
#   external\openvr\      Valve's OpenVR SDK v2.15.6: header, import library, redistributable DLL (BSD-3) - for the OpenVR helper
#   external\qvf\         mbucchia's official Quad-Views-Foveated 1.1.3 installer (MIT) - bundled, offered by our installer
# Needs the GitHub CLI (gh) and Visual Studio 2022. Nothing is executed or installed.
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'external'
New-Item -ItemType Directory -Force "$root\libobs", "$root\libobs-lib", "$root\openvr", "$root\qvf" | Out-Null

function Fetch($repo, $ref, $path, $target) {
    New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
    gh api "repos/$repo/contents/$path`?ref=$ref" -H 'Accept: application/vnd.github.raw' > $target
    if ($LASTEXITCODE -ne 0) { throw "could not fetch $repo $ref $path" }
}

if (-not (Test-Path "$root\openvr\openvr.h")) {
    foreach ($file in 'headers/openvr.h', 'lib/win64/openvr_api.lib', 'bin/win64/openvr_api.dll', 'LICENSE') {
        Fetch 'ValveSoftware/openvr' 'v2.15.6' $file (Join-Path "$root\openvr" (Split-Path $file -Leaf))
    }
    'OpenVR SDK fetched.'
}

if (-not (Test-Path "$root\libobs\obs-module.h")) {
    $tree = gh api 'repos/obsproject/obs-studio/git/trees/32.1.2?recursive=1' --jq '.tree[] | select(.path | startswith("libobs/")) | select((.path | endswith(".h")) or (.path | endswith(".hpp"))) | .path'
    foreach ($path in $tree) { Fetch 'obsproject/obs-studio' '32.1.2' $path (Join-Path $root ($path -replace '/', '\')) }
    Fetch 'obsproject/obs-studio' '32.1.2' 'COPYING' "$root\libobs\COPYING"
    "libobs headers fetched ($($tree.Count) files)."
}

if (-not (Test-Path "$root\libobs-lib\obs.lib")) {
    $obs = 'C:\Program Files\obs-studio\bin\64bit\obs.dll'
    if (-not (Test-Path $obs)) { throw "OBS Studio is not installed at $obs" }
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
    $bin = (Get-ChildItem "$vs\VC\Tools\MSVC" -Recurse -Filter dumpbin.exe | Where-Object { $_.FullName -match 'Hostx64\\x64' } | Select-Object -First 1).DirectoryName
    $names = & "$bin\dumpbin.exe" /exports $obs | Where-Object { $_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]{8}\s+(\S+)' } | ForEach-Object { $Matches[1] }
    "LIBRARY obs.dll`r`nEXPORTS`r`n" + ($names -join "`r`n") | Set-Content "$root\libobs-lib\obs.def" -Encoding ascii
    & "$bin\lib.exe" /nologo /machine:x64 /def:"$root\libobs-lib\obs.def" /out:"$root\libobs-lib\obs.lib" | Out-Null
    "obs.lib made from the installed OBS ($($names.Count) exports)."
}

if (-not (Test-Path "$root\qvf\Quad-Views-Foveated-1.1.3.msi")) {
    gh release download 1.1.3 --repo mbucchia/Quad-Views-Foveated --pattern 'Quad-Views-Foveated-1.1.3.msi' --dir "$root\qvf" --clobber
    Fetch 'mbucchia/Quad-Views-Foveated' '1.1.3' 'LICENSE' "$root\qvf\LICENSE"
    'Quad-Views-Foveated 1.1.3 installer fetched.'
}
'External files are in place.'
