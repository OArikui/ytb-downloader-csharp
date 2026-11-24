@echo off
setlocal

rem Launch YtbDownloader in tray mode without keeping a console window open.
rem Usage:
rem   start-tray.bat                 -> uses config.toml next to the exe (or defaults)
rem   start-tray.bat PATH\config.toml -> uses the specified config file

set "EXE=%~dp0..\src\YtbDownloader\bin\Release\net9.0-windows\win-x64\publish\YtbDownloader.exe"

if not exist "%EXE%" (
    echo [ERROR] YtbDownloader.exe not found:
    echo   %EXE%
    echo.
    echo Run publish first:
    echo   cd /d "%~dp0..\src\YtbDownloader"
    echo   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
    echo.
    pause
    exit /b 1
)

if "%~1"=="" (
    start "" "%EXE%" --tray
) else (
    start "" "%EXE%" --config "%~1" --tray
)

exit /b 0
