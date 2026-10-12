@echo off
setlocal
set "SUBTITLE_ROOT=%~dp0"
set "SUBTITLE_MODELS_DIR=%~dp0models"
if not exist "%~dp0.venv\Scripts\python.exe" (
  echo Run Setup.cmd first to prepare the local Python environment.
  pause
  exit /b 1
)
start "" /D "%~dp0" "%~dp0ACGTranslator.App.exe"
