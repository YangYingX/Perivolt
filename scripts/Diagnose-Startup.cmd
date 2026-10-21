@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0Perivolt.exe" (
    echo Place this file next to Perivolt.exe in the installation folder.
    pause
    exit /b 1
)
set "diagdir=%LOCALAPPDATA%\PowerHub\logs"
if not exist "%diagdir%" mkdir "%diagdir%" 2>nul
if not exist "%diagdir%" set "diagdir=%TEMP%\Perivolt-logs"
if not exist "%diagdir%" mkdir "%diagdir%" 2>nul
set "DOTNET_HOST_TRACE=1"
set "DOTNET_HOST_TRACEFILE=%diagdir%\host-startup.log"
set "DOTNET_HOST_TRACE_VERBOSITY=4"
ver > "%diagdir%\launch-result.txt"
echo Architecture: %PROCESSOR_ARCHITECTURE% >> "%diagdir%\launch-result.txt"
echo Starting Perivolt with a visible panel. No settings are reset.
echo If it opens, exit from its tray menu to complete this check.
"%~dp0Perivolt.exe" --show > "%diagdir%\app-startup.txt" 2>&1
set "result=%errorlevel%"
echo ExitCode: %result% >> "%diagdir%\launch-result.txt"
echo Exit code: %result%
echo Local logs: %diagdir%
pause
exit /b %result%
