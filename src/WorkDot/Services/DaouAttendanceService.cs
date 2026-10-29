using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WorkDot.Services;

public enum WorkStatusKind
{
    Unknown,
    NeedsLogin,
    OffWork,   // 퇴근 / 미출근 (빨간색)
    Working    // 근무중 (초록색)
}

public sealed record AttendanceSnapshot(
    WorkStatusKind Status,
    string StatusLabel,
    string SubText,
    string? ClockInTimeText,
    string? ClockOutTimeText,
    string? WorkedDurationText,
    DateTime UpdatedAt);

public sealed class DaouAttendanceService
{
    public const string TargetAttendanceUrl = "https://alinestudio.daouoffice.com/ehr/app/attend/my-attendance-status";
    public const string BaseDomain = "alinestudio.daouoffice.com";

    private CoreWebView2Environment? _env;
    private WebView2? _hiddenWebView;
    private bool _isInitialized;

    public AttendanceSnapshot CurrentSnapshot { get; private set; } = new(
        WorkStatusKind.OffWork,
        "퇴근 (연동 대기)",
        "클릭하여 그룹웨어 연결",
        null,
        null,
        null,
        DateTime.Now);

    public event Action<AttendanceSnapshot>? SnapshotChanged;

    public string UserDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkDot", "WebView2Profile");

    public async Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync()
    {
        if (_env != null)
            return _env;

        Directory.CreateDirectory(UserDataFolder);
        _env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
        return _env;
    }

    public async Task InitializeHiddenWebViewAsync(WebView2 hiddenWebView)
    {
        if (_isInitialized)
            return;

        _hiddenWebView = hiddenWebView;
        var env = await GetOrCreateEnvironmentAsync();
        await _hiddenWebView.EnsureCoreWebView2Async(env);

        ConfigureWebViewPopupAndNavigationGuard(_hiddenWebView.CoreWebView2, autoRedirectToAttendance: true);

        _hiddenWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        _hiddenWebView.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;

        _hiddenWebView.CoreWebView2.ScriptDialogOpening += (_, args) =>
        {
            args.Accept();
        };

        _isInitialized = true;
        _hiddenWebView.Source = new Uri(TargetAttendanceUrl);
    }

    /// <summary>
    /// 다우오피스 로그인 직후 설문조사/공지 팝업(window.open 또는 강제 리다이렉트)이
    /// 화면을 가로막는 현상을 방지하도록 WebView2에 가드를 설정합니다.
    /// </summary>
    public void ConfigureWebViewPopupAndNavigationGuard(CoreWebView2 coreWebView, bool autoRedirectToAttendance)
    {
        // 1. window.open 새 창 팝업(설문조사/공지 팝업)이 뜨면서 원래 창을 덮어쓰거나 가로막는 것 차단
        coreWebView.NewWindowRequested += (_, e) =>
        {
            string targetUri = e.Uri ?? string.Empty;

            // 설문조사(survey), 공지 팝업(popup/notice) 새 창은 즉시 무시
            if (IsBlockingSurveyOrPopupUrl(targetUri))
            {
                e.Handled = true;
                return;
            }

            // 그 외 유효한 근태/로그인 이동이면 새 창 대신 현재 창에서 이동
            e.Handled = true;
            if (!string.IsNullOrWhiteSpace(targetUri) && targetUri.Contains(BaseDomain, StringComparison.OrdinalIgnoreCase))
            {
                coreWebView.Navigate(targetUri);
            }
        };

        // 2. 로그인 완료 후 설문조사(/app/survey)나 홈(/app/home) 등으로 납치될 경우 근태 페이지로 자동 복귀
        coreWebView.SourceChanged += async (_, _) =>
        {
            string current = coreWebView.Source ?? string.Empty;
            if (!autoRedirectToAttendance)
                return;

            if (IsRedirectedAwayFromAttendanceAfterLogin(current))
            {
                await Task.Delay(300);
                coreWebView.Navigate(TargetAttendanceUrl);
            }
        };
    }

    public static bool IsBlockingSurveyOrPopupUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        return url.Contains("/survey", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("popup", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/notice", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRedirectedAwayFromAttendanceAfterLogin(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (IsLoginUrl(url))
            return false;

        // 이미 근태(ehr) 페이지에 있으면 정상
        if (url.Contains("/ehr/", StringComparison.OrdinalIgnoreCase))
            return false;

        // 로그인 후 설문조사(/app/survey), 대시보드(/app/home), 공지 게시판(/app/board) 등으로 튕긴 경우
        if (url.Contains("/app/survey", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/app/home", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("/app/board", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith(".com/", StringComparison.OrdinalIgnoreCase) ||
            url.EndsWith("/app", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 화면을 덮고 있는 다우오피스 설문조사/공지 레이어 팝업(.go_popup, .layer_normal, .dimmed 등)을 강제로 닫거나 제거합니다.
    /// </summary>
    public async Task<int> DismissBlockingPopupsAndSurveysAsync(CoreWebView2 webView)
    {
        try
        {
            string raw = await webView.ExecuteScriptAsync(GetDismissPopupsScript());
            if (int.TryParse(raw, out int closedCount))
                return closedCount;
        }
        catch
        {
            // Ignore script errors
        }
        return 0;
    }

    public async Task RefreshStatusAsync()
    {
        if (_hiddenWebView?.CoreWebView2 == null)
            return;

        string currentUrl = _hiddenWebView.CoreWebView2.Source ?? string.Empty;
        if (IsLoginUrl(currentUrl))
        {
            UpdateSnapshot(new AttendanceSnapshot(
                WorkStatusKind.NeedsLogin,
                "퇴근 · 로그인 필요",
                "그룹웨어 로그인 후 자동 연동",
                null,
                null,
                null,
                DateTime.Now));
            return;
        }

        if (!currentUrl.Contains("/ehr/", StringComparison.OrdinalIgnoreCase))
        {
            _hiddenWebView.CoreWebView2.Navigate(TargetAttendanceUrl);
            return;
        }

        await DismissBlockingPopupsAndSurveysAsync(_hiddenWebView.CoreWebView2);
        await ProbeDomAndApiStatusAsync(_hiddenWebView.CoreWebView2);
    }

    /// <summary>
    /// 오버레이에서 원클릭으로 출근 또는 퇴근 처리를 수행합니다.
    /// </summary>
    public async Task<(bool Success, string Message)> TriggerClockActionAsync(bool clockIn)
    {
        if (_hiddenWebView?.CoreWebView2 == null)
            return (false, "WebView2가 아직 초기화되지 않았습니다.");

        string currentUrl = _hiddenWebView.CoreWebView2.Source ?? string.Empty;
        if (IsLoginUrl(currentUrl))
            return (false, "그룹웨어 로그인이 필요합니다. 로그인 창을 열어주세요.");

        if (!currentUrl.Contains("/ehr/", StringComparison.OrdinalIgnoreCase))
        {
            _hiddenWebView.CoreWebView2.Navigate(TargetAttendanceUrl);
            await Task.Delay(1500);
        }

        await DismissBlockingPopupsAndSurveysAsync(_hiddenWebView.CoreWebView2);

        string script = clockIn ? GetClockInTriggerScript() : GetClockOutTriggerScript();

        try
        {
            string rawResult = await _hiddenWebView.CoreWebView2.ExecuteScriptAsync(script);
            string result = JsonSerializer.Deserialize<string>(rawResult) ?? "UNKNOWN";

            if (result.StartsWith("OK:", StringComparison.Ordinal))
            {
                await Task.Delay(600);
                await _hiddenWebView.CoreWebView2.ExecuteScriptAsync(GetConfirmModalClickScript());
                await Task.Delay(900);
                await ProbeDomAndApiStatusAsync(_hiddenWebView.CoreWebView2);

                return (true, clockIn ? "출근 처리가 요청되었습니다." : "퇴근 처리가 요청되었습니다.");
            }

            return (false, $"다우오피스 페이지에서 {(clockIn ? "출근" : "퇴근")} 버튼을 찾지 못했습니다 ({result}). 그룹웨어 창에서 직접 확인해주세요.");
        }
        catch (Exception ex)
        {
            return (false, $"처리 중 오류: {ex.Message}");
        }
    }

    public void SetManualStatusOverride(WorkStatusKind status)
    {
        string nowTime = DateTime.Now.ToString("HH:mm");
        if (status == WorkStatusKind.Working)
        {
            UpdateSnapshot(new AttendanceSnapshot(
                WorkStatusKind.Working,
                "근무중",
                $"출근 {CurrentSnapshot.ClockInTimeText ?? nowTime}",
                CurrentSnapshot.ClockInTimeText ?? nowTime,
                null,
                CurrentSnapshot.WorkedDurationText,
                DateTime.Now));
        }
        else
        {
            UpdateSnapshot(new AttendanceSnapshot(
                WorkStatusKind.OffWork,
                "퇴근",
                $"퇴근 {nowTime}",
                CurrentSnapshot.ClockInTimeText,
                nowTime,
                CurrentSnapshot.WorkedDurationText,
                DateTime.Now));
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_hiddenWebView?.CoreWebView2 == null)
            return;

        string url = _hiddenWebView.CoreWebView2.Source ?? string.Empty;
        if (IsLoginUrl(url))
        {
            UpdateSnapshot(new AttendanceSnapshot(
                WorkStatusKind.NeedsLogin,
                "퇴근 · 로그인 필요",
                "그룹웨어 로그인 필요",
                null,
                null,
                null,
                DateTime.Now));
            return;
        }

        if (IsRedirectedAwayFromAttendanceAfterLogin(url))
        {
            _hiddenWebView.CoreWebView2.Navigate(TargetAttendanceUrl);
            return;
        }

        await Task.Delay(1000);
        await DismissBlockingPopupsAndSurveysAsync(_hiddenWebView.CoreWebView2);
        await ProbeDomAndApiStatusAsync(_hiddenWebView.CoreWebView2);
    }

    private async void OnWebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        try
        {
            string uri = e.Request.Uri;
            if (!uri.Contains("/api/ehr/", StringComparison.OrdinalIgnoreCase) &&
                !uri.Contains("timeline", StringComparison.OrdinalIgnoreCase) &&
                !uri.Contains("attend", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (e.Response.StatusCode == 401 || e.Response.StatusCode == 403)
            {
                UpdateSnapshot(new AttendanceSnapshot(
                    WorkStatusKind.NeedsLogin,
                    "퇴근 · 로그인 필요",
                    "세션 만료됨",
                    null,
                    null,
                    null,
                    DateTime.Now));
                return;
            }

            if (_hiddenWebView?.CoreWebView2 != null)
            {
                await Task.Delay(500);
                await ProbeDomAndApiStatusAsync(_hiddenWebView.CoreWebView2);
            }
        }
        catch
        {
            // Ignore stream inspection errors
        }
    }

    public async Task ProbeDomAndApiStatusAsync(CoreWebView2 webView)
    {
        try
        {
            string rawJson = await webView.ExecuteScriptAsync(GetAttendanceProbeScript());
            string? innerJson = JsonSerializer.Deserialize<string>(rawJson);
            if (string.IsNullOrWhiteSpace(innerJson))
                return;

            using var doc = JsonDocument.Parse(innerJson);
            var root = doc.RootElement;

            bool isLogin = root.TryGetProperty("isLoginPage", out var loginProp) && loginProp.GetBoolean();
            if (isLogin)
            {
                UpdateSnapshot(new AttendanceSnapshot(
                    WorkStatusKind.NeedsLogin,
                    "퇴근 · 로그인 필요",
                    "클릭하여 다우오피스 로그인",
                    null,
                    null,
                    null,
                    DateTime.Now));
                return;
            }

            string state = root.TryGetProperty("state", out var sProp) ? (sProp.GetString() ?? "UNKNOWN") : "UNKNOWN";
            string? clockIn = root.TryGetProperty("clockIn", out var inProp) ? inProp.GetString() : null;
            string? clockOut = root.TryGetProperty("clockOut", out var outProp) ? outProp.GetString() : null;
            string? duration = root.TryGetProperty("duration", out var durProp) ? durProp.GetString() : null;
            string? badgeText = root.TryGetProperty("badgeText", out var bProp) ? bProp.GetString() : null;

            if (state == "WORKING")
            {
                string sub = !string.IsNullOrWhiteSpace(clockIn)
                    ? $"출근 {clockIn}" + (!string.IsNullOrWhiteSpace(duration) ? $" · {duration}" : "")
                    : (!string.IsNullOrWhiteSpace(badgeText) ? badgeText : "정상 근무중");

                UpdateSnapshot(new AttendanceSnapshot(
                    WorkStatusKind.Working,
                    "근무중",
                    sub,
                    clockIn,
                    clockOut,
                    duration,
                    DateTime.Now));
            }
            else if (state == "OFF_WORK")
            {
                string sub = !string.IsNullOrWhiteSpace(clockOut)
                    ? $"퇴근 {clockOut}"
                    : (!string.IsNullOrWhiteSpace(badgeText) ? badgeText : "업무 종료 / 출근 전");

                UpdateSnapshot(new AttendanceSnapshot(
                    WorkStatusKind.OffWork,
                    "퇴근",
                    sub,
                    clockIn,
                    clockOut,
                    duration,
                    DateTime.Now));
            }
        }
        catch
        {
            // Ignore transient script failures
        }
    }

    private void UpdateSnapshot(AttendanceSnapshot next)
    {
        CurrentSnapshot = next;
        SnapshotChanged?.Invoke(next);
    }

    public static bool IsLoginUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return url.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("sso", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 다우오피스 내 설문조사·공지사항 모달 팝업 레이어를 닫거나 DOM에서 제거하는 스크립트입니다.
    /// </summary>
    private static string GetDismissPopupsScript() => """
        (() => {
            let count = 0;
            const clean = (s) => (s || '').replace(/\s+/g, ' ').trim();

            // 1. 닫기 버튼이나 '오늘 하루 열지 않음', '다음에 하기', '닫기' 버튼 클릭
            const closeBtnSelectors = [
                '.go_popup .btn_layer_x',
                '.go_popup a.btn_minor_s',
                '.layer_normal .btn_layer_x',
                '.layer_notice .btn_layer_x',
                '[data-role="layerClose"]',
                '.btn_popup_close',
                '.survey_popup .close'
            ];
            for (const sel of closeBtnSelectors) {
                document.querySelectorAll(sel).forEach(el => {
                    try { el.click(); count++; } catch {}
                });
            }

            // 2. 팝업 컨테이너 내부의 '닫기' / '취소' / '나중에' 버튼 탐색 후 클릭 및 레이어 제거
            const popupContainers = document.querySelectorAll(
                '.go_popup, .layer_normal, .layer_notice, .layer_survey, #popOverlay, .overlay, .dimmed, [id*="popup"], [class*="popup_layer"]'
            );
            popupContainers.forEach(popup => {
                const txt = clean(popup.innerText);
                // 출/퇴근 확인 팝업이 아닌 설문/공지 팝업이거나 딤드 배경인 경우 제거
                if (!txt.includes('출근') && !txt.includes('퇴근')) {
                    popup.remove();
                    count++;
                }
            });

            // 3. body 스크롤 잠금 해제
            if (document.body) {
                document.body.style.overflow = 'auto';
                document.body.classList.remove('modal-open', 'scroll_lock');
            }
            return count;
        })();
        """;

    private static string GetAttendanceProbeScript() => """
        (() => {
            const url = location.href.toLowerCase();

            if (url.includes('/login') || document.querySelector('input[type="password"]')) {
                return JSON.stringify({ isLoginPage: true });
            }

            const clean = (s) => (s || '').replace(/\s+/g, ' ').trim();

            let clockIn = null;
            let clockOut = null;
            let duration = null;
            let badgeText = null;

            const allElements = Array.from(document.querySelectorAll('li, div, dl, tr, span, p'));
            for (const el of allElements) {
                const txt = clean(el.innerText);
                if (!txt || txt.length > 60) continue;

                if (!clockIn && /^(출근|출근시간|업무시작)\s*[:：]?\s*(\d{2}:\d{2}(:\d{2})?)/.test(txt)) {
                    const m = txt.match(/(\d{2}:\d{2}(:\d{2})?)/);
                    if (m) clockIn = m[1];
                }
                if (!clockOut && /^(퇴근|퇴근시간|업무종료)\s*[:：]?\s*(\d{2}:\d{2}(:\d{2})?)/.test(txt)) {
                    const m = txt.match(/(\d{2}:\d{2}(:\d{2})?)/);
                    if (m) clockOut = m[1];
                }
            }

            const workInEl = document.querySelector('#workInTime, .work_in_time, [data-role="clockInTime"], .clock-in-time');
            if (workInEl) {
                const m = clean(workInEl.innerText).match(/(\d{2}:\d{2}(:\d{2})?)/);
                if (m) clockIn = m[1];
            }
            const workOutEl = document.querySelector('#workOutTime, .work_out_time, [data-role="clockOutTime"], .clock-out-time');
            if (workOutEl) {
                const m = clean(workOutEl.innerText).match(/(\d{2}:\d{2}(:\d{2})?)/);
                if (m) clockOut = m[1];
            }

            const statusCandidates = Array.from(document.querySelectorAll('.state, .status, .badge, .chip, .label, .txt_state, [class*="status"], [class*="state"]'));
            for (const el of statusCandidates) {
                const t = clean(el.innerText);
                if (['근무중', '근무 중', '업무중', '업무 중', '정상근무', '연장근무'].includes(t)) {
                    badgeText = t;
                    break;
                }
                if (['퇴근', '업무종료', '업무 종료', '출근전', '출근 전', '미출근'].includes(t)) {
                    badgeText = t;
                }
            }

            const buttons = Array.from(document.querySelectorAll('button, a.btn, [role="button"], span.btn_major, span.btn_minor'));
            let clockInBtnDisabled = false;
            let clockOutBtnActive = false;

            for (const btn of buttons) {
                const t = clean(btn.innerText);
                const cls = (btn.className || '') + ' ' + (btn.parentElement?.className || '');
                const isDisabled = btn.disabled || cls.includes('disabled') || cls.includes('off') || btn.getAttribute('aria-disabled') === 'true';

                if (t === '출근하기' || t === '출근' || btn.id === 'workIn' || btn.id === 'clockIn') {
                    if (isDisabled) clockInBtnDisabled = true;
                }
                if (t === '퇴근하기' || t === '퇴근' || btn.id === 'workOut' || btn.id === 'clockOut') {
                    if (!isDisabled) clockOutBtnActive = true;
                }
            }

            let state = 'OFF_WORK';

            const hasValidClockIn = clockIn && !clockIn.startsWith('00:00') && !clockIn.includes('--');
            const hasValidClockOut = clockOut && !clockOut.startsWith('00:00') && !clockOut.includes('--');

            if (badgeText && (badgeText.includes('근무') || badgeText.includes('업무중') || badgeText.includes('업무 중'))) {
                state = 'WORKING';
            } else if (badgeText && (badgeText.includes('퇴근') || badgeText.includes('종료') || badgeText.includes('출근전') || badgeText.includes('미출근'))) {
                state = 'OFF_WORK';
            } else if (hasValidClockIn && !hasValidClockOut) {
                state = 'WORKING';
            } else if (hasValidClockIn && hasValidClockOut) {
                state = 'OFF_WORK';
            } else if (clockInBtnDisabled && clockOutBtnActive) {
                state = 'WORKING';
            }

            return JSON.stringify({
                isLoginPage: false,
                state,
                clockIn,
                clockOut,
                duration,
                badgeText
            });
        })();
        """;

    private static string GetClockInTriggerScript() => """
        (() => {
            const clean = (s) => (s || '').replace(/\s+/g, ' ').trim();
            const direct = document.querySelector('#workIn, #clockIn, [data-role="clockIn"], [data-action="clockIn"], .btn_work_in');
            if (direct) {
                direct.click();
                return 'OK:DIRECT';
            }
            const candidates = Array.from(document.querySelectorAll('button, a, span, div[role="button"]'));
            for (const el of candidates) {
                const t = clean(el.innerText);
                if (t === '출근하기' || t === '출근' || t === '업무시작') {
                    el.click();
                    return 'OK:' + t;
                }
            }
            return 'NOT_FOUND';
        })();
        """;

    private static string GetClockOutTriggerScript() => """
        (() => {
            const clean = (s) => (s || '').replace(/\s+/g, ' ').trim();
            const direct = document.querySelector('#workOut, #clockOut, [data-role="clockOut"], [data-action="clockOut"], .btn_work_out');
            if (direct) {
                direct.click();
                return 'OK:DIRECT';
            }
            const candidates = Array.from(document.querySelectorAll('button, a, span, div[role="button"]'));
            for (const el of candidates) {
                const t = clean(el.innerText);
                if (t === '퇴근하기' || t === '퇴근' || t === '업무종료') {
                    el.click();
                    return 'OK:' + t;
                }
            }
            return 'NOT_FOUND';
        })();
        """;

    private static string GetConfirmModalClickScript() => """
        (() => {
            const clean = (s) => (s || '').replace(/\s+/g, ' ').trim();
            const modalBtns = Array.from(document.querySelectorAll('.go_popup button, .go_popup a.btn_major_s, .modal button, .layer_normal a.btn_major_s, button.btn_major, a.btn_major'));
            for (const btn of modalBtns) {
                const t = clean(btn.innerText);
                if (t === '확인' || t === '예' || t === '출근' || t === '퇴근') {
                    btn.click();
                    return 'CONFIRMED';
                }
            }
            return 'NO_MODAL';
        })();
        """;
}
