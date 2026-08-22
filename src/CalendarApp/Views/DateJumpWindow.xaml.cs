using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CalendarApp.ViewModels;

namespace CalendarApp.Views;

public partial class DateJumpWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Point _screenCenter;
    private bool _allowDismiss;

    /// <summary>Required by WPF's parser; the app uses the parameterized constructor below instead.</summary>
    public DateJumpWindow() : this(null!, DateTime.Today, new Point())
    {
    }

    public DateJumpWindow(MainViewModel viewModel, DateTime displayMonth, Point screenCenter)
    {
        _viewModel = viewModel;
        _screenCenter = screenCenter;
        InitializeComponent();

        YearBox.Text = displayMonth.Year.ToString(CultureInfo.InvariantCulture);
        MonthBox.Text = displayMonth.Month.ToString(CultureInfo.InvariantCulture);
        DayBox.Text = string.Empty;

        Closed += (_, _) => _allowDismiss = false;
        Deactivated += OnDeactivated;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Clicking another window or the desktop deactivates this independent
        // topmost window. It is the reliable light-dismiss equivalent without
        // using an owned Popup.
        if (_allowDismiss)
        {
            Close();
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PositionAtPointer();
        YearBox.Focus();
        _allowDismiss = true;
    }

    private void PositionAtPointer()
    {
        var width = Math.Max(1, ActualWidth);
        var height = Math.Max(1, ActualHeight);
        Left = _screenCenter.X - width / 2;
        Top = _screenCenter.Y - height / 2;
    }

    private void OnJumpDateKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnJumpDateClicked(sender, e);
            e.Handled = true;
        }
    }

    private void OnJumpDateClicked(object? sender, RoutedEventArgs e)
    {
        if (TryGetJumpDate(out var date))
        {
            _viewModel.JumpToDate(date);
            Close();
        }
        else
        {
            JumpDateError.Visibility = Visibility.Visible;
        }
    }

    private bool TryGetJumpDate(out DateTime date)
    {
        date = default;

        if (!int.TryParse(YearBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            || year is < 1 or > 9999)
        {
            return false;
        }

        if (!int.TryParse(MonthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var month)
            || month is < 1 or > 12)
        {
            return false;
        }

        var day = 1;
        if (!string.IsNullOrWhiteSpace(DayBox.Text)
            && (!int.TryParse(DayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out day)
                || day is < 1 or > 31))
        {
            return false;
        }

        try
        {
            date = new DateTime(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
