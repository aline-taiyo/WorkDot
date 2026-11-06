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

        // 브라우저 창을 열었을 때 웹뷰가 검은 화면(크래시 또는 멈춤) 상태인지 헬스체크하고 필요 시 즉각 복구
        if (PortalWebView.CoreWebView2 != null)
        {
            try
            {
                var task = PortalWebView.CoreWebView2.ExecuteScriptAsync("1");
                if (await Task.WhenAny(task, Task.Delay(500)) != task)
                {
                    PortalWebView.CoreWebView2.Reload();
                }
            }
            catch
            {
                try { PortalWebView.CoreWebView2.Reload(); } catch { }
            }
        }
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

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isAppShuttingDown)
            return;

        e.Cancel = true;
        await SyncAndCloseToBackgroundAsync();
    }

    /// <summary>브라우저 창을 닫을 때 '동기화 및 닫기'와 동일하게 근태 상태를 동기화하고 백그라운드로 숨긴다.</summary>
    private async Task SyncAndCloseToBackgroundAsync()
    {
        _attendanceService.ClearManualStatusOverride();
        _isInteractiveRequested = false;
        _attendanceService.IsInteractiveBrowsing = false;
        ShowInTaskbar = false;
        Hide();

        if (PortalWebView.CoreWebView2 != null)
        {
            await _attendanceService.ProbeDomAndApiStatusAsync(PortalWebView.CoreWebView2);
        }

        await _attendanceService.RefreshStatusAsync();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (PortalWebView.CoreWebView2?.CanGoBack == true)
        {
            PortalWebView.CoreWebView2.GoBack();
        }
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (PortalWebView.CoreWebView2?.CanGoForward == true)
        {
            PortalWebView.CoreWebView2.GoForward();
        }
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        try
        {
            PortalWebView.CoreWebView2?.Reload();
        }
        catch
        {
            try { PortalWebView.Reload(); } catch { }
        }
    }

    private void OnUrlTextKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || PortalWebView.CoreWebView2 == null)
            return;

        string target = (UrlText.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(target))
            return;

        if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            target = "https://" + target;
        }

        try
        {
            PortalWebView.CoreWebView2.Navigate(target);
            e.Handled = true;
        }
        catch
        {
            // Ignore invalid URL navigation errors
        }
    }

    private async void OnGoHomeClick(object sender, RoutedEventArgs e)
    {
        if (PortalWebView.CoreWebView2 == null)
            return;

        try
        {
            // 화면을 가로막는 딤 레이어 정리
            await PortalWebView.CoreWebView2.ExecuteScriptAsync("""
                (() => {
                    document.querySelectorAll('.layer_dimm, .dimm, .backdrop, [class*="dimm"]').forEach(el => el.remove());
                })();
            """);

            string current = PortalWebView.CoreWebView2.Source ?? string.Empty;
            if (current.Contains("/ehr/app/attend", StringComparison.OrdinalIgnoreCase))
            {
                PortalWebView.CoreWebView2.Reload();
            }
            else
            {
                PortalWebView.CoreWebView2.Navigate(DaouAttendanceService.TargetAttendanceUrl);
            }
        }
        catch
        {
            try { PortalWebView.CoreWebView2.Reload(); } catch { }
        }
    }
}