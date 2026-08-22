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
        // Capture every click in the panel, including clicks handled by child
        // buttons, so Show Desktop placement survives post-dialog interaction.
        AddHandler(
            Mouse.PreviewMouseDownEvent,
            OnMainWindowPointerPressedForDesktopLayer,
            handledEventsToo: true);
    }

    /// <summary>Clicking a calendar day cell selects it and shows that day's events.</summary>
    private void OnDayCellPressed(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed
            && sender is Border { DataContext: CalendarDayCell cell }
            && DataContext is MainViewModel vm)
        {
            vm.SelectDayCommand.Execute(cell);
        }
    }

    /// <summary>Clicking an item bar toggles its detail box, unless the click lands on the edit button.</summary>
    private void OnItemBarPressed(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not Border { DataContext: ScheduleEventItem item })
        {
            return;
        }

        for (var v = e.OriginalSource as DependencyObject; v is not null; v = System.Windows.Media.VisualTreeHelper.GetParent(v))
        {
            if (v is Button)
            {
                return;
            }
        }

        item.IsExpanded = !item.IsExpanded;
    }

    /// <summary>Clicking the edit button opens the editor pre-filled with the item.</summary>
    private void OnItemEditClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ScheduleEventItem item }
            && DataContext is MainViewModel vm)
        {
            vm.EditItem(item);
        }
    }

    /// <summary>Context-menu action: mark the item as a long-term task (deadline 2099-01-01).</summary>
    private void OnMarkLongTerm(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ScheduleEventItem item }
            && DataContext is MainViewModel vm)
        {
            vm.MarkLongTerm(item);
        }
    }

    /// <summary>Context-menu action: mark the item done (deadline moves just into the past).</summary>
    private void OnMarkCompleted(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: ScheduleEventItem item }
            && DataContext is MainViewModel vm)
        {
            vm.MarkCompleted(item);
        }
    }

    /// <summary>Opens the opaque owned date-jump dialog, prefilled with the displayed month.</summary>
    private void OnMonthTitlePressed(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        e.Handled = true;
        if (DataContext is not MainViewModel vm || _dateJumpWindow is not null)
        {
            return;
        }

        var localPoint = e.GetPosition(this);
        var screenCenter = new Point(
            Left + localPoint.X * 1.0,
            Top + localPoint.Y * 1.0);

        var dialog = new DateJumpWindow(vm, vm.DisplayMonth, screenCenter);
        _dateJumpWindow = dialog;
        DesktopGlassBehavior.NotifyTransientWindow(this, true);
        _openingDateJumpWindow = true;
        AddHandler(Mouse.PreviewMouseDownEvent,
            OnMainWindowPointerPressedWhileDateDialogOpen,
            handledEventsToo: true);
        dialog.Closed += (_, _) =>
        {
            DesktopGlassBehavior.NotifyTransientWindow(this, false);
            RemoveHandler(Mouse.PreviewMouseDownEvent,
                OnMainWindowPointerPressedWhileDateDialogOpen);
            _openingDateJumpWindow = false;
            if (ReferenceEquals(_dateJumpWindow, dialog))
            {
                _dateJumpWindow = null;
            }
        };

        // Deliberately do not assign the main window as Owner. The independent
        // Topmost window stays above the panel without controlling its visibility.
        dialog.Show();
    }

    private void OnMainWindowPointerPressedForDesktopLayer(object? sender, MouseButtonEventArgs e)
    {
        DesktopGlassBehavior.NotifyMainWindowInteraction(this);
    }

    private void OnMainWindowPointerPressedWhileDateDialogOpen(object? sender, MouseButtonEventArgs e)
    {
        if (_openingDateJumpWindow)
        {
            _openingDateJumpWindow = false;
            return;
        }

        _dateJumpWindow?.Close();
    }

    /// <summary>Left-drag on the title bar moves the window.</summary>
    private void OnTitleBarPressed(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // The gear settings button lives inside the title bar: presses that start
        // inside a button must not begin a window drag.
        for (var v = e.OriginalSource as DependencyObject; v is not null; v = System.Windows.Media.VisualTreeHelper.GetParent(v))
        {
            if (v is Button)
            {
                return;
            }
        }

        DragMove();
    }
}
