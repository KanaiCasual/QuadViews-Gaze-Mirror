# Builds both OpenXR layers (Release x64) and puts the DLLs where Collect-Payload.ps1 / Build-Installer.ps1 expect them.
# Builds go to a fresh staging folder first: while a game runs with the installed layer, its crash handler keeps the
# layer's .pdb in bin\x64\Release locked, which makes a normal build fail at link time (and delete the DLL).
param([switch]$QuadViewsOnly, [switch]$ObsMirrorOnly)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found (Visual Studio 2022 with the C++ workload is required).' }
$stamp = Get-Date -Format 'HHmmss'

function Build-Layer([string]$Name, [string]$Project, [string[]]$ExtraArgs, [string]$RepoDir, [string]$Dll) {
    $stage = Join-Path $RepoDir "bin\x64\Stage-$stamp\"
    $log = Join-Path $env:TEMP "gaze-build-$Name.log"
    & $msbuild $Project /p:Configuration=Release /p:Platform=x64 "/p:OutDir=$stage" @ExtraArgs /m /v:m /nologo *> $log
    if ($LASTEXITCODE -ne 0) { Get-Content $log | Select-String ' error ' | Select-Object -First 10; throw "$Name failed to build (full log: $log)" }
    $release = Join-Path $RepoDir 'bin\x64\Release'
    New-Item -ItemType Directory -Force $release | Out-Null
    Copy-Item (Join-Path $stage $Dll) (Join-Path $release $Dll) -Force
    '{0,-22} {1}' -f $Name, (Get-Item (Join-Path $release $Dll)).LastWriteTime
}

if (-not $ObsMirrorOnly) {
    $repo = Join-Path $root 'Quad-Views-Foveated'
    Build-Layer 'Quad-Views-Foveated' (Join-Path $repo 'openxr-api-layer\openxr-api-layer.vcxproj') `
        @("/p:SolutionDir=$repo\", '/p:SolutionName=XR_APILAYER_MBUCCHIA_quad_views_foveated') $repo 'XR_APILAYER_MBUCCHIA_quad_views_foveated.dll'
}
if (-not $QuadViewsOnly) {
    $repo = Join-Path $root 'OpenXR-Layer-OBSMirror'
    Build-Layer 'OBSMirror' (Join-Path $repo 'OpenXR-Layer-OBSMirror.sln') @() $repo 'XR_APILAYER_NOVENDOR_OBSMirror.dll'
}
