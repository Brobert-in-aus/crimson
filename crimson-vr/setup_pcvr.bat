@echo off
setlocal EnableExtensions

if /I "%~1"=="/?" goto :usage
if /I "%~1"=="--help" goto :usage

set "GAME_DIR=%~1"
set "REPO_ROOT=%~dp0.."

call :find_python
if errorlevel 1 exit /b %errorlevel%

pushd "%REPO_ROOT%" || exit /b 4
if defined GAME_DIR (
    call :prepare "%GAME_DIR%" --pcvr
) else (
    call :prepare --pcvr
)
set "RESULT=%ERRORLEVEL%"
popd

if not "%RESULT%"=="0" (
    echo.
    echo PCVR setup failed. Correct the error above and run this file again.
    exit /b %RESULT%
)

echo.
echo PCVR asset setup complete.
echo Start SteamVR, VDXR, or another OpenXR runtime, then run CrimsonVR.exe
echo from the fully extracted Windows package.
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

:usage
echo CrimsonVR PCVR setup
echo.
echo Usage:
echo   setup_pcvr.bat [CLASSIC_GAME_DIR]
echo.
echo Examples:
echo   setup_pcvr.bat
echo   setup_pcvr.bat "D:\GOG Games\Crimsonland"
echo.
echo This creates an asset pack from your own Crimsonland Classic installation
echo and places it in CrimsonVR's first-run PCVR inbox.
exit /b 0
