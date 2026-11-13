@echo off
cd /d "%~dp0"
dotnet test tests\LtzPrintBridge.Tests %*
