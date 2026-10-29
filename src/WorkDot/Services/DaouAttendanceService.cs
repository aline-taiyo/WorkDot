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

        _hiddenWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        _hiddenWebView.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;

        // 다우오피스 확인 alert/confirm 대화상자 자동 승인 처리 (출퇴근 버튼 클릭 시)
        _hiddenWebView.CoreWebView2.ScriptDialogOpening += (_, args) =>
        {
            args.Accept();
        };

        _isInitialized = true;
        _hiddenWebView.Source = new Uri(TargetAttendanceUrl);
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

        string script = clockIn ? GetClockInTriggerScript() : GetClockOutTriggerScript();

        try
        {
            string rawResult = await _hiddenWebView.CoreWebView2.ExecuteScriptAsync(script);
            string result = JsonSerializer.Deserialize<string>(rawResult) ?? "UNKNOWN";

            if (result.StartsWith("OK:", StringComparison.Ordinal))
            {
                // 클릭 후 모달 확인 버튼이 뜰 수 있으므로 500ms 후 확인 버튼 자동 클릭 및 상태 재조회
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

        // SPA 렌더링 대기 후 DOM/API 상태 추출
        await Task.Delay(1200);
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
            // Ignore transient script failures during navigation
        }
    }

    private void UpdateSnapshot(AttendanceSnapshot next)
    {
        CurrentSnapshot = next;
        SnapshotChanged?.Invoke(next);
    }

    private static bool IsLoginUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        return url.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("sso", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 다우오피스 근태현황(EHR) 페이지의 DOM 및 표시 텍스트를 정밀 분석하여
    /// 출근 시각, 퇴근 시각, 현재 근무중/퇴근 여부를 JSON으로 반환하는 스크립트입니다.
    /// </summary>
    private static string GetAttendanceProbeScript() => """
        (() => {
            const url = location.href.toLowerCase();
            const bodyText = (document.body ? document.body.innerText : '') || '';

            if (url.includes('/login') || document.querySelector('input[type="password"]')) {
                return JSON.stringify({ isLoginPage: true });
            }

            const clean = (s) => (s || '').replace(/\s+/g, ' ').trim();

            // 1. 출근/퇴근 시간 추출 정규식 및 셀렉터
            let clockIn = null;
            let clockOut = null;
            let duration = null;
            let badgeText = null;

            // 다우오피스 타임라인 출퇴근 시간 영역 탐색
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

            // ID/Class 기반 보조 탐색 (DaouOffice EHR 표준 셀렉터)
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

            // 상태 배지 탐색 ("근무중", "업무중", "업무종료", "퇴근", "출근전")
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

            // 버튼 활성/비활성 상태 확인
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

            // 종합 판정:
            // 1) 배지가 명시적으로 '근무중'/'업무중'이거나
            // 2) 출근 시각(clockIn)이 찍혀 있고 퇴근 시각(clockOut)이 아직 없거나 ('--:--' / 미기록)
            // 3) 출근 버튼은 비활성화되어 있고 퇴근 버튼이 활성화되어 있으면 -> WORKING (근무중)
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
