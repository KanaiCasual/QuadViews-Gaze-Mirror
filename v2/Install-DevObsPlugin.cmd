@echo off
rem Copies the 2.0 DEVELOPMENT OBS plugin into OBS Studio (Program Files, so Windows asks for permission once).
rem Close OBS first. The 1.x "OpenXR Mirror Capture" plugin stays where it is; this adds a second source, "Gaze Mirror".
rem Undo with Uninstall-DevObsPlugin.cmd.
set "SRC=%~dp0bin\obs-plugin"
set "OBS=C:\Program Files\obs-studio"
if not exist "%SRC%\gaze-mirror-capture.dll" (
  echo The plugin has not been built yet: %SRC%\gaze-mirror-capture.dll
  pause
  exit /b 1
)
if not exist "%OBS%\bin\64bit\obs64.exe" (
  echo OBS Studio was not found at %OBS%
  pause
  exit /b 1
)
powershell -NoProfile -Command "Start-Process cmd.exe -Verb RunAs -Wait -ArgumentList '/c', 'copy /y \"%SRC%\gaze-mirror-capture.dll\" \"%OBS%\obs-plugins\64bit\\\" ^&^& xcopy /y /i /q \"%SRC%\data\obs-plugins\gaze-mirror-capture\" \"%OBS%\data\obs-plugins\gaze-mirror-capture\\\" /s'"
echo.
if exist "%OBS%\obs-plugins\64bit\gaze-mirror-capture.dll" (echo Installed. Start OBS and add a "Gaze Mirror" source.) else (echo NOT installed - the copy was refused.)
pause
