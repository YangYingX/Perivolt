@echo off
cd /d "%~dp0"
if not exist "%~dp0Perivolt.exe" (
    echo Perivolt.exe was not found. Extract the entire preview ZIP before running this file.
    pause
    exit /b 1
)
start "" "%~dp0Perivolt.exe" --preview --data-dir "%~dp0data" --show
