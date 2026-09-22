# Builds the 2.0 development tree - the OpenXR layer (v2\bin\layer), the dev viewer (v2\bin\viewer) and the offline
# test (v2\bin\test) - and runs that test. Nothing is installed and no installer is made; see Install-DevLayer.cmd for
# trying the layer in a game.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found (Visual Studio 2022 with the C++ workload is required).' }

foreach ($project in 'core\GazeMirrorCore.vcxproj', 'layer\GazeMirrorLayer.vcxproj', 'openvr-helper\GazeMirrorHelper.vcxproj', 'obs-plugin\GazeMirrorCapture.vcxproj', 'mirror-window\MirrorWindow.vcxproj', 'viewer\GazeMirrorViewer.vcxproj', 'test\LayerTest.vcxproj') {
    $log = Join-Path $env:TEMP ('gaze-build-v2-' + [IO.Path]::GetFileNameWithoutExtension($project) + '.log')
    & $msbuild (Join-Path $root $project) /p:Configuration=Release /p:Platform=x64 /m /v:m /nologo *> $log
    if ($LASTEXITCODE -ne 0) { Get-Content $log | Select-String ' error |warning C' | Select-Object -First 15; throw "$project failed to build (full log: $log)" }
    Get-Content $log | Select-String 'warning C|warning LNK' | Select-Object -First 10
}
# The optional VRCFaceTracking module (C#, .NET 7 like VRCFT itself). Compiles against VRCFT's library source (Get-External.ps1).
$moduleLog = Join-Path $env:TEMP 'gaze-build-v2-vrcft-module.log'
dotnet build (Join-Path $root 'vrcft-module\GazeMirror.VRCFT.csproj') -c Release -p:Platform=x64 -v q --nologo *> $moduleLog
if ($LASTEXITCODE -ne 0) { Get-Content $moduleLog | Select-String 'error' | Select-Object -First 15; throw "the VRCFT module failed to build (full log: $moduleLog)" }
Get-ChildItem (Join-Path $root 'bin\layer\*.dll'), (Join-Path $root 'bin\vrcft-module\GazeMirror.VRCFT.dll'), (Join-Path $root 'bin\helper\*.exe'), (Join-Path $root 'bin\obs-plugin\*.dll'), (Join-Path $root 'bin\mirror-window\*.exe'), (Join-Path $root 'bin\viewer\*.exe') | ForEach-Object { '{0}  {1:N0} bytes  {2}' -f $_.FullName, $_.Length, $_.LastWriteTime }

# The offline test: plays game and runtime for the layer and saves what comes out as PNGs.
$testFolder = Join-Path $root 'bin\test'
& (Join-Path $testFolder 'layer_test.exe') (Join-Path $root 'bin\layer\XR_APILAYER_NOVENDOR_gaze_mirror.dll') (Join-Path $testFolder 'out')
if ($LASTEXITCODE -ne 0) { throw 'The offline layer test failed.' }
