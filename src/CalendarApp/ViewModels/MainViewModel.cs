using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CalendarApp.Data;
using CalendarApp.Models;
using CalendarApp.Platform;

namespace CalendarApp.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly CultureInfo ZhCn = CultureInfo.GetCultureInfo("zh-CN");

    private static readonly Brush SelectedSegmentBrush =
        new SolidColorBrush(Color.FromArgb(0x2E, 255, 255, 255));
    private static readonly Brush NormalSegmentBrush = Brushes.Transparent;

    private readonly AppServices _services;
    private DateTime _selectedDate = DateTime.Today;

    /// <summary>
    /// False until the user explicitly picks a date. While false, today's cell
    /// keeps its translucent-white "today" look instead of the accent fill.
    /// </summary>
    private bool _hasExplicitSelection;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _services.Items.Changed += () => Refresh();
        _services.Items.ItemsChanged += () =>
            Application.Current?.Dispatcher.BeginInvoke(ReloadItems);
        Refresh();

        // 恢复上次关闭时显示的子窗口（日历 / 事项）
        if (LastViewSettings.Load().IsTasksView)
        {
            SetView(calendar: false);
        }
    }

    /// <summary>Raised when the user asks to open the data-file/settings window.</summary>
    public event Action? RequestOpenSettings;

    /// <summary>Raised when the user asks to hide the glass panel (app-internal only).</summary>
    public event Action? RequestHide;

    /// <summary>Raised when the user asks to create a new item (opens the editor window).</summary>
    public event Action? RequestNewItem;

    /// <summary>Raised when the user asks to edit an existing item.</summary>
    public event Action<ScheduleEventItem>? RequestEditItem;

    /// <summary>Raised after an item is saved, so views can refresh.</summary>
    public event Action? ItemsChanged;

    public ObservableCollection<ScheduleEventItem> Items { get; } = [];

    public ObservableCollection<CalendarDayCell> CalendarDays { get; } = [];

    public ObservableCollection<ScheduleEventItem> DayEvents { get; } = [];

    [ObservableProperty]
    public partial string TodayText { get; set; } = string.Empty;

    /// <summary>Lunar date shown under the Gregorian date in the title bar, e.g. "农历 七月初九".</summary>
    [ObservableProperty]
    public partial string TodayLunarText { get; set; } = string.Empty;

    /// <summary>
    /// True while the selected day is not today, or the displayed month is not
    /// the current month — drives the "back to today" (↩) button visibility.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowBackToToday { get; set; }

    [ObservableProperty]
    public partial string DataFileName { get; set; } = "未打开存储文件夹";

    /// <summary>The storage folder root when open; used by the item editor window sizing.</summary>
    public string? StoragePath => _services.Items.RootPath;

    [ObservableProperty]
    public partial string BackgroundColor { get; set; } = "#0D1117";

    [ObservableProperty]
    public partial double BackgroundOpacity { get; set; } = 0.6;

    [ObservableProperty]
    public partial double BlurRadius { get; set; } = 15;

    partial void OnBlurRadiusChanged(double value)
    {
        if (Application.Current?.MainWindow is not null)
            BlurBackdrop.SetBlurRadius(Application.Current.MainWindow, value);
    }

    /// <summary>Composite background brush for the glass panel.</summary>
    [ObservableProperty]
    public partial Brush PanelBackground { get; set; } = MakeBrush("#0D1117", 0.6);

    [ObservableProperty]
    public partial bool HasDataFile { get; set; }

    [ObservableProperty]
    public partial bool HasItems { get; set; }

    // ── View switching（顶栏分段按钮：日历 / 事项） ─────────────────────

    [ObservableProperty]
    public partial bool IsCalendarView { get; set; } = true;

    [ObservableProperty]
    public partial Brush CalendarButtonBrush { get; set; } = SelectedSegmentBrush;

    [ObservableProperty]
    public partial Brush TaskButtonBrush { get; set; } = NormalSegmentBrush;

    // ── Calendar state ────────────────────────────────────────────────

    /// <summary>First day of the displayed month.</summary>
    [ObservableProperty]
    public partial DateTime DisplayMonth { get; set; } = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty]
    public partial string CalendarTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSelectedDayEvents { get; set; }


    [RelayCommand]
    private void OpenSettings() => RequestOpenSettings?.Invoke();

    [RelayCommand]
    private void Hide() => RequestHide?.Invoke();

    [RelayCommand]
    private void NewItem() => RequestNewItem?.Invoke();

    /// <summary>Inserts a new item into the folder store and refreshes the panel.</summary>
    public void AddItem(ScheduleEvent item)
    {
        _services.Items.Insert(item);
        ItemsChanged?.Invoke();
        Refresh();
    }

    /// <summary>
    /// Replaces an existing item's file and refreshes the panel. The store moves
    /// the file between items/ and archive/ based on the new deadline — e.g.
    /// updating a completed (archived) item's time into the future brings it
    /// back into items/.
    /// </summary>
    public void UpdateItem(ScheduleEvent oldItem, ScheduleEvent newItem)
    {
        _services.Items.Update(oldItem, newItem);
        ItemsChanged?.Invoke();
        Refresh();
    }

    /// <summary>Asks the host to open the editor pre-filled with the given item.</summary>
    public void EditItem(ScheduleEventItem item) => RequestEditItem?.Invoke(item);

    /// <summary>Sets the deadline to the long-term sentinel (2099-01-01 12:00) and rewrites the file.</summary>
    public void MarkLongTerm(ScheduleEventItem item)
    {
        item.Event.EndsAt = new DateTimeOffset(new DateTime(2099, 1, 1, 12, 0, 0));
        UpdateItem(item.Event, item.Event);
    }

    /// <summary>Marks the item done by moving its deadline just into the past; the list hides it on reload.</summary>
    public void MarkCompleted(ScheduleEventItem item)
    {
        item.Event.EndsAt = DateTimeOffset.Now.AddSeconds(-1);
        UpdateItem(item.Event, item.Event);
    }

    /// <summary>Soft-deletes the item by moving its file into the deleted/ folder.</summary>
    public void DeleteItem(ScheduleEventItem item)
    {
        _services.Items.MoveToDeleted(item.Event);
        ItemsChanged?.Invoke();
        Refresh();
    }

    [RelayCommand]
    private void ShowCalendar() => SetView(calendar: true);

    [RelayCommand]
    private void ShowTasks()
    {
        _selectedDate = DateTime.Today;
        _hasExplicitSelection = false;
        UpdateHeaderState();
        SetView(calendar: false);
    }

    [RelayCommand]
    private void PrevMonth() => SetDisplayMonth(DisplayMonth.AddMonths(-1));

    [RelayCommand]
    private void NextMonth() => SetDisplayMonth(DisplayMonth.AddMonths(1));

    [RelayCommand]
    private void SelectDay(CalendarDayCell? cell)
    {
        if (cell is null)
        {
            return;
        }

        _selectedDate = cell.Date;
        _hasExplicitSelection = true;
        UpdateHeaderState();
        RebuildCalendar();
        LoadDayEvents();
    }

    /// <summary>Jumps the calendar back to today (selected day + displayed month) and hides the ↩ button.</summary>
    [RelayCommand]
    private void GoToToday()
    {
        _selectedDate = DateTime.Today;
        _hasExplicitSelection = false;
        SetDisplayMonth(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
        LoadDayEvents();
    }

    /// <summary>
    /// Jumps the calendar to the given date: displays its month, selects it,
    /// and refreshes the header and that day's events. Used by the month-title flyout.
    /// </summary>
    public void JumpToDate(DateTime date)
    {
        _selectedDate = date.Date;
        _hasExplicitSelection = true;
        SetDisplayMonth(new DateTime(date.Year, date.Month, 1));
        LoadDayEvents();
    }

    private void SetView(bool calendar)
    {
        IsCalendarView = calendar;
        CalendarButtonBrush = calendar ? SelectedSegmentBrush : NormalSegmentBrush;
        TaskButtonBrush = calendar ? NormalSegmentBrush : SelectedSegmentBrush;
        if (!calendar)
        {
            LoadUpcomingItems();
        }

        // 记住当前子窗口，下次启动恢复
        new LastViewSettings { View = calendar ? "calendar" : "tasks" }.Save();
    }

    private void SetDisplayMonth(DateTime month)
    {
        DisplayMonth = new DateTime(month.Year, month.Month, 1);
        CalendarTitle = $"{DisplayMonth.Year}年{DisplayMonth.Month}月";
        RebuildCalendar();
        UpdateHeaderState();
    }

    /// <summary>
    /// Title-bar date follows the selected day; the ↩ button shows whenever the
    /// selected day or displayed month is not today's.
    /// </summary>
    private void UpdateHeaderState()
    {
        TodayText = _selectedDate.ToString("M月d日 dddd", ZhCn);
        TodayLunarText = "农历 " + LunarCalendar.GetMonthDayText(_selectedDate);
        ShowBackToToday = _selectedDate.Date != DateTime.Today
            || DisplayMonth.Year != DateTime.Today.Year
            || DisplayMonth.Month != DateTime.Today.Month;
    }


    /// <summary>Builds the 6x7 day grid (Sunday-first) with event/today/selection markers.</summary>
    private void RebuildCalendar()
    {
        CalendarDays.Clear();
        if (!_services.Items.IsOpen)
        {
            return;
        }

        var first = DisplayMonth;
        var gridStart = first.AddDays(-(int)first.DayOfWeek);
        var events = _services.Items.GetUpcoming(
            new DateTimeOffset(first), new DateTimeOffset(first.AddMonths(1)), limit: 5000);
        var eventDays = events.Select(e => e.StartsAt.ToLocalTime().Date).ToHashSet();
        var today = DateTime.Today;

        for (var i = 0; i < 42; i++)
        {
            var d = gridStart.AddDays(i);
            CalendarDays.Add(new CalendarDayCell
            {
                Date = d,
                IsInMonth = d.Month == first.Month,
                HasEvents = eventDays.Contains(d),
                IsToday = d == today,
                IsSelected = _hasExplicitSelection && d == _selectedDate,
                LunarText = LunarCalendar.GetDayName(d),
            });
        }
    }

    private void LoadDayEvents()
    {
        DayEvents.Clear();
        if (_services.Items.IsOpen)
        {
            foreach (var item in _services.Items.GetEventsOnDay(new DateTimeOffset(_selectedDate)))
            {
                DayEvents.Add(new ScheduleEventItem { Event = item });
            }
        }

        HasSelectedDayEvents = DayEvents.Count > 0;
    }

    /// <summary>未到期事项：截止时间尚未过去的所有事项。</summary>
    private void LoadUpcomingItems()
    {
        Items.Clear();
        if (_services.Items.IsOpen)
        {
            var now = DateTimeOffset.Now;
            foreach (var item in _services.Items.GetItems()
                         .Where(i => i.EndsAt is null || i.EndsAt >= now)
                         .Take(200))
            {
                Items.Add(new ScheduleEventItem { Event = item });
            }
        }

        HasItems = Items.Count > 0;
    }

    /// <summary>
    /// Reloads item-derived data (calendar markers, selected-day events, tasks list)
    /// after an external change to the storage folder, without resetting the user's
    /// selected date or active view.
    /// </summary>
    private void ReloadItems()
    {
        if (!_services.Items.IsOpen)
        {
            return;
        }

        // Files may have been added/edited outside the app; sweep any items
        // whose deadline has passed into archive/ (idempotent).
        _services.Items.ArchiveExpiredItems();

        RebuildCalendar();
        LoadDayEvents();
        LoadUpcomingItems();
    }

    /// <summary>Reloads the background color/opacity and accent color from settings.json.</summary>
    public void ReloadAppearance()
    {
        BackgroundColor = _services.Settings.BackgroundColor;
        BackgroundOpacity = _services.Settings.BackgroundOpacity;
        BlurRadius = _services.Settings.BlurRadius;
        Theme.ApplyAccent(_services.Settings.AccentColor);
        RebuildBackground();
    }

    public void Refresh()
    {
        // Items whose deadline has passed belong in archive/ per 存储目录设计.md.
        if (_services.Items.IsOpen)
        {
            _services.Items.ArchiveExpiredItems();
        }

        _selectedDate = DateTime.Today;
        _hasExplicitSelection = false;
        UpdateHeaderState();

        ReloadAppearance();

        if (_services.Items.IsOpen)
        {
            DataFileName = Path.GetFileName(_services.Items.RootPath!);
            HasDataFile = true;
        }
        else
        {
            DataFileName = "未打开存储文件夹";
            HasDataFile = false;
        }

        SetDisplayMonth(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
        RebuildCalendar();
        LoadDayEvents();
        LoadUpcomingItems();
    }

    private void RebuildBackground()
    {
        if (TryParseHexColor(BackgroundColor, out var color))
        {
            PanelBackground = new SolidColorBrush(color) { Opacity = BackgroundOpacity };
        }
    }

    private static Brush MakeBrush(string hex, double opacity)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static bool TryParseHexColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }
}


