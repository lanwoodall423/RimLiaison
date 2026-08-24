@echo off
setlocal EnableExtensions
set "RIMLIAISON_ROOT=%~dp0"
set "OBSERVABILITY_PROJECT=%RIMLIAISON_ROOT%src\RimLiaison.Desktop\RimLiaison.Desktop.csproj"
set "OBSERVABILITY_EXE=%RIMLIAISON_ROOT%src\RimLiaison.Desktop\bin\Release\net8.0-windows\RimLiaison.Desktop.exe"

pushd "%RIMLIAISON_ROOT%" >nul 2>&1
if errorlevel 1 goto startup_failed

title RimLiaison Observability UI
if exist "%OBSERVABILITY_EXE%" goto run_compiled

where dotnet >nul 2>&1
if errorlevel 1 goto dotnet_missing

echo Release observability UI not found; building it with the .NET 8 SDK...
dotnet run --project "%OBSERVABILITY_PROJECT%" --configuration Release
set "OBSERVABILITY_EXIT=%ERRORLEVEL%"
popd
endlocal & exit /b %OBSERVABILITY_EXIT%

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

:startup_failed
echo RimLiaison Observability UI could not start in its repository folder:
echo   %RIMLIAISON_ROOT%
set "OBSERVABILITY_EXIT=1"

:finish_with_pause
pause
endlocal & exit /b %OBSERVABILITY_EXIT%
