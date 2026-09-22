@echo off
rem Registers the 2.0 DEVELOPMENT layer with OpenXR, straight from v2\bin\layer (nothing is copied anywhere).
rem Windows asks for permission once, because the list of OpenXR layers lives in the machine-wide registry.
rem It is added at the END of the list = closest to the runtime = below Quad-Views-Foveated, where it has to be.
rem The installed 1.x stays as it is: its mirror layer does no work while nothing reads from it.
rem Undo with Uninstall-DevLayer.cmd. Close the game first.
set "JSON=%~dp0bin\layer\XR_APILAYER_NOVENDOR_gaze_mirror.json"
if not exist "%JSON%" (
  echo The layer has not been built yet: %JSON%
  pause
  exit /b 1
)
powershell -NoProfile -Command "Start-Process reg.exe -Verb RunAs -Wait -ArgumentList 'add','HKLM\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit','/v','%JSON%','/t','REG_DWORD','/d','0','/f'"
echo.
echo OpenXR layers now registered (first = closest to the game):
reg query "HKLM\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit"
pause
z