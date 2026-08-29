@echo off
setlocal
cd /d "%~dp0"

echo Building self-contained PyQsirchgui native Windows app...
echo.

set "PACKAGE=dist\PyQsirchgui"

if exist "%PACKAGE%" rmdir /s /q "%PACKAGE%"
mkdir "%PACKAGE%" || exit /b 1
mkdir "%PACKAGE%\config" || exit /b 1
mkdir "%PACKAGE%\data" || exit /b 1
mkdir "%PACKAGE%\logs" || exit /b 1
mkdir "%PACKAGE%\resources" || exit /b 1

dotnet publish src\PyQsirchgui.Windows\PyQsirchgui.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%PACKAGE%" || goto :cleanup_error

rem Release packages always receive the sanitized repository configuration.
copy /Y "config.json" "%PACKAGE%\config\config.json" >nul || goto :cleanup_error

xcopy /E /I /Y "src\PyQsirchgui.Windows\Assets" "%PACKAGE%\resources\Assets" >nul || (
    echo ERROR: Failed to copy package resources.
    exit /b 1
)

copy /Y "PyQsirchgui-README.txt" "%PACKAGE%\resources\README.txt" >nul

echo.
echo SUCCESS
echo   %PACKAGE%\PyQsirchgui.exe
echo   %PACKAGE%\config\config.json

pause
exit /b 0

:cleanup_error
echo ERROR: Build did not complete.
exit /b 1
