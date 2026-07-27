@echo off
setlocal enabledelayedexpansion

rem Builds the SnapZap Windows installer end to end: publish, then package.
rem
rem   scripts\build-installer.bat            publish and build the installer
rem   scripts\build-installer.bat --no-build use the existing artifacts\win-x64 as-is
rem
rem Output: artifacts\installer\SnapZap-<version>-setup.exe
rem
rem Publishing here rather than assuming artifacts\win-x64 is current is deliberate. The one
rem thing this installer must never do is ship a stale or partial folder, and a publish that
rem is already up to date costs seconds.

set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO=%%~fI"

set "SKIP_BUILD="
if /I "%~1"=="--no-build" set "SKIP_BUILD=1"

rem --- locate the Inno Setup compiler -------------------------------------------------
set "ISCC="
for %%P in (
  "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
  "%ProgramFiles%\Inno Setup 6\ISCC.exe"
  "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
) do if exist "%%~P" if not defined ISCC set "ISCC=%%~P"

if not defined ISCC (
  where iscc >nul 2>&1 && for /f "delims=" %%I in ('where iscc') do if not defined ISCC set "ISCC=%%I"
)

if not defined ISCC (
  echo.
  echo ERROR: Inno Setup 6 was not found.
  echo.
  echo   winget install --id JRSoftware.InnoSetup -e
  echo.
  echo   or download it from https://jrsoftware.org/isdl.php
  echo.
  exit /b 1
)

rem --- publish ------------------------------------------------------------------------
if not defined SKIP_BUILD (
  echo Publishing win-x64...
  dotnet publish "%REPO%\src\SnapZap.App" -c Release -r win-x64 --self-contained ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%REPO%\artifacts\win-x64"
  if errorlevel 1 exit /b 1
)

rem The publish step asserts these exist (see the VerifyPublishOutput target), but --no-build
rem skips the publish entirely, and packaging a folder without them ships a dead UI.
if not exist "%REPO%\artifacts\win-x64\SnapZap.App.exe" (
  echo ERROR: artifacts\win-x64\SnapZap.App.exe is missing. Run without --no-build.
  exit /b 1
)
if not exist "%REPO%\artifacts\win-x64\wwwroot\_framework\blazor.web.js" (
  echo ERROR: artifacts\win-x64\wwwroot\_framework\blazor.web.js is missing.
  echo        Packaging this folder would ship an app that renders unstyled and dead.
  exit /b 1
)

rem --- package ------------------------------------------------------------------------
echo Building installer with "%ISCC%"...
"%ISCC%" /Q "%REPO%\installer\SnapZap.iss"
if errorlevel 1 exit /b 1

rem Full path and size, not `dir /b`. The bare filename was all this printed, which told the one
rem person who had just run it the one thing they already knew and not where the file went.
echo.
echo Done. The installer is:
for %%F in ("%REPO%\artifacts\installer\*.exe") do @echo   %%~fF  (%%~zF bytes)
echo.
endlocal
