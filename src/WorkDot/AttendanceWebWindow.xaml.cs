using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Wpf;
using WorkDot.Services;

namespace WorkDot;

public partial class AttendanceWebWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly DaouAttendanceService _attendanceService;
    private readonly TaskCompletionSource<bool> _initTcs = new();
    private readonly SemaphoreSlim _webViewLock = new(1, 1);
    private WebView2 _currentWebView;
    private bool _isAppShuttingDown;
    private bool _isWebViewInitialized;
    private bool _isInteractiveRequested;
    private bool _needsFreshWebViewOnShow;

    public AttendanceWebWindow(DaouAttendanceService attendanceService)
    {
        InitializeComponent();
        _attendanceService = attendanceService;
        _currentWebView = PortalWebView;
        _attendanceService.RebuildWebViewHandler = RebuildWebViewAsync;

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
            // 창이 숨겨지면(닫기·동기화 후 닫기 등) 백그라운드 동기화 모드로 돌아가고,
            // 다음에 다시 열 때는 검은 화면(장시간 숨김 후 DirectComposition 표면 유실) 방지를 위해 새 WebView2 컨트롤로 교체한다.
            if (!IsVisible)
            {
                _attendanceService.IsInteractiveBrowsing = false;
                _needsFreshWebViewOnShow = true;
            }
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
            _needsFreshWebViewOnShow = true;
            Hide();
        }
    }

    public async void ShowInteractive()
    {
        _isInteractiveRequested = true;
        bool wasVisible = IsVisible;

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

        // 숨겨져 있던 창을 열거나 오랫동안 시간이 지난 뒤 열 때는
        // 화면에 창이 정상 표시된 상태에서 새 WebView2 컨트롤을 생성해 검은 화면(HwndHost/GPU 표면 유실)을 원천 차단한다.
        if (!wasVisible || _needsFreshWebViewOnShow || _currentWebView?.CoreWebView2 == null)
        {
            _needsFreshWebViewOnShow = false;
            await RebuildWebViewAsync(DaouAttendanceService.TargetAttendanceUrl);
        }
        else if (!await IsCurrentWebViewHealthyAsync())
        {
            string? lastUrl = null;
            try { lastUrl = _currentWebView.CoreWebView2?.Source; } catch { }
            await RebuildWebViewAsync(lastUrl);
        }

        await _attendanceService.OnPortalShownInteractiveAsync();
    }

    public void ShutdownClose()
    {
        _isAppShuttingDown = true;
        try { _currentWebView?.Dispose(); } catch { }
        try { Close(); } catch { }
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

    private async Task<bool> IsCurrentWebViewHealthyAsync()
    {
        if (_currentWebView?.CoreWebView2 == null)
            return false;

        try
        {
            var task = _currentWebView.CoreWebView2.ExecuteScriptAsync("document.body ? 1 : 0");
            if (await Task.WhenAny(task, Task.Delay(500)) != task)
                return false;

            string res = await task;
            return res == "1";
        }
        catch
        {
            return false;
        }
    }

    private void AttachUrlSync(WebView2 webView)
    {
        if (webView.CoreWebView2 != null)
        {
            webView.CoreWebView2.SourceChanged += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        UrlText.Text = webView.CoreWebView2?.Source ?? string.Empty;
                    }
                    catch { }
                });
            };
        }
    }

    private async Task EnsureWebViewReadyAsync()
    {
        if (_isWebViewInitialized)
        {
            await _initTcs.Task;
            return;
        }

        await _webViewLock.WaitAsync();
        try
        {
            if (_isWebViewInitialized)
                return;

            _isWebViewInitialized = true;
            await _attendanceService.InitializeWebViewAsync(_currentWebView);
            AttachUrlSync(_currentWebView);
            _initTcs.TrySetResult(true);
        }
        catch (Exception ex)
        {
            _initTcs.TrySetException(ex);
            throw;
        }
        finally
        {
            _webViewLock.Release();
        }
    }

    /// <summary>
    /// 기존 WebView2 컨트롤을 폐기하고 새 WebView2 인스턴스를 생성하여 교체합니다.
    /// 장시간 유휴 후 GPU/렌더러/DirectComposition 표면이 소실되어 검은 화면이 된 경우에도 앱 재시작 없이 즉시 복구됩니다.
    /// </summary>
    public async Task RebuildWebViewAsync(string? targetUrl = null)
    {
        await _webViewLock.WaitAsync();
        try
        {
            string navUrl = !string.IsNullOrWhiteSpace(targetUrl) && !targetUrl.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                ? targetUrl
                : DaouAttendanceService.TargetAttendanceUrl;

            var oldWebView = _currentWebView;
            var newWebView = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.White
            };

            _currentWebView = newWebView;
            WebViewHostContainer.Child = newWebView;

            if (oldWebView != null)
            {
                try { oldWebView.Dispose(); } catch { }
            }

            _isWebViewInitialized = true;
            await _attendanceService.InitializeWebViewAsync(newWebView, forceReinitialize: true, initialUrl: navUrl);
            AttachUrlSync(newWebView);
            UrlText.Text = newWebView.CoreWebView2?.Source ?? navUrl;
            _initTcs.TrySetResult(true);
        }
        catch
        {
            // Ignore transient rebuild errors
        }
        finally
        {
            _webViewLock.Release();
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

        if (_currentWebView?.CoreWebView2 != null)
        {
            await _attendanceService.ProbeDomAndApiStatusAsync(_currentWebView.CoreWebView2);
        }

        await _attendanceService.RefreshStatusAsync();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_currentWebView?.CoreWebView2?.CanGoBack == true)
        {
            _currentWebView.CoreWebView2.GoBack();
        }
    }

    private void OnForwardClick(object sender, RoutedEventArgs e)
    {
        if (_currentWebView?.CoreWebView2?.CanGoForward == true)
        {
            _currentWebView.CoreWebView2.GoForward();
        }
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        string? currentUrl = null;
        try
        {
            currentUrl = _currentWebView?.CoreWebView2?.Source;
        }
        catch { }

        if (string.IsNullOrWhiteSpace(currentUrl) || currentUrl.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
        {
            currentUrl = (UrlText.Text ?? string.Empty).Trim();
        }

        await RebuildWebViewAsync(currentUrl);
    }

    private async void OnUrlTextKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        string target = (UrlText.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(target))
            return;

        if (!target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            target = "https://" + target;
        }

        e.Handled = true;

        if (await IsCurrentWebViewHealthyAsync())
        {
            try
            {
                _currentWebView.CoreWebView2!.Navigate(target);
                return;
            }
            catch { }
        }

        await RebuildWebViewAsync(target);
    }

    private async void OnGoHomeClick(object sender, RoutedEventArgs e)
    {
        await RebuildWebViewAsync(DaouAttendanceService.TargetAttendanceUrl);
    }
}