@echo off
rem Starts the installer next to this file. Bypass only applies to this one script.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo.
pause
