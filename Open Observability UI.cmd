@echo off
setlocal EnableExtensions
set "RIMLIAISON_ROOT=%~dp0"
set "OBSERVABILITY_PROJECT=%RIMLIAISON_ROOT%src\RimLiaison.Desktop\RimLiaison.Desktop.csproj"
set "OBSERVABILITY_EXE=%RIMLIAISON_ROOT%src\RimLiaison.Desktop\bin\Release\net8.0-windows\RimLiaison.Desktop.exe"

pushd "%RIMLIAISON_ROOT%" >nul 2>&1
if errorlevel 1 goto startup_failed

title RimLiaison Observability UI
where dotnet >nul 2>&1
if errorlevel 1 goto dotnet_missing

echo Ensuring the Release observability UI matches current source...
dotnet build "%OBSERVABILITY_PROJECT%" --configuration Release
if errorlevel 1 goto build_failed
if not exist "%OBSERVABILITY_EXE%" goto startup_failed
goto run_compiled

:run_compiled
"%OBSERVABILITY_EXE%"
set "OBSERVABILITY_EXIT=%ERRORLEVEL%"
popd
endlocal & exit /b %OBSERVABILITY_EXIT%

:dotnet_missing
echo The observability UI is not built and the .NET 8 SDK was not found.
echo Install the .NET 8 SDK, then run this launcher again.
set "OBSERVABILITY_EXIT=1"
popd
goto finish_with_pause

:build_failed
echo The current Release observability UI could not be built.
set "OBSERVABILITY_EXIT=1"
popd
goto finish_with_pause

:startup_failed
echo RimLiaison Observability UI could not start in its repository folder:
echo   %RIMLIAISON_ROOT%
set "OBSERVABILITY_EXIT=1"

:finish_with_pause
pause
endlocal & exit /b %OBSERVABILITY_EXIT%
