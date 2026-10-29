@echo off
chcp 65001 >nul
pushd "%~dp0"

echo [1/3] 실행 중인 WorkDot 프로세스 종료 중...
taskkill /F /IM WorkDot.exe >nul 2>&1

echo [2/3] 최신 소스 Release 빌드 중...
dotnet build "src\WorkDot\WorkDot.csproj" -c Release --nologo
if errorlevel 1 (
    echo.
    echo [오류] 빌드에 실패했습니다.
    popd
    pause
    exit /b 1
)

echo [3/3] WorkDot 실행 중...
start "" "%~dp0src\WorkDot\bin\Release\net10.0-windows\WorkDot.exe"
popd
