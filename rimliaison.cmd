@echo off
setlocal
set "RIMLIAISON_ROOT=%~dp0"
if not defined RIMTEST_ROOT set "RIMTEST_ROOT=%RIMLIAISON_ROOT%"
if not defined RIMTEST_DEVBRIDGE_ROOT set "RIMTEST_DEVBRIDGE_ROOT=%RIMLIAISON_ROOT%..\DevBridge2"
if not defined RIMTEST_DEVBRIDGE_CMD set "RIMTEST_DEVBRIDGE_CMD=%RIMTEST_DEVBRIDGE_ROOT%\DevBridge.cmd"
set "RIMLIAISON_EXE=%RIMLIAISON_ROOT%src\RimLiaison.Cli\bin\Release\net8.0\rimliaison.exe"
if exist "%RIMLIAISON_EXE%" goto run_compiled
dotnet run --project "%RIMLIAISON_ROOT%src\RimLiaison.Cli\RimLiaison.Cli.csproj" -- %*
exit /b %ERRORLEVEL%

:run_compiled
"%RIMLIAISON_EXE%" %*
exit /b %ERRORLEVEL%