/// <summary>Lightweight display projection of a schedule item for the glass panel.</summary>
public sealed partial class ScheduleEventItem : ObservableObject
{
    public required ScheduleEvent Event { get; init; }

    /// <summary>Whether the item's detail box is expanded in the tasks view.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public string Title => Event.Title;

    public string TimeText => Event.AllDay ? "全天" : Event.StartsAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Start date as "MM.dd", e.g. "08.21".</summary>
    public string StartDateText => Event.StartsAt.ToLocalTime().ToString("MM.dd", CultureInfo.InvariantCulture);

    /// <summary>End date as "MM.dd", or "-" for long-term items (year 2099).</summary>
    public string EndDateText
    {
        get
        {
            var end = (Event.EndsAt ?? Event.StartsAt).ToLocalTime();
            return end.Year == 2099 ? "-" : end.ToString("MM.dd", CultureInfo.InvariantCulture);
        }
    }

    public string LocationText => Event.Location;

    public bool HasLocation => !string.IsNullOrWhiteSpace(Event.Location);

    /// <summary>Whitespace-collapsed notes used for the two-line preview under the title.</summary>
    public string ContentPreview => CollapseWhitespace(Event.Notes);

    /// <summary>Full notes shown in the expanded detail box.</summary>
    public string DetailText => string.IsNullOrWhiteSpace(Event.Notes) ? "（无内容）" : Event.Notes;

