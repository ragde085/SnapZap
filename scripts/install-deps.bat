@echo off
rem Install SnapZap's two optional sidecars - Windows.
rem
rem   scripts\install-deps.bat                 install both into the repo (dev)
rem   scripts\install-deps.bat --dest DIR      install beside a published SnapZap.App.exe
rem   scripts\install-deps.bat --model-only    NSFW model only
rem   scripts\install-deps.bat --czkawka-only  similar-photo detection only
rem   scripts\install-deps.bat --force         re-download even if already present
rem
rem Both are optional: SnapZap scans, finds exact duplicates, exports and deletes without
rem either one. The model unlocks NSFW scoring; czkawka_cli unlocks similar-photo detection.
rem
rem Every download is pinned to an exact revision and SHA-256 verified before it is put in
rem place. Nothing is installed from a checksum that does not match.
rem
rem Needs curl.exe and certutil.exe, both built into Windows 10 1803 and later.

setlocal enabledelayedexpansion
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..") do set "REPO_ROOT=%%~fI"

rem --- pinned versions ------------------------------------------------------------------
rem The model: Falconsai/nsfw_image_detection (Apache-2.0), ONNX conversion by onnx-community,
rem pinned to an immutable revision so the checksums below stay meaningful.
set "MODEL_REV=1ceb3c7fe1e9f3f2507e6df577437f23a9149fd5"
set "MODEL_BASE=https://huggingface.co/onnx-community/nsfw_image_detection-ONNX/resolve/%MODEL_REV%"
set "MODEL_SHA=a4316a4fb750169ac4fcabaabee1fcbd982b0ee8c0cc63fe3e944954bb9a7d9c"
set "CONFIG_SHA=ae9bb157b9629887cc74913a4e7c12c9308f374f0930e8072320e8f2e1583c5e"

rem czkawka: pinned to 12.0.0, the release CzkawkaFinder's JSON parser is tested against.
set "CZKAWKA_VER=12.0.0"
set "CZKAWKA_URL=https://github.com/qarmin/czkawka/releases/download/%CZKAWKA_VER%/windows_czkawka_cli.exe"
set "CZKAWKA_SHA=fbdc5ced0fefd6f3222cf4920ede2c2c9f6286433a6a96b9c1e8e525c788597e"

set "DEST="
set "WANT_MODEL=1"
set "WANT_CZKAWKA=1"
set "FORCE=0"

:parse
if "%~1"=="" goto parsed
if /i "%~1"=="--dest" goto opt_dest
if /i "%~1"=="--model-only" goto opt_model_only
if /i "%~1"=="--czkawka-only" goto opt_czkawka_only
if /i "%~1"=="--force" goto opt_force
if /i "%~1"=="-h" goto usage
if /i "%~1"=="--help" goto usage
echo unknown option: %~1
goto usage

:opt_model_only
set "WANT_CZKAWKA=0"
shift
goto parse

:opt_czkawka_only
set "WANT_MODEL=0"
shift
goto parse

:opt_force
set "FORCE=1"
shift
goto parse

:opt_dest
if not "%~2"=="" goto opt_dest_ok
echo --dest needs a directory
exit /b 1
:opt_dest_ok
set "DEST=%~f2"
shift
shift
goto parse

:parsed
where curl.exe >nul 2>&1
if not errorlevel 1 goto have_curl
echo curl.exe not found - needs Windows 10 1803 or later
exit /b 1
:have_curl

rem Where the app looks. With no --dest we install into the repo and let the build copy both
rem into the output directory (see SnapZap.App.csproj), so `dotnet run` picks them up with no
rem environment variables and no manual copying.
set "MODEL_DIR=%REPO_ROOT%\models"
set "CZKAWKA_DIR=%REPO_ROOT%\tools"
if not defined DEST goto dirs_ready
if not exist "%DEST%" mkdir "%DEST%"
set "MODEL_DIR=%DEST%\models"
set "CZKAWKA_DIR=%DEST%"

