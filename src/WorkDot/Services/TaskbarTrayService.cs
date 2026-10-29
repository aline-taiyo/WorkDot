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
        var (initIcon, initHIcon, _) = CreateModernTrayIcon(System.Drawing.Color.FromArgb(255, 239, 68, 68));
        _currentIcon = initIcon;
        _currentHIcon = initHIcon;

        _notifyIcon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = $"WorkDot {AppSettingsService.AppVersion}",
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
        string stateText = isWorking ? "근무중" : "퇴근";

        string tooltip = $"WorkDot ({stateText}) · {snapshot.SubText}";
        _notifyIcon.Text = tooltip.Length > 60 ? tooltip[..60] : tooltip;

        var drawColor = System.Drawing.Color.FromArgb(
            activeMediaColor.A,
            activeMediaColor.R,
            activeMediaColor.G,
            activeMediaColor.B);

        var (newIcon, newHIcon, wpfImage) = CreateModernTrayIcon(drawColor);
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

    private static (Icon TrayIcon, IntPtr HIcon, ImageSource WpfIcon) CreateModernTrayIcon(System.Drawing.Color signalColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            using var path = CreateRoundedRectPath(new Rectangle(2, 2, 28, 28), 8);
            using var bgBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 9, 9, 11));
            g.FillPath(bgBrush, path);

            using var borderPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 39, 39, 42), 1.5f);
            g.DrawPath(borderPen, path);

            using var haloBrush = new SolidBrush(System.Drawing.Color.FromArgb(55, signalColor));
            g.FillEllipse(haloBrush, 7, 7, 18, 18);

            using var coreBrush = new SolidBrush(signalColor);
            g.FillEllipse(coreBrush, 10, 10, 12, 12);
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

    private static GraphicsPath CreateRoundedRectPath(Rectangle bounds, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
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
