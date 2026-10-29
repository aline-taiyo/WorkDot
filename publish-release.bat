@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "TAG=%~1"
if "%TAG%"=="" set "TAG=v1.5.0"

echo [1/3] WorkDot Release 빌드 및 게시 중...
dotnet publish src\WorkDot\WorkDot.csproj -c Release -r win-x64 --self-contained false -o dist\publish
if errorlevel 1 (
    echo 빌드 실패.
    exit /b 1
)

echo [2/3] 배포 패키지(dist\WorkDot-win-x64.zip) 생성 중...
powershell -NoProfile -Command "if (Test-Path 'dist\WorkDot-win-x64.zip') { Remove-Item 'dist\WorkDot-win-x64.zip' -Force }; Compress-Archive -Path 'dist\publish\*' -DestinationPath 'dist\WorkDot-win-x64.zip' -Force"
if errorlevel 1 (
    echo 압축 생성 실패.
    exit /b 1
)

echo [3/3] 패키지 생성 완료: %~dp0dist\WorkDot-win-x64.zip
where gh >nul 2>&1
if %errorlevel%==0 (
    echo GitHub CLI(gh)를 통해 릴리스(%TAG%) 업로드를 시도합니다...
    gh release create "%TAG%" "dist\WorkDot-win-x64.zip" --title "WorkDot %TAG%" --generate-notes
) else (
    echo 참고: GitHub에 태그(git tag %TAG% ^&^& git push origin %TAG%)를 푸시하면 GitHub Actions가 자동으로 Release를 배포하거나,
    echo https://github.com/xl-taiyo/WorkDot/releases/new 에 dist\WorkDot-win-x64.zip 파일을 업로드할 수 있습니다.
)

endlocal
