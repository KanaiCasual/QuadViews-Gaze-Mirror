# Builds MirrorWindow\bin\x64\Release\MirrorWindow.exe - the capturable mirror window (see MirrorWindow\main.cpp).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild was not found (Visual Studio 2022 with the C++ workload is required).' }
$log = Join-Path $env:TEMP 'gaze-build-MirrorWindow.log'
& $msbuild (Join-Path $root 'MirrorWindow\MirrorWindow.vcxproj') /p:Configuration=Release /p:Platform=x64 /m /v:m /nologo *> $log
if ($LASTEXITCODE -ne 0) { Get-Content $log | Select-String ' error |warning C' | Select-Object -First 15; throw "MirrorWindow failed to build (full log: $log)" }
Get-Content $log | Select-String 'warning C' | Select-Object -First 10
$exe = Get-Item (Join-Path $root 'MirrorWindow\bin\x64\Release\MirrorWindow.exe')
'{0}  {1:N0} bytes  {2}' -f $exe.FullName, $exe.Length, $exe.LastWriteTime
