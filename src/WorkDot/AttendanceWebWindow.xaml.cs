using System.Windows;
using WorkDot.Services;

namespace WorkDot;

public partial class AttendanceWebWindow : Window
{
    private readonly DaouAttendanceService _attendanceService;

    public AttendanceWebWindow(DaouAttendanceService attendanceService)
    {
        InitializeComponent();
        _attendanceService = attendanceService;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var env = await _attendanceService.GetOrCreateEnvironmentAsync();
        await PortalWebView.EnsureCoreWebView2Async(env);

        var core = PortalWebView.CoreWebView2;

        // 포털 WebView에도 실시간 API/DOM 훅 및 설문/팝업 우회 가드 장착
        await _attendanceService.AttachWebViewHooksAsync(core, isHiddenInstance: false);

        core.SourceChanged += (_, _) =>
        {
            UrlText.Text = core.Source ?? string.Empty;
        };

        PortalWebView.Source = new Uri(DaouAttendanceService.TargetAttendanceUrl);
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

        string current = PortalWebView.CoreWebView2.Source ?? string.Empty;
        if (!current.Contains("/ehr/app/attend", StringComparison.OrdinalIgnoreCase))
        {
            PortalWebView.CoreWebView2.Navigate(DaouAttendanceService.TargetAttendanceUrl);
        }
        else
        {
            await _attendanceService.ProbeDomAndApiStatusAsync(PortalWebView.CoreWebView2, fromPortalWindow: true);
        }
    }

    private void OnGoHomeClick(object sender, RoutedEventArgs e)
    {
        PortalWebView.CoreWebView2?.Navigate(DaouAttendanceService.TargetAttendanceUrl);
    }

    private async void OnSyncAndCloseClick(object sender, RoutedEventArgs e)
    {
        if (PortalWebView.CoreWebView2 != null)
        {
            await _attendanceService.DismissBlockingPopupsAndSurveysAsync(PortalWebView.CoreWebView2);
            await _attendanceService.ProbeDomAndApiStatusAsync(PortalWebView.CoreWebView2, fromPortalWindow: true);
        }
        await _attendanceService.RefreshStatusAsync();
        Close();
    }
}
