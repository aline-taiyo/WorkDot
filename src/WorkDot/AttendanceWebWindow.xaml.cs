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

        PortalWebView.CoreWebView2.SourceChanged += (_, _) =>
        {
            UrlText.Text = PortalWebView.CoreWebView2.Source;
        };

        PortalWebView.CoreWebView2.NavigationCompleted += async (_, _) =>
        {
            if (PortalWebView.CoreWebView2 != null)
            {
                await Task.Delay(900);
                await _attendanceService.ProbeDomAndApiStatusAsync(PortalWebView.CoreWebView2);
            }
        };

        PortalWebView.Source = new Uri(DaouAttendanceService.TargetAttendanceUrl);
    }

    private void OnGoHomeClick(object sender, RoutedEventArgs e)
    {
        if (PortalWebView.CoreWebView2 != null)
        {
            PortalWebView.CoreWebView2.Navigate(DaouAttendanceService.TargetAttendanceUrl);
        }
    }

    private async void OnSyncAndCloseClick(object sender, RoutedEventArgs e)
    {
        if (PortalWebView.CoreWebView2 != null)
        {
            await _attendanceService.ProbeDomAndApiStatusAsync(PortalWebView.CoreWebView2);
        }
        await _attendanceService.RefreshStatusAsync();
        Close();
    }
}
