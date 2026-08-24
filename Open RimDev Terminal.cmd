@echo off
setlocal EnableExtensions
set "RIMDEV_ROOT=%~dp0"

pushd "%RIMDEV_ROOT%" >nul 2>&1
if errorlevel 1 goto startup_failed

set "PATH=%RIMDEV_ROOT%;%PATH%"
title RimDev Terminal

set "RIMDEV_READY=1"
if not exist "%RIMDEV_ROOT%rimdev.cmd" set "RIMDEV_READY=0"
if exist "%RIMDEV_ROOT%src\RimLiaison.Cli\bin\Release\net8.0\rimliaison.exe" goto show_banner
if exist "%RIMDEV_ROOT%src\RimLiaison.Cli\bin\Debug\net8.0\rimliaison.exe" goto show_banner
where dotnet >nul 2>&1
if errorlevel 1 set "RIMDEV_READY=0"

:show_banner
echo.
if "%RIMDEV_READY%"=="1" (
  echo RimDev ready.
) else (
  echo RimDev terminal opened, but the RimDev command is not ready yet.
  echo Install the .NET 8 SDK or ask your development agent to build RimLiaison.
)
echo.
echo Common commands:
echo   rimdev status    Show everything
echo   rimdev all       Sync, test, build, deploy, push
echo   rimdev deploy    Deploy validated changes
echo   rimdev push      Push committed changes
echo   rimdev merge     Review and confirm approved work
echo   rimdev help      Show all commands
echo.
echo Type rimdev for a quick menu. Local files are never discarded by RimDev.
echo.
cmd /k
set "RIMDEV_EXIT=%ERRORLEVEL%"
popd
endlocal & exit /b %RIMDEV_EXIT%

:startup_failed
echo RimDev could not start in its repository folder:
echo   %RIMDEV_ROOT%
echo The terminal will stay open so you can read this message.
cmd /k
set "RIMDEV_EXIT=%ERRORLEVEL%"
endlocal & exit /b %RIMDEV_EXIT%
