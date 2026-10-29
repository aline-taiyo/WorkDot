using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

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
    public bool SnapLeft { get; init; }
    public bool SnapRight { get; init; }
    public bool SnapTop { get; init; }
    public bool SnapBottom { get; init; }
    public bool UseMonitorBoundsHoriz { get; init; }
    public bool UseMonitorBoundsVert { get; init; }
}

/// <summary>
/// 일반 애플리케이션 창은 제외하고 오직 Windows 화면(모니터/작업 표시줄) 경계에만
/// 히스테리시스(진입/이탈 버퍼) 및 물리 커서 좌표(GetCursorPos) 기반 단일 패스 이동을 적용하여
/// 경계 근처 마우스 이동 시 떨림(Jitter)을 완전히 방지합니다.
/// </summary>
public sealed class Win32SnapService : IDisposable
{
    private const int SnapInThresholdPx = 16;   // 경계 진입 자석 거리 (px)
    private const int SnapOutThresholdPx = 38;  // 경계 이탈 유지 버퍼 (px) - 경계 근처 마우스 이동 시 떨림 방지
    private const int DragStartThresholdPx = 5; // 드래그 시작 판정 최소 이동 거리 (px)
    private const int ShadowInsetDip = 0;       // 창 크기와 위젯 외곽선이 1:1 일치

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private readonly Window _overlayWindow;
    private IntPtr _overlayHwnd;
    private SnapTargetInfo? _currentSnap;
    private bool _isDraggingOverlay;
    private bool _isUpdatingPosition;
    private int _lastAppliedPhysicalX = int.MinValue;
    private int _lastAppliedPhysicalY = int.MinValue;

