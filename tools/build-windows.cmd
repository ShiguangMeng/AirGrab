@echo off
REM Double-click entry point for build-windows.ps1
REM The bypass flag is needed because the default execution policy blocks local scripts.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-windows.ps1" %*
echo.
pause
