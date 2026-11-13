@echo off
rem Double-click for an unsigned test build, or pass the same options as build-windows.ps1.
cd /d "%~dp0..\.."
powershell -NoProfile -ExecutionPolicy Bypass -File "release\windows\build-windows.ps1" %*
echo.
pause
