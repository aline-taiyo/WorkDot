using System.Windows;
using System.Windows.Input;

namespace WorkDot;

public partial class ConfirmClockOutWindow : Window
{
    public bool IsConfirmed { get; private set; }

    public ConfirmClockOutWindow(Window ownerWidget)
    {
        InitializeComponent();
        Owner = ownerWidget;
        ShowInTaskbar = false;

        Loaded += (_, _) =>
        {
            PositionNearWidget(ownerWidget);
            Activate();
            Focus();
        };
    }

    private void PositionNearWidget(Window widget)
    {
        var workArea = SystemParameters.WorkArea;

        double widgetLeft = widget.Left;
        double widgetTop = widget.Top;
        try
        {
            System.Windows.Point screenPx = widget.PointToScreen(new System.Windows.Point(0, 0));
            var source = PresentationSource.FromVisual(widget);
            if (source?.CompositionTarget != null)
            {
                System.Windows.Point dipPt = source.CompositionTarget.TransformFromDevice.Transform(screenPx);
                widgetLeft = dipPt.X;
                widgetTop = dipPt.Y;
            }
        }
        catch
        {
            // Fallback to widget.Left / widget.Top
        }

        double targetLeft = widgetLeft + (widget.ActualWidth - Width) / 2.0;
        double targetTop = widgetTop - Height - 8;

        // 위쪽 공간이 부족하면 위젯 아래쪽에 배치
        if (targetTop < workArea.Top + 4)
        {
            targetTop = widgetTop + widget.ActualHeight + 8;
        }

        targetLeft = Math.Clamp(targetLeft, workArea.Left + 6, workArea.Right - Width - 6);
        targetTop = Math.Clamp(targetTop, workArea.Top + 6, workArea.Bottom - Height - 6);

        Left = targetLeft;
        Top = targetTop;
    }

    private void OnYesClick(object sender, RoutedEventArgs e)
    {
        IsConfirmed = true;
        DialogResult = true;
        Close();
    }

    private void OnNoClick(object sender, RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
        Close();
    }

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Y || e.Key == Key.Enter)
        {
            IsConfirmed = true;
            DialogResult = true;
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.N || e.Key == Key.Escape)
        {
            IsConfirmed = false;
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }
}
