@echo off
setlocal
set "PY=C:\Users\Compumarts\AppData\Local\Programs\Python\Python312\python.exe"
if exist "%PY%" goto RUN
python --version >nul 2>&1
if %errorlevel% equ 0 set "PY=python" & goto RUN
echo [!] Python 3 not found.
pause
exit /b 1

:RUN
cd /d "%~dp0"
"%PY%" latency_harvester.py %*
