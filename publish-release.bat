@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "TAG=%~1"
if "%TAG%"=="" (
    for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$xml = [xml](Get-Content 'src\WorkDot\WorkDot.csproj'); 'v' + $xml.Project.PropertyGroup.Version"`) do set "TAG=%%v"
)
if "%TAG%"=="" set "TAG=v1.5.4"

echo [1/3] Building WorkDot (%TAG%) Release...
dotnet publish src\WorkDot\WorkDot.csproj -c Release -r win-x64 --self-contained false -o dist\publish
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

echo [2/3] Packaging dist\WorkDot-win-x64.zip...
powershell -NoProfile -Command "New-Item -ItemType Directory -Force -Path 'dist' | Out-Null; if (Test-Path 'dist\WorkDot-win-x64.zip') { Remove-Item 'dist\WorkDot-win-x64.zip' -Force }; Compress-Archive -Path 'dist\publish\*' -DestinationPath 'dist\WorkDot-win-x64.zip' -Force"
if errorlevel 1 (
    echo Zip packaging failed.
    exit /b 1
)

echo [3/3] Package created: %~dp0dist\WorkDot-win-x64.zip
where gh >nul 2>&1
if %errorlevel%==0 (
    echo Uploading GitHub Release %TAG% via gh CLI...
    gh release create "%TAG%" "dist\WorkDot-win-x64.zip" --title "WorkDot %TAG%" --generate-notes
) else (
    echo Push tag to publish via GitHub Actions: git tag -f %TAG% ^&^& git push origin %TAG% --force
)

endlocal
