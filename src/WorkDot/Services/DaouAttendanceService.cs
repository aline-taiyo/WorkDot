using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

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
    DateTime? ActiveBreakStart = null,
    bool CanClockIn = false,
    bool CanResumeWork = false,
    IReadOnlyList<BreakTimeWindow>? UserBreakWindows = null)
{
    private static readonly IReadOnlyList<BreakTimeWindow> DefaultBreakWindows =
    [
        new BreakTimeWindow(new TimeSpan(12, 0, 0), new TimeSpan(13, 0, 0))
    ];

    /// <summary>
    /// 시각 문자열("10:17:20" 또는 "10:14")을 위젯 표시용 "HH:mm" 형식으로 변환합니다.
    /// </summary>
    public static string FormatClockTimeHm(string? rawTime)
    {
        if (!string.IsNullOrWhiteSpace(rawTime) &&
            TimeSpan.TryParse(rawTime.Trim(), CultureInfo.InvariantCulture, out TimeSpan ts))
        {
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}";
        }
        return "--:--";
    }

    /// <summary>
    /// 오늘 날짜(DateTime.Today) 기준으로 유효한 출근 기록이 존재하는지 여부.
    /// 스냅샷 날짜가 오늘이 아니거나 출근 시각이 없으면 false입니다.
    /// </summary>
    public bool HasClockedInToday =>
        UpdatedAt.Date == DateTime.Today && !string.IsNullOrWhiteSpace(ClockInTimeText);

    /// <summary>
    /// 다우오피스 근태 페이지에서 출근상태로 변경(첫 출근)이 가능한지 여부.
    /// 출근하기 버튼이 활성화되어 있거나 출근 상태 변경이 가능한 경우 true.
    /// </summary>
    public bool IsClockInAvailable =>
        Status == WorkStatusKind.OffWork && (CanClockIn || (!CanResumeWork && !HasClockedInToday));

    /// <summary>
    /// 다우오피스 근태 페이지에서 퇴근 상태에서 '업무' 상태로 전환할 수 있는지 여부.
    /// 다우오피스 근무상태변경 목록에 '업무'가 있거나 당일 출근 이력이 있고 출근 버튼이 비활성화된 경우 true.
    /// </summary>
    public bool IsResumeWorkAvailable =>
        Status == WorkStatusKind.OffWork && (CanResumeWork || (HasClockedInToday && !CanClockIn));

    /// <summary>
    /// 다우오피스에서 수집된 출근 시각 및 휴게시간(fixedBreakTimeList / 기본 12:00~13:00 / 사용자 수동 휴게)을 바탕으로
    /// 휴게시간을 제외한 실근무 경과 시간(TimeSpan)을 계산합니다.
    /// </summary>
    public TimeSpan? GetWorkingElapsedSpanExcludingBreaks(DateTime now)
    {
        if (Status == WorkStatusKind.OffWork && !HasClockedInToday)
        {
            return null;
        }

        DateTime effectiveNow;
        if (Status == WorkStatusKind.OffWork)
        {
            if (!string.IsNullOrWhiteSpace(ClockOutTimeText) &&
                TimeSpan.TryParse(ClockOutTimeText.Trim(), CultureInfo.InvariantCulture, out TimeSpan clockOutTod))
            {
                effectiveNow = now.Date.Add(clockOutTod);
            }
            else if (!string.IsNullOrWhiteSpace(WorkedDurationText) &&
                     TimeSpan.TryParse(WorkedDurationText.Trim(), CultureInfo.InvariantCulture, out TimeSpan offDur))
            {
                return offDur;
            }
            else
            {
                return null;
            }
        }
        else
        {
            effectiveNow = (ActiveBreakStart.HasValue && now > ActiveBreakStart.Value)
                ? ActiveBreakStart.Value
                : now;
        }

        if (!string.IsNullOrWhiteSpace(ClockInTimeText) &&
            TimeSpan.TryParse(ClockInTimeText.Trim(), CultureInfo.InvariantCulture, out TimeSpan clockInTod))
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
            TimeSpan.TryParse(WorkedDurationText.Trim(), CultureInfo.InvariantCulture, out TimeSpan parsedDur))
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

    /// <summary>
    /// 오늘 누적된 사용자 휴게(근무상태변경 휴게·외출 등, 기본 점심시간 제외) 경과 시간(TimeSpan)을 계산합니다.
    /// </summary>
    public TimeSpan GetUserBreakElapsedSpan(DateTime now)
    {
        TimeSpan total = TimeSpan.Zero;
        if (UserBreakWindows != null && UserBreakWindows.Count > 0)
        {
            foreach (var bw in UserBreakWindows)
            {
                TimeSpan dur = bw.End >= bw.Start
                    ? (bw.End - bw.Start)
                    : (bw.End + TimeSpan.FromDays(1) - bw.Start);
                if (dur > TimeSpan.Zero)
                {
                    total += dur;
                }
            }
        }

        if (Status == WorkStatusKind.Break && ActiveBreakStart.HasValue && now > ActiveBreakStart.Value)
        {
            total += (now - ActiveBreakStart.Value);
        }

        return total;
    }

    /// <summary>
    /// 오늘 누적된 사용자 휴게 시간을 "HH:mm" 형식으로 반환합니다.
    /// </summary>
    public string GetUserBreakElapsedText(DateTime now)
    {
        TimeSpan span = GetUserBreakElapsedSpan(now);
        int hours = (int)span.TotalHours;
        int minutes = span.Minutes;
        return $"{hours:D2}:{minutes:D2}";
    }

    /// <summary>
    /// 오늘 사용자 휴게 기록이 존재하거나 현재 휴게 중인지 여부.
    /// </summary>
    public bool HasUserBreak(DateTime now) =>
        Status == WorkStatusKind.Break ||
        GetUserBreakElapsedSpan(now).TotalMinutes >= 1.0 ||
        (UserBreakWindows != null && UserBreakWindows.Count > 0);
}

