using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WorkDot.Services;

public enum WorkStatusKind
{
    Unknown,
    NeedsLogin,
    OffWork,   // 퇴근 / 미출근
    Working,   // 근무중 (초록/만충 시 빨강 게이지)
    Break      // 휴게 / 일시정지 (더블클릭으로 전환, 앰버 게이지)
}

public sealed record BreakTimeWindow(TimeSpan Start, TimeSpan End);

public sealed record AttendanceSnapshot(
    WorkStatusKind Status,
    string StatusLabel,
    string SubText,
    string? ClockInTimeText,
    string? ClockOutTimeText,
    string? WorkedDurationText,
    DateTime UpdatedAt,
    IReadOnlyList<BreakTimeWindow>? BreakWindows = null,
    DateTime? ActiveBreakStart = null)
{
    private static readonly IReadOnlyList<BreakTimeWindow> DefaultBreakWindows =
    [
        new BreakTimeWindow(new TimeSpan(12, 0, 0), new TimeSpan(13, 0, 0))
    ];

    /// <summary>
    /// 다우오피스에서 수집된 출근 시각 및 휴게시간(fixedBreakTimeList / 기본 12:00~13:00 / 사용자 수동 휴게)을 바탕으로
    /// 휴게시간을 제외한 실근무 경과 시간(TimeSpan)을 계산합니다.
    /// </summary>
    public TimeSpan? GetWorkingElapsedSpanExcludingBreaks(DateTime now)
    {
        DateTime effectiveNow = (ActiveBreakStart.HasValue && now > ActiveBreakStart.Value)
            ? ActiveBreakStart.Value
            : now;

        if (!string.IsNullOrWhiteSpace(ClockInTimeText) &&
            TimeSpan.TryParse(ClockInTimeText.Trim(), System.Globalization.CultureInfo.InvariantCulture, out TimeSpan clockInTod))
        {
            DateTime start = effectiveNow.Date.Add(clockInTod);
            if (start > effectiveNow.AddMinutes(1))
            {
                start = start.AddDays(-1);
            }

            TimeSpan totalSpan = effectiveNow - start;
            if (totalSpan < TimeSpan.Zero)
            {
                totalSpan = TimeSpan.Zero;
            }

            var breaks = (BreakWindows != null && BreakWindows.Count > 0) ? BreakWindows : DefaultBreakWindows;
            foreach (var bw in breaks)
            {
                DateTime bStart = start.Date.Add(bw.Start);
                DateTime bEnd = start.Date.Add(bw.End);
                if (bEnd <= bStart)
                {
                    bEnd = bEnd.AddDays(1);
                }

                DateTime overlapStart = start > bStart ? start : bStart;
                DateTime overlapEnd = effectiveNow < bEnd ? effectiveNow : bEnd;
                if (overlapEnd > overlapStart)
                {
                    totalSpan -= (overlapEnd - overlapStart);
                }
            }

            if (totalSpan < TimeSpan.Zero)
            {
                totalSpan = TimeSpan.Zero;
            }

            return totalSpan;
        }

        if (!string.IsNullOrWhiteSpace(WorkedDurationText) &&
            TimeSpan.TryParse(WorkedDurationText.Trim(), System.Globalization.CultureInfo.InvariantCulture, out TimeSpan parsedDur))
        {
            return parsedDur;
        }

        return null;
    }

    /// <summary>
    /// 다우오피스에서 수집된 출근 시각 및 휴게시간(fixedBreakTimeList / 기본 12:00~13:00)을 바탕으로
    /// 휴게시간을 제외한 실근무 경과 시간(HH:mm)을 계산합니다.
    /// </summary>
    public string? GetWorkingElapsedExcludingBreaks(DateTime now)
    {
        TimeSpan? span = GetWorkingElapsedSpanExcludingBreaks(now);
        if (span.HasValue)
        {
            int hours = (int)span.Value.TotalHours;
            int minutes = span.Value.Minutes;
            return $"{hours:D2}:{minutes:D2}";
        }

        if (!string.IsNullOrWhiteSpace(WorkedDurationText))
        {
            return WorkedDurationText;
        }

        return null;
    }
}

internal sealed record SavedCookieItem(
    string Name,
    string Value,
    string Domain,
    string Path,
    bool IsHttpOnly,
    bool IsSecure,
    CoreWebView2CookieSameSiteKind SameSite);

internal sealed record SavedCredentials(
    string Username,
    string Password);

public sealed class DaouAttendanceService
{
    public const string TargetAttendanceUrl = "https://alinestudio.daouoffice.com/ehr/app/attend/my-attendance-status";
    public const string BaseUrl = "https://alinestudio.daouoffice.com";
    public const string BaseDomain = "alinestudio.daouoffice.com";

    private CoreWebView2Environment? _env;
    private WebView2? _webView;
    private bool _isInitialized;
    private DateTime _lastAutoLoginAttempt = DateTime.MinValue;
    private WorkStatusKind? _manualStatusOverride;
    private DateTime _manualOverrideDate;
    private DateTime? _activeBreakStartTime;
    private DateTime _userBreaksDate = DateTime.Today;
    private readonly List<BreakTimeWindow> _userBreakWindowsToday = new();

    // 로그인 직후 다우오피스가 홈(/app/home 등)으로 보내는 경우에만 근태 페이지로 1회 되돌리기 위한 플래그
    private bool _pendingAttendanceRedirect = true;

    /// <summary>
    /// 사용자가 브라우저 창을 열어 직접 탐색 중인지 여부.
    /// true 인 동안에는 자동으로 근태 페이지로 되돌리지 않는다 (로그인 직후 1회 제외).
    /// </summary>
    public bool IsInteractiveBrowsing { get; set; }

