@echo off
setlocal
cd /d "%~dp0"

echo Building PyQsirchgui native Windows app...
echo.

dotnet publish src\PyQsirchgui.Windows\PyQsirchgui.Windows.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false || exit /b 1

echo.
echo SUCCESS
echo   src\PyQsirchgui.Windows\bin\Release\net9.0-windows\win-x64\publish\PyQsirchgui.exe
