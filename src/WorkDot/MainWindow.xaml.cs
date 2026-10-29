using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private readonly DispatcherTimer _confirmResetTimer;

    private AttendanceWebWindow? _portalWindow;
    private SettingsWindow? _settingsWindow;

    private bool _isPointerDown;
    private bool _hasMovedDuringDrag;
    private System.Windows.Point _dragStartScreenPoint;
    private double _windowStartLeft;
    private double _windowStartTop;
    private bool _isMiniDotMode;
    private bool _isActionBusy;
    private bool _isAwaitingInlineConfirm;

    public MainWindow()
    {
        InitializeComponent();

        ShowInTaskbar = false;

        _settingsService = new AppSettingsService();
        _snapService = new Win32SnapService(this);
        _attendanceService = new DaouAttendanceService();
        _trayService = new TaskbarTrayService();

        string ver = AppSettingsService.AppVersion;
        MenuVersionHeader.Header = $"WorkDot {ver}";

        _settingsService.SettingsChanged += _ => Dispatcher.Invoke(ApplySettingsAndVisuals);
        _snapService.SnapStateChanged += OnSnapStateChanged;
        _attendanceService.SnapshotChanged += _ => Dispatcher.Invoke(() =>
        {
            _isAwaitingInlineConfirm = false;
            ApplySettingsAndVisuals();
        });

        WireTrayEvents();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(45)
        };
        _pollTimer.Tick += async (_, _) => await _attendanceService.RefreshStatusAsync();

        _confirmResetTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.5)
        };
        _confirmResetTimer.Tick += (_, _) =>
        {
            _confirmResetTimer.Stop();
            _isAwaitingInlineConfirm = false;
            ApplySettingsAndVisuals();
        };

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
        _trayService.OpenPortalRequested += () => Dispatcher.Invoke(OpenPortalWindow);
        _trayService.ToggleOverlayVisibilityRequested += () => Dispatcher.Invoke(ToggleOverlayVisibility);
        _trayService.TrayContextMenuRequested += () => Dispatcher.Invoke(() =>
        {
            Activate();
            OverlayContextMenu.Placement = PlacementMode.MousePoint;
            OverlayContextMenu.IsOpen = true;
        });
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

        try
        {
            _portalWindow = new AttendanceWebWindow(_attendanceService);
            await _portalWindow.InitializeInBackgroundAsync();
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            SubStatusText.Text = $"오류: {ex.Message}";
        }
    }

    private void ApplySettingsAndVisuals()
    {
        var settings = _settingsService.Current;
        var snapshot = _attendanceService.CurrentSnapshot;

        Topmost = settings.AlwaysOnTop;
        ShowInTaskbar = false;
        CapsuleBorder.Opacity = settings.WidgetOpacity;

        bool scaleChanged = Math.Abs(CapsuleScaleTransform.ScaleX - settings.WidgetScale) > 0.005;
        CapsuleScaleTransform.ScaleX = settings.WidgetScale;
        CapsuleScaleTransform.ScaleY = settings.WidgetScale;

        bool isLightTheme = _settingsService.IsEffectiveLightTheme();
        if (isLightTheme)
        {
            CapsuleBorder.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF"));
            CapsuleBorder.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E4E4E7"));
            ActionDivider.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E4E4E7"));
            SubStatusText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#52525B"));

            SnapBadge.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F4F4F5"));
            SnapBadge.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E4E4E7"));
            SnapBadgeText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#71717A"));

            ApplyGhostButtonTheme((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#71717A"));
        }
        else
        {
            CapsuleBorder.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#09090B"));
            CapsuleBorder.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#27272A"));
            ActionDivider.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#27272A"));
            SubStatusText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A1A1AA"));

            SnapBadge.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#18181B"));
            SnapBadge.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#27272A"));
            SnapBadgeText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#71717A"));

            ApplyGhostButtonTheme((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A1A1AA"));
        }

        System.Windows.Media.Color workingColor = AppSettingsService.ParseColorOrDefault(settings.WorkingColorHex, "#10B981");
        System.Windows.Media.Color offWorkColor = AppSettingsService.ParseColorOrDefault(settings.OffWorkColorHex, "#EF4444");

        bool isWorking = snapshot.Status == WorkStatusKind.Working;
        System.Windows.Media.Color activeStatusColor = isWorking ? workingColor : offWorkColor;
        var activeBrush = new SolidColorBrush(activeStatusColor);

        // Status Badge Chip (Soft Tinted Background + Subtle Border)
        StatusBadgeBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
            isLightTheme ? (byte)22 : (byte)28,
            activeStatusColor.R,
            activeStatusColor.G,
            activeStatusColor.B));
        StatusBadgeBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(
            isLightTheme ? (byte)70 : (byte)65,
            activeStatusColor.R,
            activeStatusColor.G,
            activeStatusColor.B));

        StatusCoreDot.Fill = activeBrush;
        PulseRing.Fill = activeBrush;
        StatusTitleText.Text = isWorking ? "근무중" : "퇴근";
        StatusTitleText.Foreground = activeBrush;

        if (!_isAwaitingInlineConfirm)
        {
            SubStatusText.Text = snapshot.SubText;
        }

        // shadcn/ui Neutral Action Button (색상 충돌을 피하고 확인 단계에서만 시그널 컬러 강조)
        if (snapshot.Status == WorkStatusKind.NeedsLogin)
        {
            PrimaryActionButton.Content = "로그인";
            PrimaryActionButton.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isLightTheme ? "#09090B" : "#FAFAFA"));
            PrimaryActionButton.BorderBrush = PrimaryActionButton.Background;
            PrimaryActionButton.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isLightTheme ? "#FAFAFA" : "#09090B"));
            PrimaryActionButton.ToolTip = "다우오피스 로그인 창 열기";
        }
        else if (_isAwaitingInlineConfirm)
        {
            System.Windows.Media.Color confirmColor = isWorking ? offWorkColor : workingColor;
            PrimaryActionButton.Content = isWorking ? "퇴근 확인" : "출근 확인";
            PrimaryActionButton.Background = new SolidColorBrush(confirmColor);
            PrimaryActionButton.BorderBrush = new SolidColorBrush(confirmColor);
            PrimaryActionButton.Foreground = GetContrastingTextBrush(confirmColor);
        }
        else if (isWorking)
        {
            PrimaryActionButton.Content = "퇴근하기";
            PrimaryActionButton.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isLightTheme ? "#F4F4F5" : "#18181B"));
            PrimaryActionButton.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isLightTheme ? "#E4E4E7" : "#27272A"));
            PrimaryActionButton.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isLightTheme ? "#09090B" : "#FAFAFA"));
            PrimaryActionButton.ToolTip = "클릭하여 퇴근 처리";
        }
        else
        {
            PrimaryActionButton.Content = "출근하기";
            PrimaryActionButton.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isLightTheme ? "#09090B" : "#FAFAFA"));
            PrimaryActionButton.BorderBrush = PrimaryActionButton.Background;
            PrimaryActionButton.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(isLightTheme ? "#FAFAFA" : "#09090B"));
            PrimaryActionButton.ToolTip = "클릭하여 출근 처리";
        }

        ImageSource dynamicIcon = _trayService.UpdateTrayAndWindowIcon(snapshot, activeStatusColor);
        Icon = dynamicIcon;

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

    private void ApplyGhostButtonTheme(System.Windows.Media.Color fg)
    {
        var fgBrush = new SolidColorBrush(fg);
        PortalIconButton.Foreground = fgBrush;
        RefreshIconButton.Foreground = fgBrush;
        SettingsIconButton.Foreground = fgBrush;
    }

    private static SolidColorBrush GetContrastingTextBrush(System.Windows.Media.Color bg)
    {
        double luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return luminance > 0.58
            ? new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#09090B"))
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
                SnapBadgeText.Text = snapInfo.TargetTitle;
                SnapBadge.ToolTip = $"{snapInfo.TargetTitle} 경계에 스냅됨";
            }
        });
    }

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
        }
        else
        {
            InfoPanel.Visibility = Visibility.Visible;
            ActionPanel.Visibility = Visibility.Visible;
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

        if (!_isAwaitingInlineConfirm)
        {
            _isAwaitingInlineConfirm = true;
            SubStatusText.Text = $"한 번 더 누르면 {actionName} 처리";
            ApplySettingsAndVisuals();
            _confirmResetTimer.Stop();
            _confirmResetTimer.Start();
            return;
        }

        _confirmResetTimer.Stop();
        _isAwaitingInlineConfirm = false;
        _isActionBusy = true;
        PrimaryActionButton.IsEnabled = false;
        SubStatusText.Text = $"{actionName} 요청 중...";

        try
        {
            var (success, _) = await _attendanceService.TriggerClockActionAsync(targetClockIn);
            if (success)
            {
                _attendanceService.SetManualStatusOverride(targetClockIn ? WorkStatusKind.Working : WorkStatusKind.OffWork);
                SubStatusText.Text = $"{actionName} 완료 ({DateTime.Now:HH:mm})";
            }
            else
            {
                SubStatusText.Text = "포털 창에서 확인 필요";
                OpenPortalWindow();
            }
        }
        finally
        {
            PrimaryActionButton.IsEnabled = true;
            _isActionBusy = false;
            ApplySettingsAndVisuals();
        }
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        OverlayContextMenu.Placement = PlacementMode.MousePoint;
        MenuAlwaysOnTop.IsChecked = _settingsService.Current.AlwaysOnTop;
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