    public AttendanceSnapshot CurrentSnapshot { get; private set; } = new(
        WorkStatusKind.OffWork,
        "퇴근 (연동 대기)",
        "그룹웨어 동기화 중...",
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

    private string SavedCookiesFilePath =>
        Path.Combine(AppDataFolder, "session-cookies.dat");

    private string SavedCredentialsFilePath =>
        Path.Combine(AppDataFolder, "auth-credentials.dat");

    public async Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync()
    {
        if (_env != null)
            return _env;

        Directory.CreateDirectory(UserDataFolder);
        _env = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
        return _env;
    }

    public async Task InitializeWebViewAsync(WebView2 webView)
    {
        if (_isInitialized)
            return;

        _webView = webView;
        var env = await GetOrCreateEnvironmentAsync();
        await _webView.EnsureCoreWebView2Async(env);

        // 1. 앱 시작 시 저장된 다우오피스 세션 쿠키(GOSSOcookie 등)를 먼저 복원하여 재로그인 방지
        await RestoreSessionCookiesAsync(_webView.CoreWebView2);

        // 2. 실시간 API/DOM 훅 및 자동 로그인 캡처 장착
        await AttachWebViewHooksAsync(_webView.CoreWebView2);

        _isInitialized = true;
        _webView.Source = new Uri(TargetAttendanceUrl);
    }

    private async Task AttachWebViewHooksAsync(CoreWebView2 coreWebView)
    {
        await coreWebView.AddScriptToExecuteOnDocumentCreatedAsync(GetDocumentCreatedHookScript());

        coreWebView.WebMessageReceived += (_, e) =>
        {
            try
            {
                string? json = e.TryGetWebMessageAsString();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    ProcessWebMessageJson(json);
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

            // 백그라운드 동기화 중에만 설문/공지 팝업을 차단한다.
            // 사용자가 브라우저 창에서 직접 탐색 중이면 결재 문서·공지 등 새 창 링크도 같은 창에서 연다.
            if (!IsInteractiveBrowsing && IsBlockingSurveyOrPopupUrl(targetUri))
                return;

            if (!string.IsNullOrWhiteSpace(targetUri) &&
                targetUri.Contains(BaseDomain, StringComparison.OrdinalIgnoreCase))
            {
                coreWebView.Navigate(targetUri);
            }
        };

        coreWebView.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
        };

        coreWebView.SourceChanged += async (_, _) =>
        {
            string current = coreWebView.Source ?? string.Empty;
            if (IsLoginUrl(current))
            {
                _pendingAttendanceRedirect = true;
                return;
            }

            if (ShouldReturnToAttendance(current))
            {
                _pendingAttendanceRedirect = false;
                await Task.Delay(250);
                coreWebView.Navigate(TargetAttendanceUrl);
            }
        };

        coreWebView.NavigationCompleted += async (_, _) =>
        {
            string url = coreWebView.Source ?? string.Empty;
            if (IsLoginUrl(url))
            {
                // 저장된 자격증명이 있으면 백그라운드 자동 로그인 시도
                bool autoLoggedIn = await TryAutoLoginIfPossibleAsync(coreWebView);
                if (!autoLoggedIn)
                {
                    UpdateSnapshot(new AttendanceSnapshot(
                        WorkStatusKind.NeedsLogin,
                        "퇴근 · 로그인 필요",
                        "클릭하여 다우오피스 로그인 (1회 로그인 시 자동 유지)",
                        null,
                        null,
                        null,
                        DateTime.Now));
                }
                return;
            }

            // 로그인 성공 상태이므로 세션 쿠키(GOSSOcookie 등)를 영구 쿠키로 승격 및 백업 저장!
            await PersistSessionCookiesAsync(coreWebView);

            if (ShouldReturnToAttendance(url))
            {
                _pendingAttendanceRedirect = false;
                coreWebView.Navigate(TargetAttendanceUrl);
                return;
            }

            // 로그인 후 첫 페이지 로드가 끝났으므로 이후의 이동은 사용자 탐색으로 본다
            _pendingAttendanceRedirect = false;

            int[] delays = [500, 1000, 1500];
            foreach (int delay in delays)
            {
                await Task.Delay(delay);
                await ProbeDomAndApiStatusAsync(coreWebView);
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
                        "세션 만료됨 · 재로그인 중",
                        null,
                        null,
                        null,
                        DateTime.Now));
                    return;
                }

                await Task.Delay(350);
                await ProbeDomAndApiStatusAsync(coreWebView);
            }
            catch
            {
                // Ignore
            }
        };
    }

    /// <summary>
    /// 다우오피스 세션 쿠키(GOSSOcookie 등)가 앱 종료 시 삭제되지 않도록
    /// 만료일을 30일 뒤로 연장하고 DPAPI 암호화 파일로 백업합니다.
    /// </summary>
    public async Task PersistSessionCookiesAsync(CoreWebView2 coreWebView)
    {
        try
        {
            var cookies = await coreWebView.CookieManager.GetCookiesAsync(BaseUrl);
            if (cookies == null || cookies.Count == 0)
                return;

            var savedList = new List<SavedCookieItem>();
            DateTime expiresAt = DateTime.UtcNow.AddDays(30);

            foreach (var c in cookies)
            {
                if (string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Value))
                    continue;

                savedList.Add(new SavedCookieItem(
                    c.Name,
                    c.Value,
                    c.Domain,
                    c.Path,
                    c.IsHttpOnly,
                    c.IsSecure,
                    c.SameSite));

                // 세션 전용 쿠키(앱 종료 시 증발하는 쿠키)를 30일 영구 쿠키로 승격
                if (c.IsSession)
                {
                    var persistentCookie = coreWebView.CookieManager.CreateCookie(c.Name, c.Value, c.Domain, c.Path);
                    persistentCookie.IsHttpOnly = c.IsHttpOnly;
                    persistentCookie.IsSecure = c.IsSecure;
                    persistentCookie.SameSite = c.SameSite;
                    persistentCookie.Expires = expiresAt;
                    coreWebView.CookieManager.AddOrUpdateCookie(persistentCookie);
                }
            }

            if (savedList.Count > 0)
            {
                string json = JsonSerializer.Serialize(savedList);
                byte[] encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(json),
                    null,
                    DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(AppDataFolder);
                await File.WriteAllBytesAsync(SavedCookiesFilePath, encrypted);
            }
        }
        catch
        {
            // Ignore cookie persistence errors
        }
    }

    private async Task RestoreSessionCookiesAsync(CoreWebView2 coreWebView)
    {
        try
        {
            if (!File.Exists(SavedCookiesFilePath))
                return;

            byte[] encrypted = await File.ReadAllBytesAsync(SavedCookiesFilePath);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(decrypted);

            var savedList = JsonSerializer.Deserialize<List<SavedCookieItem>>(json);
            if (savedList == null || savedList.Count == 0)
                return;

            DateTime expiresAt = DateTime.UtcNow.AddDays(30);
            foreach (var item in savedList)
            {
                var cookie = coreWebView.CookieManager.CreateCookie(item.Name, item.Value, item.Domain, item.Path);
                cookie.IsHttpOnly = item.IsHttpOnly;
                cookie.IsSecure = item.IsSecure;
                cookie.SameSite = item.SameSite;
                cookie.Expires = expiresAt;
                coreWebView.CookieManager.AddOrUpdateCookie(cookie);
            }
        }
        catch
        {
            // Ignore corrupted cookie cache
        }
    }

    private void SaveCredentials(string username, string password)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return;

            var creds = new SavedCredentials(username.Trim(), password);
            string json = JsonSerializer.Serialize(creds);
            byte[] encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json),
                null,
                DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(AppDataFolder);
            File.WriteAllBytes(SavedCredentialsFilePath, encrypted);
        }
        catch
        {
            // Ignore
        }
    }

    private SavedCredentials? LoadCredentials()
    {
        try
        {
            if (!File.Exists(SavedCredentialsFilePath))
                return null;

            byte[] encrypted = File.ReadAllBytes(SavedCredentialsFilePath);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            string json = Encoding.UTF8.GetString(decrypted);
            return JsonSerializer.Deserialize<SavedCredentials>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 서버 세션이 만료되어 /login 화면으로 이동했을 때,
    /// 이전에 로그인하며 저장된 계정 정보가 있으면 자동으로 입력·로그인합니다.
    /// </summary>
    private async Task<bool> TryAutoLoginIfPossibleAsync(CoreWebView2 coreWebView)
    {
        if ((DateTime.Now - _lastAutoLoginAttempt).TotalSeconds < 25)
            return false;

        var creds = LoadCredentials();
        if (creds == null || string.IsNullOrWhiteSpace(creds.Username) || string.IsNullOrWhiteSpace(creds.Password))
            return false;

        _lastAutoLoginAttempt = DateTime.Now;

        UpdateSnapshot(new AttendanceSnapshot(
            CurrentSnapshot.Status,
            "자동 로그인 중...",
            "저장된 세션으로 다우오피스 연결 중",
            CurrentSnapshot.ClockInTimeText,
            CurrentSnapshot.ClockOutTimeText,
            CurrentSnapshot.WorkedDurationText,
            DateTime.Now));

        await Task.Delay(600);

        string userJson = JsonSerializer.Serialize(creds.Username);
        string passJson = JsonSerializer.Serialize(creds.Password);

        string autoLoginScript = $$"""
            (() => {
                const username = {{userJson}};
                const password = {{passJson}};

                const passInput = document.querySelector('input[type="password"], #password, input[name="password"]');
                if (!passInput) return 'NO_FORM';

                const userInput = document.querySelector('#username, #userId, #loginId, input[name="username"], input[name="userId"], input[type="text"], input[type="email"]');
                if (!userInput) return 'NO_USER_INPUT';

                const setNativeValue = (el, val) => {
                    const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;
                    if (setter) setter.call(el, val);
                    else el.value = val;
                    el.dispatchEvent(new Event('input', { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                };

                setNativeValue(userInput, username);
                setNativeValue(passInput, password);

                const loginBtn = document.querySelector('#login_submit, #btnLogin, .btn_login, button[type="submit"], input[type="submit"], a.btn_login');
                if (loginBtn) {
                    loginBtn.click();
                    return 'SUBMITTED';
                }

                // 텍스트가 '로그인'인 버튼/링크 탐색
                const candidates = Array.from(document.querySelectorAll('button, a, span, div[role="button"]'));
                for (const c of candidates) {
                    if ((c.innerText || '').trim() === '로그인') {
                        c.click();
                        return 'SUBMITTED_TEXT';
                    }
                }

                const form = passInput.closest('form');
                if (form) {
                    form.submit();
                    return 'FORM_SUBMITTED';
                }
                return 'NO_BTN';
            })();
            """;

        try
        {
            string raw = await coreWebView.ExecuteScriptAsync(autoLoginScript);
            string res = JsonSerializer.Deserialize<string>(raw) ?? "";
            return res.StartsWith("SUBMITTED", StringComparison.Ordinal) || res == "FORM_SUBMITTED";
        }
        catch
        {
            return false;
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

    /// <summary>
    /// 근태 페이지로 자동 복귀할지 판단한다.
    /// 백그라운드 동기화 중이거나 로그인 직후 홈으로 보내진 경우에만 복귀하고,
    /// 사용자가 브라우저 창에서 다른 메뉴로 이동한 경우에는 그대로 둔다.
    /// </summary>
    private bool ShouldReturnToAttendance(string url)
    {
        if (!IsRedirectedAwayFromAttendanceAfterLogin(url))
            return false;

        return _pendingAttendanceRedirect || !IsInteractiveBrowsing;
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
        if (_webView?.CoreWebView2 == null)
            return;

        string currentUrl = _webView.CoreWebView2.Source ?? string.Empty;
        bool isOnEhrPage = !IsLoginUrl(currentUrl) &&
                           currentUrl.Contains("/ehr/", StringComparison.OrdinalIgnoreCase);
        if (!isOnEhrPage)
        {
            // 사용자가 브라우저 창에서 다른 메뉴를 보거나 로그인 입력 중이면 주기 동기화가 페이지를 빼앗지 않는다.
            // 창을 닫으면(숨기면) 다음 동기화에서 근태 페이지로 돌아간다.
            if (IsInteractiveBrowsing)
                return;

            _webView.CoreWebView2.Navigate(TargetAttendanceUrl);
            return;
        }

        await PersistSessionCookiesAsync(_webView.CoreWebView2);
        await ProbeDomAndApiStatusAsync(_webView.CoreWebView2);
    }

    public async Task<(bool Success, string Message)> TriggerClockActionAsync(bool clockIn)
    {
        if (_webView?.CoreWebView2 == null)
            return (false, "WebView2가 아직 초기화되지 않았습니다.");

        string currentUrl = _webView.CoreWebView2.Source ?? string.Empty;
        if (IsLoginUrl(currentUrl) || !currentUrl.Contains("/ehr/", StringComparison.OrdinalIgnoreCase))
        {
            _webView.CoreWebView2.Navigate(TargetAttendanceUrl);
            await Task.Delay(1800);
            currentUrl = _webView.CoreWebView2.Source ?? string.Empty;
            if (IsLoginUrl(currentUrl))
                return (false, "그룹웨어 로그인이 필요합니다. 로그인 창을 열어주세요.");
        }

        string script = clockIn ? GetClockInTriggerScript() : GetClockOutTriggerScript();

        try
        {
            string rawResult = await _webView.CoreWebView2.ExecuteScriptAsync(script);
            string result = JsonSerializer.Deserialize<string>(rawResult) ?? "UNKNOWN";

            if (result.StartsWith("OK:", StringComparison.Ordinal))
            {
                await Task.Delay(600);
                string modalRaw = await _webView.CoreWebView2.ExecuteScriptAsync(GetConfirmModalClickScript());
                string modalRes = JsonSerializer.Deserialize<string>(modalRaw) ?? "NO_MODAL";
                if (modalRes.StartsWith("NEEDS_USER_INPUT", StringComparison.Ordinal))
                {
                    return (false, "다우오피스 추가 확인(근무지 선택 등)이 필요합니다.");
                }

                await Task.Delay(1000);
                await ProbeDomAndApiStatusAsync(_webView.CoreWebView2);

                return (true, clockIn ? "출근 처리가 완료되었습니다." : "퇴근 처리가 완료되었습니다.");
            }

            return (false, $"다우오피스 페이지에서 {(clockIn ? "출근" : "퇴근")} 버튼을 찾지 못했습니다 ({result}). 그룹웨어 창에서 직접 확인해주세요.");
        }
        catch (Exception ex)
        {
            return (false, $"처리 중 오류: {ex.Message}");
        }
    }

    public void ClearManualStatusOverride()
    {
        _manualStatusOverride = null;
        _activeBreakStartTime = null;
    }

    public void ToggleBreakStatus()
    {
        if (CurrentSnapshot.Status == WorkStatusKind.Working)
        {
            SetManualStatusOverride(WorkStatusKind.Break);
        }
        else if (CurrentSnapshot.Status == WorkStatusKind.Break)
        {
            SetManualStatusOverride(WorkStatusKind.Working);
        }
    }

    public void SetManualStatusOverride(WorkStatusKind status)
    {
        if (_userBreaksDate != DateTime.Today)
        {
            _userBreaksDate = DateTime.Today;
            _userBreakWindowsToday.Clear();
        }

        // 휴게 상태에서 근무중/퇴근으로 돌아올 때 방금까지의 수동 휴게 구간을 휴게 목록에 누적
        if (_activeBreakStartTime.HasValue && status != WorkStatusKind.Break)
        {
            DateTime now = DateTime.Now;
            if (now > _activeBreakStartTime.Value)
            {
                _userBreakWindowsToday.Add(new BreakTimeWindow(_activeBreakStartTime.Value.TimeOfDay, now.TimeOfDay));
            }
            _activeBreakStartTime = null;
        }

        _manualStatusOverride = status;
        _manualOverrideDate = DateTime.Today;

        var mergedBreaks = MergeBreakWindows(CurrentSnapshot.BreakWindows, _userBreakWindowsToday);
        string nowTime = DateTime.Now.ToString("HH:mm");

        if (status == WorkStatusKind.Break)
        {
            _activeBreakStartTime ??= DateTime.Now;
            UpdateSnapshot(new AttendanceSnapshot(
                WorkStatusKind.Break,
                "휴게",
                $"휴게중 ({_activeBreakStartTime.Value:HH:mm}~)",
                CurrentSnapshot.ClockInTimeText ?? nowTime,
                null,
                CurrentSnapshot.WorkedDurationText,
                DateTime.Now,
                mergedBreaks,
                _activeBreakStartTime));
        }
        else if (status == WorkStatusKind.Working)
        {
            UpdateSnapshot(new AttendanceSnapshot(
                WorkStatusKind.Working,
                "근무중",
                $"출근 {CurrentSnapshot.ClockInTimeText ?? nowTime}",
                CurrentSnapshot.ClockInTimeText ?? nowTime,
                null,
                CurrentSnapshot.WorkedDurationText,
                DateTime.Now,
                mergedBreaks,
                null));
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
                DateTime.Now,
                mergedBreaks,
                null));
        }
    }

    private static IReadOnlyList<BreakTimeWindow> MergeBreakWindows(
        IReadOnlyList<BreakTimeWindow>? baseBreaks,
        IReadOnlyList<BreakTimeWindow> userBreaks)
    {
        var result = new List<BreakTimeWindow>();
        if (baseBreaks != null && baseBreaks.Count > 0)
        {
            result.AddRange(baseBreaks);
        }
        else
        {
            result.Add(new BreakTimeWindow(new TimeSpan(12, 0, 0), new TimeSpan(13, 0, 0)));
        }

        foreach (var ub in userBreaks)
        {
            if (!result.Contains(ub))
            {
                result.Add(ub);
            }
        }
        return result;
    }

    public async Task ProbeDomAndApiStatusAsync(CoreWebView2 webView)
    {
        try
        {
            string rawJson = await webView.ExecuteScriptAsync(GetAttendanceProbeScript());
            string? innerJson = JsonSerializer.Deserialize<string>(rawJson);
            if (!string.IsNullOrWhiteSpace(innerJson))
            {
                ProcessWebMessageJson(innerJson);
            }
        }
        catch
        {
            // Ignore transient script failures
        }
    }

    private void ProcessWebMessageJson(string innerJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(innerJson);
            var root = doc.RootElement;

            // 로그인 폼 제출 시 자격증명(ID/PW) 캡처 메시지인 경우 DPAPI로 암호화 저장
            if (root.TryGetProperty("msgType", out var msgTypeProp) &&
                msgTypeProp.GetString() == "LOGIN_CREDENTIALS")
            {
                string? user = root.TryGetProperty("username", out var uProp) ? uProp.GetString() : null;
                string? pass = root.TryGetProperty("password", out var pProp) ? pProp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
                {
                    SaveCredentials(user, pass);
                }
                return;
            }

            try
            {
                File.WriteAllText(LastProbeLogPath, innerJson);
            }
            catch
            {
                // Ignore file lock
            }

            bool isLogin = root.TryGetProperty("isLoginPage", out var loginProp) && loginProp.GetBoolean();
            if (isLogin)
            {
                UpdateSnapshot(new AttendanceSnapshot(
                    WorkStatusKind.NeedsLogin,
                    "퇴근 · 로그인 필요",
                    "클릭하여 다우오피스 로그인 (1회 로그인 시 자동 유지)",
                    null,
                    null,
                    null,
                    DateTime.Now));
                return;
            }

            if (_webView?.CoreWebView2 != null)
            {
                _ = PersistSessionCookiesAsync(_webView.CoreWebView2);
            }

            string state = root.TryGetProperty("state", out var sProp) ? (sProp.GetString() ?? "UNKNOWN") : "UNKNOWN";
            if (state == "LOADING" || state == "UNKNOWN")
                return;

            string? clockIn = root.TryGetProperty("clockIn", out var inProp) ? inProp.GetString() : null;
            string? clockOut = root.TryGetProperty("clockOut", out var outProp) ? outProp.GetString() : null;
            string? duration = root.TryGetProperty("duration", out var durProp) ? durProp.GetString() : null;
            string? badgeText = root.TryGetProperty("badgeText", out var bProp) ? bProp.GetString() : null;

            List<BreakTimeWindow>? breakWindows = null;
            if (root.TryGetProperty("breakRanges", out var brProp) && brProp.ValueKind == JsonValueKind.Array)
            {
                breakWindows = new List<BreakTimeWindow>();
                foreach (var item in brProp.EnumerateArray())
                {
                    string? rangeStr = item.GetString();
                    if (!string.IsNullOrWhiteSpace(rangeStr))
                    {
                        var parts = rangeStr.Split('~', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (parts.Length == 2 &&
                            TimeSpan.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out TimeSpan bs) &&
                            TimeSpan.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out TimeSpan be))
                        {
                            breakWindows.Add(new BreakTimeWindow(bs, be));
                        }
                    }
                }
            }
            if ((breakWindows == null || breakWindows.Count == 0) && CurrentSnapshot.BreakWindows != null)
            {
                breakWindows = CurrentSnapshot.BreakWindows.ToList();
            }

            if (_userBreaksDate == DateTime.Today && _userBreakWindowsToday.Count > 0)
            {
                breakWindows = MergeBreakWindows(breakWindows, _userBreakWindowsToday).ToList();
            }

            if (_manualStatusOverride.HasValue && _manualOverrideDate == DateTime.Today)
            {
                if (_manualStatusOverride.Value == WorkStatusKind.Break)
                {
                    string breakClockIn = !string.IsNullOrWhiteSpace(clockIn) ? clockIn : (CurrentSnapshot.ClockInTimeText ?? DateTime.Now.ToString("HH:mm"));
                    DateTime breakStart = _activeBreakStartTime ?? DateTime.Now;
                    UpdateSnapshot(new AttendanceSnapshot(
                        WorkStatusKind.Break,
                        "휴게",
                        $"휴게중 ({breakStart:HH:mm}~)",
                        breakClockIn,
                        null,
                        duration,
                        DateTime.Now,
                        breakWindows,
                        breakStart));
                    return;
                }

                state = _manualStatusOverride.Value == WorkStatusKind.Working ? "WORKING" : "OFF_WORK";
                if (state == "WORKING" && string.IsNullOrWhiteSpace(clockIn))
                {
                    clockIn = CurrentSnapshot.ClockInTimeText;
                }
            }

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
                    DateTime.Now,
                    breakWindows));
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
                    DateTime.Now,
                    breakWindows));
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

            // 로그인 폼 제출 시 ID/PW를 캡처하여 이후 앱 재실행/세션 만료 시 자동 로그인에 활용
            const captureLoginCredentials = () => {
                try {
                    const passInput = document.querySelector('input[type="password"], #password, input[name="password"]');
                    const userInput = document.querySelector('#username, #userId, #loginId, input[name="username"], input[name="userId"], input[type="text"], input[type="email"]');
                    if (passInput && userInput && userInput.value && passInput.value && window.chrome?.webview?.postMessage) {
                        window.chrome.webview.postMessage(JSON.stringify({
                            msgType: 'LOGIN_CREDENTIALS',
                            username: userInput.value,
                            password: passInput.value
                        }));
                    }
                } catch {}
            };

            window.addEventListener('submit', captureLoginCredentials, true);
            window.addEventListener('click', (e) => {
                const target = e.target;
                if (target && document.querySelector('input[type="password"]')) {
                    captureLoginCredentials();
                }
            }, true);
            window.addEventListener('keydown', (e) => {
                if (e.key === 'Enter' && document.querySelector('input[type="password"]')) {
                    captureLoginCredentials();
                }
            }, true);

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
            const closeBtnSelectors = [
                '.go_popup .btn_layer_x',
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
            return count;
        })();
        """;

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

                const docs = [document];
                document.querySelectorAll('iframe').forEach(ifr => {
                    try { if (ifr.contentDocument) docs.push(ifr.contentDocument); } catch {}
                });

                const liveClockTimes = new Set();
                const now = new Date();
                const pad2 = (n) => String(n).padStart(2, '0');
                for (let diff = -2; diff <= 2; diff++) {
                    const d = new Date(now.getTime() + diff * 1000);
                    liveClockTimes.add(`${pad2(d.getHours())}:${pad2(d.getMinutes())}:${pad2(d.getSeconds())}`);
                }
                for (const doc of docs) {
                    doc.querySelectorAll('#timelineClock, .time_info, .clock, #clock, .now_time, .real_time').forEach(el => {
                        const m = clean(el.innerText).match(timeOnlyRe);
                        if (m) liveClockTimes.add(m[0]);
                    });
                }

                const isLiveClockValue = (t) => t && liveClockTimes.has(t);

                let clockIn = null;
                let clockOut = null;
                let clockOutExplicitlyUnset = false;
                let duration = null;
                let badgeText = null;
                let apiStatus = null;
                const breakRangesSet = new Set();

                const addBreakRange = (startStr, endStr) => {
                    if (!startStr || !endStr) return;
                    const sm = String(startStr).match(/([01]\d|2[0-3]):[0-5]\d/);
                    const em = String(endStr).match(/([01]\d|2[0-3]):[0-5]\d/);
                    if (sm && em) {
                        breakRangesSet.add(`${sm[0]}~${em[0]}`);
                    }
                };

                const yyyy = now.getFullYear();
                const mm = pad2(now.getMonth() + 1);
                const dd = pad2(now.getDate());
                const todayIso = `${yyyy}-${mm}-${dd}`;
                const todayDot = `${yyyy}.${mm}.${dd}`;
                const todayCompact = `${yyyy}${mm}${dd}`;

                // 전략 0: 다우오피스 EHR 좌측 실시간 근태 가젯(.lnb_gadget.gadget_attend) DOM에서 최우선 추출
                for (const doc of docs) {
                    const gadget = doc.querySelector('.lnb_gadget.gadget_attend, .attend_card');
                    if (!gadget) continue;

                    const tag = gadget.querySelector('.gadget_header .basic_tag, .card_header .basic_tag');
                    if (tag) {
                        const tagCls = (tag.className || '').toLowerCase();
                        const tagTxt = clean(tag.innerText);
                        if (tagCls.includes('positive')) {
                            badgeText = '근무중';
                            apiStatus = tagTxt || 'ON';
                        } else if (tagCls.includes('neutral') || tagTxt === '퇴근' || tagTxt === '업무종료') {
                            badgeText = '퇴근';
                            apiStatus = tagTxt || 'OFF';
                        }
                    }

                    const workTypes = Array.from(gadget.querySelectorAll('.working_hours .work_type'));
                    for (const wt of workTypes) {
                        const tit = clean(wt.querySelector('.tit')?.innerText || '');
                        const data = clean(wt.querySelector('.data')?.innerText || '');
                        if (tit.includes('출근')) {
                            const m = data.match(timeOnlyRe);
                            if (m && !isLiveClockValue(m[0])) clockIn = m[0];
                        } else if (tit.includes('퇴근')) {
                            const m = data.match(timeOnlyRe);
                            if (m && !isLiveClockValue(m[0])) {
                                clockOut = m[0];
                                clockOutExplicitlyUnset = false;
                            } else if (unsetRe.test(data) && !clockOut) {
                                clockOutExplicitlyUnset = true;
                            }
                        }
                    }
                }

                // 전략 1: 가로챈 다우오피스 API JSON(__workdotApiLogs)에서 최신 오늘 데이터 및 휴게시간(fixedBreakTimeList) 추출
                if (Array.isArray(window.__workdotApiLogs)) {
                    let lockedLatestAttendRecord = Boolean(clockIn || clockOut);
                    for (let i = window.__workdotApiLogs.length - 1; i >= 0; i--) {
                        const entry = window.__workdotApiLogs[i];
                        if (!entry || !entry.data) continue;

                        let entryHadAttendRecord = false;
                        const visit = (obj) => {
                            if (!obj || typeof obj !== 'object') return;
                            if (Array.isArray(obj)) {
                                obj.forEach(visit);
                                return;
                            }

                            // 다우오피스 근무그룹 휴게시간 목록 (fixedBreakTimeList) 추출
                            if (Array.isArray(obj.fixedBreakTimeList)) {
                                obj.fixedBreakTimeList.forEach(b => {
                                    if (b && b.breakStartTime && b.breakEndTime) {
                                        addBreakRange(b.breakStartTime, b.breakEndTime);
                                    }
                                });
                            }

                            const dVal = obj.date || obj.accrualDate || obj.logDate || obj.workDate || obj.baseDate || obj.targetDate || obj.attndDate || obj.solarDate;
                            if (typeof dVal === 'string') {
                                const dClean = dVal.slice(0, 10);
                                if (/^\d{4}[-.]?\d{2}[-.]?\d{2}/.test(dClean) &&
                                    !dClean.includes(todayIso) && !dClean.includes(todayDot) && !dClean.includes(todayCompact)) {
                                    return;
                                }
                            }

                            // 일일 인정 근무시간(dailyWorkHoursDto.sumWorkingHours) 초 단위 추출
                            if (obj.dailyWorkHoursDto && typeof obj.dailyWorkHoursDto.sumWorkingHours === 'number' && obj.dailyWorkHoursDto.sumWorkingHours > 0 && !duration) {
                                const totalSec = obj.dailyWorkHoursDto.sumWorkingHours;
                                const h = Math.floor(totalSec / 3600);
                                const m = Math.floor((totalSec % 3600) / 60);
                                duration = `${pad2(h)}:${pad2(m)}`;
                            }

                            // 이미 최신 API 응답(또는 실시간 가젯 DOM)에서 출퇴근 시각을 확보했다면 과거 로그로 덮어쓰지 않음
                            if (!lockedLatestAttendRecord) {
                                const inRaw = obj.startWorkTime ?? obj.clockInTime ?? obj.workInTime ?? obj.attndInTime ?? obj.inTime ?? obj.checkInTime ?? obj.realClockInTime;
                                const outRaw = obj.endWorkTime ?? obj.clockOutTime ?? obj.workOutTime ?? obj.attndOutTime ?? obj.outTime ?? obj.checkOutTime ?? obj.realClockOutTime;
                                const stRaw = obj.displayWorkType ?? obj.workStatusName ?? obj.workStatus ?? obj.timelineStatus ?? obj.attndStatus ?? obj.workingStatus ?? obj.statusName ?? obj.stateName;

                                if (typeof inRaw === 'string' && timeOnlyRe.test(inRaw) && !clockIn) {
                                    const m = inRaw.match(timeOnlyRe)[0];
                                    if (!isLiveClockValue(m)) {
                                        clockIn = m;
                                        entryHadAttendRecord = true;
                                    }
                                }
                                if (typeof outRaw === 'string') {
                                    if (timeOnlyRe.test(outRaw) && !outRaw.startsWith('00:00') && !clockOut) {
                                        const m = outRaw.match(timeOnlyRe)[0];
                                        if (!isLiveClockValue(m)) {
                                            clockOut = m;
                                            clockOutExplicitlyUnset = false;
                                            entryHadAttendRecord = true;
                                        }
                                    } else if ((!outRaw || unsetRe.test(outRaw)) && !clockOut) {
                                        clockOutExplicitlyUnset = true;
                                    }
                                } else if (inRaw && (outRaw === null || outRaw === undefined || outRaw === '') && !clockOut) {
                                    clockOutExplicitlyUnset = true;
                                }

                                if (typeof stRaw === 'string' && !apiStatus) {
                                    apiStatus = stRaw;
                                }
                            }

                            for (const k of Object.keys(obj)) {
                                if (typeof obj[k] === 'object') visit(obj[k]);
                            }
                        };
                        try { visit(entry.data); } catch {}
                        if (entryHadAttendRecord) {
                            lockedLatestAttendRecord = true;
                        }
                    }
                }

                // 전략 2: DOM 탐색
                const isInsidePastTableRow = (el) => {
                    const tr = el.closest('tr, .tb_row, .timeline_row, [class*="row"]');
                    if (!tr) return false;
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
                    const inSelectors = ['#workInTime', '#clockInTime', '.work_in_time', '.clock_in_time', '[data-role="clockInTime"]', '[data-id="clockInTime"]'];
                    for (const sel of inSelectors) {
                        const el = doc.querySelector(sel);
                        if (el) {
                            const m = clean(el.innerText).match(timeOnlyRe);
                            if (m && !isLiveClockValue(m[0]) && !clockIn) clockIn = m[0];
                        }
                    }

                    const outSelectors = ['#workOutTime', '#clockOutTime', '.work_out_time', '.clock_out_time', '[data-role="clockOutTime"]', '[data-id="clockOutTime"]'];
                    for (const sel of outSelectors) {
                        const el = doc.querySelector(sel);
                        if (el) {
                            const t = clean(el.innerText);
                            const m = t.match(timeOnlyRe);
                            if (m && !isLiveClockValue(m[0]) && !clockOut) clockOut = m[0];
                            else if (unsetRe.test(t)) clockOutExplicitlyUnset = true;
                        }
                    }

                    const allNodes = Array.from(doc.querySelectorAll('dt, th, span, div, p, strong, label, li'));
                    for (const el of allNodes) {
                        if (isInsidePastTableRow(el)) continue;

                        const ownText = clean(el.innerText);
                        if (!ownText || ownText.length > 40) continue;

                        if (/^(출근시간|출근시각|출근|업무시작)$/.test(ownText)) {
                            const sib = el.nextElementSibling;
                            if (sib) {
                                const sibText = clean(sib.innerText);
                                const m = sibText.match(timeOnlyRe);
                                if (m && !isLiveClockValue(m[0]) && !clockIn) clockIn = m[0];
                            }
                        } else if (!clockIn) {
                            const afterInMatch = ownText.match(/(?:출근시간|출근시각|출근|업무시작)\s*[:：]?\s*([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?/);
                            if (afterInMatch) {
                                const m = afterInMatch[0].match(timeOnlyRe);
                                if (m && !isLiveClockValue(m[0])) clockIn = m[0];
                            }
                        }

                        if (/^(퇴근시간|퇴근시각|퇴근|업무종료)$/.test(ownText)) {
                            const sib = el.nextElementSibling;
                            if (sib) {
                                const sibText = clean(sib.innerText);
                                const m = sibText.match(timeOnlyRe);
                                if (m && !isLiveClockValue(m[0]) && !clockOut) clockOut = m[0];
                                else if (unsetRe.test(sibText)) clockOutExplicitlyUnset = true;
                            }
                        } else if (!clockOut) {
                            const afterOutMatch = ownText.match(/(?:퇴근시간|퇴근시각|퇴근|업무종료)\s*[:：]?\s*([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?/);
                            if (afterOutMatch) {
                                const m = afterOutMatch[0].match(timeOnlyRe);
                                if (m && !isLiveClockValue(m[0])) clockOut = m[0];
                            } else if (/(?:퇴근시간|퇴근시각|퇴근)\s*[:：]?\s*(미등록|--|-)/.test(ownText)) {
                                clockOutExplicitlyUnset = true;
                            }
                        }

                        // 다우오피스 근무그룹 카드의 휴게시간 (예: "12:00 ~ 13:00(1h)") 추출
                        if (/^(휴게시간|휴게)$/.test(ownText)) {
                            const sib = el.nextElementSibling;
                            if (sib) {
                                const sibText = clean(sib.innerText);
                                const rangeMatches = sibText.matchAll(/([01]\d|2[0-3]):[0-5]\d\s*~\s*([01]\d|2[0-3]):[0-5]\d/g);
                                for (const rm of rangeMatches) {
                                    addBreakRange(rm[1] ? rm[0].split('~')[0] : null, rm[0].split('~')[1]);
                                }
                            }
                        }

                        // 다우오피스 일일 요약의 총 근무시간 (예: "3h 15m 0s") 추출 (0h 0m 0s 제외)
                        if (/^(총 근무시간|총근무시간|인정근무시간)$/.test(ownText) && !duration) {
                            const sib = el.nextElementSibling;
                            if (sib) {
                                const sibText = clean(sib.innerText);
                                const hmMatch = sibText.match(/(\d+)\s*h\s*(\d+)\s*m/i) || sibText.match(/(\d+)\s*시간\s*(\d+)\s*분/);
                                if (hmMatch) {
                                    const h = parseInt(hmMatch[1], 10);
                                    const m = parseInt(hmMatch[2], 10);
                                    if (h > 0 || m > 0) {
                                        duration = `${pad2(h)}:${pad2(m)}`;
                                    }
                                }
                            }
                        }
                    }

                    const todayRows = Array.from(doc.querySelectorAll('tr.today, .today_row, .tb_attend .today'));
                    for (const row of todayRows) {
                        const rowToCheck = row.closest('tr') || row;
                        const times = (clean(rowToCheck.innerText).match(/\b([01]\d|2[0-3]):[0-5]\d(:[0-5]\d)?\b/g) || [])
                            .filter(t => !isLiveClockValue(t));
                        if (times.length === 1 && !clockIn) {
                            clockIn = times[0];
                            clockOutExplicitlyUnset = true;
                        } else if (times.length >= 2) {
                            if (!clockIn) clockIn = times[0];
                            if (!clockOut && !clockOutExplicitlyUnset) clockOut = times[1];
                        }
                    }

                    const statusEls = Array.from(doc.querySelectorAll(
                        'select option:checked, .state, .status, .badge, .chip, .txt_state, .btn_state'
                    ));
                    for (const el of statusEls) {
                        if (el.tagName !== 'OPTION' && el.offsetParent === null) continue;
                        if (el.closest('ul, .layer_transition, .dropdown, [style*="display: none"], [style*="display:none"]')) continue;
                        if (isInsidePastTableRow(el)) continue;

                        const t = clean(el.innerText);
                        if (!t || t.length > 12) continue;

                        if (['근무중', '근무 중', '업무중', '업무 중', '정상근무', '연장근무', '외근', '출장'].includes(t)) {
                            badgeText = t;
                            break;
                        }
                        if (['퇴근', '업무종료', '업무 종료', '출근전', '출근 전', '미출근'].includes(t)) {
                            badgeText = t;
                        }
                    }
                }

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
                            if (m && !isLiveClockValue(m[0]) && !clockIn) clockIn = m[0];
                        }
                        if (id === 'workout' || id === 'clockout' || t.startsWith('퇴근하기') || t === '퇴근') {
                            clockOutBtnFound = true;
                            if (isDisabled) clockOutBtnDisabled = true;
                            const m = t.match(timeOnlyRe);
                            if (m && !isLiveClockValue(m[0]) && !clockOut) clockOut = m[0];
                        }
                    }
                }

                if (clockOutExplicitlyUnset && !clockOut) {
                    clockOut = null;
                }

                const hasValidClockIn = Boolean(clockIn && !clockIn.startsWith('00:00'));
                const hasValidClockOut = Boolean(clockOut && !clockOut.startsWith('00:00'));

                let state = 'UNKNOWN';

                if (clockInBtnFound && clockInBtnDisabled && clockOutBtnFound && !clockOutBtnDisabled && !hasValidClockOut) {
                    state = 'WORKING';
                } else if (hasValidClockIn && !hasValidClockOut) {
                    state = 'WORKING';
                } else if (hasValidClockOut || (clockInBtnFound && clockInBtnDisabled && clockOutBtnFound && clockOutBtnDisabled)) {
                    state = 'OFF_WORK';
                } else if (clockInBtnFound && !clockInBtnDisabled) {
                    state = 'OFF_WORK';
                } else {
                    const combinedStatus = ((apiStatus || '') + ' ' + (badgeText || '')).toUpperCase();
                    if (combinedStatus.includes('WORKING') || combinedStatus.includes('WORK_IN') || combinedStatus.includes('ON_WORK') ||
                        combinedStatus.includes('근무중') || combinedStatus.includes('근무 중') ||
                        combinedStatus.includes('업무중') || combinedStatus.includes('업무 중')) {
                        state = 'WORKING';
                    } else if (combinedStatus.includes('WORK_OUT') || combinedStatus.includes('OFF_WORK') ||
                               combinedStatus.includes('업무종료') || combinedStatus.includes('출근전')) {
                        state = 'OFF_WORK';
                    } else {
                        state = 'LOADING';
                    }
                }

                return JSON.stringify({
                    isLoginPage: false,
                    url,
                    state,
                    clockIn,
                    clockOut,
                    clockOutExplicitlyUnset,
                    duration,
                    breakRanges: Array.from(breakRangesSet),
                    badgeText,
                    apiStatus,
                    clockInBtnFound,
                    clockInBtnDisabled,
                    clockOutBtnFound,
                    clockOutBtnDisabled
                });
            };

            window.__workdotRunProbe = runProbe;
            return runProbe();
        })();
        """;

    private static string GetClockInTriggerScript() => """
        (() => {
            const clean = (s) => (s || '').replace(/\s+/g, ' ').trim();
            // 1. 다우오피스 EHR 좌측 근태 가젯(.lnb_gadget.gadget_attend .attend_btn button) 최우선 탐색
            const gadgetBtns = Array.from(document.querySelectorAll('.lnb_gadget.gadget_attend .attend_btn button, .attend_card .attend_btn button, .attend_btn button'));
            for (const btn of gadgetBtns) {
                const t = clean(btn.innerText);
                if (t === '출근하기' || t.startsWith('출근하기') || t === '출근' || t === '업무시작') {
                    if (btn.disabled || btn.classList.contains('disabled')) {
                        return 'DISABLED:' + t;
                    }
                    btn.click();
                    return 'OK:GADGET:' + t;
                }
            }

            // 2. 레거시 직접 셀렉터
            const direct = document.querySelector('#workIn, #clockIn, [data-role="clockIn"], [data-action="clockIn"], .btn_work_in');
            if (direct) {
                direct.click();
                return 'OK:DIRECT';
            }

            // 3. 일반 버튼 요소 탐색 (단순 텍스트 span 제외)
            const candidates = Array.from(document.querySelectorAll('button, a, [role="button"]'));
            for (const el of candidates) {
                if (el.offsetParent === null) continue;
                const t = clean(el.innerText);
                if (t === '출근하기' || t.startsWith('출근하기') || t === '업무시작') {
                    if (el.disabled || el.classList.contains('disabled')) {
                        return 'DISABLED:' + t;
                    }
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
            // 1. 다우오피스 EHR 좌측 근태 가젯(.lnb_gadget.gadget_attend .attend_btn button) 최우선 탐색
            const gadgetBtns = Array.from(document.querySelectorAll('.lnb_gadget.gadget_attend .attend_btn button, .attend_card .attend_btn button, .attend_btn button'));
            for (const btn of gadgetBtns) {
                const t = clean(btn.innerText);
                if (t === '퇴근하기' || t.startsWith('퇴근하기') || t === '퇴근' || t === '업무종료') {
                    if (btn.disabled || btn.classList.contains('disabled')) {
                        return 'DISABLED:' + t;
                    }
                    btn.click();
                    return 'OK:GADGET:' + t;
                }
            }

            // 2. 레거시 직접 셀렉터
            const direct = document.querySelector('#workOut, #clockOut, [data-role="clockOut"], [data-action="clockOut"], .btn_work_out');
            if (direct) {
                direct.click();
                return 'OK:DIRECT';
            }

            // 3. 일반 버튼 요소 탐색 (캘린더/표 내부의 단순 텍스트 span은 클릭하지 않도록 제외)
            const candidates = Array.from(document.querySelectorAll('button, a, [role="button"]'));
            for (const el of candidates) {
                if (el.offsetParent === null) continue;
                const t = clean(el.innerText);
                if (t === '퇴근하기' || t.startsWith('퇴근하기') || t === '업무종료') {
                    if (el.disabled || el.classList.contains('disabled')) {
                        return 'DISABLED:' + t;
                    }
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

            // 1. 다우오피스 EHR React 다이얼로그 (.dop_dialog.open) 처리
            const dopDialog = document.querySelector('.dop_dialog.open, [role="dialog"]');
            if (dopDialog) {
                const radios = Array.from(dopDialog.querySelectorAll('.dop_radio input[type="radio"]'));
                if (radios.length === 1 && !radios[0].checked) {
                    radios[0].click();
                } else if (radios.length > 1 && !radios.some(r => r.checked)) {
                    return 'NEEDS_USER_INPUT:WORK_LOCATION';
                }

                const majorBtn = dopDialog.querySelector('.popup_btn_area button.solid.major, button.solid.major');
                if (majorBtn && !majorBtn.disabled) {
                    majorBtn.click();
                    return 'CONFIRMED:DOP_DIALOG';
                }

                const btns = Array.from(dopDialog.querySelectorAll('.popup_btn_area button, button'));
                for (const btn of btns) {
                    const t = clean(btn.innerText);
                    if ((t === '확인' || t === '예' || t === '출근' || t === '퇴근') && !btn.disabled) {
                        btn.click();
                        return 'CONFIRMED:' + t;
                    }
                }
            }

            // 2. 레거시 다우오피스 팝업 처리
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
