@echo off
setlocal
set "RIMDEV_REPO_ROOT=%~dp0"
if "%RIMDEV_REPO_ROOT:~-1%"=="\" set "RIMDEV_REPO_ROOT=%RIMDEV_REPO_ROOT:~0,-1%"
if not defined RIMDEV_ROOT set "RIMDEV_ROOT=%RIMDEV_REPO_ROOT%\..\.."
call "%RIMDEV_REPO_ROOT%\rimliaison.cmd" rimdev --root "%RIMDEV_ROOT%" %*
set "RIMDEV_EXIT=%ERRORLEVEL%"
endlocal & exit /b %RIMDEV_EXIT%
