@echo off
setlocal
rem Point PYTHON_EXE at your interpreter, or leave it unset to use python from PATH.
if not defined PYTHON_EXE set "PY=python"
if exist "%PY%" goto RUN
python --version >nul 2>&1
if %errorlevel% equ 0 set "PY=python" & goto RUN
echo [!] Python 3 not found.
pause
exit /b 1

:RUN
cd /d "%~dp0"
"%PY%" latency_harvester.py %*
