using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WorkDot.Services;

namespace WorkDot;

public partial class MainWindow : Window
{
    private readonly AppSettingsService _settingsService;
    private readonly Win32SnapService _snapService;
    private readonly DaouAttendanceService _attendanceService;
    private readonly TaskbarTrayService _trayService;
    private readonly DispatcherTimer _pollTimer;

    private AttendanceWebWindow? _portalWindow;
    private SettingsWindow? _settingsWindow;

    private bool _isPointerDown;
    private bool _hasMovedDuringDrag;
    private System.Windows.Point _dragStartScreenPoint;
    private double _windowStartLeft;
    private double _windowStartTop;
    private bool _isMiniDotMode;
    private bool _isActionBusy;

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new AppSettingsService();
        _snapService = new Win32SnapService(this);
        _attendanceService = new DaouAttendanceService();
        _trayService = new TaskbarTrayService();

        string ver = AppSettingsService.AppVersion;
        MenuVersionHeader.Header = $"✨ WorkDot {ver}";
        InlineVersionText.Text = $" · {ver}";

        _settingsService.SettingsChanged += _ => Dispatcher.Invoke(ApplySettingsAndVisuals);
        _snapService.SnapStateChanged += OnSnapStateChanged;
        _attendanceService.SnapshotChanged += _ => Dispatcher.Invoke(ApplySettingsAndVisuals);

