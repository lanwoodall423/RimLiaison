@echo off
setlocal
set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
pwsh -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\scripts\cross-stack-fake-devbridge.ps1" %*
exit /b %ERRORLEVEL%
