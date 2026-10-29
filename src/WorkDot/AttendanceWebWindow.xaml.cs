using System.Windows;
using Microsoft.Web.WebView2.Core;
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

        // 새 창(window.open)으로 뜨는 설문조사/공지 팝업 차단
        core.NewWindowRequested += (_, args) =>
        {
            string uri = args.Uri ?? string.Empty;
            args.Handled = true;

            if (AutoBypassSurveyCheck.IsChecked == true && DaouAttendanceService.IsBlockingSurveyOrPopupUrl(uri))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(uri) && uri.Contains(DaouAttendanceService.BaseDomain, StringComparison.OrdinalIgnoreCase))
            {
                core.Navigate(uri);
            }
        };

        core.SourceChanged += async (_, _) =>
        {
            string current = core.Source ?? string.Empty;
            UrlText.Text = current;

            // 로그인 직후 설문조사 페이지(/app/survey)나 홈(/app/home)으로 납치되면 자동으로 근태 페이지로 이동
            if (AutoBypassSurveyCheck.IsChecked == true &&
                DaouAttendanceService.IsRedirectedAwayFromAttendanceAfterLogin(current))
            {
                await Task.Delay(250);
                core.Navigate(DaouAttendanceService.TargetAttendanceUrl);
            }
        };

        core.NavigationCompleted += async (_, _) =>
        {
            if (PortalWebView.CoreWebView2 == null)
                return;

            string current = PortalWebView.CoreWebView2.Source ?? string.Empty;

            if (AutoBypassSurveyCheck.IsChecked == true)
            {
                if (DaouAttendanceService.IsRedirectedAwayFromAttendanceAfterLogin(current))
                {
                    PortalWebView.CoreWebView2.Navigate(DaouAttendanceService.TargetAttendanceUrl);
                    return;
                }

                await Task.Delay(500);
                await _attendanceService.DismissBlockingPopupsAndSurveysAsync(PortalWebView.CoreWebView2);
            }

            await Task.Delay(600);
            await _attendanceService.ProbeDomAndApiStatusAsync(PortalWebView.CoreWebView2);
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
            await _attendanceService.ProbeDomAndApiStatusAsync(PortalWebView.CoreWebView2);
        }
        await _attendanceService.RefreshStatusAsync();
        Close();
    }
}