/// <summary>
/// 크롬 확장프로그램 로컬 브릿지(ChromeExtensionBridgeService)를 통해
/// 사용자가 사용 중인 크롬 브라우저의 다우오피스 로그인 세션을 그대로 공유하여 근태 상태를 동기화하고 출퇴근 액션을 실행합니다.
/// </summary>
public sealed class DaouAttendanceService : IDisposable
{
    public const string TargetAttendanceUrl = "https://alinestudio.daouoffice.com/ehr/app/attend/my-attendance-status";
    public const string BaseUrl = "https://alinestudio.daouoffice.com";
    public const string BaseDomain = "alinestudio.daouoffice.com";

    private readonly ChromeExtensionBridgeService _bridge;

    private DateTime _lastKnownDate = DateTime.Today;
    private WorkStatusKind? _manualStatusOverride;
    private DateTime _manualOverrideDate;
    private DateTime? _activeBreakStartTime;
    private DateTime _userBreaksDate = DateTime.Today;
    private readonly List<BreakTimeWindow> _userBreakWindowsToday = new();

    public bool IsExtensionConnected => _bridge.IsExtensionConnected;

    public int BridgePort => _bridge.ActivePort;

    public AttendanceSnapshot CurrentSnapshot { get; private set; } = new(
        WorkStatusKind.Unknown,
        "연결 대기",
        "크롬 확장프로그램 연결 대기 중 (우클릭 → 크롬 확장 설치)",
        null,
        null,
        null,
        DateTime.Now);

    public event Action<AttendanceSnapshot>? SnapshotChanged;
    public event Action<bool>? BridgeConnectionChanged;

    public string AppDataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WorkDot");

    public string LastProbeLogPath =>
        Path.Combine(AppDataFolder, "last-probe.json");

    public DaouAttendanceService()
    {
        TryRestoreTodaySnapshotFromCache();

        _bridge = new ChromeExtensionBridgeService();
        _bridge.ProbeReceived += OnBridgeProbeReceived;
        _bridge.ConnectionStateChanged += connected =>
        {
            BridgeConnectionChanged?.Invoke(connected);
        };
        _bridge.Start();
    }

