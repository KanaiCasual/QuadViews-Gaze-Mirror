@echo off
rem Removes the 2.0 DEVELOPMENT layer from OpenXR's list again. Close the game first.
set "JSON=%~dp0bin\layer\XR_APILAYER_NOVENDOR_gaze_mirror.json"
powershell -NoProfile -Command "Start-Process reg.exe -Verb RunAs -Wait -ArgumentList 'delete','HKLM\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit','/v','%JSON%','/f'"
echo.
echo OpenXR layers now registered:
reg query "HKLM\SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit"
pause
