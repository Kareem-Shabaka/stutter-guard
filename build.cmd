@echo off
rem Stutter Guard - build. Needs the .NET 10 SDK:  winget install Microsoft.DotNet.SDK.10
rem Output: publish\StutterGuard.exe (one self-contained file). Log: build-log.txt
setlocal
cd /d "%~dp0"
set "LOG=%~dp0build-log.txt"
echo Stutter Guard build %DATE% %TIME% > "%LOG%"

where dotnet >> "%LOG%" 2>&1
if errorlevel 1 (
  echo .NET SDK not found. Install it with:  winget install Microsoft.DotNet.SDK.10 >> "%LOG%"
  type "%LOG%"
  exit /b 1
)
dotnet --list-sdks >> "%LOG%" 2>&1

echo. >> "%LOG%"
echo === self tests === >> "%LOG%"
dotnet run --project tests\DpcTweaker.SelfTest -c Release >> "%LOG%" 2>&1
if errorlevel 1 echo SELF TESTS FAILED >> "%LOG%"

echo. >> "%LOG%"
echo === publish === >> "%LOG%"
rem If publish\StutterGuard.exe is currently running, Windows locks it from being overwritten.
rem It watches for publish\close-request, so ask it to close and wait up to 20 seconds.
if not exist "publish\StutterGuard.exe" goto :notrunning
(type nul >> "publish\StutterGuard.exe") 2>nul
if not errorlevel 1 goto :notrunning

echo Asking the open Stutter Guard to close... >> "%LOG%"
echo Asking the open Stutter Guard to close...
if not exist publish mkdir publish
echo close> "publish\close-request"
for /L %%i in (1,1,20) do (
  (type nul >> "publish\StutterGuard.exe") 2>nul && goto :notrunning
  ping -n 2 127.0.0.1 >NUL
)
del "publish\close-request" >NUL 2>&1
echo Stutter Guard is still open, so its .exe can't be replaced. Close it, then run build.cmd again. >> "%LOG%"
echo BUILD FAILED >> "%LOG%"
type "%LOG%"
echo.
echo *** BUILD FAILED: Stutter Guard is still open. Close it, then run build.cmd again. ***
exit /b 1

:notrunning
del "publish\close-request" >NUL 2>&1
dotnet publish src\DpcTweaker.App\DpcTweaker.App.csproj -c Release -o publish >> "%LOG%" 2>&1
if errorlevel 1 (
  echo BUILD FAILED >> "%LOG%"
  type "%LOG%"
  echo.
  echo *** BUILD FAILED: see the errors above. ***
  exit /b 1
)
echo BUILD OK: %~dp0publish\StutterGuard.exe >> "%LOG%"
type "%LOG%"
echo.
echo *** BUILD OK ***
exit /b 0
