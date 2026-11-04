using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WorkDot.Services;

namespace WorkDot;

public partial class AttendanceWebWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly DaouAttendanceService _attendanceService;
    private readonly TaskCompletionSource<bool> _initTcs = new();
    private bool _isAppShuttingDown;
    private bool _isWebViewInitialized;
    private bool _isInteractiveRequested;

    public AttendanceWebWindow(DaouAttendanceService attendanceService)
    {
        InitializeComponent();
        _attendanceService = attendanceService;
        Closing += OnWindowClosing;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int darkMode = 1;
                // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 1809+, Windows 11)
                DwmSetWindowAttribute(hwnd, 20, ref darkMode, sizeof(int));
            }
        };
        IsVisibleChanged += (_, _) =>
        {
            // 창이 숨겨지면(닫기·동기화 후 닫기 등) 백그라운드 동기화 모드로 돌아간다
            if (!IsVisible)
                _attendanceService.IsInteractiveBrowsing = false;
        };
    }

    public async Task InitializeInBackgroundAsync()
    {
        if (_isWebViewInitialized)
            return;

        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Width = 1120;
        Height = 780;
        Opacity = 0;

        Show();
        await EnsureWebViewReadyAsync();

        if (!_isInteractiveRequested)
        {
            Hide();
        }
    }

    public async void ShowInteractive()
    {
        _isInteractiveRequested = true;

        var workArea = SystemParameters.WorkArea;
        if (Left < -1000 || Top < -1000 || double.IsNaN(Left) || double.IsNaN(Top))
        {
            Left = Math.Max(workArea.Left, workArea.Left + (workArea.Width - Width) / 2);
            Top = Math.Max(workArea.Top, workArea.Top + (workArea.Height - Height) / 2);
        }

        Opacity = 1.0;
        ShowInTaskbar = true;
        ShowActivated = true;

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Topmost = true;
        Activate();
        Focus();
        Topmost = false;

        _attendanceService.IsInteractiveBrowsing = true;

        await EnsureWebViewReadyAsync();
    }

    public void ShutdownClose()
    {
        _isAppShuttingDown = true;
        Close();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async Task EnsureWebViewReadyAsync()
    {
        if (_isWebViewInitialized)
        {
            await _initTcs.Task;
            return;
        }

        _isWebViewInitialized = true;
        try
        {
            await _attendanceService.InitializeWebViewAsync(PortalWebView);

            if (PortalWebView.CoreWebView2 != null)
            {
                PortalWebView.CoreWebView2.SourceChanged += (_, _) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        UrlText.Text = PortalWebView.CoreWebView2.Source ?? string.Empty;
                    });
                };
            }
            _initTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _initTcs.TrySetException(ex);
            throw;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isAppShuttingDown)
            return;

        e.Cancel = true;
        HideToBackground();
    }

    /// <summary>창을 숨기고 백그라운드 동기화로 전환한다 (다른 메뉴를 보고 있었다면 근태 페이지로 복귀)</summary>
    private void HideToBackground()
    {
        _isInteractiveRequested = false;
        _attendanceService.IsInteractiveBrowsing = false;
        ShowInTaskbar = false;
        Hide();
        _ = _attendanceService.RefreshStatusAsync();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (PortalWebView.CoreWebView2?.CanGoBack == true)
        {
            PortalWebView.CoreWebView2.GoBack();
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        PortalWebView.CoreWebView2?.Reload();
    }

    private async void OnDismissPopupsClick(object sender, RoutedEventArgs e)
    {
        if (PortalWebView.CoreWebView2 == null)
            return;

        await _attendanceService.DismissBlockingPopupsAndSurveysAsync(PortalWebView.CoreWebView2);

        // 팝업만 닫고 현재 보고 있는 페이지는 유지한다 (근태 페이지 이동은 '홈' 버튼으로)
        string current = PortalWebView.CoreWebView2.Source ?? string.Empty;
        if (current.Contains("/ehr/app/attend", StringComparison.OrdinalIgnoreCase))
        {
            await _attendanceService.ProbeDomAndApiStatusAsync(PortalWebView.CoreWebView2);
        }
    }

    private void OnGoHomeClick(object sender, RoutedEventArgs e)
    {
        PortalWebView.CoreWebView2?.Navigate(DaouAttendanceService.TargetAttendanceUrl);
    }

    private async void OnSyncAndCloseClick(object sender, RoutedEventArgs e)
    {
        _attendanceService.ClearManualStatusOverride();
        if (PortalWebView.CoreWebView2 != null)
        {
            await _attendanceService.ProbeDomAndApiStatusAsync(PortalWebView.CoreWebView2);
        }
        HideToBackground();
    }
}