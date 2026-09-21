@echo off
rem Removes the 2.0 DEVELOPMENT OBS plugin from OBS Studio again. Close OBS first.
powershell -NoProfile -Command "Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-File','\"%~dp0tools\Copy-ObsPlugin.ps1\"','-Remove'"
echo.
if exist "C:\Program Files\obs-studio\obs-plugins\64bit\gaze-mirror-capture.dll" (echo NOT removed.) else (echo Removed.)
pause