:dirs_ready
echo SnapZap optional sidecars
echo.

if "%WANT_MODEL%"=="0" goto skip_model
echo NSFW scoring model to %MODEL_DIR%
call :fetch "%MODEL_BASE%/onnx/model.onnx" "%MODEL_DIR%\nsfw.onnx" "%MODEL_SHA%" "nsfw.onnx (328 MB)"
if errorlevel 1 exit /b 1
call :fetch "%MODEL_BASE%/preprocessor_config.json" "%MODEL_DIR%\preprocessor_config.json" "%CONFIG_SHA%" "preprocessor_config.json"
if errorlevel 1 exit /b 1
echo.

:skip_model
if "%WANT_CZKAWKA%"=="0" goto skip_czkawka
echo Similar-photo detection to %CZKAWKA_DIR%
call :fetch "%CZKAWKA_URL%" "%CZKAWKA_DIR%\czkawka_cli.exe" "%CZKAWKA_SHA%" "czkawka_cli %CZKAWKA_VER%"
if errorlevel 1 exit /b 1
echo.

:skip_czkawka
echo Done.
if defined DEST goto done_dest
echo The build copies both into the app's output directory, so:
echo.
echo     dotnet run --project src\SnapZap.App
echo.
echo will find them. Confirm under Setup in the app's left rail.
exit /b 0

:done_dest
echo Installed beside the binary in %DEST%.
echo Start the app and check Setup in the left rail.
exit /b 0

rem ---------------------------------------------------------------------------------------
rem :fetch URL OUT EXPECTED_SHA LABEL
rem Downloads to a .partial file, verifies, then moves into place - so an interrupted or
rem corrupted transfer can never leave a half-written model where the app would load it.
:fetch
setlocal enabledelayedexpansion
set "URL=%~1"
set "OUT=%~2"
set "WANT=%~3"
set "LABEL=%~4"

if "%FORCE%"=="1" goto download
if not exist "%OUT%" goto download
call :sha256 "%OUT%" HAVE
if /i not "!HAVE!"=="%WANT%" goto download
echo   [ok] %LABEL% - already installed
endlocal & exit /b 0

:download
echo   [..] %LABEL%
rem Errors are swallowed rather than tested: "already exists" is the common case and is fine,
rem and a directory that genuinely can't be created shows up as a curl failure a line later.
for %%I in ("%OUT%") do mkdir "%%~dpI" 2>nul
curl.exe -fL --retry 3 --retry-delay 2 -C - --progress-bar -o "%OUT%.partial" "%URL%"
if errorlevel 1 goto fetch_failed

call :sha256 "%OUT%.partial" GOT
if /i "!GOT!"=="%WANT%" goto fetch_ok
echo   [fail] %LABEL% - checksum mismatch, not installed 1>&2
echo        expected %WANT% 1>&2
echo        got      !GOT! 1>&2
del /q "%OUT%.partial" >nul 2>&1
endlocal & exit /b 1

:fetch_failed
echo   [fail] %LABEL% - download failed 1>&2
del /q "%OUT%.partial" >nul 2>&1
endlocal & exit /b 1

:fetch_ok
move /y "%OUT%.partial" "%OUT%" >nul
echo   [ok] %LABEL%
endlocal & exit /b 0

rem :sha256 FILE VARNAME - certutil prints the digest on the line after its header.
:sha256
setlocal enabledelayedexpansion
set "HASH="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%~1" SHA256') do if not defined HASH set "HASH=%%H"
set "HASH=%HASH: =%"
endlocal & set "%~2=%HASH%" & exit /b 0

:usage
echo Install SnapZap's two optional sidecars.
echo.
echo   scripts\install-deps.bat                 install both into the repo (dev)
echo   scripts\install-deps.bat --dest DIR      install beside a published SnapZap.App.exe
echo   scripts\install-deps.bat --model-only    NSFW model only
echo   scripts\install-deps.bat --czkawka-only  similar-photo detection only
echo   scripts\install-deps.bat --force         re-download even if already present
exit /b 1
