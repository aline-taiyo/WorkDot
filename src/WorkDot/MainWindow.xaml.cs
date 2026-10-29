using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
            StatusBadgeBorder.ToolTip = $"오류: {ex.Message}";
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
        System.Windows.Media.Color iconFgColor;
        if (isLightTheme)
        {
            CapsuleBorder.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF"));
            CapsuleBorder.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E4E4E7"));
            ActionDivider.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E4E4E7"));
            SnapClipIconRect.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#71717A"));
            iconFgColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#52525B");
        }
        else
        {
            CapsuleBorder.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#09090B"));
            CapsuleBorder.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#27272A"));
            ActionDivider.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#27272A"));
            SnapClipIconRect.Fill = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A1A1AA"));
            iconFgColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A1A1AA");
        }

        ApplyGhostButtonTheme(iconFgColor);

        System.Windows.Media.Color workingColor = AppSettingsService.ParseColorOrDefault(settings.WorkingColorHex, "#10B981");
        System.Windows.Media.Color offWorkColor = AppSettingsService.ParseColorOrDefault(settings.OffWorkColorHex, "#EF4444");

        bool isWorking = snapshot.Status == WorkStatusKind.Working;
        System.Windows.Media.Color activeStatusColor = isWorking ? workingColor : offWorkColor;
        var activeBrush = new SolidColorBrush(activeStatusColor);

        // 1. Sun (근무중) / Moon (퇴근) Status Chip
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

        StatusIconRect.OpacityMask = (System.Windows.Media.Brush)Resources[isWorking ? "BrushIconSun" : "BrushIconMoon"];
        StatusIconRect.Fill = activeBrush;
        StatusBadgeBorder.ToolTip = $"{(isWorking ? "근무중" : "퇴근")} · {snapshot.SubText}";

        // 2. 텍스트 최소화: 확인/로그인 외에는 출퇴근 시각(HH:mm)만 짧게 표시하거나 아예 숨김
        if (_isActionBusy)
        {
            SubStatusText.Text = "...";
            SubStatusText.Foreground = activeBrush;
            SubStatusText.Visibility = Visibility.Visible;
        }
        else if (_isAwaitingInlineConfirm)
        {
            System.Windows.Media.Color confirmColor = isWorking ? offWorkColor : workingColor;
            SubStatusText.Text = isWorking ? "퇴근?" : "출근?";
            SubStatusText.Foreground = new SolidColorBrush(confirmColor);
            SubStatusText.Visibility = Visibility.Visible;
        }
        else if (snapshot.Status == WorkStatusKind.NeedsLogin)
        {
            SubStatusText.Text = "로그인";
            SubStatusText.Foreground = activeBrush;
            SubStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            string? compactTime = isWorking ? snapshot.ClockInTimeText : snapshot.ClockOutTimeText;
            if (!string.IsNullOrWhiteSpace(compactTime))
            {
                SubStatusText.Text = compactTime;
                SubStatusText.Foreground = activeBrush;
                SubStatusText.Visibility = Visibility.Visible;
            }
            else
            {
                SubStatusText.Text = string.Empty;
                SubStatusText.Visibility = Visibility.Collapsed;
            }
        }

        // 3. Shutdown / Power 아이콘 버튼 (출퇴근 액션)
        if (_isAwaitingInlineConfirm)
        {
            System.Windows.Media.Color confirmColor = isWorking ? offWorkColor : workingColor;
            PrimaryActionButton.Background = new SolidColorBrush(confirmColor);
            PrimaryActionButton.Foreground = GetContrastingTextBrush(confirmColor);
            PrimaryActionButton.ToolTip = isWorking ? "한 번 더 클릭: 퇴근 처리" : "한 번 더 클릭: 출근 처리";
        }
        else
        {
            PrimaryActionButton.Background = System.Windows.Media.Brushes.Transparent;
            PrimaryActionButton.Foreground = new SolidColorBrush(iconFgColor);
            PrimaryActionButton.ToolTip = snapshot.Status == WorkStatusKind.NeedsLogin
                ? "로그인"
                : (isWorking ? "퇴근하기" : "출근하기");
        }

        ImageSource dynamicIcon = _trayService.UpdateTrayAndWindowIcon(snapshot, activeStatusColor);
        Icon = dynamicIcon;

        if (IsLoaded)
        {
            UpdateLayout();
            if (scaleChanged && _snapService.CurrentSnap != null)
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
                SnapBadge.ToolTip = "윈도우 경계 스냅됨";
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
            ActionPanel.Visibility = Visibility.Visible;
        }
    }

    private void OnOverlayMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isMiniDotMode && !_isPointerDown)
        {
            ActionPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ToggleMiniMode()
    {
        _isMiniDotMode = !_isMiniDotMode;
        ActionPanel.Visibility = _isMiniDotMode ? Visibility.Collapsed : Visibility.Visible;

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

        if (!_isAwaitingInlineConfirm)
        {
            _isAwaitingInlineConfirm = true;
            ApplySettingsAndVisuals();
            _confirmResetTimer.Stop();
            _confirmResetTimer.Start();
            return;
        }

        _confirmResetTimer.Stop();
        _isAwaitingInlineConfirm = false;
        _isActionBusy = true;
        PrimaryActionButton.IsEnabled = false;
        ApplySettingsAndVisuals();

        try
        {
            var (success, _) = await _attendanceService.TriggerClockActionAsync(targetClockIn);
            if (success)
            {
                _attendanceService.SetManualStatusOverride(targetClockIn ? WorkStatusKind.Working : WorkStatusKind.OffWork);
            }
            else
            {
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
