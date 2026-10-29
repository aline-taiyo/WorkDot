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
    public bool IsTaskbar { get; init; }
}

/// <summary>
/// 일반 애플리케이션 창은 제외하고 오직 Windows 화면(모니터/작업 표시줄) 경계에만 자석 스냅을 수행합니다.
/// </summary>
public sealed class Win32SnapService : IDisposable
{
    private const int SnapThresholdPx = 22;
    private const int ShadowInsetDip = 8; // RootContainer Margin(14) 중 화면 테두리 여백 보정치
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
                snapBottom: true);
            return;
        }

        var workArea = SystemParameters.WorkArea;
        _overlayWindow.Left = workArea.Right - _overlayWindow.ActualWidth + ShadowInsetDip;
        _overlayWindow.Top = workArea.Bottom - _overlayWindow.ActualHeight + ShadowInsetDip;
        AttachToBoundary("윈도우 경계", SnapEdge.BottomInside, false, true, false, true);
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

        int myWidth = myRect.Right - myRect.Left;
        int myHeight = myRect.Bottom - myRect.Top;

        int visualLeft = myRect.Left + insetX;
        int visualRight = myRect.Right - insetX;
        int visualTop = myRect.Top + insetY;
        int visualBottom = myRect.Bottom - insetY;

        int snappedX = myRect.Left;
        int snappedY = myRect.Top;

        bool snapLeft = false;
        bool snapRight = false;
        bool snapTop = false;
        bool snapBottom = false;

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
        }
        if (distLeftMon <= SnapThresholdPx && distLeftMon < bestHorizDist)
        {
            bestHorizDist = distLeftMon;
            snappedX = mon.Left - insetX;
            snapLeft = true;
            snapRight = false;
        }
        if (distRightWork <= SnapThresholdPx && distRightWork < bestHorizDist)
        {
            bestHorizDist = distRightWork;
            snappedX = work.Right - myWidth + insetX;
            snapRight = true;
            snapLeft = false;
        }
        if (distRightMon <= SnapThresholdPx && distRightMon < bestHorizDist)
        {
            bestHorizDist = distRightMon;
            snappedX = mon.Right - myWidth + insetX;
            snapRight = true;
            snapLeft = false;
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
        }
        if (distTopMon <= SnapThresholdPx && distTopMon < bestVertDist)
        {
            bestVertDist = distTopMon;
            snappedY = mon.Top - insetY;
            snapTop = true;
            snapBottom = false;
        }
        if (distBottomWork <= SnapThresholdPx && distBottomWork < bestVertDist)
        {
            bestVertDist = distBottomWork;
            snappedY = work.Bottom - myHeight + insetY;
            snapBottom = true;
            snapTop = false;
        }
        if (distBottomMon <= SnapThresholdPx && distBottomMon < bestVertDist)
        {
            bestVertDist = distBottomMon;
            snappedY = mon.Bottom - myHeight + insetY;
            snapBottom = true;
            snapTop = false;
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

            AttachToBoundary("윈도우 경계", edge, snapLeft, snapRight, snapTop, snapBottom);
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
        bool snapBottom)
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
            IsTaskbar = true
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
        // No unmanaged hooks required for screen-edge-only snapping
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
}
