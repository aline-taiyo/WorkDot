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
/// 일반 애플리케이션 창은 제외하고 오직 Windows 화면(모니터/작업 표시줄) 경계에만 자석 스냅을 수행합니다.
/// </summary>
public sealed class Win32SnapService : IDisposable
{
    private const int SnapThresholdPx = 24;
    private const int ShadowInsetDip = 0; // 그림자 제거로 창 크기와 위젯 외곽선이 1:1 일치
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private readonly Window _overlayWindow;
    private IntPtr _overlayHwnd;
    private SnapTargetInfo? _currentSnap;
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
            // Windows 11 DWM 사각 테두리 및 기본 그림자 완전 비활성화
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

        if (targetX != myRect.Left || targetY != myRect.Top)
        {
            SetOverlayPhysicalPosition(targetX, targetY);
        }
    }

    /// <summary>
    /// 오직 모니터 작업 영역(WorkArea) 및 화면(Monitor) 테두리 경계에만 자석 스냅을 적용합니다.
    /// </summary>
    public void EvaluateMagneticSnap(bool applyPosition = true)
    {
        if (_overlayHwnd == IntPtr.Zero || _isUpdatingPosition)
            return;

        if (!GetWindowRect(_overlayHwnd, out RECT myRect))
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
            myWidth = myRect.Right - myRect.Left;
            myHeight = myRect.Bottom - myRect.Top;
        }

        int visualLeft = myRect.Left + insetX;
        int visualRight = myRect.Left + myWidth - insetX;
        int visualTop = myRect.Top + insetY;
        int visualBottom = myRect.Top + myHeight - insetY;

        int snappedX = myRect.Left;
        int snappedY = myRect.Top;

        bool snapLeft = false;
        bool snapRight = false;
        bool snapTop = false;
        bool snapBottom = false;
        bool useMonHoriz = false;
        bool useMonVert = false;

        RECT work = mi.rcWork;
        RECT mon = mi.rcMonitor;

        // 1. 좌/우측 윈도우 화면 경계 스냅 검사
        int distLeftWork = Math.Abs(visualLeft - work.Left);
        int distLeftMon = Math.Abs(visualLeft - mon.Left);
        int distRightWork = Math.Abs(visualRight - work.Right);
        int distRightMon = Math.Abs(visualRight - mon.Right);

        int bestHorizDist = SnapThresholdPx + 1;
        if (distLeftWork <= SnapThresholdPx && distLeftWork < bestHorizDist)
        {
            bestHorizDist = distLeftWork;
            snappedX = work.Left - insetX;
            snapLeft = true;
            snapRight = false;
            useMonHoriz = false;
        }
        if (distLeftMon <= SnapThresholdPx && distLeftMon < bestHorizDist)
        {
            bestHorizDist = distLeftMon;
            snappedX = mon.Left - insetX;
            snapLeft = true;
            snapRight = false;
            useMonHoriz = true;
        }
        if (distRightWork <= SnapThresholdPx && distRightWork < bestHorizDist)
        {
            bestHorizDist = distRightWork;
            snappedX = work.Right - myWidth + insetX;
            snapRight = true;
            snapLeft = false;
            useMonHoriz = false;
        }
        if (distRightMon <= SnapThresholdPx && distRightMon < bestHorizDist)
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

        int bestVertDist = SnapThresholdPx + 1;
        if (distTopWork <= SnapThresholdPx && distTopWork < bestVertDist)
        {
            bestVertDist = distTopWork;
            snappedY = work.Top - insetY;
            snapTop = true;
            snapBottom = false;
            useMonVert = false;
        }
        if (distTopMon <= SnapThresholdPx && distTopMon < bestVertDist)
        {
            bestVertDist = distTopMon;
            snappedY = mon.Top - insetY;
            snapTop = true;
            snapBottom = false;
            useMonVert = true;
        }
        if (distBottomWork <= SnapThresholdPx && distBottomWork < bestVertDist)
        {
            bestVertDist = distBottomWork;
            snappedY = work.Bottom - myHeight + insetY;
            snapBottom = true;
            snapTop = false;
            useMonVert = false;
        }
        if (distBottomMon <= SnapThresholdPx && distBottomMon < bestVertDist)
        {
            bestVertDist = distBottomMon;
            snappedY = mon.Bottom - myHeight + insetY;
            snapBottom = true;
            snapTop = false;
            useMonVert = true;
        }

        if (snapLeft || snapRight || snapTop || snapBottom)
        {
            if (applyPosition)
            {
                SetOverlayPhysicalPosition(snappedX, snappedY);
            }

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

        SnapStateChanged?.Invoke(_currentSnap);
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
        if (_overlayHwnd != IntPtr.Zero && GetWindowRect(_overlayHwnd, out RECT r))
        {
            return (r.Right - r.Left, r.Bottom - r.Top);
        }

        var (scaleX, scaleY) = GetDpiScale();
        return ((int)(_overlayWindow.ActualWidth * scaleX), (int)(_overlayWindow.ActualHeight * scaleY));
    }

    private void SetOverlayPhysicalPosition(int physicalX, int physicalY)
    {
        _isUpdatingPosition = true;
        try
        {
            var (scaleX, scaleY) = GetDpiScale();
            _overlayWindow.Left = physicalX / scaleX;
            _overlayWindow.Top = physicalY / scaleY;
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
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint attrValue, int attrSize);
}
