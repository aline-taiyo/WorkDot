using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WorkDot.Services;

public enum SnapEdge
{
    None,
    TopOutside,
    BottomOutside,
    LeftOutside,
    RightOutside,
    TopInside,
    BottomInside
}

public sealed class SnapTargetInfo
{
    public IntPtr TargetHwnd { get; init; }
    public string TargetTitle { get; init; } = string.Empty;
    public SnapEdge Edge { get; init; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public bool IsTaskbar { get; init; }
}

public sealed class Win32SnapService : IDisposable
{
    private const int SnapThresholdPx = 16;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    private readonly Window _overlayWindow;
    private IntPtr _overlayHwnd;
    private IntPtr _locationHook;
    private IntPtr _stateHook;
    private readonly WinEventDelegate _winEventProc;
    private readonly DispatcherTimer _trackingTimer;

    private SnapTargetInfo? _currentSnap;
    private RECT _lastTargetRect;
    private bool _isDraggingOverlay;
    private bool _isUpdatingPosition;

    public event Action<SnapTargetInfo?>? SnapStateChanged;

    public SnapTargetInfo? CurrentSnap => _currentSnap;

    public bool IsDraggingOverlay
    {
        get => _isDraggingOverlay;
        set => _isDraggingOverlay = value;
    }

    public Win32SnapService(Window overlayWindow)
    {
        _overlayWindow = overlayWindow;
        _winEventProc = OnWinEvent;

        _trackingTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16) // 60fps smooth follow
        };
        _trackingTimer.Tick += (_, _) => FollowSnappedTarget();
    }

    public void Initialize()
    {
        _overlayHwnd = new WindowInteropHelper(_overlayWindow).Handle;

        uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _locationHook = SetWinEventHook(
            EVENT_OBJECT_LOCATIONCHANGE,
            EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            flags);

        _stateHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_OBJECT_DESTROY,
            IntPtr.Zero,
            _winEventProc,
            0,
            0,
            flags);
    }

    /// <summary>
    /// 작업표시줄 근처 기본 스냅 위치로 초기 배치합니다.
    /// </summary>
    public void SnapToTaskbarDefault()
    {
        IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
        if (taskbarHwnd != IntPtr.Zero && TryGetWindowBounds(taskbarHwnd, out RECT tbRect))
        {
            var (overlayW, overlayH) = GetOverlayPhysicalSize();
            int targetX = tbRect.Right - overlayW - 260;
            int targetY = tbRect.Top - overlayH - 6;

            if (tbRect.Top > 100) // 하단 작업표시줄
            {
                SetOverlayPhysicalPosition(targetX, targetY);
                AttachToTarget(taskbarHwnd, "작업 표시줄", SnapEdge.TopOutside, targetX - tbRect.Left, targetY - tbRect.Top, true);
                return;
            }
        }

        // 기본 우측 하단 배치
        var workArea = SystemParameters.WorkArea;
        _overlayWindow.Left = workArea.Right - _overlayWindow.ActualWidth - 24;
        _overlayWindow.Top = workArea.Bottom - _overlayWindow.ActualHeight - 16;
    }

    /// <summary>
    /// 사용자가 오버레이를 드래그하는 중 또는 드래그를 마쳤을 때 주변 창/작업표시줄 경계로 자석 스냅합니다.
    /// </summary>
    public void EvaluateMagneticSnap(bool applyPosition = true)
    {
        if (_overlayHwnd == IntPtr.Zero || _isUpdatingPosition)
            return;

        if (!GetWindowRect(_overlayHwnd, out RECT myRect))
            return;

        int myWidth = myRect.Right - myRect.Left;
        int myHeight = myRect.Bottom - myRect.Top;

        int bestDistance = SnapThresholdPx + 1;
        int snappedX = myRect.Left;
        int snappedY = myRect.Top;
        IntPtr bestHwnd = IntPtr.Zero;
        string bestTitle = string.Empty;
        SnapEdge bestEdge = SnapEdge.None;
        bool bestIsTaskbar = false;
        RECT bestTargetRect = default;

        foreach (var candidate in EnumerateSnapCandidates())
        {
            RECT r = candidate.Bounds;

            // 가로 범위가 겹칠 때 상/하단 스냅 검사
            bool horizontalOverlap = myRect.Right >= r.Left - SnapThresholdPx && myRect.Left <= r.Right + SnapThresholdPx;
            // 세로 범위가 겹칠 때 좌/우측 스냅 검사
            bool verticalOverlap = myRect.Bottom >= r.Top - SnapThresholdPx && myRect.Top <= r.Bottom + SnapThresholdPx;

            if (horizontalOverlap)
            {
                // 1) 대상 창의 위쪽 외곽에 붙기 (오버레이 하단 -> 대상 상단)
                int distTopOut = Math.Abs(myRect.Bottom - r.Top);
                if (distTopOut < bestDistance)
                {
                    bestDistance = distTopOut;
                    snappedY = r.Top - myHeight;
                    snappedX = ClampWithCornerMagnet(myRect.Left, myWidth, r.Left, r.Right);
                    bestHwnd = candidate.Hwnd;
                    bestTitle = candidate.Title;
                    bestEdge = SnapEdge.TopOutside;
                    bestIsTaskbar = candidate.IsTaskbar;
                    bestTargetRect = r;
                }

                // 2) 대상 창의 위쪽 내곽(타이틀바 라인)에 붙기 (오버레이 상단 -> 대상 상단)
                int distTopIn = Math.Abs(myRect.Top - r.Top);
                if (distTopIn < bestDistance)
                {
                    bestDistance = distTopIn;
                    snappedY = r.Top + (candidate.IsTaskbar ? 2 : 6);
                    snappedX = ClampWithCornerMagnet(myRect.Left, myWidth, r.Left, r.Right);
                    bestHwnd = candidate.Hwnd;
                    bestTitle = candidate.Title;
                    bestEdge = SnapEdge.TopInside;
                    bestIsTaskbar = candidate.IsTaskbar;
                    bestTargetRect = r;
                }

                // 3) 대상 창의 아래쪽 외곽에 붙기 (오버레이 상단 -> 대상 하단)
                int distBottomOut = Math.Abs(myRect.Top - r.Bottom);
                if (distBottomOut < bestDistance)
                {
                    bestDistance = distBottomOut;
                    snappedY = r.Bottom;
                    snappedX = ClampWithCornerMagnet(myRect.Left, myWidth, r.Left, r.Right);
                    bestHwnd = candidate.Hwnd;
                    bestTitle = candidate.Title;
                    bestEdge = SnapEdge.BottomOutside;
                    bestIsTaskbar = candidate.IsTaskbar;
                    bestTargetRect = r;
                }

                // 4) 대상 창의 아래쪽 내곽에 붙기 (오버레이 하단 -> 대상 하단)
                int distBottomIn = Math.Abs(myRect.Bottom - r.Bottom);
                if (distBottomIn < bestDistance)
                {
                    bestDistance = distBottomIn;
                    snappedY = r.Bottom - myHeight - 4;
                    snappedX = ClampWithCornerMagnet(myRect.Left, myWidth, r.Left, r.Right);
                    bestHwnd = candidate.Hwnd;
                    bestTitle = candidate.Title;
                    bestEdge = SnapEdge.BottomInside;
                    bestIsTaskbar = candidate.IsTaskbar;
                    bestTargetRect = r;
                }
            }

            if (verticalOverlap)
            {
                // 5) 대상 창의 왼쪽 외곽에 붙기 (오버레이 우측 -> 대상 좌측)
                int distLeftOut = Math.Abs(myRect.Right - r.Left);
                if (distLeftOut < bestDistance)
                {
                    bestDistance = distLeftOut;
                    snappedX = r.Left - myWidth;
                    snappedY = ClampWithCornerMagnet(myRect.Top, myHeight, r.Top, r.Bottom);
                    bestHwnd = candidate.Hwnd;
                    bestTitle = candidate.Title;
                    bestEdge = SnapEdge.LeftOutside;
                    bestIsTaskbar = candidate.IsTaskbar;
                    bestTargetRect = r;
                }

                // 6) 대상 창의 오른쪽 외곽에 붙기 (오버레이 좌측 -> 대상 우측)
                int distRightOut = Math.Abs(myRect.Left - r.Right);
                if (distRightOut < bestDistance)
                {
                    bestDistance = distRightOut;
                    snappedX = r.Right;
                    snappedY = ClampWithCornerMagnet(myRect.Top, myHeight, r.Top, r.Bottom);
                    bestHwnd = candidate.Hwnd;
                    bestTitle = candidate.Title;
                    bestEdge = SnapEdge.RightOutside;
                    bestIsTaskbar = candidate.IsTaskbar;
                    bestTargetRect = r;
                }
            }
        }

        if (bestHwnd != IntPtr.Zero)
        {
            if (applyPosition)
            {
                SetOverlayPhysicalPosition(snappedX, snappedY);
            }

            int offsetX = snappedX - bestTargetRect.Left;
            int offsetY = snappedY - bestTargetRect.Top;
            AttachToTarget(bestHwnd, bestTitle, bestEdge, offsetX, offsetY, bestIsTaskbar);
        }
        else
        {
            DetachTarget();
        }
    }

    public void DetachTarget()
    {
        if (_currentSnap != null)
        {
            _currentSnap = null;
            _trackingTimer.Stop();
            SnapStateChanged?.Invoke(null);
        }
    }

    private void AttachToTarget(IntPtr hwnd, string title, SnapEdge edge, int offsetX, int offsetY, bool isTaskbar)
    {
        if (TryGetWindowBounds(hwnd, out RECT rect))
        {
            _lastTargetRect = rect;
        }

        _currentSnap = new SnapTargetInfo
        {
            TargetHwnd = hwnd,
            TargetTitle = title,
            Edge = edge,
            OffsetX = offsetX,
            OffsetY = offsetY,
            IsTaskbar = isTaskbar
        };

        if (!_trackingTimer.IsEnabled)
        {
            _trackingTimer.Start();
        }

        SnapStateChanged?.Invoke(_currentSnap);
    }

    private static int ClampWithCornerMagnet(int pos, int size, int targetStart, int targetEnd)
    {
        if (Math.Abs(pos - targetStart) <= SnapThresholdPx)
            return targetStart;
        if (Math.Abs((pos + size) - targetEnd) <= SnapThresholdPx)
            return targetEnd - size;
        return pos;
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_currentSnap == null || _isDraggingOverlay)
            return;

        if (hwnd != _currentSnap.TargetHwnd)
            return;

        if (eventType == EVENT_OBJECT_DESTROY && idObject == 0)
        {
            _overlayWindow.Dispatcher.BeginInvoke(DetachTarget);
            return;
        }

        if (eventType == EVENT_SYSTEM_MINIMIZESTART)
        {
            // 스냅된 앱이 최소화되면 작업표시줄 위로 잠시 이동하거나 숨기지 않고 유지
            return;
        }

        if (eventType == EVENT_OBJECT_LOCATIONCHANGE || eventType == EVENT_SYSTEM_MINIMIZEEND)
        {
            FollowSnappedTarget();
        }
    }

    private void FollowSnappedTarget()
    {
        if (_currentSnap == null || _isDraggingOverlay || _isUpdatingPosition)
            return;

        IntPtr targetHwnd = _currentSnap.TargetHwnd;
        if (!IsWindow(targetHwnd))
        {
            DetachTarget();
            return;
        }

        if (IsIconic(targetHwnd))
            return;

        if (!TryGetWindowBounds(targetHwnd, out RECT rect))
            return;

        if (rect.Left == _lastTargetRect.Left &&
            rect.Top == _lastTargetRect.Top &&
            rect.Right == _lastTargetRect.Right &&
            rect.Bottom == _lastTargetRect.Bottom)
        {
            return;
        }

        int prevWidth = _lastTargetRect.Right - _lastTargetRect.Left;
        int newWidth = rect.Right - rect.Left;
        int prevHeight = _lastTargetRect.Bottom - _lastTargetRect.Top;
        int newHeight = rect.Bottom - rect.Top;

        // 우측/하단 경계에 붙어 있었을 경우 창 크기(Resize) 변화도 반영
        if (_currentSnap.Edge == SnapEdge.RightOutside && newWidth != prevWidth)
        {
            _currentSnap.OffsetX += (newWidth - prevWidth);
        }
        else if (_currentSnap.Edge == SnapEdge.BottomOutside || _currentSnap.Edge == SnapEdge.BottomInside)
        {
            if (newHeight != prevHeight)
                _currentSnap.OffsetY += (newHeight - prevHeight);
        }

        _lastTargetRect = rect;

        int newX = rect.Left + _currentSnap.OffsetX;
        int newY = rect.Top + _currentSnap.OffsetY;

        SetOverlayPhysicalPosition(newX, newY);
    }

    private (int Width, int Height) GetOverlayPhysicalSize()
    {
        if (_overlayHwnd != IntPtr.Zero && GetWindowRect(_overlayHwnd, out RECT r))
        {
            return (r.Right - r.Left, r.Bottom - r.Top);
        }

        var source = PresentationSource.FromVisual(_overlayWindow);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        return ((int)(_overlayWindow.ActualWidth * scaleX), (int)(_overlayWindow.ActualHeight * scaleY));
    }

    private void SetOverlayPhysicalPosition(int physicalX, int physicalY)
    {
        _isUpdatingPosition = true;
        try
        {
            var source = PresentationSource.FromVisual(_overlayWindow);
            double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            _overlayWindow.Left = physicalX / scaleX;
            _overlayWindow.Top = physicalY / scaleY;
        }
        finally
        {
            _isUpdatingPosition = false;
        }
    }

    private List<CandidateWindow> EnumerateSnapCandidates()
    {
        var list = new List<CandidateWindow>();

        // 1. 작업표시줄 추가
        IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
        if (taskbarHwnd != IntPtr.Zero && TryGetWindowBounds(taskbarHwnd, out RECT tbRect))
        {
            list.Add(new CandidateWindow(taskbarHwnd, "작업 표시줄", tbRect, true));
        }

        // 2. 일반 최상위 앱 창들 열거
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == _overlayHwnd || hwnd == taskbarHwnd)
                return true;

            if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;

            // Cloaked 창(백그라운드 UWP, 다른 가상 데스크톱 창) 제외
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            int length = GetWindowTextLength(hwnd);
            if (length <= 0)
                return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title) || title == "Program Manager")
                return true;

            if (!TryGetWindowBounds(hwnd, out RECT bounds))
                return true;

            if (bounds.Right - bounds.Left < 100 || bounds.Bottom - bounds.Top < 60)
                return true;

            list.Add(new CandidateWindow(hwnd, title, bounds, false));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static bool TryGetWindowBounds(IntPtr hwnd, out RECT rect)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<RECT>()) == 0)
            return true;

        return GetWindowRect(hwnd, out rect);
    }

    public void Dispose()
    {
        _trackingTimer.Stop();
        if (_locationHook != IntPtr.Zero)
        {
            UnhookWinEvent(_locationHook);
            _locationHook = IntPtr.Zero;
        }
        if (_stateHook != IntPtr.Zero)
        {
            UnhookWinEvent(_stateHook);
            _stateHook = IntPtr.Zero;
        }
    }

    private readonly record struct CandidateWindow(IntPtr Hwnd, string Title, RECT Bounds, bool IsTaskbar);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