    public bool HasContent => !string.IsNullOrWhiteSpace(Event.Notes);

    /// <summary>Tag text shown in the item bar chip.</summary>
    public string TagText => Event.Tags;

    public bool HasTag => !string.IsNullOrWhiteSpace(Event.Tags);

    /// <summary>Semi-transparent colored background for the tag chip, keyed by the first tag.</summary>
    public Brush TagBackground
    {
        get
        {
            var color = (Color)ColorConverter.ConvertFromString(ResolveTagColor(Event.Tags));
            return new SolidColorBrush(Color.FromArgb(0x4D, color.R, color.G, color.B));
        }
    }

    /// <summary>Priority + tags line shown in the day-events view, e.g. "P1 · 工作-生活".</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            parts.Add($"P{Event.Priority}");
            if (!string.IsNullOrWhiteSpace(Event.Tags))
            {
                parts.Add(Event.Tags);
            }
            return string.Join(" · ", parts);
        }
    }

    public bool HasMeta => true;

    private static readonly Dictionary<string, string> TagColors = new()
    {
        ["工作"] = "#3B82F6",
        ["生活"] = "#22C55E",
        ["学习"] = "#8B5CF6",
        ["健康"] = "#EF4444",
        ["财务"] = "#F59E0B",
    };

    /// <summary>Resolves a tag (or the first of "工作-生活" style multi-tags) to its color hex.</summary>
    private static string ResolveTagColor(string tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return "#64748B";
        }

        foreach (var part in tags.Split('-'))
        {
            if (TagColors.TryGetValue(part, out var color))
            {
                return color;
            }
        }

        return "#64748B";
    }

    private static string CollapseWhitespace(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        var lastSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (lastSpace) continue;
                lastSpace = true;
                sb.Append(' ');
            }
            else
            {
                lastSpace = false;
                sb.Append(c);
            }
        }
        return sb.ToString().Trim();
    }
}


/// <summary>A single day cell of the month calendar grid.</summary>
public sealed class CalendarDayCell
{
    public required DateTime Date { get; init; }

    public bool IsInMonth { get; init; }

    public bool HasEvents { get; init; }

    public bool IsToday { get; init; }

    public bool IsSelected { get; init; }

    public string DayText => Date.Day.ToString(CultureInfo.InvariantCulture);

    /// <summary>Lunar day name shown under the Gregorian numeral, e.g. "初五".</summary>
    public string LunarText { get; init; } = string.Empty;

    /// <summary>Out-of-month filler cells are faded.</summary>
    public double CellOpacity => IsInMonth ? 1.0 : 0.35;

    public Brush Background => IsSelected
        ? Theme.Accent
        : IsToday
            ? new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255))
            : Brushes.Transparent;

    public Brush Foreground => IsSelected
        ? Brushes.White
        : new SolidColorBrush(Color.FromArgb(0xD9, 255, 255, 255));
}
