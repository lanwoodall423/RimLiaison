@echo off
setlocal
if /I "%RIMLIAISON_TOOLCHAIN_MODE%"=="experimental" goto run_experimental
if /I "%~1"=="qualification" goto run_experimental

set "RIMLIAISON_MANIFEST=%RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST%"
if not defined RIMLIAISON_MANIFEST set "RIMLIAISON_MANIFEST=C:\RimDev\.rimdev\production-toolchain.json"
set "RIMLIAISON_ROUTE="
for /f "usebackq delims=" %%I in (`powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; try { if (-not (Test-Path -LiteralPath $env:RIMLIAISON_MANIFEST -PathType Leaf)) { throw 'promoted production manifest is missing' }; $manifest = Get-Content -Raw -LiteralPath $env:RIMLIAISON_MANIFEST | ConvertFrom-Json; if ($manifest.schemaVersion -ne 'rimliaison-production-toolchain/v1' -or $manifest.productionState -ne 'MODERN_PRODUCTION') { throw 'promoted production manifest is invalid' }; $exe = [IO.Path]::GetFullPath([string]$manifest.rimLiaisonExecutablePath); $assembly = [IO.Path]::GetFullPath([string]$manifest.rimLiaisonAssemblyPath); if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or -not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw 'promoted production CLI artifact is missing' }; $exeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $exe).Hash; $assemblyHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $assembly).Hash; if ($exeHash -ne [string]$manifest.rimLiaisonExecutableSha256 -or $assemblyHash -ne [string]$manifest.rimLiaisonAssemblySha256) { throw 'promoted production CLI artifact hash does not match the manifest' }; if ($env:RIMLIAISON_PRODUCTION_CLI -and $env:RIMLIAISON_PRODUCTION_CLI -ne $exe) { throw 'RIMLIAISON_PRODUCTION_CLI does not match the promoted manifest' }; [Console]::WriteLine('OK|' + $exe) } catch { [Console]::WriteLine('FAIL|' + $_.Exception.Message); exit 1 }"`) do set "RIMLIAISON_ROUTE=%%I"
if not defined RIMLIAISON_ROUTE (
    echo {\"schemaVersion\":\"rimliaison-toolchain-binding/v1\",\"status\":\"blocked\",\"code\":\"PRODUCTION_TOOLCHAIN_MANIFEST_INVALID\",\"error\":\"The promoted production CLI could not be resolved from the manifest.\",\"nextAction\":\"Repair or re-promote the production toolchain; the launcher will not fall back to an older artifact.\"}
    exit /b 3
)
if /I not "%RIMLIAISON_ROUTE:~0,3%"=="OK|" (
    echo {\"schemaVersion\":\"rimliaison-toolchain-binding/v1\",\"status\":\"blocked\",\"code\":\"PRODUCTION_TOOLCHAIN_MANIFEST_INVALID\",\"error\":\"%RIMLIAISON_ROUTE:~5%\",\"nextAction\":\"Repair or re-promote the production toolchain; the launcher will not fall back to an older artifact.\"}
    exit /b 3
)
set "RIMLIAISON_EXE=%RIMLIAISON_ROUTE:~3%"
if not exist "%RIMLIAISON_EXE%" (
    echo {\"schemaVersion\":\"rimliaison-toolchain-binding/v1\",\"status\":\"blocked\",\"code\":\"PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING\",\"error\":\"The promoted production CLI executable is missing.\",\"nextAction\":\"Repair or re-promote the production toolchain; the launcher will not fall back to an older artifact.\"}
    exit /b 3
)
"%RIMLIAISON_EXE%" %*
exit /b %ERRORLEVEL%

:: Explicit qualification/parity mode only. The sibling DevBridge2 checkout is never
:: the production lifecycle owner; production resolves RimLiaison's promoted runtime manifest.
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
