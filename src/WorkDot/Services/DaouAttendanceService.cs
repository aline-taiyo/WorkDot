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

    public string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkDot");

    public string UserDataFolder =>
        Path.Combine(AppDataFolder, "WebView2Profile");

    public string LastProbeLogPath =>
        Path.Combine(AppDataFolder, "last-probe.json");

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

        await AttachWebViewHooksAsync(_hiddenWebView.CoreWebView2, isHiddenInstance: true);

        _isInitialized = true;
        _hiddenWebView.Source = new Uri(TargetAttendanceUrl);
    }

    /// <summary>
    /// 숨겨진 WebView2 및 포털 창 WebView2 양쪽에 공통으로
    /// 1) API 응답/DOM 변화 실시간 감지 훅
    /// 2) 설문조사/팝업 차단 가드
    /// 3) 네비게이션 완료 후 다단 프로브를 장착합니다.
    /// </summary>
    public async Task AttachWebViewHooksAsync(CoreWebView2 coreWebView, bool isHiddenInstance)
    {
        // 문서 생성 시점에 fetch/XHR 가로채기 및 DOM MutationObserver 등록
        await coreWebView.AddScriptToExecuteOnDocumentCreatedAsync(GetDocumentCreatedHookScript());

        coreWebView.WebMessageReceived += (_, e) =>
        {
            try
            {
                string? json = e.TryGetWebMessageAsString();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    ProcessProbeJson(json, fromPortalWindow: !isHiddenInstance);
                }
            }
            catch
            {
                // Ignore malformed messages
            }
        };

        coreWebView.NewWindowRequested += (_, e) =>
        {
            string targetUri = e.Uri ?? string.Empty;
            e.Handled = true;

            if (IsBlockingSurveyOrPopupUrl(targetUri))
                return;

            if (!string.IsNullOrWhiteSpace(targetUri) &&
                targetUri.Contains(BaseDomain, StringComparison.OrdinalIgnoreCase))
            {
                coreWebView.Navigate(targetUri);
            }
        };

        coreWebView.SourceChanged += async (_, _) =>
        {
            string current = coreWebView.Source ?? string.Empty;
            if (IsRedirectedAwayFromAttendanceAfterLogin(current))
            {
                await Task.Delay(250);
                coreWebView.Navigate(TargetAttendanceUrl);
            }
        };

        coreWebView.NavigationCompleted += async (_, _) =>
        {
            string url = coreWebView.Source ?? string.Empty;
            if (IsLoginUrl(url))
            {
                // 포털 창에서 이미 로그인된 상태가 아니라면 로그인 필요 표시
                if (CurrentSnapshot.Status == WorkStatusKind.Unknown ||
                    CurrentSnapshot.Status == WorkStatusKind.NeedsLogin ||
                    !isHiddenInstance)
                {
                    UpdateSnapshot(new AttendanceSnapshot(
                        WorkStatusKind.NeedsLogin,
                        "퇴근 · 로그인 필요",
                        "클릭하여 다우오피스 로그인",
                        null,
                        null,
                        null,
                        DateTime.Now));
                }
                return;
            }

            if (IsRedirectedAwayFromAttendanceAfterLogin(url))
            {
                coreWebView.Navigate(TargetAttendanceUrl);
                return;
            }

            // 포털 창에서 로그인에 성공해 근태 페이지에 진입했다면,
            // 백그라운드 HiddenWebView도 즉시 근태 페이지로 이동시켜 세션 동기화!
            if (!isHiddenInstance && _hiddenWebView?.CoreWebView2 != null)
            {
                string hiddenUrl = _hiddenWebView.CoreWebView2.Source ?? string.Empty;
                if (IsLoginUrl(hiddenUrl) || !hiddenUrl.Contains("/ehr/", StringComparison.OrdinalIgnoreCase))
                {
                    _hiddenWebView.CoreWebView2.Navigate(TargetAttendanceUrl);
                }
            }

            // SPA 비동기 렌더링 대응: 0.6초, 1.8초, 3.5초 3단계 프로브 실행
            int[] delays = [600, 1200, 1700];
            foreach (int delay in delays)
            {
                await Task.Delay(delay);
                await DismissBlockingPopupsAndSurveysAsync(coreWebView);
                await ProbeDomAndApiStatusAsync(coreWebView, fromPortalWindow: !isHiddenInstance);
            }
        };

        coreWebView.WebResourceResponseReceived += async (_, e) =>
        {
            try
            {
                string uri = e.Request.Uri;
                if (!uri.Contains("/api/ehr", StringComparison.OrdinalIgnoreCase) &&
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
                        "세션 만료됨 · 다시 로그인 필요",
                        null,
                        null,
                        null,
                        DateTime.Now));
                    return;
                }

                await Task.Delay(350);
                await ProbeDomAndApiStatusAsync(coreWebView, fromPortalWindow: !isHiddenInstance);
            }
            catch
            {
                // Ignore
            }
        };

        if (isHiddenInstance)
        {
            coreWebView.ScriptDialogOpening += (_, args) => args.Accept();
        }
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

        if (url.Contains("/ehr/", StringComparison.OrdinalIgnoreCase))
            return false;

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

        // 중요: HiddenWebView가 이전에 /login에 머물러 있더라도,
        // 포털 창에서 로그인했을 수 있으므로 무조건 TargetAttendanceUrl로 재접속을 시도합니다!
        if (IsLoginUrl(currentUrl) || !currentUrl.Contains("/ehr/", StringComparison.OrdinalIgnoreCase))
        {
            _hiddenWebView.CoreWebView2.Navigate(TargetAttendanceUrl);
            return;
        }

        await DismissBlockingPopupsAndSurveysAsync(_hiddenWebView.CoreWebView2);
        await ProbeDomAndApiStatusAsync(_hiddenWebView.CoreWebView2, fromPortalWindow: false);
    }

    public async Task<(bool Success, string Message)> TriggerClockActionAsync(bool clockIn)
    {
        if (_hiddenWebView?.CoreWebView2 == null)
            return (false, "WebView2가 아직 초기화되지 않았습니다.");

        string currentUrl = _hiddenWebView.CoreWebView2.Source ?? string.Empty;
        if (IsLoginUrl(currentUrl) || !currentUrl.Contains("/ehr/", StringComparison.OrdinalIgnoreCase))
        {
            _hiddenWebView.CoreWebView2.Navigate(TargetAttendanceUrl);
            await Task.Delay(1800);
            currentUrl = _hiddenWebView.CoreWebView2.Source ?? string.Empty;
            if (IsLoginUrl(currentUrl))
                return (false, "그룹웨어 로그인이 필요합니다. 로그인 창을 열어주세요.");
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
                await Task.Delay(1000);
                await ProbeDomAndApiStatusAsync(_hiddenWebView.CoreWebView2, fromPortalWindow: false);

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

    public async Task ProbeDomAndApiStatusAsync(CoreWebView2 webView, bool fromPortalWindow = false)
    {
        try
        {
            string rawJson = await webView.ExecuteScriptAsync(GetAttendanceProbeScript());
            string? innerJson = JsonSerializer.Deserialize<string>(rawJson);
            if (!string.IsNullOrWhiteSpace(innerJson))
            {
                ProcessProbeJson(innerJson, fromPortalWindow);
            }
        }
        catch
        {
            // Ignore transient script failures
        }
    }

    private void ProcessProbeJson(string innerJson, bool fromPortalWindow)
    {
        try
        {
            // 디버그 확인용 최근 프로브 결과 기록
            try
            {
                File.WriteAllText(LastProbeLogPath, innerJson);
            }
            catch
            {
                // Ignore file lock
            }

            using var doc = JsonDocument.Parse(innerJson);
            var root = doc.RootElement;

            bool isLogin = root.TryGetProperty("isLoginPage", out var loginProp) && loginProp.GetBoolean();
            if (isLogin)
            {
                if (fromPortalWindow || CurrentSnapshot.Status == WorkStatusKind.Unknown)
                {
                    UpdateSnapshot(new AttendanceSnapshot(
                        WorkStatusKind.NeedsLogin,
                        "퇴근 · 로그인 필요",
                        "클릭하여 다우오피스 로그인",
                        null,
                        null,
                        null,
                        DateTime.Now));
                }
                return;
            }

            string state = root.TryGetProperty("state", out var sProp) ? (sProp.GetString() ?? "UNKNOWN") : "UNKNOWN";
            if (state == "LOADING" || state == "UNKNOWN")
                return;

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
                    : (!string.IsNullOrWhiteSpace(clockIn)
                        ? $"출근 {clockIn} · 퇴근완료"
                        : (!string.IsNullOrWhiteSpace(badgeText) ? badgeText : "출근 전 / 업무 종료"));

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
            // Ignore parse errors
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
    /// 페이지 로드 시작 시 주입되어 fetch / XMLHttpRequest의 근태 API 응답을 캐시하고,
    /// DOM 변경 시 자동으로 상태를 측정해 C# 호스트로 전송하는 스크립트입니다.
    /// </summary>
    private static string GetDocumentCreatedHookScript() => """
        (() => {
            window.__workdotApiLogs = window.__workdotApiLogs || [];

            const recordApiData = (url, data) => {
                try {
                    if (!url) return;
                    const u = String(url).toLowerCase();
                    if (u.includes('ehr') || u.includes('attend') || u.includes('timeline') || u.includes('clock') || u.includes('status')) {
                        window.__workdotApiLogs.push({ url: String(url), data, ts: Date.now() });
                        if (window.__workdotApiLogs.length > 30) window.__workdotApiLogs.shift();
                        if (typeof window.__workdotScheduleProbe === 'function') {
                            window.__workdotScheduleProbe(200);
                        }
                    }
                } catch {}
            };

            // 1. fetch 가로채기
            const origFetch = window.fetch;
            if (origFetch) {
                window.fetch = async function(...args) {
                    const res = await origFetch.apply(this, args);
                    try {
                        const url = (args[0] && args[0].url) ? args[0].url : String(args[0] || '');
                        const clone = res.clone();
                        clone.json().then(data => recordApiData(url, data)).catch(() => {});
                    } catch {}
                    return res;
                };
            }

            // 2. XMLHttpRequest 가로채기
            const origOpen = XMLHttpRequest.prototype.open;
            const origSend = XMLHttpRequest.prototype.send;
            XMLHttpRequest.prototype.open = function(method, url, ...rest) {
                this.__workdotUrl = url;
                return origOpen.call(this, method, url, ...rest);
            };
            XMLHttpRequest.prototype.send = function(...args) {
                this.addEventListener('load', function() {
                    try {
                        if (this.responseText && this.responseText.trim().startsWith('{')) {
                            const data = JSON.parse(this.responseText);
                            recordApiData(this.__workdotUrl, data);
                        }
                    } catch {}
                });
                return origSend.apply(this, args);
            };

            // 3. DOM 변경 시 자동 프로브 스케줄러
            let probeTimer = null;
            window.__workdotScheduleProbe = (delayMs = 400) => {
                if (probeTimer) clearTimeout(probeTimer);
                probeTimer = setTimeout(() => {
                    try {
                        if (typeof window.__workdotRunProbe === 'function' && window.chrome?.webview?.postMessage) {
                            const resultJson = window.__workdotRunProbe();
                            if (resultJson) window.chrome.webview.postMessage(resultJson);
                        }
                    } catch {}
                }, delayMs);
            };

            window.addEventListener('DOMContentLoaded', () => {
                if (document.body) {
                    const obs = new MutationObserver(() => window.__workdotScheduleProbe(500));
                    obs.observe(document.body, { childList: true, subtree: true, characterData: true });
                }
            });
        })();
        """;

    private static string GetDismissPopupsScript() => """
        (() => {
            let count = 0;
            const clean = (s) => (s || '').replace(/\s+/g, ' ').trim();

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

            const popupContainers = document.querySelectorAll(
                '.go_popup, .layer_normal, .layer_notice, .layer_survey, #popOverlay, .overlay, .dimmed, [id*="popup"], [class*="popup_layer"]'
            );
            popupContainers.forEach(popup => {
                const txt = clean(popup.innerText);
                if (!txt.includes('출근') && !txt.includes('퇴근')) {
                    popup.remove();
                    count++;
                }
            });

            if (document.body) {
                document.body.style.overflow = 'auto';
                document.body.classList.remove('modal-open', 'scroll_lock');
            }
            return count;
        })();
        """;

    /// <summary>
    /// 다우오피스 내 근태 현황 페이지에서
    /// 1) 가로챈 REST API 응답(__workdotApiLogs)
    /// 2) 좌측/상단 오늘의 출퇴근 위젯 (출근시간 / 퇴근시간 라벨-값 쌍 및 버튼 상태)
    /// 3) 일별 테이블의 오늘(today) 행
    /// 을 종합 분석하여 정확한 근무 상태를 반환합니다.
    /// </summary>
    private static string GetAttendanceProbeScript() => """
        (() => {
            const runProbe = () => {
                const url = location.href;
                const urlLower = url.toLowerCase();

                if (urlLower.includes('/login') || document.querySelector('input[type="password"]')) {
                    return JSON.stringify({ isLoginPage: true, url });
                }

                const clean = (s) => (s || '').replace(/\s+/g, ' ').trim();
                const timeOnlyRe = /\b([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?\b/;
                const unsetRe = /(미등록|미체크|퇴근전|출근전|--:--|^-$|^-\s*:\s*-)/;

                // 모든 document 수집 (메인 + 동일 출처 iframe)
                const docs = [document];
                document.querySelectorAll('iframe').forEach(ifr => {
                    try { if (ifr.contentDocument) docs.push(ifr.contentDocument); } catch {}
                });

                let clockIn = null;
                let clockOut = null;
                let clockOutExplicitlyUnset = false;
                let duration = null;
                let badgeText = null;
                let apiStatus = null;

                // -----------------------------------------------------------------
                // 전략 1: 가로챈 다우오피스 API JSON(__workdotApiLogs)에서 오늘 데이터 추출
                // -----------------------------------------------------------------
                const now = new Date();
                const yyyy = now.getFullYear();
                const mm = String(now.getMonth() + 1).padStart(2, '0');
                const dd = String(now.getDate()).padStart(2, '0');
                const todayIso = `${yyyy}-${mm}-${dd}`;
                const todayDot = `${yyyy}.${mm}.${dd}`;
                const todayCompact = `${yyyy}${mm}${dd}`;

                if (Array.isArray(window.__workdotApiLogs)) {
                    for (let i = window.__workdotApiLogs.length - 1; i >= 0; i--) {
                        const entry = window.__workdotApiLogs[i];
                        if (!entry || !entry.data) continue;

                        const visit = (obj) => {
                            if (!obj || typeof obj !== 'object') return;
                            if (Array.isArray(obj)) {
                                obj.forEach(visit);
                                return;
                            }

                            // 날짜 필드가 있는 객체면 오늘 날짜인지 확인
                            const dVal = obj.date || obj.logDate || obj.workDate || obj.baseDate || obj.targetDate || obj.attndDate || obj.solarDate;
                            if (typeof dVal === 'string') {
                                const dClean = dVal.slice(0, 10);
                                if (/^\d{4}[-.]?\d{2}[-.]?\d{2}/.test(dClean) &&
                                    !dClean.includes(todayIso) && !dClean.includes(todayDot) && !dClean.includes(todayCompact)) {
                                    return; // 과거/미래 날짜 레코드는 스킵!
                                }
                            }

                            const inRaw = obj.clockInTime ?? obj.workInTime ?? obj.attndInTime ?? obj.inTime ?? obj.checkInTime ?? obj.realClockInTime;
                            const outRaw = obj.clockOutTime ?? obj.workOutTime ?? obj.attndOutTime ?? obj.outTime ?? obj.checkOutTime ?? obj.realClockOutTime;
                            const stRaw = obj.workStatus ?? obj.timelineStatus ?? obj.attndStatus ?? obj.workingStatus ?? obj.statusName ?? obj.stateName;

                            if (typeof inRaw === 'string' && timeOnlyRe.test(inRaw) && !clockIn) {
                                clockIn = inRaw.match(timeOnlyRe)[0];
                            }
                            if (typeof outRaw === 'string') {
                                if (timeOnlyRe.test(outRaw) && !outRaw.startsWith('00:00') && !clockOut) {
                                    clockOut = outRaw.match(timeOnlyRe)[0];
                                } else if (!outRaw || unsetRe.test(outRaw)) {
                                    clockOutExplicitlyUnset = true;
                                }
                            } else if (inRaw && (outRaw === null || outRaw === undefined || outRaw === '')) {
                                clockOutExplicitlyUnset = true;
                            }

                            if (typeof stRaw === 'string' && !apiStatus) {
                                apiStatus = stRaw;
                            }

                            for (const k of Object.keys(obj)) {
                                if (typeof obj[k] === 'object') visit(obj[k]);
                            }
                        };
                        try { visit(entry.data); } catch {}
                    }
                }

                // -----------------------------------------------------------------
                // 전략 2: DOM 탐색 (과거 날짜 테이블 행을 제외하고 오늘의 근태 영역만 탐색!)
                // -----------------------------------------------------------------
                const isInsidePastTableRow = (el) => {
                    const tr = el.closest('tr, .tb_row, .timeline_row, [class*="row"]');
                    if (!tr) return false;
                    // 만약 tr이 달력/일별 목록 테이블 내부라면 today 표시가 있는지 확인
                    const trText = clean(tr.innerText);
                    const hasDatePrefix = /\b\d{2}\s*\([월화수목금토일]\)|\b\d{4}[-.]\d{2}[-.]\d{2}\b/.test(trText);
                    if (!hasDatePrefix) return false;

                    const isTodayRow =
                        tr.classList.contains('today') ||
                        tr.querySelector('.today, .badge_today, [class*="today"]') !== null ||
                        trText.includes(todayIso) ||
                        trText.includes(todayDot) ||
                        trText.includes(`${mm}.${dd}`) ||
                        trText.includes(`${mm}-${dd}`) ||
                        new RegExp(`\\b${dd}\\s*\\([월화수목금토일]\\)`).test(trText);

                    return !isTodayRow;
                };

                for (const doc of docs) {
                    // 2-A. 명시적 ID / Class 셀렉터
                    const inSelectors = ['#workInTime', '#clockInTime', '.work_in_time', '.clock_in_time', '[data-role="clockInTime"]', '[data-id="clockInTime"]'];
                    for (const sel of inSelectors) {
                        const el = doc.querySelector(sel);
                        if (el) {
                            const t = clean(el.innerText);
                            const m = t.match(timeOnlyRe);
                            if (m && !clockIn) clockIn = m[0];
                        }
                    }

                    const outSelectors = ['#workOutTime', '#clockOutTime', '.work_out_time', '.clock_out_time', '[data-role="clockOutTime"]', '[data-id="clockOutTime"]'];
                    for (const sel of outSelectors) {
                        const el = doc.querySelector(sel);
                        if (el) {
                            const t = clean(el.innerText);
                            const m = t.match(timeOnlyRe);
                            if (m && !clockOut) clockOut = m[0];
                            else if (unsetRe.test(t)) clockOutExplicitlyUnset = true;
                        }
                    }

                    // 2-B. 라벨('출근시간'/'출근', '퇴근시간'/'퇴근')과 인접 형제(nextElementSibling) 또는 부모 쌍 탐색
                    const allNodes = Array.from(doc.querySelectorAll('dt, th, span, div, p, strong, label, a, button, li'));
                    for (const el of allNodes) {
                        if (isInsidePastTableRow(el)) continue;

                        const ownText = clean(el.innerText);
                        if (!ownText || ownText.length > 45) continue;

                        // (1) 출근 라벨 매칭
                        if (/^(출근시간|출근시각|출근|업무시작|출근하기)$/.test(ownText)) {
                            const sib = el.nextElementSibling;
                            if (sib) {
                                const sibText = clean(sib.innerText);
                                const m = sibText.match(timeOnlyRe);
                                if (m && !clockIn) clockIn = m[0];
                            } else if (el.parentElement) {
                                const pText = clean(el.parentElement.innerText);
                                if (pText.length < 60) {
                                    const m = pText.match(timeOnlyRe);
                                    if (m && !clockIn) clockIn = m[0];
                                }
                            }
                        } else if (!clockIn && /(출근시간|출근시각|출근|업무시작)\s*[:：]?\s*([01]\d|2[0-3]):[0-5]\d/.test(ownText)) {
                            const m = ownText.match(timeOnlyRe);
                            if (m) clockIn = m[0];
                        }

                        // (2) 퇴근 라벨 매칭
                        if (/^(퇴근시간|퇴근시각|퇴근|업무종료|퇴근하기)$/.test(ownText)) {
                            const sib = el.nextElementSibling;
                            if (sib) {
                                const sibText = clean(sib.innerText);
                                const m = sibText.match(timeOnlyRe);
                                if (m && !clockOut) clockOut = m[0];
                                else if (unsetRe.test(sibText)) clockOutExplicitlyUnset = true;
                            } else if (el.parentElement) {
                                const pText = clean(el.parentElement.innerText);
                                if (pText.length < 60) {
                                    const afterLabel = pText.replace(/^(퇴근시간|퇴근시각|퇴근|업무종료|퇴근하기)/, '');
                                    const m = afterLabel.match(timeOnlyRe);
                                    if (m && !clockOut) clockOut = m[0];
                                    else if (unsetRe.test(afterLabel)) clockOutExplicitlyUnset = true;
                                }
                            }
                        } else if (!clockOut && /(퇴근시간|퇴근시각|퇴근|업무종료)\s*[:：]?\s*([01]\d|2[0-3]):[0-5]\d/.test(ownText)) {
                            const m = ownText.match(timeOnlyRe);
                            if (m) clockOut = m[0];
                        } else if (/(퇴근시간|퇴근시각|퇴근)\s*[:：]?\s*(미등록|--|-)/.test(ownText)) {
                            clockOutExplicitlyUnset = true;
                        }
                    }

                    // 2-C. 오늘(today) 테이블 행에서 출근/퇴근 시간 보조 추출
                    const todayRows = Array.from(doc.querySelectorAll('tr.today, .today_row, .tb_attend .today'));
                    for (const row of todayRows) {
                        const rowToCheck = row.closest('tr') || row;
                        const times = (clean(rowToCheck.innerText).match(/\b([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?\b/g)) || [];
                        if (times.length === 1 && !clockIn) {
                            clockIn = times[0];
                            clockOutExplicitlyUnset = true;
                        } else if (times.length >= 2) {
                            if (!clockIn) clockIn = times[0];
                            if (!clockOut && !clockOutExplicitlyUnset) clockOut = times[1];
                        }
                    }

                    // 2-D. 상태 배지 / 드롭다운 탐색 ("근무중", "업무중", "업무종료", "출근전" 등)
                    const statusEls = Array.from(doc.querySelectorAll(
                        'select option:checked, .state, .status, .badge, .chip, .label, .txt_state, .btn_state, [class*="status"], [class*="state"]'
                    ));
                    for (const el of statusEls) {
                        if (isInsidePastTableRow(el)) continue;
                        const t = clean(el.innerText);
                        if (!t || t.length > 15) continue;

                        if (['근무중', '근무 중', '업무중', '업무 중', '정상근무', '연장근무', '외근', '출장'].includes(t)) {
                            badgeText = t;
                            break;
                        }
                        if (['퇴근', '업무종료', '업무 종료', '출근전', '출근 전', '미출근', '근무종료'].includes(t)) {
                            badgeText = t;
                        }
                    }
                }

                // -----------------------------------------------------------------
                // 3. 버튼 활성/비활성 상태 확인
                // -----------------------------------------------------------------
                let clockInBtnFound = false;
                let clockInBtnDisabled = false;
                let clockOutBtnFound = false;
                let clockOutBtnDisabled = false;

                for (const doc of docs) {
                    const buttons = Array.from(doc.querySelectorAll('button, a, span[role="button"], div[role="button"], #workIn, #workOut, #clockIn, #clockOut'));
                    for (const btn of buttons) {
                        const t = clean(btn.innerText);
                        const id = (btn.id || '').toLowerCase();
                        const cls = ((btn.className || '') + ' ' + (btn.parentElement?.className || '')).toLowerCase();
                        const isDisabled =
                            btn.disabled ||
                            cls.includes('disabled') ||
                            cls.includes('disable') ||
                            cls.includes('off') ||
                            cls.includes('done') ||
                            btn.getAttribute('aria-disabled') === 'true';

                        if (id === 'workin' || id === 'clockin' || t.startsWith('출근하기') || t === '출근') {
                            clockInBtnFound = true;
                            if (isDisabled) clockInBtnDisabled = true;
                            const m = t.match(timeOnlyRe);
                            if (m && !clockIn) clockIn = m[0];
                        }
                        if (id === 'workout' || id === 'clockout' || t.startsWith('퇴근하기') || t === '퇴근') {
                            clockOutBtnFound = true;
                            if (isDisabled) clockOutBtnDisabled = true;
                            const m = t.match(timeOnlyRe);
                            if (m && !clockOut) clockOut = m[0];
                        }
                    }
                }

                if (clockOutExplicitlyUnset) {
                    clockOut = null;
                }

                const hasValidClockIn = Boolean(clockIn && !clockIn.startsWith('00:00'));
                const hasValidClockOut = Boolean(clockOut && !clockOut.startsWith('00:00'));

                // -----------------------------------------------------------------
                // 4. 최종 근무중(WORKING) / 퇴근(OFF_WORK) / 로딩중(LOADING) 판정
                // -----------------------------------------------------------------
                let state = 'UNKNOWN';

                const combinedStatus = ((apiStatus || '') + ' ' + (badgeText || '')).toUpperCase();

                if (combinedStatus.includes('WORKING') || combinedStatus.includes('WORK_IN') || combinedStatus.includes('ON_WORK') ||
                    combinedStatus.includes('근무중') || combinedStatus.includes('근무 중') ||
                    combinedStatus.includes('업무중') || combinedStatus.includes('업무 중')) {
                    state = 'WORKING';
                } else if (combinedStatus.includes('WORK_OUT') || combinedStatus.includes('OFF_WORK') ||
                           combinedStatus.includes('업무종료') || combinedStatus.includes('업무 종료') ||
                           combinedStatus.includes('근무종료') || combinedStatus.includes('출근전') || combinedStatus.includes('출근 전')) {
                    state = 'OFF_WORK';
                } else if (hasValidClockIn && !hasValidClockOut) {
                    state = 'WORKING';
                } else if (hasValidClockIn && hasValidClockOut) {
                    state = 'OFF_WORK';
                } else if (clockInBtnFound && clockInBtnDisabled && clockOutBtnFound && !clockOutBtnDisabled) {
                    state = 'WORKING';
                } else if (clockInBtnFound || clockOutBtnFound || badgeText) {
                    state = 'OFF_WORK';
                } else {
                    // 아직 SPA DOM이 렌더링되기 전이면 LOADING 반환하여 잘못된 상태 덮어쓰기 방지
                    state = 'LOADING';
                }

                const sidebarSample = clean((document.querySelector('.go_side, aside, .ehr_stat, .attend_contents, main, #content') || document.body)?.innerText || '').slice(0, 600);

                return JSON.stringify({
                    isLoginPage: false,
                    url,
                    state,
                    clockIn,
                    clockOut,
                    clockOutExplicitlyUnset,
                    duration,
                    badgeText,
                    apiStatus,
                    clockInBtnFound,
                    clockInBtnDisabled,
                    clockOutBtnFound,
                    clockOutBtnDisabled,
                    sidebarSample
                });
            };

            window.__workdotRunProbe = runProbe;
            return runProbe();
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
