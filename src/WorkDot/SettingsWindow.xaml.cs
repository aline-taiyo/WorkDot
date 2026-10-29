using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorkDot.Services;

namespace WorkDot;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private bool _isLoadingUi;

    public SettingsWindow(AppSettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;

        string version = AppSettingsService.AppVersion;
        VersionBadgeText.Text = version;
        FooterVersionText.Text = $"WorkDot {version} · AlineStudio";

        LoadSettingsIntoUi();
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

            WorkingHexBox.Text = s.WorkingColorHex;
            UpdatePreviewColor(WorkingColorPreview, s.WorkingColorHex, "#10B981");

            OffWorkHexBox.Text = s.OffWorkColorHex;
            UpdatePreviewColor(OffWorkColorPreview, s.OffWorkColorHex, "#EF4444");

            OpacitySlider.Value = s.WidgetOpacity;
            OpacityValueText.Text = $"{(int)Math.Round(s.WidgetOpacity * 100)}%";

            ScaleSlider.Value = s.WidgetScale;
            ScaleValueText.Text = $"{(int)Math.Round(s.WidgetScale * 100)}%";

            AlwaysOnTopCheck.IsChecked = s.AlwaysOnTop;
            RunAtStartupCheck.IsChecked = s.RunAtStartup;
        }
        finally
        {
            _isLoadingUi = false;
        }
    }

    private static void UpdatePreviewColor(System.Windows.Shapes.Ellipse preview, string hex, string fallback)
    {
        var c = AppSettingsService.ParseColorOrDefault(hex, fallback);
        preview.Fill = new SolidColorBrush(c);
    }

    private void OnThemeRadioChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi)
            return;

        WidgetThemeMode mode = WidgetThemeMode.System;
        if (ThemeDarkRadio.IsChecked == true)
            mode = WidgetThemeMode.Dark;
        else if (ThemeLightRadio.IsChecked == true)
            mode = WidgetThemeMode.Light;

        _settingsService.SaveAndApply(s => s.ThemeMode = mode);
    }

    private void OnWorkingSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hex)
        {
            WorkingHexBox.Text = hex;
        }
    }

    private void OnOffWorkSwatchClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hex)
        {
            OffWorkHexBox.Text = hex;
        }
    }

    private void OnWorkingHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingUi)
            return;

        string text = WorkingHexBox.Text.Trim();
        if (AppSettingsService.TryParseColor(text, out Color c))
        {
            WorkingColorPreview.Fill = new SolidColorBrush(c);
            string normalized = text.StartsWith('#') ? text.ToUpperInvariant() : "#" + text.ToUpperInvariant();
            _settingsService.SaveAndApply(s => s.WorkingColorHex = normalized);
        }
    }

    private void OnOffWorkHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingUi)
            return;

        string text = OffWorkHexBox.Text.Trim();
        if (AppSettingsService.TryParseColor(text, out Color c))
        {
            OffWorkColorPreview.Fill = new SolidColorBrush(c);
            string normalized = text.StartsWith('#') ? text.ToUpperInvariant() : "#" + text.ToUpperInvariant();
            _settingsService.SaveAndApply(s => s.OffWorkColorHex = normalized);
        }
    }

    private void OnOpacitySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingUi || OpacityValueText == null)
            return;

        double val = Math.Round(e.NewValue, 2);
        OpacityValueText.Text = $"{(int)Math.Round(val * 100)}%";
        _settingsService.SaveAndApply(s => s.WidgetOpacity = val);
    }

    private void OnScaleSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isLoadingUi || ScaleValueText == null)
            return;

        double val = Math.Round(e.NewValue, 2);
        ScaleValueText.Text = $"{(int)Math.Round(val * 100)}%";
        _settingsService.SaveAndApply(s => s.WidgetScale = val);
    }

    private void OnQuickScaleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr &&
            double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale))
        {
            ScaleSlider.Value = scale;
        }
    }

    private void OnBehaviorCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoadingUi)
            return;

        bool topMost = AlwaysOnTopCheck.IsChecked == true;
        bool startup = RunAtStartupCheck.IsChecked == true;

        _settingsService.SaveAndApply(s =>
        {
            s.AlwaysOnTop = topMost;
            s.RunAtStartup = startup;
        });
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
