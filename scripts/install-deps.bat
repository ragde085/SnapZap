@echo off
rem Install SnapZap's optional sidecar - Windows.
rem
rem   scripts\install-deps.bat                 install into the repo (dev)
rem   scripts\install-deps.bat --dest DIR      install beside a published SnapZap.App.exe
rem   scripts\install-deps.bat --model-only    same thing; kept so older notes still work
rem   scripts\install-deps.bat --force         re-download even if already present
rem
rem It is optional: SnapZap scans, finds duplicates, exports and deletes without it. The model
rem unlocks NSFW scoring, and nothing else. (czkawka_cli used to be installed here too;
rem similar-photo detection moved in-process - see docs/DEDUP-V2.md.)
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


set "DEST="
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
rem Accepted and ignored: there is only one sidecar left, and failing on a flag that used to
rem work would break every note, script and shell history that still passes it.
shift
goto parse

:opt_czkawka_only
echo   ! --czkawka-only no longer does anything: similar-photo detection is built in.
echo     See docs/DEDUP-V2.md.
exit /b 0

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
if not defined DEST goto dirs_ready
if not exist "%DEST%" mkdir "%DEST%"
set "MODEL_DIR=%DEST%\models"

:dirs_ready
echo SnapZap optional sidecar
echo.

echo NSFW scoring model to %MODEL_DIR%
call :fetch "%MODEL_BASE%/onnx/model.onnx" "%MODEL_DIR%\nsfw.onnx" "%MODEL_SHA%" "nsfw.onnx (328 MB)"
if errorlevel 1 exit /b 1
call :fetch "%MODEL_BASE%/preprocessor_config.json" "%MODEL_DIR%\preprocessor_config.json" "%CONFIG_SHA%" "preprocessor_config.json"
if errorlevel 1 exit /b 1
echo.

echo Done.
if defined DEST goto done_dest
echo The build copies it into the app's output directory, so:
echo.
echo     dotnet run --project src\SnapZap.App
echo.
echo will find it. Confirm under Setup in the app's left rail.
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
echo Install SnapZap's optional sidecar.
echo.
echo   scripts\install-deps.bat                 install into the repo (dev)
echo   scripts\install-deps.bat --dest DIR      install beside a published SnapZap.App.exe
echo   scripts\install-deps.bat --model-only    same thing; kept so older notes still work
echo   scripts\install-deps.bat --force         re-download even if already present
exit /b 1