        WireTrayEvents();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(45)
        };
        _pollTimer.Tick += async (_, _) => await _attendanceService.RefreshStatusAsync();

        Loaded += OnMainWindowLoaded;
        Closed += (_, _) =>
        {
            _portalWindow?.ShutdownClose();
            _trayService.Dispose();
            _snapService.Dispose();
            _settingsService.Dispose();
        };
    }

    private void WireTrayEvents()
    {
        _trayService.ClockActionRequested += () => Dispatcher.Invoke(() =>
        {
            EnsureOverlayVisible();
            OnPrimaryActionClick(this, new RoutedEventArgs());
        });

        _trayService.OpenPortalRequested += () => Dispatcher.Invoke(OpenPortalWindow);
        _trayService.RefreshRequested += () => Dispatcher.Invoke(async () => await _attendanceService.RefreshStatusAsync());
        _trayService.OpenSettingsRequested += () => Dispatcher.Invoke(() => OnOpenSettingsClick(this, new RoutedEventArgs()));
        _trayService.ToggleOverlayVisibilityRequested += () => Dispatcher.Invoke(ToggleOverlayVisibility);
        _trayService.SnapToTaskbarRequested += () => Dispatcher.Invoke(() =>
        {
            EnsureOverlayVisible();
            _snapService.SnapToTaskbarDefault();
        });
        _trayService.ToggleMiniModeRequested += () => Dispatcher.Invoke(() =>
        {
            EnsureOverlayVisible();
            ToggleMiniMode();
        });
        _trayService.ManualStatusRequested += status => Dispatcher.Invoke(() => _attendanceService.SetManualStatusOverride(status));
        _trayService.ScaleRequested += scale => Dispatcher.Invoke(() => _settingsService.SaveAndApply(s => s.WidgetScale = scale));
        _trayService.ThemeRequested += mode => Dispatcher.Invoke(() => _settingsService.SaveAndApply(s => s.ThemeMode = mode));
        _trayService.AlwaysOnTopChanged += val => Dispatcher.Invoke(() => _settingsService.SaveAndApply(s => s.AlwaysOnTop = val));
        _trayService.ShowInTaskbarChanged += val => Dispatcher.Invoke(() => _settingsService.SaveAndApply(s => s.ShowInTaskbar = val));
        _trayService.RunAtStartupChanged += val => Dispatcher.Invoke(() => _settingsService.SaveAndApply(s => s.RunAtStartup = val));
        _trayService.ExitRequested += () => Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown());
    }

    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (Resources["PulseStoryboard"] is Storyboard pulse)
        {
            pulse.Begin(this, true);
        }

        ApplySettingsAndVisuals();

        _snapService.Initialize();
        _snapService.SnapToTaskbarDefault();

        // MainWindow 밖의 별도 백그라운드 창에서 WebView2를 초기화 (Airspace 가림 문제 원천 방지)
        try
        {
            _portalWindow = new AttendanceWebWindow(_attendanceService);
            await _portalWindow.InitializeInBackgroundAsync();
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            SubStatusText.Text = $"WebView2 오류: {ex.Message}";
        }
    }

    private void ApplySettingsAndVisuals()
    {
        var settings = _settingsService.Current;
        var snapshot = _attendanceService.CurrentSnapshot;

        Topmost = settings.AlwaysOnTop;
        ShowInTaskbar = settings.ShowInTaskbar;
        CapsuleBorder.Opacity = settings.WidgetOpacity;

        bool scaleChanged = Math.Abs(CapsuleScaleTransform.ScaleX - settings.WidgetScale) > 0.005;
        CapsuleScaleTransform.ScaleX = settings.WidgetScale;
        CapsuleScaleTransform.ScaleY = settings.WidgetScale;

        bool isLightTheme = _settingsService.IsEffectiveLightTheme();
        if (isLightTheme)
        {
            CapsuleBorder.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F4FFFFFF"));
            SubStatusText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#475569"));
            InlineVersionText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#94A3B8"));

            SnapBadge.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E2E8F0"));
            SnapBadge.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CBD5E1"));
            SnapBadgeText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#334155"));

            ApplyGhostButtonTheme(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E2E8F0"),
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#334155"));
        }
        else
        {
            CapsuleBorder.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#EE0F172A"));
            SubStatusText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#94A3B8"));
            InlineVersionText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#64748B"));

            SnapBadge.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E293B"));
            SnapBadge.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#334155"));
            SnapBadgeText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#94A3B8"));

            ApplyGhostButtonTheme(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E293B"),
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#CBD5E1"));
        }

        System.Windows.Media.Color workingColor = AppSettingsService.ParseColorOrDefault(settings.WorkingColorHex, "#10B981");
        System.Windows.Media.Color offWorkColor = AppSettingsService.ParseColorOrDefault(settings.OffWorkColorHex, "#EF4444");

        bool isWorking = snapshot.Status == WorkStatusKind.Working;
        System.Windows.Media.Color activeStatusColor = isWorking ? workingColor : offWorkColor;
        System.Windows.Media.Color activeBorderColor = System.Windows.Media.Color.FromArgb(
            150, activeStatusColor.R, activeStatusColor.G, activeStatusColor.B);
        var activeBrush = new SolidColorBrush(activeStatusColor);

        StatusCoreDot.Fill = activeBrush;
        PulseRing.Fill = activeBrush;
        DotGlow.Color = activeStatusColor;
        CapsuleGlow.Color = activeStatusColor;
        CapsuleBorder.BorderBrush = new SolidColorBrush(activeBorderColor);

        StatusTitleText.Text = isWorking ? "근무중" : "퇴근";
        StatusTitleText.Foreground = activeBrush;
        SubStatusText.Text = snapshot.SubText;

        Title = $"WorkDot · {(isWorking ? "근무중" : "퇴근")}";
        WindowTaskbarInfo.Description = $"WorkDot {AppSettingsService.AppVersion} · {(isWorking ? "근무중" : "퇴근")} ({snapshot.SubText})";

        if (snapshot.Status == WorkStatusKind.NeedsLogin)
        {
            PrimaryActionButton.Content = "로그인";
            PrimaryActionButton.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F59E0B"));
            PrimaryActionButton.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E293B"));
            PrimaryActionButton.ToolTip = "클릭하여 다우오피스에 로그인합니다";
            ThumbClockActionBtn.Description = "다우오피스 로그인";
        }
        else if (isWorking)
        {
            PrimaryActionButton.Content = "퇴근하기";
            PrimaryActionButton.Background = new SolidColorBrush(offWorkColor);
            PrimaryActionButton.Foreground = GetContrastingTextBrush(offWorkColor);
            PrimaryActionButton.ToolTip = "클릭하여 퇴근 처리를 진행합니다";
            ThumbClockActionBtn.Description = "지금 퇴근 처리하기";
        }
        else
        {
            PrimaryActionButton.Content = "출근하기";
            PrimaryActionButton.Background = new SolidColorBrush(workingColor);
            PrimaryActionButton.Foreground = GetContrastingTextBrush(workingColor);
            PrimaryActionButton.ToolTip = "클릭하여 출근 처리를 진행합니다";
            ThumbClockActionBtn.Description = "지금 출근 처리하기";
        }

        ImageSource dynamicIcon = _trayService.UpdateTrayAndWindowIcon(
            snapshot,
            settings,
            activeStatusColor,
            Visibility == Visibility.Visible);
        Icon = dynamicIcon;
        ThumbClockActionBtn.ImageSource = dynamicIcon;

        if (scaleChanged && IsLoaded)
        {
            UpdateLayout();
            if (_snapService.CurrentSnap != null)
            {
                _snapService.EvaluateMagneticSnap(applyPosition: true);
            }
        }
    }

    private void ToggleOverlayVisibility()
    {
        if (Visibility == Visibility.Visible)
        {
            Visibility = Visibility.Hidden;
        }
        else
        {
            EnsureOverlayVisible();
        }
        ApplySettingsAndVisuals();
    }

    private void EnsureOverlayVisible()
    {
        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void ApplyGhostButtonTheme(System.Windows.Media.Color bg, System.Windows.Media.Color fg)
    {
        var bgBrush = new SolidColorBrush(bg);
        var fgBrush = new SolidColorBrush(fg);

        PortalIconButton.Background = bgBrush;
        PortalIconButton.Foreground = fgBrush;
        RefreshIconButton.Background = bgBrush;
        RefreshIconButton.Foreground = fgBrush;
        SettingsIconButton.Background = bgBrush;
        SettingsIconButton.Foreground = fgBrush;
    }

    private static SolidColorBrush GetContrastingTextBrush(System.Windows.Media.Color bg)
    {
        double luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return luminance > 0.62
            ? new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0F172A"))
            : System.Windows.Media.Brushes.White;
    }

    private void OnSnapStateChanged(SnapTargetInfo? snapInfo)
    {
        Dispatcher.Invoke(() =>
        {
            if (snapInfo == null)
            {
                SnapBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                SnapBadge.Visibility = Visibility.Visible;
                SnapBadgeText.Text = $"🧲 {snapInfo.TargetTitle}";
                SnapBadge.ToolTip = $"'{snapInfo.TargetTitle}' 창 경계에 스냅되어 함께 이동합니다";
            }
        });
    }

    // -------------------------------------------------------------------------
    // 마우스 드래그, 자석 스냅 & Ctrl+휠 크기 조절
    // -------------------------------------------------------------------------
    private void OnOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMiniMode();
            e.Handled = true;
            return;
        }

        if (e.OriginalSource is DependencyObject dep && IsInsideButton(dep))
            return;

        _isPointerDown = true;
        _hasMovedDuringDrag = false;
        _dragStartScreenPoint = PointToScreen(e.GetPosition(this));
        _windowStartLeft = Left;
        _windowStartTop = Top;

        _snapService.IsDraggingOverlay = true;
        ((UIElement)sender).CaptureMouse();
    }

    private void OnOverlayMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isPointerDown)
            return;

        System.Windows.Point currentScreenPoint = PointToScreen(e.GetPosition(this));
        var source = PresentationSource.FromVisual(this);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        double deltaX = (currentScreenPoint.X - _dragStartScreenPoint.X) / scaleX;
        double deltaY = (currentScreenPoint.Y - _dragStartScreenPoint.Y) / scaleY;

        if (!_hasMovedDuringDrag && (Math.Abs(deltaX) > 3 || Math.Abs(deltaY) > 3))
        {
            _hasMovedDuringDrag = true;
        }

        if (_hasMovedDuringDrag)
        {
            Left = _windowStartLeft + deltaX;
            Top = _windowStartTop + deltaY;
            _snapService.EvaluateMagneticSnap(applyPosition: true);
        }
    }

    private void OnOverlayMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isPointerDown)
            return;

        _isPointerDown = false;
        _snapService.IsDraggingOverlay = false;
        ((UIElement)sender).ReleaseMouseCapture();

        if (_hasMovedDuringDrag)
        {
            _snapService.EvaluateMagneticSnap(applyPosition: true);
        }
    }

    private void OnOverlayMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            double step = e.Delta > 0 ? 0.05 : -0.05;
            double nextScale = Math.Clamp(_settingsService.Current.WidgetScale + step, 0.70, 1.60);
            _settingsService.SaveAndApply(s => s.WidgetScale = nextScale);
            e.Handled = true;
        }
    }

    private void OnOverlayMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isMiniDotMode)
        {
            InfoPanel.Visibility = Visibility.Visible;
            ActionPanel.Visibility = Visibility.Visible;
        }
    }

    private void OnOverlayMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isMiniDotMode && !_isPointerDown)
        {
            InfoPanel.Visibility = Visibility.Collapsed;
            ActionPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ToggleMiniMode()
    {
        _isMiniDotMode = !_isMiniDotMode;
        if (_isMiniDotMode)
        {
            InfoPanel.Visibility = Visibility.Collapsed;
            ActionPanel.Visibility = Visibility.Collapsed;
            CapsuleBorder.Padding = new Thickness(8);
        }
        else
        {
            InfoPanel.Visibility = Visibility.Visible;
            ActionPanel.Visibility = Visibility.Visible;
            CapsuleBorder.Padding = new Thickness(12, 8, 12, 8);
        }

        UpdateLayout();
        _snapService.EvaluateMagneticSnap(applyPosition: true);
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.Button)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // 출근 / 퇴근 원클릭 액션, 설정 창, 작업표시줄 & 컨텍스트 메뉴 핸들러
    // -------------------------------------------------------------------------
    private async void OnPrimaryActionClick(object sender, RoutedEventArgs e)
    {
        if (_isActionBusy)
            return;

        var snapshot = _attendanceService.CurrentSnapshot;

        if (snapshot.Status == WorkStatusKind.NeedsLogin)
        {
            OpenPortalWindow();
            return;
        }

        bool targetClockIn = snapshot.Status != WorkStatusKind.Working;
        string actionName = targetClockIn ? "출근" : "퇴근";

        var confirm = System.Windows.MessageBox.Show(
            this,
            $"다우오피스에 [{actionName}] 처리를 진행할까요?\n\n• 확인을 누르면 그룹웨어 {actionName} 버튼을 자동으로 실행합니다.",
            $"WorkDot · {actionName} 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _isActionBusy = true;
        PrimaryActionButton.IsEnabled = false;
        SubStatusText.Text = $"{actionName} 처리 요청 중...";

        try
        {
            var (success, message) = await _attendanceService.TriggerClockActionAsync(targetClockIn);
            if (success)
            {
                _attendanceService.SetManualStatusOverride(targetClockIn ? WorkStatusKind.Working : WorkStatusKind.OffWork);
                SubStatusText.Text = $"{actionName} 완료 ({DateTime.Now:HH:mm})";
            }
            else
            {
                SubStatusText.Text = "그룹웨어 창에서 확인 필요";
                var openPortal = System.Windows.MessageBox.Show(
                    this,
                    $"{message}\n\n지금 다우오피스 근태 창을 열까요?",
                    "WorkDot 안내",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (openPortal == MessageBoxResult.Yes)
                {
                    OpenPortalWindow();
                }
            }
        }
        finally
        {
            PrimaryActionButton.IsEnabled = true;
            _isActionBusy = false;
        }
    }

    private void OnThumbClockActionClick(object sender, EventArgs e)
    {
        OnPrimaryActionClick(this, new RoutedEventArgs());
    }

    private void OnThumbOpenPortalClick(object sender, EventArgs e)
    {
        OpenPortalWindow();
    }

    private async void OnThumbRefreshClick(object sender, EventArgs e)
    {
        await _attendanceService.RefreshStatusAsync();
    }

    private void OnThumbSettingsClick(object sender, EventArgs e)
    {
        OnOpenSettingsClick(this, new RoutedEventArgs());
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        MenuAlwaysOnTop.IsChecked = _settingsService.Current.AlwaysOnTop;
        MenuShowInTaskbar.IsChecked = _settingsService.Current.ShowInTaskbar;
        MenuRunAtStartup.IsChecked = _settingsService.Current.RunAtStartup;
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow != null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settingsService);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnMenuAlwaysOnTopClick(object sender, RoutedEventArgs e)
    {
        bool next = MenuAlwaysOnTop.IsChecked;
        _settingsService.SaveAndApply(s => s.AlwaysOnTop = next);
    }

    private void OnMenuShowInTaskbarClick(object sender, RoutedEventArgs e)
    {
        bool next = MenuShowInTaskbar.IsChecked;
        _settingsService.SaveAndApply(s => s.ShowInTaskbar = next);
    }

    private void OnMenuRunAtStartupClick(object sender, RoutedEventArgs e)
    {
        bool next = MenuRunAtStartup.IsChecked;
        _settingsService.SaveAndApply(s => s.RunAtStartup = next);
    }

    private void OnMenuQuickScaleClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem item && item.Tag is string tagStr &&
            double.TryParse(tagStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double scale))
        {
            _settingsService.SaveAndApply(s => s.WidgetScale = scale);
        }
    }

    private void OnOpenPortalClick(object sender, RoutedEventArgs e)
    {
        OpenPortalWindow();
    }

    private void OpenPortalWindow()
    {
        _portalWindow ??= new AttendanceWebWindow(_attendanceService);
        _portalWindow.ShowInteractive();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        SubStatusText.Text = "동기화 중...";
        await _attendanceService.RefreshStatusAsync();
    }

    private void OnForceWorkingClick(object sender, RoutedEventArgs e)
    {
        _attendanceService.SetManualStatusOverride(WorkStatusKind.Working);
    }

    private void OnForceOffWorkClick(object sender, RoutedEventArgs e)
    {
        _attendanceService.SetManualStatusOverride(WorkStatusKind.OffWork);
    }

    private void OnSnapToTaskbarClick(object sender, RoutedEventArgs e)
    {
        _snapService.SnapToTaskbarDefault();
    }

    private void OnToggleMiniModeClick(object sender, RoutedEventArgs e)
    {
        ToggleMiniMode();
    }

    private void OnHideOverlayClick(object sender, RoutedEventArgs e)
    {
        ToggleOverlayVisibility();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }
}
