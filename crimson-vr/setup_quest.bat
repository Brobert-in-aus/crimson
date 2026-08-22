@echo off
setlocal EnableExtensions

if "%~1"=="" goto :usage_error
if /I "%~1"=="/?" goto :usage
if /I "%~1"=="--help" goto :usage

set "APK=%~f1"
set "GAME_DIR=%~2"
set "DEVICE=%~3"
set "REPO_ROOT=%~dp0.."

if not exist "%APK%" (
    echo ERROR: Quest APK not found: "%APK%"
    exit /b 2
)

where adb >nul 2>nul
if errorlevel 1 (
    echo ERROR: adb was not found on PATH.
    echo Install Android platform-tools, enable Quest Developer Mode, connect by USB,
    echo approve USB debugging in the headset, then run this file again.
    exit /b 3
)

call :find_python
if errorlevel 1 exit /b %errorlevel%

pushd "%REPO_ROOT%" || exit /b 4
if defined GAME_DIR (
    if defined DEVICE (
        call :prepare "%GAME_DIR%" --quest --apk "%APK%" --device "%DEVICE%"
    ) else (
        call :prepare "%GAME_DIR%" --quest --apk "%APK%"
    )
) else (
    if defined DEVICE (
        call :prepare --quest --apk "%APK%" --device "%DEVICE%"
    ) else (
        call :prepare --quest --apk "%APK%"
    )
)
set "RESULT=%ERRORLEVEL%"
popd

if not "%RESULT%"=="0" (
    echo.
    echo Quest setup failed. Correct the error above and run this file again.
    exit /b %RESULT%
)

echo.
echo Quest setup complete. Put on the headset and launch CrimsonVR from the app library.
echo The locally generated asset pack was not uploaded anywhere.
exit /b 0

:find_python
for /f "delims=" %%I in ('where uv 2^>nul') do if not defined UV_EXE set "UV_EXE=%%I"
if not defined UV_EXE if exist "%USERPROFILE%\.local\bin\uv.exe" set "UV_EXE=%USERPROFILE%\.local\bin\uv.exe"
if defined UV_EXE exit /b 0
if exist "%REPO_ROOT%\.venv\Scripts\python.exe" (
    set "PYTHON_EXE=%REPO_ROOT%\.venv\Scripts\python.exe"
    exit /b 0
)
echo ERROR: uv is required to prepare assets.
echo Install it with: winget install --id=astral-sh.uv -e
exit /b 5

:prepare
if defined UV_EXE (
    "%UV_EXE%" run python crimson-vr\tools\prepare_assets.py %*
) else (
    "%PYTHON_EXE%" crimson-vr\tools\prepare_assets.py %*
)
exit /b %ERRORLEVEL%

:usage_error
call :usage
exit /b 1

:usage
echo CrimsonVR Quest setup
echo.
echo Usage:
echo   setup_quest.bat APK_PATH [CLASSIC_GAME_DIR] [ADB_DEVICE]
echo.
echo Examples:
echo   setup_quest.bat "%USERPROFILE%\Downloads\CrimsonVR.quest.apk"
echo   setup_quest.bat "C:\Downloads\CrimsonVR.quest.apk" "D:\GOG Games\Crimsonland"
echo.
echo This installs the asset-free APK, creates an asset pack from your own
echo Crimsonland Classic installation, transfers it to the Quest, and leaves
echo the app stopped for a normal first launch from the headset library.
exit /b 0
