@echo off
setlocal
set "RIMDEV_ROOT=%~dp0"
if "%RIMDEV_ROOT:~-1%"=="\" set "RIMDEV_ROOT=%RIMDEV_ROOT:~0,-1%"
call "%RIMDEV_ROOT%\rimliaison.cmd" rimdev --root "%RIMDEV_ROOT%" %*
set "RIMDEV_EXIT=%ERRORLEVEL%"
endlocal & exit /b %RIMDEV_EXIT%
