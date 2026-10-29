using System.Drawing;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WorkDot.Services;

public sealed class TaskbarTrayService : IDisposable
{
    private static readonly Uri AppIconPackUri =
        new("pack://application:,,,/WorkDot;component/Assets/Icons/workdot.ico", UriKind.Absolute);

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _appTrayIcon;
    private readonly ImageSource _wpfAppIcon;

    public event Action? OpenPortalRequested;
    public event Action? ToggleOverlayVisibilityRequested;
    public event Action? TrayContextMenuRequested;

    public TaskbarTrayService()
    {
        _appTrayIcon = LoadEmbeddedAppIcon();
        _wpfAppIcon = LoadWpfAppIcon();

        _notifyIcon = new NotifyIcon
        {
            Icon = _appTrayIcon,
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
        _notifyIcon.Icon = _appTrayIcon;
        _notifyIcon.Visible = true;

        return _wpfAppIcon;
    }

    private static Icon LoadEmbeddedAppIcon()
    {
        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(AppIconPackUri);
            if (streamInfo?.Stream != null)
            {
                using var s = streamInfo.Stream;
                return new Icon(s, new System.Drawing.Size(32, 32));
            }
        }
        catch
        {
            // Fallback below
        }

        string exePath = Environment.ProcessPath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var extracted = Icon.ExtractAssociatedIcon(exePath);
            if (extracted != null)
                return extracted;
        }

        return SystemIcons.Application;
    }

    private static ImageSource LoadWpfAppIcon()
    {
        var frame = BitmapFrame.Create(AppIconPackUri);
        frame.Freeze();
        return frame;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _appTrayIcon.Dispose();
    }
}