    private void OnBridgeProbeReceived(string json)
    {
        ProcessWebMessageJson(json);
    }

    /// <summary>
    /// 자정이 지나 날짜가 변경되었을 때(어제 -> 오늘) 어제의 출퇴근/휴게 상태를 로컬에서 깨끗이 리셋합니다.
    /// 백그라운드에서 별도 로그인 시도나 중복 로그인을 유발하지 않습니다.
    /// </summary>
    public void CheckDateRollover()
    {
        if (DateTime.Today != _lastKnownDate)
        {
            _lastKnownDate = DateTime.Today;
            _userBreaksDate = DateTime.Today;
            _userBreakWindowsToday.Clear();
            _manualStatusOverride = null;
            _manualOverrideDate = DateTime.MinValue;
            _activeBreakStartTime = null;

            if (CurrentSnapshot.Status != WorkStatusKind.NeedsLogin)
            {
                UpdateSnapshot(new AttendanceSnapshot(
                    WorkStatusKind.OffWork,
                    "퇴근",
                    "출근 전",
                    null,
                    null,
                    null,
                    DateTime.Now));
            }
        }
    }

    public async Task OpenPortalInBrowserAsync()
    {
        if (_bridge.IsExtensionConnected)
        {
            var result = await _bridge.SendCommandAsync("OPEN_PORTAL", TimeSpan.FromSeconds(5));
            if (result.Success)
                return;
        }

        OpenDefaultBrowserUrl(TargetAttendanceUrl);
    }

    public void OpenPortalInBrowser()
    {
        _ = OpenPortalInBrowserAsync();
    }

