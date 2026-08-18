@echo off
setlocal
set "ROOT=%~dp0"
set "CLI=%ROOT%src\RimError.Cli\bin\Release\net8.0\rimerror.exe"

if not exist "%CLI%" set "CLI=%ROOT%src\RimError.Cli\bin\Debug\net8.0\rimerror.exe"

if not exist "%CLI%" (
  >&2 echo rimerror is not built. Run: dotnet build RimLiaison.sln -c Release
  exit /b 1
)

"%CLI%" %*
exit /b %ERRORLEVEL%
