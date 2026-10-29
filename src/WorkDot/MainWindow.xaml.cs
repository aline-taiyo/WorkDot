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
    private const double IdleSecondsBeforeShowingElapsed = 10.0;
    private const double LongPressHoldMs = 450.0;

    private readonly AppSettingsService _settingsService;
    private readonly AutoUpdateService _autoUpdateService;
    private readonly Win32SnapService _snapService;
    private readonly DaouAttendanceService _attendanceService;
    private readonly TaskbarTrayService _trayService;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _longPressTimer;
    private readonly DispatcherTimer _idleTimer;
    private readonly DispatcherTimer _hoverCollapseTimer;

    private AttendanceWebWindow? _portalWindow;
    private SettingsWindow? _settingsWindow;

    private bool _isPointerDown;
    private bool _hasMovedDuringDrag;
    private bool _isPressOnStatusIcon;
    private bool _isLongPressArmed;
    private bool _isCursorInsideDuringLongPress;
    private bool _isActionBusy;
    private DateTime _lastInteractionUtc = DateTime.UtcNow;
    private bool _isIdleElapsedVisible;

    public MainWindow()
    {
        InitializeComponent();

        ShowInTaskbar = false;

        _settingsService = new AppSettingsService();
        _autoUpdateService = new AutoUpdateService(_settingsService);
        _snapService = new Win32SnapService(this);
        _attendanceService = new DaouAttendanceService();
        _trayService = new TaskbarTrayService();

        string ver = AppSettingsService.AppVersion;
        MenuVersionHeader.Header = $"WorkDot {ver}";

        _settingsService.SettingsChanged += _ => Dispatcher.Invoke(ApplySettingsAndVisuals);
        _attendanceService.SnapshotChanged += _ => Dispatcher.Invoke(ApplySettingsAndVisuals);

        WireTrayEvents();

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(45)
        };
        _pollTimer.Tick += async (_, _) => await _attendanceService.RefreshStatusAsync();

        // 상태 아이콘 롱프레스(꾹 누르기) 감지 타이머
        _longPressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(LongPressHoldMs)
        };
        _longPressTimer.Tick += OnLongPressTimerTick;

        // 1초 주기로 위젯 비활성(10초 이상 미조작) 상태를 체크하여 (현재시간 - 출근시간, 휴게시간 제외) 표시를 갱신
        _idleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _idleTimer.Tick += OnIdleTimerTick;

        // 화면 경계에서 마우스 커서가 미세하게 흔들릴 때 확장/축소가 떨리지 않도록 디바운스 버퍼 적용
        _hoverCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _hoverCollapseTimer.Tick += (_, _) =>
        {
            _hoverCollapseTimer.Stop();
            if (!_isPointerDown && !IsMouseOver && !OverlayContextMenu.IsOpen && !_isLongPressArmed && !_isActionBusy)
            {
                SetExpandedMode(false);
                ApplySettingsAndVisuals();
            }
        };

        Loaded += OnMainWindowLoaded;
        Closed += (_, _) =>
        {
            _hoverCollapseTimer.Stop();
            _idleTimer.Stop();
            _pollTimer.Stop();
            _longPressTimer.Stop();
            _autoUpdateService.Dispose();
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
        RecordInteraction();
        ActionPanel.Visibility = Visibility.Collapsed;
        ApplySettingsAndVisuals();

        _snapService.Initialize();
        _snapService.SnapToTaskbarDefault();
        _idleTimer.Start();
        _autoUpdateService.StartBackgroundSchedule();

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

    private void RecordInteraction()
    {
        _lastInteractionUtc = DateTime.UtcNow;
        if (_isIdleElapsedVisible)
        {
            _isIdleElapsedVisible = false;
            ApplySettingsAndVisuals();
        }
    }

    private void OnIdleTimerTick(object? sender, EventArgs e)
    {
        if (_isPointerDown || IsMouseOver || OverlayContextMenu.IsOpen)
        {
            _lastInteractionUtc = DateTime.UtcNow;
            if (_isIdleElapsedVisible)
            {
                _isIdleElapsedVisible = false;
                ApplySettingsAndVisuals();
            }
            return;
        }

        double idleSeconds = (DateTime.UtcNow - _lastInteractionUtc).TotalSeconds;
        bool shouldShowElapsed = idleSeconds >= IdleSecondsBeforeShowingElapsed &&
                                 _attendanceService.CurrentSnapshot.Status == WorkStatusKind.Working;

        if (shouldShowElapsed || _isIdleElapsedVisible)
        {
            _isIdleElapsedVisible = shouldShowElapsed;
            ApplySettingsAndVisuals();
        }
    }

    private static string? FormatWorkingElapsedTime(AttendanceSnapshot snapshot)
    {
        return snapshot.GetWorkingElapsedExcludingBreaks(DateTime.Now);
    }

    private void ApplySettingsAndVisuals()
    {
        var settings = _settingsService.Current;
        var snapshot = _attendanceService.CurrentSnapshot;

        Topmost = settings.AlwaysOnTop;
        ShowInTaskbar = false;
        CapsuleBorder.Opacity = settings.WidgetOpacity;

        CapsuleScaleTransform.ScaleX = settings.WidgetScale;
        CapsuleScaleTransform.ScaleY = settings.WidgetScale;

        bool isLightTheme = _settingsService.IsEffectiveLightTheme();
        System.Windows.Media.Color iconFgColor;
        if (isLightTheme)
        {
            CapsuleBorder.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF"));
            CapsuleBorder.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E4E4E7"));
            ActionDivider.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E4E4E7"));
            iconFgColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#52525B");
        }
        else
        {
            CapsuleBorder.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#09090B"));
            CapsuleBorder.BorderBrush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#27272A"));
            ActionDivider.Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#27272A"));
            iconFgColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A1A1AA");
        }

        ApplyGhostButtonTheme(iconFgColor);

        System.Windows.Media.Color workingColor = AppSettingsService.ParseColorOrDefault(settings.WorkingColorHex, "#10B981");
        System.Windows.Media.Color offWorkColor = AppSettingsService.ParseColorOrDefault(settings.OffWorkColorHex, "#EF4444");

        bool isWorking = snapshot.Status == WorkStatusKind.Working;
        System.Windows.Media.Color activeStatusColor = isWorking ? workingColor : offWorkColor;
        var activeBrush = new SolidColorBrush(activeStatusColor);

        // 1. Sun (근무중) / Moon (퇴근) Status Chip
        if (_isLongPressArmed && _isCursorInsideDuringLongPress)
        {
            // 꾹 누르기 완료 시 목표 상태 색상으로 배지를 강조 표시 (마우스가 위젯 밖으로 나가면 해제됨)
            System.Windows.Media.Color readyColor = isWorking ? offWorkColor : workingColor;
            StatusBadgeBorder.Background = new SolidColorBrush(readyColor);
            StatusBadgeBorder.BorderBrush = new SolidColorBrush(readyColor);
            StatusIconRect.OpacityMask = (System.Windows.Media.Brush)Resources[isWorking ? "BrushIconSun" : "BrushIconMoon"];
            StatusIconRect.Fill = GetContrastingTextBrush(readyColor);
        }
        else
        {
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
        }

        string? elapsedText = isWorking ? FormatWorkingElapsedTime(snapshot) : null;
        string actionHint = isWorking ? "길게 눌러 퇴근" : "길게 눌러 출근";
        StatusBadgeBorder.ToolTip = isWorking && !string.IsNullOrWhiteSpace(elapsedText)
            ? $"근무중 ({elapsedText}) · {snapshot.SubText} ({actionHint})"
            : $"{(isWorking ? "근무중" : "퇴근")} · {snapshot.SubText} ({actionHint})";

        // 2. 드래그 중에는 무조건 미니모드(텍스트 숨김), 평소에는 10초 이상 미조작 시에만 근무중인 시간 표시
        if (_hasMovedDuringDrag)
        {
            SubStatusText.Text = string.Empty;
            SubStatusText.Visibility = Visibility.Collapsed;
        }
        else if (_isActionBusy)
        {
            SubStatusText.Text = "...";
            SubStatusText.Foreground = activeBrush;
            SubStatusText.Visibility = Visibility.Visible;
        }
        else if (snapshot.Status == WorkStatusKind.NeedsLogin)
        {
            SubStatusText.Text = "로그인";
            SubStatusText.Foreground = activeBrush;
            SubStatusText.Visibility = Visibility.Visible;
        }
        else if (_isIdleElapsedVisible && isWorking && !string.IsNullOrWhiteSpace(elapsedText))
        {
            SubStatusText.Text = elapsedText;
            SubStatusText.Foreground = activeBrush;
            SubStatusText.Visibility = Visibility.Visible;
        }
        else
        {
            SubStatusText.Text = string.Empty;
            SubStatusText.Visibility = Visibility.Collapsed;
        }

        ImageSource dynamicIcon = _trayService.UpdateTrayAndWindowIcon(snapshot, activeStatusColor);
        Icon = dynamicIcon;

        if (IsLoaded && !_isPointerDown)
        {
            UpdateLayout();
            _snapService.AlignToSnappedEdges();
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

    private void OnLongPressTimerTick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();

        if (!_isPointerDown || _hasMovedDuringDrag || !_isPressOnStatusIcon || _isActionBusy)
            return;

        if (!_snapService.IsCursorInsideWindow())
            return;

        _isLongPressArmed = true;
        _isCursorInsideDuringLongPress = true;
        ApplySettingsAndVisuals();
    }

    private void OnOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RecordInteraction();
        _hoverCollapseTimer.Stop();

        var dep = e.OriginalSource as DependencyObject;
        if (dep != null && IsInsideButton(dep))
            return;

        _isPointerDown = true;
        _hasMovedDuringDrag = false;
        _isLongPressArmed = false;
        _isCursorInsideDuringLongPress = true;
        _isPressOnStatusIcon = dep == null || !IsInsideElement(dep, ActionPanel);

        _longPressTimer.Stop();
        if (_isPressOnStatusIcon && !_isActionBusy)
        {
            _longPressTimer.Start();
        }

        _snapService.BeginDrag();
        ((UIElement)sender).CaptureMouse();
    }

    private void OnOverlayMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        RecordInteraction();

        if (!_isPointerDown)
            return;

        // 롱프레스(꾹 누르기)가 활성화된 상태에서는 창을 드래그하지 않고,
        // 커서가 위젯 내부에 있는지 여부만 실시간 추적 (위젯 밖으로 나가면 무시)
        if (_isLongPressArmed)
        {
            bool inside = _snapService.IsCursorInsideWindow();
            if (inside != _isCursorInsideDuringLongPress)
            {
                _isCursorInsideDuringLongPress = inside;
                ApplySettingsAndVisuals();
            }
            return;
        }

        if (!_hasMovedDuringDrag && _snapService.HasExceededDragThreshold())
        {
            _longPressTimer.Stop();
            _hasMovedDuringDrag = true;

            // 드래그 시작 시 즉시 미니모드로 축소하여 경계 스냅 기준 크기를 고정
            ActionPanel.Visibility = Visibility.Collapsed;
            SubStatusText.Visibility = Visibility.Collapsed;
            UpdateLayout();
            _snapService.AdjustDragOriginAfterMiniCollapse();
        }

        if (_hasMovedDuringDrag)
        {
            _snapService.UpdateDragPositionFromCursor();
        }
    }

    private async void OnOverlayMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        RecordInteraction();
        _longPressTimer.Stop();

        if (!_isPointerDown)
            return;

        bool wasDragging = _hasMovedDuringDrag;
        bool wasLongPressArmed = _isLongPressArmed;
        bool releasedInsideWidget = _snapService.IsCursorInsideWindow();

        _isPointerDown = false;
        _hasMovedDuringDrag = false;
        _isPressOnStatusIcon = false;
        _isLongPressArmed = false;
        _isCursorInsideDuringLongPress = false;
        _snapService.IsDraggingOverlay = false;
        ((UIElement)sender).ReleaseMouseCapture();

        ApplySettingsAndVisuals();

        if (wasDragging)
        {
            UpdateLayout();
            _snapService.EvaluateMagneticSnap(applyPosition: true);
            return;
        }

        if (wasLongPressArmed)
        {
            // 마우스가 위젯 바깥으로 나간 상태에서 press up 한 경우 무시
            if (!releasedInsideWidget)
            {
                if (!IsMouseOver)
                {
                    SetExpandedMode(false);
                }
                return;
            }

            await HandleStatusIconLongPressReleaseAsync();
            return;
        }

        if (IsMouseOver)
        {
            SetExpandedMode(true);
        }
    }

    private void OnOverlayMouseWheel(object sender, MouseWheelEventArgs e)
    {
        RecordInteraction();

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
        RecordInteraction();
        _hoverCollapseTimer.Stop();

        if (!_isPointerDown)
        {
            SetExpandedMode(true);
        }
    }

    private void OnOverlayMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        RecordInteraction();

        if (!_isPointerDown)
        {
            _hoverCollapseTimer.Stop();
            _hoverCollapseTimer.Start();
        }
    }

    private void SetExpandedMode(bool expanded)
    {
        Visibility target = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (ActionPanel.Visibility != target)
        {
            ActionPanel.Visibility = target;
            if (IsLoaded && !_isPointerDown)
            {
                UpdateLayout();
                _snapService.AlignToSnappedEdges();
            }
        }
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

    private static bool IsInsideElement(DependencyObject? source, DependencyObject target)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, target))
                return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private async Task HandleStatusIconLongPressReleaseAsync()
    {
        if (_isActionBusy)
            return;

        var snapshot = _attendanceService.CurrentSnapshot;

        // 근무중일 때 꾹 누르고 위젯 안에서 떼면 "퇴근 할래요?" Y/N 팝업 표시
        if (snapshot.Status == WorkStatusKind.Working)
        {
            var confirmPopup = new ConfirmClockOutWindow(this);
            bool confirmed = confirmPopup.ShowDialog() == true && confirmPopup.IsConfirmed;
            if (!IsMouseOver && !_isPointerDown)
            {
                SetExpandedMode(false);
            }
            if (!confirmed)
                return;

            await ExecuteClockActionAsync(targetClockIn: false);
            return;
        }

        // 퇴근 상태에서 다시 꾹 누르고 위젯 안에서 떼면 팝업 없이 출근으로 바로 변경
        await ExecuteClockActionAsync(targetClockIn: true);
    }

    private async void OnMenuClockActionClick(object sender, RoutedEventArgs e)
    {
        RecordInteraction();
        var snapshot = _attendanceService.CurrentSnapshot;
        if (snapshot.Status == WorkStatusKind.NeedsLogin)
        {
            OpenPortalWindow();
            return;
        }

        if (snapshot.Status == WorkStatusKind.Working)
        {
            var confirmPopup = new ConfirmClockOutWindow(this);
            bool confirmed = confirmPopup.ShowDialog() == true && confirmPopup.IsConfirmed;
            if (!confirmed)
                return;

            await ExecuteClockActionAsync(targetClockIn: false);
        }
        else
        {
            await ExecuteClockActionAsync(targetClockIn: true);
        }
    }

    private async Task ExecuteClockActionAsync(bool targetClockIn)
    {
        if (_isActionBusy)
            return;

        _isActionBusy = true;
        ApplySettingsAndVisuals();

        try
        {
            // 위젯 상태를 즉시 목표 상태(출근/퇴근)로 반영하고 다우오피스 웹 액션도 함께 트리거
            _attendanceService.SetManualStatusOverride(targetClockIn ? WorkStatusKind.Working : WorkStatusKind.OffWork);
            await _attendanceService.TriggerClockActionAsync(targetClockIn);
        }
        finally
        {
            _isActionBusy = false;
            ApplySettingsAndVisuals();
        }
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        RecordInteraction();
        _hoverCollapseTimer.Stop();
        OverlayContextMenu.Placement = PlacementMode.MousePoint;
        MenuAlwaysOnTop.IsChecked = _settingsService.Current.AlwaysOnTop;
        MenuRunAtStartup.IsChecked = _settingsService.Current.RunAtStartup;

        var status = _attendanceService.CurrentSnapshot.Status;
        MenuClockActionItem.Header = status == WorkStatusKind.NeedsLogin
            ? "다우오피스 로그인..."
            : (status == WorkStatusKind.Working ? "퇴근하기" : "출근하기");
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        RecordInteraction();

        if (_settingsWindow != null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settingsService, _autoUpdateService);
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
        RecordInteraction();
        OpenPortalWindow();
    }

    private void OpenPortalWindow()
    {
        _portalWindow ??= new AttendanceWebWindow(_attendanceService);
        _portalWindow.ShowInteractive();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        RecordInteraction();
        _attendanceService.ClearManualStatusOverride();
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
        ActionPanel.Visibility = Visibility.Collapsed;
        UpdateLayout();
        _snapService.SnapToTaskbarDefault();
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