    public static void OpenDefaultBrowserUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore browser launch errors
        }
    }

    public async Task RefreshStatusAsync(bool isUserAction = false)
    {
        CheckDateRollover();
        _bridge.CheckConnectionTimeout();

        if (_bridge.IsExtensionConnected)
        {
            if (isUserAction)
            {
                await _bridge.SendCommandAsync("REFRESH", TimeSpan.FromSeconds(12));
            }
            else
            {
                _bridge.EnqueueFireAndForget("REFRESH");
            }
            return;
        }

        if (CurrentSnapshot.Status == WorkStatusKind.Unknown)
        {
            UpdateSnapshot(new AttendanceSnapshot(
                WorkStatusKind.Unknown,
                "연결 대기",
                "크롬 확장프로그램 연결 대기 중 (우클릭 → 크롬 확장 설치)",
                null,
                null,
                null,
                DateTime.Now));
        }
    }

    public async Task<(bool Success, string Message)> TriggerClockActionAsync(bool clockIn)
    {
        CheckDateRollover();

        if (clockIn && CurrentSnapshot.IsResumeWorkAvailable)
        {
            return await TriggerResumeWorkActionAsync();
        }

        return await ExecuteBridgeActionAsync(
            clockIn ? "CLOCK_IN" : "CLOCK_OUT",
            clockIn ? "출근 처리가 완료되었습니다." : "퇴근 처리가 완료되었습니다.");
    }

    /// <summary>
    /// 오늘 이미 출근 후 퇴근한 상태에서 다시 업무를 시작할 때,
    /// 크롬의 다우오피스 탭에서 '근무상태변경' -> '업무' 메뉴를 트리거합니다.
    /// </summary>
    public async Task<(bool Success, string Message)> TriggerResumeWorkActionAsync()
    {
        CheckDateRollover();
        return await ExecuteBridgeActionAsync("RESUME_WORK", "업무 상태로 전환되었습니다.");
    }

    public async Task<(bool Success, string Message)> TriggerBreakActionAsync(bool toBreak)
    {
        CheckDateRollover();
        return await ExecuteBridgeActionAsync(
            toBreak ? "BREAK_IN" : "BREAK_OUT",
            toBreak ? "휴게 상태로 전환되었습니다." : "근무중 상태로 전환되었습니다.");
    }

    private async Task<(bool Success, string Message)> ExecuteBridgeActionAsync(string action, string successMessage)
    {
        if (!_bridge.IsExtensionConnected)
        {
            ClearManualStatusOverride();
            OpenDefaultBrowserUrl(TargetAttendanceUrl);
            return (false, "크롬 확장프로그램이 연결되지 않아 브라우저에서 다우오피스를 열었습니다. 확장프로그램을 설치·연결해주세요.");
        }

        BridgeCommandResult res = await _bridge.SendCommandAsync(action, TimeSpan.FromSeconds(22));
        if (res.Success)
        {
            return (true, successMessage);
        }

        ClearManualStatusOverride();

        if (res.NeedsLogin)
        {
            UpdateSnapshot(new AttendanceSnapshot(
                WorkStatusKind.NeedsLogin,
                "로그인 필요",
                "클릭하여 크롬에서 다우오피스 로그인 / 2차 인증 완료",
                null,
                null,
                null,
                DateTime.Now));
            return (false, string.IsNullOrWhiteSpace(res.Message) ? "크롬에서 다우오피스 로그인이 필요합니다." : res.Message);
        }

        _bridge.EnqueueFireAndForget("REFRESH");
        return (false, string.IsNullOrWhiteSpace(res.Message) ? "처리 중 오류가 발생했습니다." : res.Message);
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
        CheckDateRollover();

        if (_userBreaksDate != DateTime.Today)
        {
            _userBreaksDate = DateTime.Today;
            _userBreakWindowsToday.Clear();
        }

        if (_activeBreakStartTime.HasValue && status != WorkStatusKind.Break)
        {
            DateTime now = DateTime.Now;
            if (now > _activeBreakStartTime.Value)
            {
                _userBreakWindowsToday.Add(new BreakTimeWindow(_activeBreakStartTime.Value.TimeOfDay, now.TimeOfDay));
            }
            _activeBreakStartTime = null;
        }

        if (CurrentSnapshot.Status == WorkStatusKind.OffWork && status == WorkStatusKind.Working && CurrentSnapshot.IsResumeWorkAvailable)
        {
            if (TimeSpan.TryParse(CurrentSnapshot.ClockOutTimeText, out TimeSpan offTime))
            {
                TimeSpan nowTimeSpan = DateTime.Now.TimeOfDay;
                if (nowTimeSpan > offTime)
                {
                    _userBreakWindowsToday.Add(new BreakTimeWindow(offTime, nowTimeSpan));
                }
            }
        }

        _manualStatusOverride = status;
        _manualOverrideDate = DateTime.Today;

        var mergedUserBreaks = NormalizeAndMergeBreakWindows(
            (CurrentSnapshot.UserBreakWindows ?? []).Concat(_userBreakWindowsToday));
        var mergedBreaks = MergeBreakWindows(CurrentSnapshot.BreakWindows, mergedUserBreaks);
        string nowTime = DateTime.Now.ToString("HH:mm");

        if (status == WorkStatusKind.Break)
        {
            _activeBreakStartTime ??= DateTime.Now;
            string breakClockIn = CurrentSnapshot.HasClockedInToday
                ? (CurrentSnapshot.ClockInTimeText ?? nowTime)
                : (GetCachedClockInToday() ?? nowTime);
            UpdateSnapshot(new AttendanceSnapshot(
                WorkStatusKind.Break,
                "휴게",
                $"휴게중 ({_activeBreakStartTime.Value:HH:mm}~)",
                breakClockIn,
                null,
                CurrentSnapshot.WorkedDurationText,
                DateTime.Now,
                mergedBreaks,
                _activeBreakStartTime,
                false,
                false,
                mergedUserBreaks));
        }
        else if (status == WorkStatusKind.Working)
        {
            string workClockIn = (CurrentSnapshot.HasClockedInToday || CurrentSnapshot.IsResumeWorkAvailable)
                ? (CurrentSnapshot.ClockInTimeText ?? nowTime)
                : nowTime;
            UpdateSnapshot(new AttendanceSnapshot(
                WorkStatusKind.Working,
                "근무중",
                $"출근 {workClockIn}",
                workClockIn,
                null,
                CurrentSnapshot.WorkedDurationText,
                DateTime.Now,
                mergedBreaks,
                null,
                false,
                false,
                mergedUserBreaks));
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
                null,
                false,
                true,
                mergedUserBreaks));
        }
    }

    private void TryRestoreTodaySnapshotFromCache()
    {
        try
        {
            if (!File.Exists(LastProbeLogPath))
                return;

            DateTime lastWrite = File.GetLastWriteTime(LastProbeLogPath);
            if (lastWrite.Date != DateTime.Today)
                return;

            string json = File.ReadAllText(LastProbeLogPath);
            if (string.IsNullOrWhiteSpace(json))
                return;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("isLoginPage", out var lp) && lp.GetBoolean())
                return;

            ProcessWebMessageJson(json, persistCache: false);
        }
        catch
        {
            // Ignore cache restore errors
        }
    }

    private string? GetCachedClockInToday()
    {
        try
        {
            if (File.Exists(LastProbeLogPath) && File.GetLastWriteTime(LastProbeLogPath).Date == DateTime.Today)
            {
                string json = File.ReadAllText(LastProbeLogPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("clockIn", out var inProp))
                {
                    string? cin = TryNormalizeClockTimeText(inProp.GetString());
                    if (!string.IsNullOrWhiteSpace(cin))
                        return cin;
                }
            }
        }
        catch
        {
            // Ignore
        }
        return null;
    }

    private static string? TryNormalizeClockTimeText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string trimmed = raw.Trim();
        if (trimmed.Contains('T') &&
            DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset dto))
        {
            return dto.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        return trimmed;
    }

    private static IReadOnlyList<BreakTimeWindow> NormalizeAndMergeBreakWindows(IEnumerable<BreakTimeWindow>? windows)
    {
        if (windows == null)
            return [];

        var valid = windows
            .Where(w => w.End > w.Start)
            .OrderBy(w => w.Start)
            .ToList();

        if (valid.Count <= 1)
            return valid;

        var merged = new List<BreakTimeWindow> { valid[0] };
        for (int i = 1; i < valid.Count; i++)
        {
            var prev = merged[^1];
            var cur = valid[i];

            if (cur.Start <= prev.End.Add(TimeSpan.FromMinutes(1)))
            {
                TimeSpan maxEnd = cur.End > prev.End ? cur.End : prev.End;
                merged[^1] = new BreakTimeWindow(prev.Start, maxEnd);
            }
            else
            {
                merged.Add(cur);
            }
        }

        return merged;
    }

    private static IReadOnlyList<BreakTimeWindow> MergeBreakWindows(
        IReadOnlyList<BreakTimeWindow>? baseBreaks,
        IReadOnlyList<BreakTimeWindow> userBreaks)
    {
        var combined = new List<BreakTimeWindow>();
        if (baseBreaks != null && baseBreaks.Count > 0)
        {
            combined.AddRange(baseBreaks);
        }
        else
        {
            combined.Add(new BreakTimeWindow(new TimeSpan(12, 0, 0), new TimeSpan(13, 0, 0)));
        }

        combined.AddRange(userBreaks);
        return NormalizeAndMergeBreakWindows(combined);
    }

    public void ProcessWebMessageJson(string innerJson, bool persistCache = true)
    {
        try
        {
            using var doc = JsonDocument.Parse(innerJson);
            var root = doc.RootElement;

            if (persistCache)
            {
                try
                {
                    Directory.CreateDirectory(AppDataFolder);
                    File.WriteAllText(LastProbeLogPath, innerJson);
                }
                catch
                {
                    // Ignore file lock
                }
            }

            string probeUrl = root.TryGetProperty("url", out var urlProp) ? (urlProp.GetString() ?? "") : "";
            bool isLogin = (root.TryGetProperty("isLoginPage", out var loginProp) && loginProp.GetBoolean()) ||
                           IsLoginUrl(probeUrl);

            if (isLogin)
            {
                UpdateSnapshot(new AttendanceSnapshot(
                    WorkStatusKind.NeedsLogin,
                    "로그인 필요",
                    "클릭하여 크롬에서 다우오피스 로그인 / 2차 인증 완료",
                    null,
                    null,
                    null,
                    DateTime.Now));
                return;
            }

            string state = root.TryGetProperty("state", out var sProp) ? (sProp.GetString() ?? "UNKNOWN") : "UNKNOWN";
            if (state == "PAGE_OUTDATED_NEEDS_RELOAD" || state == "LOADING" || state == "UNKNOWN")
                return;

            string? clockIn = TryNormalizeClockTimeText(root.TryGetProperty("clockIn", out var inProp) ? inProp.GetString() : null);
            string? clockOut = TryNormalizeClockTimeText(root.TryGetProperty("clockOut", out var outProp) ? outProp.GetString() : null);
            string? duration = root.TryGetProperty("duration", out var durProp) ? durProp.GetString() : null;
            string? badgeText = root.TryGetProperty("badgeText", out var bProp) ? bProp.GetString() : null;
            bool canClockIn = root.TryGetProperty("canClockIn", out var cInProp) && cInProp.GetBoolean();
            bool canResumeWork = root.TryGetProperty("canResumeWork", out var cResProp) && cResProp.GetBoolean();

            // todayGadgetData 내 workStatusName("출근"), displayWorkType("ON") 및 UTC ISO 시각 보조 보정
            if (root.TryGetProperty("todayGadgetData", out var tgProp) &&
                tgProp.ValueKind == JsonValueKind.Object &&
                tgProp.TryGetProperty("data", out var gdProp) &&
                gdProp.ValueKind == JsonValueKind.Object)
            {
                if (string.IsNullOrWhiteSpace(clockIn) &&
                    gdProp.TryGetProperty("startWorkTime", out var swProp) &&
                    swProp.ValueKind == JsonValueKind.String)
                {
                    clockIn = TryNormalizeClockTimeText(swProp.GetString());
                }
                if (string.IsNullOrWhiteSpace(clockOut) &&
                    gdProp.TryGetProperty("endWorkTime", out var ewProp) &&
                    ewProp.ValueKind == JsonValueKind.String)
                {
                    clockOut = TryNormalizeClockTimeText(ewProp.GetString());
                }

                string ws = gdProp.TryGetProperty("workStatusName", out var wsProp) ? (wsProp.GetString() ?? "") : "";
                string dwt = gdProp.TryGetProperty("displayWorkType", out var dwtProp) ? (dwtProp.GetString() ?? "") : "";
                if (ws == "출근" || ws == "업무" || string.Equals(dwt, "ON", StringComparison.OrdinalIgnoreCase))
                {
                    if (state != "BREAK") state = "WORKING";
                }
                else if (ws == "휴게" || ws == "외출")
                {
                    state = "BREAK";
                }
            }

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
                            TimeSpan.TryParse(parts[0], CultureInfo.InvariantCulture, out TimeSpan bs) &&
                            TimeSpan.TryParse(parts[1], CultureInfo.InvariantCulture, out TimeSpan be))
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

            var domUserBreaks = new List<BreakTimeWindow>();
            if (root.TryGetProperty("userBreakRanges", out var ubrProp) && ubrProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ubrProp.EnumerateArray())
                {
                    string? rangeStr = item.GetString();
                    if (!string.IsNullOrWhiteSpace(rangeStr))
                    {
                        var parts = rangeStr.Split('~', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (parts.Length == 2 &&
                            TimeSpan.TryParse(parts[0], CultureInfo.InvariantCulture, out TimeSpan ubs) &&
                            TimeSpan.TryParse(parts[1], CultureInfo.InvariantCulture, out TimeSpan ube))
                        {
                            domUserBreaks.Add(new BreakTimeWindow(ubs, ube));
                        }
                    }
                }
            }

            if (root.TryGetProperty("activeBreakStart", out var absProp) &&
                absProp.ValueKind == JsonValueKind.String &&
                TimeSpan.TryParse(absProp.GetString(), CultureInfo.InvariantCulture, out TimeSpan absTod))
            {
                DateTime domBreakStart = DateTime.Today.Add(absTod);
                if (state == "BREAK" && domBreakStart <= DateTime.Now.AddMinutes(1))
                {
                    _activeBreakStartTime = domBreakStart;
                }
            }

            if (state == "WORKING" || state == "OFF_WORK")
            {
                if (_activeBreakStartTime.HasValue)
                {
                    DateTime endRef = DateTime.Now;
                    if (state == "OFF_WORK" &&
                        !string.IsNullOrWhiteSpace(clockOut) &&
                        TimeSpan.TryParse(clockOut.Trim(), CultureInfo.InvariantCulture, out TimeSpan outTod))
                    {
                        endRef = DateTime.Today.Add(outTod);
                    }

                    if (endRef > _activeBreakStartTime.Value)
                    {
                        _userBreakWindowsToday.Add(new BreakTimeWindow(_activeBreakStartTime.Value.TimeOfDay, endRef.TimeOfDay));
                    }
                    _activeBreakStartTime = null;
                }
            }

            var combinedUserBreaks = new List<BreakTimeWindow>();
            combinedUserBreaks.AddRange(domUserBreaks);
            if (_userBreaksDate == DateTime.Today && _userBreakWindowsToday.Count > 0)
            {
                combinedUserBreaks.AddRange(_userBreakWindowsToday);
            }
            else if (domUserBreaks.Count == 0 && CurrentSnapshot.UserBreakWindows != null && CurrentSnapshot.UpdatedAt.Date == DateTime.Today)
            {
                combinedUserBreaks.AddRange(CurrentSnapshot.UserBreakWindows);
            }

            var mergedUserBreaks = NormalizeAndMergeBreakWindows(combinedUserBreaks);
            if (_userBreaksDate == DateTime.Today)
            {
                _userBreakWindowsToday.Clear();
                _userBreakWindowsToday.AddRange(mergedUserBreaks);
            }

            breakWindows = MergeBreakWindows(breakWindows, mergedUserBreaks).ToList();

            if (_manualStatusOverride.HasValue && _manualOverrideDate == DateTime.Today)
            {
                if (state == "WORKING" || state == "BREAK" || state == "OFF_WORK")
                {
                    _manualStatusOverride = null;
                }
                else if (_manualStatusOverride.Value == WorkStatusKind.Break)
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
                        breakStart,
                        canClockIn,
                        canResumeWork,
                        mergedUserBreaks));
                    return;
                }
                else
                {
                    state = _manualStatusOverride.Value == WorkStatusKind.Working ? "WORKING" : "OFF_WORK";
                    if (state == "WORKING" && string.IsNullOrWhiteSpace(clockIn))
                    {
                        clockIn = CurrentSnapshot.ClockInTimeText;
                    }
                }
            }

            if (state == "BREAK")
            {
                string breakClockIn = !string.IsNullOrWhiteSpace(clockIn) ? clockIn : (CurrentSnapshot.ClockInTimeText ?? DateTime.Now.ToString("HH:mm"));
                DateTime breakStart = _activeBreakStartTime ?? DateTime.Now;
                _activeBreakStartTime ??= breakStart;
                UpdateSnapshot(new AttendanceSnapshot(
                    WorkStatusKind.Break,
                    "휴게",
                    $"휴게중 ({breakStart:HH:mm}~)",
                    breakClockIn,
                    null,
                    duration,
                    DateTime.Now,
                    breakWindows,
                    breakStart,
                    canClockIn,
                    canResumeWork,
                    mergedUserBreaks));
                return;
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
                    breakWindows,
                    null,
                    canClockIn,
                    canResumeWork,
                    mergedUserBreaks));
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
                    breakWindows,
                    null,
                    canClockIn,
                    canResumeWork,
                    mergedUserBreaks));
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
               url.Contains("sso", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/auth", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/otp", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("twofactor", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("2fa", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("mfa", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("alliance-v2", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAttendanceCapableUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || IsLoginUrl(url))
            return false;

        return url.Contains("/ehr/", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/home", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _bridge.Dispose();
    }
}
