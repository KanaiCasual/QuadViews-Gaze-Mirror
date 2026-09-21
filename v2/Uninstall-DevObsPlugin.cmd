@echo off
rem Removes the 2.0 DEVELOPMENT OBS plugin from OBS Studio again. Close OBS first.
set "OBS=C:\Program Files\obs-studio"
powershell -NoProfile -Command "Start-Process cmd.exe -Verb RunAs -Wait -ArgumentList '/c', 'del /q \"%OBS%\obs-plugins\64bit\gaze-mirror-capture.dll\" ^& rd /s /q \"%OBS%\data\obs-plugins\gaze-mirror-capture\"'"
echo.
if exist "%OBS%\obs-plugins\64bit\gaze-mirror-capture.dll" (echo NOT removed.) else (echo Removed.)
pause
