@echo off
setlocal
cd /d "%~dp0"
if "%~1"=="" echo Drag a video file onto verify.bat, or run:  verify.bat "yourfile.mp4"
if "%~1"=="" goto hold
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify.ps1" "%~1"
:hold
echo.
echo Press any key to close this window . . .
pause >nul
