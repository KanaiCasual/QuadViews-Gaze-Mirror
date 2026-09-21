@echo off
rem Copies the 2.0 DEVELOPMENT OBS plugin into OBS Studio (Program Files, so Windows asks for permission once).
rem Close OBS first. The 1.x "OpenXR Mirror Capture" plugin stays where it is; this adds a second source, "Gaze Mirror".
rem Undo with Uninstall-DevObsPlugin.cmd.
if not exist "%~dp0bin\obs-plugin\gaze-mirror-capture.dll" (
  echo The plugin has not been built yet.
  pause
  exit /b 1
)
powershell -NoProfile -Command "Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"%~dp0tools\Copy-ObsPlugin.ps1\"'"
echo.
if exist "C:\Program Files\obs-studio\obs-plugins\64bit\gaze-mirror-capture.dll" (echo Installed. Start OBS and add a "Gaze Mirror" source.) else (echo NOT installed - the copy was refused.)
pause
