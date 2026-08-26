@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo Building Qsirch Floating Search v10.15...
echo.

if not exist "config.json" (
    echo ERROR: config.json is missing from the source folder.
    exit /b 1
)

py -3 verify_source.py || exit /b 1
py -3 -m pip install -r requirements.txt || exit /b 1
py -3 -m pip install pyinstaller || exit /b 1

if exist build rmdir /s /q build
if exist dist rmdir /s /q dist
if exist QsirchFloating.spec del /q QsirchFloating.spec

py -3 -m PyInstaller --noconfirm --clean --onedir --windowed --name QsirchFloating qsirch_gui.py || exit /b 1

copy /Y "config.json" "dist\QsirchFloating\config.json" >nul || (
    echo ERROR: Failed to copy config.json into dist.
    exit /b 1
)

copy /Y "README.txt" "dist\QsirchFloating\README.txt" >nul

if not exist "dist\QsirchFloating\QsirchFloating.exe" (
    echo ERROR: EXE was not generated.
    exit /b 1
)

if not exist "dist\QsirchFloating\config.json" (
    echo ERROR: config.json was not generated beside the EXE.
    exit /b 1
)

echo.
echo SUCCESS
echo   dist\QsirchFloating\QsirchFloating.exe
echo   dist\QsirchFloating\config.json
echo.
echo The running UI should visibly show v10.15.
pause
