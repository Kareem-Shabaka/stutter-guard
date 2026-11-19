@echo off
rem Stutter Guard - sign the published exe with Authenticode, then verify it.
rem
rem Needs a real code-signing certificate. Two ways to point this at one:
rem   set SG_PFX=<path to .pfx>  and  set SG_PFX_PASSWORD=<password>
rem   set SG_THUMBPRINT=<thumbprint of a cert installed in Cert:\CurrentUser\My>
rem Optional: set SG_TIMESTAMP=<rfc3161 url>   default http://timestamp.digicert.com
rem
rem With none of those set, this says so and exits 2 - it never pretends to have signed.
rem An unsigned build is a supported state (it is what every release so far was); a build that *claims*
rem to be signed and is not is not.

setlocal
cd /d "%~dp0"
set "EXE=%~dp0publish\StutterGuard.exe"
if not exist "%EXE%" (
  echo No %EXE% - run build.cmd first.
  exit /b 2
)

rem ---- locate signtool.exe ----
set "SIGNTOOL="
for /f "delims=" %%F in ('where signtool.exe 2^>nul') do if not defined SIGNTOOL set "SIGNTOOL=%%F"
if not defined SIGNTOOL (
  for /d %%D in ("C:\Program Files (x86)\Windows Kits\10\bin\*") do (
    if not defined SIGNTOOL if exist "%%D\x64\signtool.exe" set "SIGNTOOL=%%D\x64\signtool.exe"
  )
)
if not defined SIGNTOOL (
  for /d %%D in ("C:\Program Files\Windows Kits\10\bin\*") do (
    if not defined SIGNTOOL if exist "%%D\x64\signtool.exe" set "SIGNTOOL=%%D\x64\signtool.exe"
  )
)
if not defined SIGNTOOL (
  echo signtool.exe was not found. It ships with the Windows SDK:
  echo   winget install Microsoft.WindowsSDK
  exit /b 2
)

if not defined SG_TIMESTAMP set "SG_TIMESTAMP=http://timestamp.digicert.com"

if defined SG_PFX (
  echo Signing with the PFX named by SG_PFX ^(password not echoed^)...
  "%SIGNTOOL%" sign /fd SHA256 /td SHA256 /tr "%SG_TIMESTAMP%" /f "%SG_PFX%" /p "%SG_PFX_PASSWORD%" "%EXE%"
) else if defined SG_THUMBPRINT (
  echo Signing with certificate %SG_THUMBPRINT% from the current user's store...
  "%SIGNTOOL%" sign /fd SHA256 /td SHA256 /tr "%SG_TIMESTAMP%" /sha1 "%SG_THUMBPRINT%" "%EXE%"
) else (
  echo No signing identity configured, so nothing was signed.
  echo   set SG_PFX and SG_PFX_PASSWORD    ^(a .pfx file^), or
  echo   set SG_THUMBPRINT                  ^(a cert in Cert:\CurrentUser\My^)
  echo The exe at %EXE% is unchanged and still unsigned.
  exit /b 2
)
if errorlevel 1 (
  echo *** SIGNING FAILED ***
  exit /b 1
)

echo.
echo Verifying the signature...
"%SIGNTOOL%" verify /pa /v "%EXE%"
if errorlevel 1 (
  echo *** THE SIGNATURE DOES NOT VERIFY: do not publish this file. ***
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "$s = Get-AuthenticodeSignature '%EXE%'; Write-Host ('status: ' + $s.Status); Write-Host ('signer: ' + $s.SignerCertificate.Subject); if ($s.TimeStamperCertificate) { Write-Host ('stamped: ' + $s.TimeStamperCertificate.Subject) }"
echo.
echo *** SIGNED AND VERIFIED ***
exit /b 0