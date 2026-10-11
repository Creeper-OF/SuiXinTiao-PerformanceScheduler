@echo off
setlocal

rem Start the WPF client from the repository root without hard-coded paths.
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo .NET SDK was not found. Install the .NET 10 SDK and try again.
    pause
    exit /b 1
)

set "CLIENT_DIR="
for /d %%D in ("%~dp0*") do (
    if exist "%%~fD\src\PerformanceScheduler.App\PerformanceScheduler.App.csproj" (
        set "CLIENT_DIR=%%~fD"
        goto :client_found
    )
)

echo Could not locate the client project under the repository root.
echo Expected: ^<repository^>\<client folder^>\src\PerformanceScheduler.App
pause
exit /b 1

:client_found
echo Starting SuiXinTiao client...
dotnet run --project "%CLIENT_DIR%\src\PerformanceScheduler.App\PerformanceScheduler.App.csproj" --configuration Debug
if errorlevel 1 (
    echo.
    echo The client stopped with an error.
    pause
    exit /b 1
)

endlocal
