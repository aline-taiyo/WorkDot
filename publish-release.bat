@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$xml = [xml](Get-Content 'src\WorkDot\WorkDot.csproj'); $xml.Project.PropertyGroup.Version"`) do set "VER=%%v"
if "%VER%"=="" set "VER=1.5.7"

set "TAG=%~1"
if "%TAG%"=="" set "TAG=v%VER%"

echo [1/3] Building WorkDot (%TAG%) Release...
if exist "dist\publish" rmdir /s /q "dist\publish"
dotnet publish src\WorkDot\WorkDot.csproj -c Release -r win-x64 --self-contained false -o dist\publish
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

echo [2/3] Building MSI Installer (dist\WorkDot-Setup-win-x64.msi)...
where wix >nul 2>&1
if errorlevel 1 (
    dotnet tool install --global wix
)
wix eula accept wix7 >nul 2>&1
wix build installer\WorkDot.wxs -arch x64 -d ProductVersion="%VER%" -o "dist\WorkDot-Setup-win-x64.msi"
if errorlevel 1 (
    echo MSI build failed.
    exit /b 1
)

echo [3/3] MSI Installer created: %~dp0dist\WorkDot-Setup-win-x64.msi
endlocal
