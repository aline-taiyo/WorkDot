@echo off
chcp 65001 >nul
pushd "%~dp0"

echo [1/3] Stopping running WorkDot process...
taskkill /F /T /IM WorkDot.exe >nul 2>&1
powershell -NoProfile -Command "Stop-Process -Name 'WorkDot' -Force -ErrorAction SilentlyContinue; for ($i=0; $i -lt 20; $i++) { if (-not (Get-Process -Name 'WorkDot' -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 150 }"

echo [2/3] Building latest Release...
dotnet build "src\WorkDot\WorkDot.csproj" -c Release --nologo
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed.
    popd
    pause
    exit /b 1
)

echo [3/3] Launching WorkDot...
start "" "%~dp0src\WorkDot\bin\Release\net10.0-windows\WorkDot.exe"
popd
