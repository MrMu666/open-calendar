using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CalendarApp.Platform;
using CalendarApp.ViewModels;

namespace CalendarApp.Views;

public partial class MainWindow : Window
{
    private DateJumpWindow? _dateJumpWindow;
    private bool _openingDateJumpWindow;

    public MainWindow()
    {
        InitializeComponent();
        PreviewMouseDown += OnMainWindowPointerPressedForDesktopLayer;
        Loaded += OnLoaded;
        Closed += OnClosed;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Rainmeter: ShowBlur() after composition check.
        BlurBackdrop.EnableBlur(this);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        BlurBackdrop.DisableBlur(this);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Rainmeter re-creates the blur region on resize (BLURMODE_FULL).
        BlurBackdrop.UpdateBlurRegion(this);
    }

    private void OnDayCellPressed(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed
            && sender is Border { DataContext: CalendarDayCell cell }
            && DataContext is MainViewModel vm)
        {
            vm.SelectDayCommand.Execute(cell);
        }
    }

    private void OnItemBarPressed(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not Border { DataContext: ScheduleEventItem item })
        {
            return;
        }

        for (var v = e.OriginalSource as DependencyObject; v is not null; v = System.Windows.Media.VisualTreeHelper.GetParent(v))
        {
            if (v is Button) return;
        }

        item.IsExpanded = !item.IsExpanded;
    }

    private void OnItemEditClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ScheduleEventItem item }
            && DataContext is MainViewModel vm)
        {
            vm.EditItem(item);
        }
    }

    private void OnMarkLongTerm(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ScheduleEventItem item }
            && DataContext is MainViewModel vm)
        {
            vm.MarkLongTerm(item);
        }
    }

    private void OnMarkCompleted(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ScheduleEventItem item }
            && DataContext is MainViewModel vm)
        {
            vm.MarkCompleted(item);
        }
    }

    private void OnMonthTitlePressed(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        e.Handled = true;
        if (DataContext is not MainViewModel vm || _dateJumpWindow is not null) return;

        var localPoint = e.GetPosition(this);
        var screenCenter = new Point(Left + localPoint.X, Top + localPoint.Y);

        var dialog = new DateJumpWindow(vm, vm.DisplayMonth, screenCenter);
        _dateJumpWindow = dialog;
        DesktopGlassBehavior.NotifyTransientWindow(this, true);
        _openingDateJumpWindow = true;
        PreviewMouseDown += OnMainWindowPointerPressedWhileDateDialogOpen;
        dialog.Closed += (_, _) =>
        {
            DesktopGlassBehavior.NotifyTransientWindow(this, false);
            PreviewMouseDown -= OnMainWindowPointerPressedWhileDateDialogOpen;
            _openingDateJumpWindow = false;
            if (ReferenceEquals(_dateJumpWindow, dialog)) _dateJumpWindow = null;
        };
        dialog.Show();
    }

    private void OnMainWindowPointerPressedForDesktopLayer(object? sender, MouseButtonEventArgs e)
    {
        DesktopGlassBehavior.NotifyMainWindowInteraction(this);
    }

    private void OnMainWindowPointerPressedWhileDateDialogOpen(object? sender, MouseButtonEventArgs e)
    {
        if (_openingDateJumpWindow) { _openingDateJumpWindow = false; return; }
        _dateJumpWindow?.Close();
    }

    private void OnTitleBarPressed(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        for (var v = e.OriginalSource as DependencyObject; v is not null; v = System.Windows.Media.VisualTreeHelper.GetParent(v))
        {
            if (v is Button) return;
        }

        DragMove();
    }
}
