@echo off
cd /d "%~dp0"
if not exist "%~dp0PowerHub.exe" (
    echo PowerHub.exe was not found. Extract the entire preview ZIP before running this file.
    pause
    exit /b 1
)
start "" "%~dp0PowerHub.exe" --preview --data-dir "%~dp0data" --show
