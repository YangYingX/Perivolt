@echo off
cd /d "%~dp0"
if exist "dist\PowerHub\Perivolt.exe" (
    start "" "dist\PowerHub\Perivolt.exe"
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Run.ps1"
    if errorlevel 1 pause
)
