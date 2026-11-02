# WorkDot 에이전트 작업 지침 (AGENTS.md)

WorkDot 프로젝트에서 코드를 수정하거나 릴리즈할 때 **모든 에이전트가 반드시 준수해야 하는 규칙**입니다.

---

## 1. 기능 변경 시 README.md 필수 갱신 원칙

- 새로운 기능이 추가되거나 기존 동작/UI/단축키/조작 방식이 변경되면, **코드 변경과 동시에 [README.md](file:///d:/work/workdot/README.md)를 반드시 업데이트**해야 합니다.
- **포함 필수 항목**:
  1. 변경된 기능의 동작 방식 및 사용자 사용 방법 (단축키, 마우스 조작, 메뉴 위치 등)
  2. UI 구조나 환경설정 화면이 변경된 경우 최신 스크린샷(`docs/screenshots/`) 캡처 및 README 링크 갱신
  3. 기본값이나 설정 옵션이 변경된 경우 옵션 목록 및 설명 최신화

---

## 2. 릴리즈 시 릴리즈 노트(CHANGELOG.md) 필수 작성 원칙

릴리즈 배포 요청이 있거나 새 버전을 발행할 때, **릴리즈 노트를 누락하고 버전만 올리는 행위는 엄격히 금지**됩니다.

- **릴리즈 표준 절차**:
  1. **README 최신화 확인**: 변경된 기능과 조작법이 [README.md](file:///d:/work/workdot/README.md)에 모두 반영되었는지 확인.
  2. **버전 상향**: `src/WorkDot/WorkDot.csproj`의 `<Version>` 및 README 최신 버전 표기 상향.
  3. **[CHANGELOG.md](file:///d:/work/workdot/CHANGELOG.md) 작성**:
     - 새 버전 헤더(`## [vX.Y.Z] - YYYY-MM-DD`) 추가.
     - `🚀 신규 기능 및 개선 (Features & Improvements)`, `🐛 버그 수정 (Bug Fixes)` 등으로 분류하여 구체적이고 상세하게 작성.
  4. **로컬 빌드 및 패키징 검증**:
     - `dotnet build WorkDot.slnx -c Release` 정상 확인.
     - `.\publish-release.bat`를 실행하여 MSI 인스톨러(`dist\WorkDot-Setup-win-x64.msi`) 생성이 정상 완료되는지 확인.
  5. **로컬 커밋 및 태그 생성**:
     - `git commit -m "chore: release vX.Y.Z"`
     - `git tag vX.Y.Z`
  6. **원격 Push 제한**:
     - **사용자의 명시적 push 요청("push해", "푸시해줘" 등)이 있을 때만** `git push origin main --tags`를 실행하며, 그 전에는 로컬 커밋 및 태그 상태만 유지.

---

## 3. 플랫폼 및 개발 환경 주의사항

- **WPF & WinForms 혼용 네임스페이스 모호성**:
  - `WorkDot.csproj`에 `<UseWindowsForms>true</UseWindowsForms>`가 켜져 있으므로, `KeyEventArgs`, `FontFamily`, `Color`, `ColorConverter` 사용 시 `System.Windows.Input`, `System.Windows.Media` 네임스페이스를 명시적으로 작성할 것.
- **WebView2 동기화**:
  - WebView2 브라우저 창은 사용자가 언제든 작업표시줄에서 식별할 수 있도록 `ShowInTaskbar="True"`를 유지하고, 포그라운드 전환 시 `Topmost` 토글 및 활성화를 보장할 것.
  - 앱 시작 시 오늘자 캐시(`last-probe.json`)를 즉시 복원하여 위젯이 퇴근 상태로 깜빡이지 않도록 유지할 것.
