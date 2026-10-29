using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WorkDot.Services;

namespace WorkDot;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly AutoUpdateService _autoUpdateService;
    private readonly DispatcherTimer _feedbackResetTimer;
    private bool _isLoadingUi = true;
    private bool _isManualChecking;

    public SettingsWindow(AppSettingsService settingsService, AutoUpdateService autoUpdateService)
    {
        _settingsService = settingsService;
        _autoUpdateService = autoUpdateService;
        _isLoadingUi = true;

        InitializeComponent();

        _feedbackResetTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.5)
        };
        _feedbackResetTimer.Tick += (_, _) =>
        {
            _feedbackResetTimer.Stop();
            ResetCheckUpdateButtonVisual();
        };

        string version = AppSettingsService.AppVersion;
        VersionBadgeText.Text = version;
        CheckUpdateButton.ToolTip = _autoUpdateService.LastStatusMessage;

        _autoUpdateService.StatusChanged += OnAutoUpdateStatusChanged;
        Closed += (_, _) =>
        {
            _feedbackResetTimer.Stop();
            _autoUpdateService.StatusChanged -= OnAutoUpdateStatusChanged;
        };

        LoadSettingsIntoUi();
    }

    private void OnAutoUpdateStatusChanged(string status)
    {
        Dispatcher.Invoke(() =>
        {
            if (CheckUpdateButton != null)
            {
                CheckUpdateButton.ToolTip = status;
                if (_isManualChecking)
                {
                    CheckUpdateButton.Content = status;
                }
            }
        });
    }

    private void ResetCheckUpdateButtonVisual()
    {
        if (CheckUpdateButton == null)
            return;

        CheckUpdateButton.Content = "업데이트 확인";
        CheckUpdateButton.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E4E4E7"));
        CheckUpdateButton.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#27272A"));
    }

    private void ShowCheckUpdateFeedback(string text, string colorHex)
    {
        if (CheckUpdateButton == null)
            return;

        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex);
        CheckUpdateButton.Content = text;
        CheckUpdateButton.Foreground = new SolidColorBrush(c);
        CheckUpdateButton.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, c.R, c.G, c.B));
        _feedbackResetTimer.Stop();
        _feedbackResetTimer.Start();
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void LoadSettingsIntoUi()
    {
        _isLoadingUi = true;
        try
        {
            var s = _settingsService.Current;

            ThemeSystemRadio.IsChecked = s.ThemeMode == WidgetThemeMode.System;
            ThemeDarkRadio.IsChecked = s.ThemeMode == WidgetThemeMode.Dark;
            ThemeLightRadio.IsChecked = s.ThemeMode == WidgetThemeMode.Light;

            OpacitySlider.Value = s.WidgetOpacity;
            OpacityValueText.Text = $"{(int)Math.Round(s.WidgetOpacity * 100)}%";

            ScaleSlider.Value = s.WidgetScale;
            ScaleValueText.Text = $"{(int)Math.Round(s.WidgetScale * 100)}%";

            AlwaysOnTopCheck.IsChecked = s.AlwaysOnTop;
            RunAtStartupCheck.IsChecked = s.RunAtStartup;
            AutoUpdateCheck.IsChecked = s.AutoUpdateEnabled;
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private void OnThemeRadioChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || _settingsService == null)
            return;

        WidgetThemeMode mode = WidgetThemeMode.System;
        if (ThemeDarkRadio.IsChecked == true)
            mode = WidgetThemeMode.Dark;
        else if (ThemeLightRadio.IsChecked == true)
            mode = WidgetThemeMode.Light;

        _settingsService.SaveAndApply(s => s.ThemeMode = mode);
    }

    private void OnOpacitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingUi || _settingsService == null || OpacityValueText == null)
            return;

        double val = Math.Round(e.NewValue, 2);
        OpacityValueText.Text = $"{(int)Math.Round(val * 100)}%";
        _settingsService.SaveAndApply(s => s.WidgetOpacity = val);
    }

    private void OnScaleSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingUi || _settingsService == null || ScaleValueText == null)
            return;

        double val = Math.Round(e.NewValue, 2);
        ScaleValueText.Text = $"{(int)Math.Round(val * 100)}%";
        _settingsService.SaveAndApply(s => s.WidgetScale = val);
    }

    private void OnQuickScaleClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string tagStr &&
            double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale))
        {
            ScaleSlider.Value = scale;
        }
    }

    private void OnBehaviorCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi || _settingsService == null)
            return;

        bool topMost = AlwaysOnTopCheck.IsChecked == true;
        bool startup = RunAtStartupCheck.IsChecked == true;
        bool autoUpdate = AutoUpdateCheck.IsChecked == true;

        _settingsService.SaveAndApply(s =>
        {
            s.AlwaysOnTop = topMost;
            s.RunAtStartup = startup;
            s.AutoUpdateEnabled = autoUpdate;
        });
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (CheckUpdateButton == null || _isManualChecking)
            return;

        _feedbackResetTimer.Stop();
        _isManualChecking = true;
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "릴리즈 확인 중...";
        CheckUpdateButton.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A1A1AA"));

        try
        {
            var result = await _autoUpdateService.CheckForUpdateAsync();
            if (result.IsUpdateAvailable)
            {
                if (!string.IsNullOrWhiteSpace(result.DownloadUrl) && !string.IsNullOrWhiteSpace(result.AssetName))
                {
                    ShowCheckUpdateFeedback($"업데이트 중 ({result.LatestVersionTag})...", "#10B981");
                    bool ok = await _autoUpdateService.DownloadAndApplyUpdateAsync(result.DownloadUrl, result.AssetName);
                    if (!ok)
                    {
                        ShowCheckUpdateFeedback("업데이트 적용 실패", "#EF4444");
                    }
                }
                else
                {
                    ShowCheckUpdateFeedback($"새 버전 {result.LatestVersionTag} (페이지 이동)", "#10B981");
                    try
                    {
                        Process.Start(new ProcessStartInfo(AutoUpdateService.GitHubReleasesUrl)
                        {
                            UseShellExecute = true
                        });
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }
            else if (result.Message.Contains("릴리스 없음", StringComparison.Ordinal))
            {
                ShowCheckUpdateFeedback("배포된 릴리즈 없음", "#F59E0B");
            }
            else if (result.Message.Contains("실패", StringComparison.Ordinal))
            {
                ShowCheckUpdateFeedback("업데이트 확인 실패", "#EF4444");
            }
            else
            {
                ShowCheckUpdateFeedback($"최신 버전입니다 ({result.CurrentVersionTag})", "#10B981");
            }
        }
        finally
        {
            _isManualChecking = false;
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OnOpenGitHubRepoClick(object sender, RoutedEventArgs e)
    {
        AutoUpdateService.OpenGitHubRepository();
    }

    private void OnResetDefaultsClick(object sender, RoutedEventArgs e)
    {
        _settingsService.ResetToDefaults();
        LoadSettingsIntoUi();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
