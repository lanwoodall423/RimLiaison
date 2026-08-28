@echo off
setlocal
if /I "%RIMLIAISON_TOOLCHAIN_MODE%"=="experimental" goto run_experimental
if /I "%~1"=="qualification" goto run_experimental

set "RIMLIAISON_EXE=%RIMLIAISON_PRODUCTION_CLI%"
if not defined RIMLIAISON_EXE if defined RIMLIAISON_PRODUCTION_ROOT set "RIMLIAISON_EXE=%RIMLIAISON_PRODUCTION_ROOT%\cli\rimliaison.exe"
if not defined RIMLIAISON_EXE set "RIMLIAISON_EXE=C:\RimDev\Staging\RimLiaison-Production\cli\rimliaison.exe"
if exist "%RIMLIAISON_EXE%" goto run_production

echo {\"schemaVersion\":\"rimliaison-toolchain-binding/v1\",\"status\":\"blocked\",\"code\":\"PRODUCTION_TOOLCHAIN_INSTALL_MISSING\",\"nextAction\":\"Install the promoted production RimLiaison toolchain or set RIMLIAISON_TOOLCHAIN_MODE=experimental for qualification/tooling work.\"}
exit /b 3

:run_production
"%RIMLIAISON_EXE%" %*
exit /b %ERRORLEVEL%

:run_experimental
set "RIMLIAISON_ROOT=%~dp0"
if not defined RIMTEST_ROOT set "RIMTEST_ROOT=%RIMLIAISON_ROOT%"
if not defined RIMTEST_DEVBRIDGE_ROOT set "RIMTEST_DEVBRIDGE_ROOT=%RIMLIAISON_ROOT%..\DevBridge2"
if not defined RIMTEST_DEVBRIDGE_CMD set "RIMTEST_DEVBRIDGE_CMD=%RIMTEST_DEVBRIDGE_ROOT%\DevBridge.cmd"
set "RIMLIAISON_EXE=%RIMLIAISON_ROOT%src\RimLiaison.Cli\bin\Release\net8.0\rimliaison.exe"
if exist "%RIMLIAISON_EXE%" goto run_experimental_compiled
dotnet run --project "%RIMLIAISON_ROOT%src\RimLiaison.Cli\RimLiaison.Cli.csproj" -- --experimental %*
exit /b %ERRORLEVEL%

:run_experimental_compiled
"%RIMLIAISON_EXE%" --experimental %*
exit /b %ERRORLEVEL%
