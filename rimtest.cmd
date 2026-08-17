@echo off
setlocal
set "RIMTEST_ROOT=%~dp0"
set "RIMTEST_EXE=%RIMTEST_ROOT%src\RimTest.Cli\bin\Release\net8.0\RimTest.Cli.exe"
if exist "%RIMTEST_EXE%" goto run_compiled
dotnet run --project "%RIMTEST_ROOT%src\RimTest.Cli\RimTest.Cli.csproj" -- %*
exit /b %ERRORLEVEL%

:run_compiled
"%RIMTEST_EXE%" %*
exit /b %ERRORLEVEL%