    private POINT _dragStartCursorPx;
    private int _dragStartWindowLeftPx;
    private int _dragStartWindowTopPx;
    private double _dragAnchorRatioX;

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
    }

    public void Initialize()
    {
        _overlayHwnd = new WindowInteropHelper(_overlayWindow).Handle;
        DisableSystemWindowBorder(_overlayHwnd);
    }

    private static void DisableSystemWindowBorder(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            int ncPolicy = 1; // DWMNCRP_DISABLED
            DwmSetWindowAttribute(hwnd, 2, ref ncPolicy, sizeof(int));

            uint borderNone = 0xFFFFFFFE; // DWMWA_COLOR_NONE
            DwmSetWindowAttribute(hwnd, 34, ref borderNone, sizeof(uint));
        }
        catch
        {
            // 구버전 Windows 호환 무시
        }
    }

    /// <summary>
    /// 드래그 시작 시점의 물리 커서 위치와 창 위치를 기록합니다.
    /// </summary>
    public void BeginDrag()
    {
        if (_overlayHwnd == IntPtr.Zero)
        {
            _overlayHwnd = new WindowInteropHelper(_overlayWindow).Handle;
        }

        GetCursorPos(out _dragStartCursorPx);
        if (GetWindowRect(_overlayHwnd, out RECT r))
        {
            _dragStartWindowLeftPx = r.Left;
            _dragStartWindowTopPx = r.Top;
            int w = Math.Max(1, r.Right - r.Left);
            _dragAnchorRatioX = Math.Clamp((double)(_dragStartCursorPx.X - r.Left) / w, 0.0, 1.0);
        }
        else
        {
            var (scaleX, scaleY) = GetDpiScale();
            _dragStartWindowLeftPx = (int)Math.Round(_overlayWindow.Left * scaleX);
            _dragStartWindowTopPx = (int)Math.Round(_overlayWindow.Top * scaleY);
            _dragAnchorRatioX = 0.5;
        }

        _isDraggingOverlay = true;
    }

    /// <summary>
    /// 드래그 시작 직후 위젯이 미니모드로 축소되었을 때 커서가 위젯 중심/내부에 자연스럽게 고정되도록 시작 좌표를 보정합니다.
    /// </summary>
    public void AdjustDragOriginAfterMiniCollapse()
    {
        var (miniW, _) = GetOverlayPhysicalSize();
        if (miniW <= 0)
            return;

        // 커서 X가 축소된 미니 위젯 범위를 벗어나 있으면 커서 위치를 기준으로 시작 X를 자연스럽게 맞춤
        if (_dragStartCursorPx.X < _dragStartWindowLeftPx || _dragStartCursorPx.X > _dragStartWindowLeftPx + miniW)
        {
            int offsetInsideMini = (int)Math.Round(miniW * Math.Clamp(_dragAnchorRatioX, 0.2, 0.8));
            _dragStartWindowLeftPx = _dragStartCursorPx.X - offsetInsideMini;
        }
    }

    /// <summary>
    /// 물리 커서 이동량이 드래그 시작 임계값을 넘었는지 확인합니다.
    /// </summary>
    public bool HasExceededDragThreshold()
    {
        if (!GetCursorPos(out POINT now))
            return false;

        int dx = now.X - _dragStartCursorPx.X;
        int dy = now.Y - _dragStartCursorPx.Y;
        return Math.Abs(dx) >= DragStartThresholdPx || Math.Abs(dy) >= DragStartThresholdPx;
    }

    /// <summary>
    /// 현재 물리 마우스 커서가 위젯 창 내부에 위치하는지 정확히 판정합니다.
    /// 마우스 캡처 중에도 화면 물리 좌표(GetCursorPos, GetWindowRect)로 판정하므로 오차가 없습니다.
    /// </summary>
    public bool IsCursorInsideWindow()
    {
        if (_overlayHwnd == IntPtr.Zero)
        {
            _overlayHwnd = new WindowInteropHelper(_overlayWindow).Handle;
        }

        if (_overlayHwnd == IntPtr.Zero)
            return _overlayWindow.IsMouseOver;

        if (!GetCursorPos(out POINT pt) || !GetWindowRect(_overlayHwnd, out RECT rect))
            return _overlayWindow.IsMouseOver;

        return pt.X >= rect.Left && pt.X <= rect.Right &&
               pt.Y >= rect.Top && pt.Y <= rect.Bottom;
    }

    /// <summary>
    /// 물리 커서 좌표(GetCursorPos)를 기반으로 드래그 중 위치를 계산하고 히스테리시스 스냅을 단 1회만 적용합니다.
    /// 창 자신의 이동에 영향을 받지 않으므로 경계 근처에서 마우스를 움직여도 떨림이 발생하지 않습니다.
    /// </summary>
    public void UpdateDragPositionFromCursor()
    {
        if (_overlayHwnd == IntPtr.Zero || _isUpdatingPosition)
            return;

        if (!GetCursorPos(out POINT now))
            return;

        int rawPhysX = _dragStartWindowLeftPx + (now.X - _dragStartCursorPx.X);
        int rawPhysY = _dragStartWindowTopPx + (now.Y - _dragStartCursorPx.Y);

        EvaluateAndApplyCore(rawPhysX, rawPhysY, applyPosition: true);
    }

    /// <summary>
    /// 작업표시줄 위(화면 우측 하단 경계) 기본 스냅 위치로 배치합니다.
    /// </summary>
    public void SnapToTaskbarDefault()
    {
        if (_overlayHwnd == IntPtr.Zero)
        {
            _overlayHwnd = new WindowInteropHelper(_overlayWindow).Handle;
        }

        var (scaleX, scaleY) = GetDpiScale();
        int insetX = (int)Math.Round(ShadowInsetDip * scaleX);
        int insetY = (int)Math.Round(ShadowInsetDip * scaleY);
        var (overlayW, overlayH) = GetOverlayPhysicalSize();

        if (TryGetCurrentMonitorInfo(out MONITORINFO mi))
        {
            int targetX = mi.rcWork.Right - overlayW + insetX;
            int targetY = mi.rcWork.Bottom - overlayH + insetY;
            SetOverlayPhysicalPosition(targetX, targetY);

            AttachToBoundary(
                title: "윈도우 경계",
                edge: SnapEdge.BottomInside,
                snapLeft: false,
                snapRight: true,
                snapTop: false,
                snapBottom: true,
                useMonHoriz: false,
                useMonVert: false);
            return;
        }

        var workArea = SystemParameters.WorkArea;
        _overlayWindow.Left = workArea.Right - _overlayWindow.ActualWidth + ShadowInsetDip;
        _overlayWindow.Top = workArea.Bottom - _overlayWindow.ActualHeight + ShadowInsetDip;
        AttachToBoundary("윈도우 경계", SnapEdge.BottomInside, false, true, false, true, false, false);
    }

    /// <summary>
    /// 위젯 크기가 호버 확장/미니 모드로 변경될 때 기존 스냅된 화면 경계(우측/하단 등) 정렬을 흔들림 없이 유지합니다.
    /// </summary>
    public void AlignToSnappedEdges()
    {
        if (_currentSnap == null || _overlayHwnd == IntPtr.Zero || _isUpdatingPosition || _isDraggingOverlay)
            return;

        if (!TryGetCurrentMonitorInfo(out MONITORINFO mi))
            return;

        var (scaleX, scaleY) = GetDpiScale();
        int insetX = (int)Math.Round(ShadowInsetDip * scaleX);
        int insetY = (int)Math.Round(ShadowInsetDip * scaleY);

        int myWidth = (int)Math.Round(_overlayWindow.ActualWidth * scaleX);
        int myHeight = (int)Math.Round(_overlayWindow.ActualHeight * scaleY);
        if (myWidth <= 0 || myHeight <= 0)
        {
            (myWidth, myHeight) = GetOverlayPhysicalSize();
        }

        if (!GetWindowRect(_overlayHwnd, out RECT myRect))
            return;

        int targetX = myRect.Left;
        int targetY = myRect.Top;

        RECT horizRef = _currentSnap.UseMonitorBoundsHoriz ? mi.rcMonitor : mi.rcWork;
        RECT vertRef = _currentSnap.UseMonitorBoundsVert ? mi.rcMonitor : mi.rcWork;

        if (_currentSnap.SnapRight)
        {
            targetX = horizRef.Right - myWidth + insetX;
        }
        else if (_currentSnap.SnapLeft)
        {
            targetX = horizRef.Left - insetX;
        }

        if (_currentSnap.SnapBottom)
        {
            targetY = vertRef.Bottom - myHeight + insetY;
        }
        else if (_currentSnap.SnapTop)
        {
            targetY = vertRef.Top - insetY;
        }

        if (Math.Abs(targetX - myRect.Left) >= 1 || Math.Abs(targetY - myRect.Top) >= 1)
        {
            SetOverlayPhysicalPosition(targetX, targetY);
        }
    }

    public void EvaluateMagneticSnap(bool applyPosition = true)
    {
        if (_overlayHwnd == IntPtr.Zero || _isUpdatingPosition)
            return;

        if (!GetWindowRect(_overlayHwnd, out RECT myRect))
            return;

        EvaluateAndApplyCore(myRect.Left, myRect.Top, applyPosition);
    }

    private void EvaluateAndApplyCore(int candidateLeftPx, int candidateTopPx, bool applyPosition)
    {
        if (!TryGetCurrentMonitorInfo(out MONITORINFO mi))
            return;

        var (scaleX, scaleY) = GetDpiScale();
        int insetX = (int)Math.Round(ShadowInsetDip * scaleX);
        int insetY = (int)Math.Round(ShadowInsetDip * scaleY);

        int myWidth = (int)Math.Round(_overlayWindow.ActualWidth * scaleX);
        int myHeight = (int)Math.Round(_overlayWindow.ActualHeight * scaleY);
        if (myWidth <= 0 || myHeight <= 0)
        {
            (myWidth, myHeight) = GetOverlayPhysicalSize();
        }

        int visualLeft = candidateLeftPx + insetX;
        int visualRight = candidateLeftPx + myWidth - insetX;
        int visualTop = candidateTopPx + insetY;
        int visualBottom = candidateTopPx + myHeight - insetY;

        int snappedX = candidateLeftPx;
        int snappedY = candidateTopPx;

        bool snapLeft = false;
        bool snapRight = false;
        bool snapTop = false;
        bool snapBottom = false;
        bool useMonHoriz = false;
        bool useMonVert = false;

        RECT work = mi.rcWork;
        RECT mon = mi.rcMonitor;

        // 이미 스냅된 축은 SnapOutThresholdPx(38px)까지 유지하고, 새로 붙을 때는 SnapInThresholdPx(16px) 이내일 때만 스냅
        int threshLeft = (_currentSnap?.SnapLeft == true) ? SnapOutThresholdPx : SnapInThresholdPx;
        int threshRight = (_currentSnap?.SnapRight == true) ? SnapOutThresholdPx : SnapInThresholdPx;
        int threshTop = (_currentSnap?.SnapTop == true) ? SnapOutThresholdPx : SnapInThresholdPx;
        int threshBottom = (_currentSnap?.SnapBottom == true) ? SnapOutThresholdPx : SnapInThresholdPx;

        // 1. 좌/우측 윈도우 화면 경계 스냅 검사
        int distLeftWork = Math.Abs(visualLeft - work.Left);
        int distLeftMon = Math.Abs(visualLeft - mon.Left);
        int distRightWork = Math.Abs(visualRight - work.Right);
        int distRightMon = Math.Abs(visualRight - mon.Right);

        int bestHorizDist = int.MaxValue;
        if (distLeftWork <= threshLeft && distLeftWork < bestHorizDist)
        {
            bestHorizDist = distLeftWork;
            snappedX = work.Left - insetX;
            snapLeft = true;
            snapRight = false;
            useMonHoriz = false;
        }
        if (distLeftMon <= threshLeft && distLeftMon < bestHorizDist)
        {
            bestHorizDist = distLeftMon;
            snappedX = mon.Left - insetX;
            snapLeft = true;
            snapRight = false;
            useMonHoriz = true;
        }
        if (distRightWork <= threshRight && distRightWork < bestHorizDist)
        {
            bestHorizDist = distRightWork;
            snappedX = work.Right - myWidth + insetX;
            snapRight = true;
            snapLeft = false;
            useMonHoriz = false;
        }
        if (distRightMon <= threshRight && distRightMon < bestHorizDist)
        {
            bestHorizDist = distRightMon;
            snappedX = mon.Right - myWidth + insetX;
            snapRight = true;
            snapLeft = false;
            useMonHoriz = true;
        }

        // 2. 상/하단 윈도우 화면 및 작업 표시줄 경계 스냅 검사
        int distTopWork = Math.Abs(visualTop - work.Top);
        int distTopMon = Math.Abs(visualTop - mon.Top);
        int distBottomWork = Math.Abs(visualBottom - work.Bottom);
        int distBottomMon = Math.Abs(visualBottom - mon.Bottom);

        int bestVertDist = int.MaxValue;
        if (distTopWork <= threshTop && distTopWork < bestVertDist)
        {
            bestVertDist = distTopWork;
            snappedY = work.Top - insetY;
            snapTop = true;
            snapBottom = false;
            useMonVert = false;
        }
        if (distTopMon <= threshTop && distTopMon < bestVertDist)
        {
            bestVertDist = distTopMon;
            snappedY = mon.Top - insetY;
            snapTop = true;
            snapBottom = false;
            useMonVert = true;
        }
        if (distBottomWork <= threshBottom && distBottomWork < bestVertDist)
        {
            bestVertDist = distBottomWork;
            snappedY = work.Bottom - myHeight + insetY;
            snapBottom = true;
            snapTop = false;
            useMonVert = false;
        }
        if (distBottomMon <= threshBottom && distBottomMon < bestVertDist)
        {
            bestVertDist = distBottomMon;
            snappedY = mon.Bottom - myHeight + insetY;
            snapBottom = true;
            snapTop = false;
            useMonVert = true;
        }

        if (applyPosition)
        {
            SetOverlayPhysicalPosition(snappedX, snappedY);
        }

        if (snapLeft || snapRight || snapTop || snapBottom)
        {
            SnapEdge edge = snapBottom ? SnapEdge.BottomInside
                : snapTop ? SnapEdge.TopInside
                : snapRight ? SnapEdge.RightOutside
                : SnapEdge.LeftOutside;

            AttachToBoundary("윈도우 경계", edge, snapLeft, snapRight, snapTop, snapBottom, useMonHoriz, useMonVert);
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
            SnapStateChanged?.Invoke(null);
        }
    }

    private void AttachToBoundary(
        string title,
        SnapEdge edge,
        bool snapLeft,
        bool snapRight,
        bool snapTop,
        bool snapBottom,
        bool useMonHoriz,
        bool useMonVert)
    {
        bool changed = _currentSnap == null ||
                       _currentSnap.SnapLeft != snapLeft ||
                       _currentSnap.SnapRight != snapRight ||
                       _currentSnap.SnapTop != snapTop ||
                       _currentSnap.SnapBottom != snapBottom;

        _currentSnap = new SnapTargetInfo
        {
            TargetHwnd = _overlayHwnd,
            TargetTitle = title,
            Edge = edge,
            SnapLeft = snapLeft,
            SnapRight = snapRight,
            SnapTop = snapTop,
            SnapBottom = snapBottom,
            UseMonitorBoundsHoriz = useMonHoriz,
            UseMonitorBoundsVert = useMonVert
        };

        if (changed)
        {
            SnapStateChanged?.Invoke(_currentSnap);
        }
    }

    private bool TryGetCurrentMonitorInfo(out MONITORINFO mi)
    {
        mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        IntPtr hMonitor = MonitorFromWindow(_overlayHwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero)
            return false;

        return GetMonitorInfo(hMonitor, ref mi);
    }

    private (double ScaleX, double ScaleY) GetDpiScale()
    {
        var source = PresentationSource.FromVisual(_overlayWindow);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        return (scaleX, scaleY);
    }

    private (int Width, int Height) GetOverlayPhysicalSize()
    {
        var (scaleX, scaleY) = GetDpiScale();
        int w = (int)Math.Round(_overlayWindow.ActualWidth * scaleX);
        int h = (int)Math.Round(_overlayWindow.ActualHeight * scaleY);
        if (w > 0 && h > 0)
        {
            return (w, h);
        }

        if (_overlayHwnd != IntPtr.Zero && GetWindowRect(_overlayHwnd, out RECT r))
        {
            return (r.Right - r.Left, r.Bottom - r.Top);
        }

        return (w, h);
    }

    private void SetOverlayPhysicalPosition(int physicalX, int physicalY)
    {
        if (physicalX == _lastAppliedPhysicalX && physicalY == _lastAppliedPhysicalY)
            return;

        _isUpdatingPosition = true;
        try
        {
            _lastAppliedPhysicalX = physicalX;
            _lastAppliedPhysicalY = physicalY;

            if (_overlayHwnd != IntPtr.Zero)
            {
                // X, Y를 단일 Win32 호출로 원자적으로 이동하여 중간 프레임 대각선 떨림을 제거
                SetWindowPos(_overlayHwnd, IntPtr.Zero, physicalX, physicalY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            else
            {
                var (scaleX, scaleY) = GetDpiScale();
                _overlayWindow.Left = physicalX / scaleX;
                _overlayWindow.Top = physicalY / scaleY;
            }
        }
        finally
        {
            _isUpdatingPosition = false;
        }
    }

    public void Dispose()
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint attrValue, int attrSize);
}
