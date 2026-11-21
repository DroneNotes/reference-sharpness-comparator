@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build.ps1"
set "photoBuildExit=%errorlevel%"
if not "%photoBuildExit%"=="0" pause
exit /b %photoBuildExit%
