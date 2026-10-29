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
    private readonly ContextMenuStrip _trayMenu;
    private Icon? _currentIcon;
    private IntPtr _currentHIcon;

    private readonly ToolStripMenuItem _statusHeaderItem;
    private readonly ToolStripMenuItem _clockActionItem;
    private readonly ToolStripMenuItem _toggleOverlayVisibleItem;
    private readonly ToolStripMenuItem _alwaysOnTopItem;
    private readonly ToolStripMenuItem _showInTaskbarItem;
    private readonly ToolStripMenuItem _runAtStartupItem;

    public event Action? ClockActionRequested;
    public event Action? OpenPortalRequested;
    public event Action? RefreshRequested;
    public event Action? OpenSettingsRequested;
    public event Action? ToggleOverlayVisibilityRequested;
    public event Action? SnapToTaskbarRequested;
    public event Action? ToggleMiniModeRequested;
    public event Action<WorkStatusKind>? ManualStatusRequested;
    public event Action<double>? ScaleRequested;
    public event Action<WidgetThemeMode>? ThemeRequested;
    public event Action<bool>? AlwaysOnTopChanged;
    public event Action<bool>? ShowInTaskbarChanged;
    public event Action<bool>? RunAtStartupChanged;
    public event Action? ExitRequested;

    public TaskbarTrayService()
    {
        _trayMenu = new ContextMenuStrip();

        // 1. 상태 헤더
        _statusHeaderItem = new ToolStripMenuItem($"WorkDot {AppSettingsService.AppVersion} · 연동 대기")
        {
            Enabled = false,
            Font = new Font(_trayMenu.Font, System.Drawing.FontStyle.Bold)
        };
        _trayMenu.Items.Add(_statusHeaderItem);
        _trayMenu.Items.Add(new ToolStripSeparator());

        // 2. 출근 / 퇴근 원클릭 액션 & 다우오피스 포털 열기
        _clockActionItem = new ToolStripMenuItem("🟢 지금 출근 처리하기", null, (_, _) => ClockActionRequested?.Invoke())
        {
            Font = new Font(_trayMenu.Font, System.Drawing.FontStyle.Bold)
        };
        _trayMenu.Items.Add(_clockActionItem);

        _trayMenu.Items.Add(new ToolStripMenuItem("🌐 다우오피스 근태/로그인 창 열기", null, (_, _) => OpenPortalRequested?.Invoke()));
        _trayMenu.Items.Add(new ToolStripMenuItem("🔄 지금 상태 새로고침", null, (_, _) => RefreshRequested?.Invoke()));
        _trayMenu.Items.Add(new ToolStripSeparator());

        // 3. 위젯 표시 / 위치 / 크기 / 테마 제어
        _toggleOverlayVisibleItem = new ToolStripMenuItem("👁️ 오버레이 위젯 숨기기/보이기", null, (_, _) => ToggleOverlayVisibilityRequested?.Invoke());
        _trayMenu.Items.Add(_toggleOverlayVisibleItem);
        _trayMenu.Items.Add(new ToolStripMenuItem("🧲 작업 표시줄 위로 스냅 이동", null, (_, _) => SnapToTaskbarRequested?.Invoke()));
        _trayMenu.Items.Add(new ToolStripMenuItem("✨ 컴팩트 / 미니 점(Dot) 모드 전환", null, (_, _) => ToggleMiniModeRequested?.Invoke()));

        // 크기 서브메뉴
        var scaleMenu = new ToolStripMenuItem("📐 위젯 크기 변경");
        scaleMenu.DropDownItems.Add(new ToolStripMenuItem("작게 (80%)", null, (_, _) => ScaleRequested?.Invoke(0.8)));
        scaleMenu.DropDownItems.Add(new ToolStripMenuItem("기본 (100%)", null, (_, _) => ScaleRequested?.Invoke(1.0)));
        scaleMenu.DropDownItems.Add(new ToolStripMenuItem("크게 (120%)", null, (_, _) => ScaleRequested?.Invoke(1.2)));
        scaleMenu.DropDownItems.Add(new ToolStripMenuItem("아주 크게 (140%)", null, (_, _) => ScaleRequested?.Invoke(1.4)));
        _trayMenu.Items.Add(scaleMenu);

        // 테마 서브메뉴
        var themeMenu = new ToolStripMenuItem("🎨 위젯 배경 테마");
        themeMenu.DropDownItems.Add(new ToolStripMenuItem("윈도우 시스템 설정 따름", null, (_, _) => ThemeRequested?.Invoke(WidgetThemeMode.System)));
        themeMenu.DropDownItems.Add(new ToolStripMenuItem("다크 모드", null, (_, _) => ThemeRequested?.Invoke(WidgetThemeMode.Dark)));
        themeMenu.DropDownItems.Add(new ToolStripMenuItem("라이트 모드", null, (_, _) => ThemeRequested?.Invoke(WidgetThemeMode.Light)));
        _trayMenu.Items.Add(themeMenu);

        // 상태 강제 전환 서브메뉴
        var manualMenu = new ToolStripMenuItem("⚡ 상태 수동 전환 (테스트/임시)");
        manualMenu.DropDownItems.Add(new ToolStripMenuItem("🟢 근무중 상태로 표시", null, (_, _) => ManualStatusRequested?.Invoke(WorkStatusKind.Working)));
        manualMenu.DropDownItems.Add(new ToolStripMenuItem("🔴 퇴근 상태로 표시", null, (_, _) => ManualStatusRequested?.Invoke(WorkStatusKind.OffWork)));
        _trayMenu.Items.Add(manualMenu);

        _trayMenu.Items.Add(new ToolStripSeparator());

        // 4. 창 동작 옵션 토글
        _alwaysOnTopItem = new ToolStripMenuItem("📌 항상 위에 표시 (Topmost)");
        _alwaysOnTopItem.Click += (_, _) => AlwaysOnTopChanged?.Invoke(!_alwaysOnTopItem.Checked);
        _trayMenu.Items.Add(_alwaysOnTopItem);

        _showInTaskbarItem = new ToolStripMenuItem("🪟 작업 표시줄에 앱 아이콘 표시");
        _showInTaskbarItem.Click += (_, _) => ShowInTaskbarChanged?.Invoke(!_showInTaskbarItem.Checked);
        _trayMenu.Items.Add(_showInTaskbarItem);

        _runAtStartupItem = new ToolStripMenuItem("🚀 Windows 시작 시 자동 실행");
        _runAtStartupItem.Click += (_, _) => RunAtStartupChanged?.Invoke(!_runAtStartupItem.Checked);
        _trayMenu.Items.Add(_runAtStartupItem);

        _trayMenu.Items.Add(new ToolStripSeparator());

        // 5. 환경설정 & 종료
        _trayMenu.Items.Add(new ToolStripMenuItem("⚙️ 환경설정 열기...", null, (_, _) => OpenSettingsRequested?.Invoke()));
        _trayMenu.Items.Add(new ToolStripMenuItem("❌ WorkDot 종료", null, (_, _) => ExitRequested?.Invoke()));

        // 초기 아이콘을 먼저 할당한 후 Visible = true 설정 (WinForms Shell_NotifyIcon 누락 방지)
        var (initIcon, initHIcon, _) = CreateDynamicDotIcon(System.Drawing.Color.FromArgb(255, 239, 68, 68));
        _currentIcon = initIcon;
        _currentHIcon = initHIcon;

        _notifyIcon = new NotifyIcon
        {
            Icon = _currentIcon,
            Text = $"WorkDot {AppSettingsService.AppVersion}",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => OpenPortalRequested?.Invoke();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleOverlayVisibilityRequested?.Invoke();
            }
        };
    }

    public ImageSource UpdateTrayAndWindowIcon(
        AttendanceSnapshot snapshot,
        AppSettings settings,
        System.Windows.Media.Color activeMediaColor,
        bool isOverlayVisible)
    {
        bool isWorking = snapshot.Status == WorkStatusKind.Working;
        string stateText = isWorking ? "근무중" : "퇴근";

        _statusHeaderItem.Text = $"{(isWorking ? "🟢" : "🔴")} WorkDot {AppSettingsService.AppVersion} · {stateText} ({snapshot.SubText})";

        if (snapshot.Status == WorkStatusKind.NeedsLogin)
        {
            _clockActionItem.Text = "🔑 다우오피스 로그인하기";
        }
        else if (isWorking)
        {
            _clockActionItem.Text = "🔴 지금 퇴근 처리하기";
        }
        else
        {
            _clockActionItem.Text = "🟢 지금 출근 처리하기";
        }

        _toggleOverlayVisibleItem.Text = isOverlayVisible
            ? "👁️ 오버레이 위젯 숨기기"
            : "👁️ 오버레이 위젯 보이기";

        _alwaysOnTopItem.Checked = settings.AlwaysOnTop;
        _showInTaskbarItem.Checked = settings.ShowInTaskbar;
        _runAtStartupItem.Checked = settings.RunAtStartup;

        string tooltip = $"WorkDot ({stateText}) - {snapshot.SubText}";
        _notifyIcon.Text = tooltip.Length > 60 ? tooltip[..60] : tooltip;

        var drawColor = System.Drawing.Color.FromArgb(
            activeMediaColor.A,
            activeMediaColor.R,
            activeMediaColor.G,
            activeMediaColor.B);

        var (newIcon, newHIcon, wpfImage) = CreateDynamicDotIcon(drawColor);
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

    private static (Icon TrayIcon, IntPtr HIcon, ImageSource WpfIcon) CreateDynamicDotIcon(System.Drawing.Color dotColor)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            // 다크 원형 배경
            using var bgBrush = new SolidBrush(System.Drawing.Color.FromArgb(235, 15, 23, 42));
            g.FillEllipse(bgBrush, 1, 1, 30, 30);

            // 외곽 글로우 링
            using var ringPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(180, dotColor), 2.2f);
            g.DrawEllipse(ringPen, 3, 3, 26, 26);

            // 내부 발광 코어 점
            using var coreBrush = new SolidBrush(dotColor);
            g.FillEllipse(coreBrush, 8, 8, 16, 16);

            // 하이라이트 반사광
            using var highlightBrush = new SolidBrush(System.Drawing.Color.FromArgb(140, 255, 255, 255));
            g.FillEllipse(highlightBrush, 10, 10, 6, 5);
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
        _trayMenu.Dispose();

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
