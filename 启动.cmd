@echo off
cd /d "%~dp0"
if exist "dist\PowerHub\PowerHub.exe" (
    start "" "dist\PowerHub\PowerHub.exe"
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Run.ps1"
    if errorlevel 1 pause
)
