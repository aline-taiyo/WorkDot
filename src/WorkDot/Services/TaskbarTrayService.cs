using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkDot.Services;

public sealed class TaskbarTrayService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private Icon? _currentIcon;
    private IntPtr _currentHIcon;

    public event Action? OpenPortalRequested;
    public event Action? ToggleOverlayVisibilityRequested;
    public event Action? TrayContextMenuRequested;

    public TaskbarTrayService()
    {
        var (initIcon, initHIcon, _) = CreateSharpTechIcon(System.Drawing.Color.FromArgb(255, 239, 68, 68));
        _currentIcon = initIcon;
        _currentHIcon = initHIcon;

        _notifyIcon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = $"WORKDOT // {AppSettingsService.AppVersion}",
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => OpenPortalRequested?.Invoke();
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleOverlayVisibilityRequested?.Invoke();
            }
            else if (e.Button == MouseButtons.Right)
            {
                TrayContextMenuRequested?.Invoke();
            }
        };
    }

    public ImageSource UpdateTrayAndWindowIcon(
        AttendanceSnapshot snapshot,
        System.Windows.Media.Color activeMediaColor)
    {
        bool isWorking = snapshot.Status == WorkStatusKind.Working;
        string stateText = isWorking ? "ON-DUTY" : "OFF-DUTY";

        string tooltip = $"WORKDOT [{stateText}] {snapshot.SubText}";
        _notifyIcon.Text = tooltip.Length > 60 ? tooltip[..60] : tooltip;

        var drawColor = System.Drawing.Color.FromArgb(
            activeMediaColor.A,
            activeMediaColor.R,
            activeMediaColor.G,
            activeMediaColor.B);

        var (newIcon, newHIcon, wpfImage) = CreateSharpTechIcon(drawColor);
        var prevIcon = _currentIcon;
        var prevHIcon = _currentHIcon;

        _currentIcon = newIcon;
        _currentHIcon = newHIcon;
        _notifyIcon.Icon = newIcon;
        _notifyIcon.Visible = true;

        prevIcon?.Dispose();
        if (prevHIcon != IntPtr.Zero)
        {
            DestroyIcon(prevHIcon);
        }

        return wpfImage;
    }

    /// <summary>
    /// Mono Tech Sharp 스타일의 정사각형 기술 인디케이터 트레이 아이콘을 생성합니다.
    /// </summary>
    private static (Icon TrayIcon, IntPtr HIcon, ImageSource WpfIcon) CreateSharpTechIcon(System.Drawing.Color signalColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.None;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(System.Drawing.Color.Transparent);

            // 1. 카본 블랙 정사각 베이스
            using var bgBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 10, 10, 12));
            g.FillRectangle(bgBrush, 2, 2, 28, 28);

            // 2. 외곽 1px 테크니컬 보더 프레임
            using var outerPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(200, signalColor), 2f);
            g.DrawRectangle(outerPen, 3, 3, 26, 26);

            // 3. 중앙 정사각 시그널 코어
            using var coreBrush = new SolidBrush(signalColor);
            g.FillRectangle(coreBrush, 10, 10, 12, 12);
        }

        IntPtr hIcon = bmp.GetHicon();
        Icon icon = Icon.FromHandle(hIcon);

        ImageSource wpfIcon = Imaging.CreateBitmapSourceFromHIcon(
            hIcon,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        wpfIcon.Freeze();

        return (icon, hIcon, wpfIcon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        _currentIcon?.Dispose();
        _currentIcon = null;

        if (_currentHIcon != IntPtr.Zero)
        {
            DestroyIcon(_currentHIcon);
            _currentHIcon = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
