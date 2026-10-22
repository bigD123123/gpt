@echo off
cd /d "%~dp0"
if not exist ".venv\Scripts\python.exe" (
  echo Koer foerst scripts\install-windows.ps1 som beskrevet i README.md.
  pause
  exit /b 1
)
".venv\Scripts\python.exe" -m vision_agent
if errorlevel 1 pause
