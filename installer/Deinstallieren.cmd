@echo off
rem Removes exactly what Installieren.cmd added.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall %*
echo.
pause
