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
        BlurBackdrop.Attach(this, WallpaperImage, WallpaperBlur);
        if (DataContext is MainViewModel vm)
        {
            BlurBackdrop.SetBlurRadius(this, vm.BlurRadius);
        }
        UpdateBackdropOpacityMask();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        BlurBackdrop.Detach(this);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        BlurBackdrop.ScheduleRefresh(this);
        UpdateBackdropOpacityMask();
    }

    /// <summary>Applies a rounded-rectangle OpacityMask to the wallpaper Image
    /// so the blur never bleeds past the panel's four rounded corners.
    /// OpacityMask is purely visual — it does NOT affect hit testing, unlike
    /// the Clip property which expands the element's hit-test geometry and
    /// can block EdgeResizer's window-level PreviewMouseLeftButtonDown.</summary>
    private void UpdateBackdropOpacityMask()
    {
        if (WallpaperImage is null) return;
        var w = WallpaperImage.ActualWidth;
        var h = WallpaperImage.ActualHeight;
        if (w <= 0 || h <= 0) return;
        WallpaperImage.OpacityMask = new System.Windows.Media.DrawingBrush(
            new System.Windows.Media.GeometryDrawing(
                System.Windows.Media.Brushes.White, null,
                new System.Windows.Media.RectangleGeometry(
                    new System.Windows.Rect(0, 0, w, h), 16, 16)));
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

    private void OnDeleteItem(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ScheduleEventItem item }
            && DataContext is MainViewModel vm)
        {
            vm.DeleteItem(item);
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
