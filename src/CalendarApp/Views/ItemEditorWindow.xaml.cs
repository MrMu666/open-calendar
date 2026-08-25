using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CalendarApp.Data;
using CalendarApp.Models;
using CalendarApp.ViewModels;

namespace CalendarApp.Views;

public partial class ItemEditorWindow : Window
{
    private static readonly string[] TagChoices = ["工作", "生活", "学习", "健康", "财务"];

    private readonly MainViewModel _viewModel;
    private readonly Point _mainCenter;
    private readonly ScheduleEvent? _existing;
    private bool _allowClose;

    /// <summary>Required by WPF's parser; the app uses the parameterized constructor below instead.</summary>
    public ItemEditorWindow() : this(null!, 0, 0, new Point())
    {
    }

    public ItemEditorWindow(MainViewModel viewModel, double width, double height, Point mainCenter, ScheduleEvent? existing = null)
    {
        _viewModel = viewModel;
        _mainCenter = mainCenter;
        _existing = existing;

        Width = Math.Max(420, width);
        Height = Math.Max(360, height);

        // Position now (before first render) to avoid a flicker at (0,0).
        // ActualWidth/Height aren't known yet, so compute from the Width/Height we just set.
        var targetLeft = mainCenter.X - Width / 2;
        var targetTop = mainCenter.Y - Height / 2;
        var workArea = SystemParameters.WorkArea;
        Left = Clamp(targetLeft, workArea.Left, workArea.Right - Width);
        Top = Clamp(targetTop, workArea.Top, workArea.Bottom - Height);

        InitializeComponent();

        if (existing is not null)
        {
            LoadExisting(existing);
        }
        else
        {
            // 开始 = 本日，截止 = 一个月后
            var today = DateTime.Today;
            SetDateBoxes(StartYearBox, StartMonthBox, StartDayBox, today);
            SetDateBoxes(EndYearBox, EndMonthBox, EndDayBox, today.AddMonths(1));

            StartTimeBox.Text = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            EndTimeBox.Text = DateTime.Now.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture);

            TagBox.SelectedIndex = 0;        // 工作
            PriorityBox.SelectedIndex = 2;   // 一般
        }

        ContentRendered += OnContentRendered;
        Closing += OnClosing;
    }

    /// <summary>Pre-fills every field from an existing item for editing.</summary>
    private void LoadExisting(ScheduleEvent item)
    {
        TitleBox.Text = item.Title;
        ContentBox.Text = item.Notes;

        var start = item.StartsAt.ToLocalTime();
        SetDateBoxes(StartYearBox, StartMonthBox, StartDayBox, start.Date);
        StartTimeBox.Text = start.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (item.EndsAt is { } end)
        {
            var endLocal = end.ToLocalTime();
            SetDateBoxes(EndYearBox, EndMonthBox, EndDayBox, endLocal.Date);
            EndTimeBox.Text = endLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        else
        {
            // 长期事项：截止三框留空
            EndYearBox.Text = string.Empty;
            EndMonthBox.Text = string.Empty;
            EndDayBox.Text = string.Empty;
            EndTimeBox.Text = string.Empty;
        }

        var tagIndex = Array.IndexOf(TagChoices, item.Tags);
        TagBox.SelectedIndex = tagIndex >= 0 ? tagIndex : 0;

        var priorityIndex = item.Priority is >= 1 and <= 4 ? item.Priority - 1 : 2;
        PriorityBox.SelectedIndex = priorityIndex;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 只有点击“保存”或右上角 ❌ 才允许关闭；Alt+F4/Esc/外部关闭一律拦截。
        if (!_allowClose)
        {
            e.Cancel = true;
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // Re-clamp: ActualWidth/Height may differ from Width/Height after DPI / chrome.
        var workArea = SystemParameters.WorkArea;
        Left = Clamp(_mainCenter.X - ActualWidth / 2, workArea.Left, workArea.Right - ActualWidth);
        Top = Clamp(_mainCenter.Y - ActualHeight / 2, workArea.Top, workArea.Bottom - ActualHeight);

        ContentBox.Focus();
        RefreshPreview();
    }

    private void OnContentChanged(object? sender, TextChangedEventArgs e)
    {
        RefreshPreview();
        UpdateDerivedTitle();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close();
    }

    private void OnTitleBarPressed(object? sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
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

        DragMove();
    }

    /// <summary>修改任意日期框时清除该框的红框提示。</summary>
    private void OnDateBoxChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
        }

        EditorError.Visibility = Visibility.Collapsed;
    }

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Close();
            return;
        }

        var title = TitleBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = DeriveTitleFromContent(ContentBox.Text ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            TitleText.Text = "请输入标题或内容";
            return;
        }

        var startOk = TryGetDate(StartYearBox, StartMonthBox, StartDayBox, out var startDate);
        if (!startOk)
        {
            MarkError(StartYearBox, StartMonthBox, StartDayBox);
        }

        // 截止三个框全空 = 长期事项（不确定结束日期）
        var endBlank = string.IsNullOrWhiteSpace(EndYearBox.Text)
            && string.IsNullOrWhiteSpace(EndMonthBox.Text)
            && string.IsNullOrWhiteSpace(EndDayBox.Text);

        DateTimeOffset? endOffset = null;
        if (!endBlank)
        {
            if (TryGetDate(EndYearBox, EndMonthBox, EndDayBox, out var endDate))
            {
                endOffset = Combine(endDate, ParseTime(EndTimeBox.Text) ?? startDate.TimeOfDay);
            }
            else
            {
                MarkError(EndYearBox, EndMonthBox, EndDayBox);
            }
        }

        if (!startOk || (!endBlank && endOffset is null))
        {
            EditorError.Visibility = Visibility.Visible;
            return;
        }

        var start = Combine(startDate, ParseTime(StartTimeBox.Text) ?? DateTime.Now.TimeOfDay);
        var priority = PriorityBox.SelectedIndex is >= 0 and <= 3 ? PriorityBox.SelectedIndex + 1 : 3;
        var tags = TagBox.SelectedIndex >= 0 && TagBox.SelectedIndex < TagChoices.Length
            ? TagChoices[TagBox.SelectedIndex]
            : TagChoices[0];

        var item = new ScheduleEvent
        {
            Title = title,
            Notes = ContentBox.Text ?? string.Empty,
            StartsAt = new DateTimeOffset(start),
            EndsAt = endOffset,
            Priority = priority,
            Tags = tags,
            AllDay = false,
            Location = string.Empty,
            CreatedAt = new DateTimeOffset(start),
            UpdatedAt = endOffset ?? new DateTimeOffset(start),
        };

        try
        {
            if (_existing is not null)
            {
                _viewModel.UpdateItem(_existing, item);
            }
            else
            {
                _viewModel.AddItem(item);
            }

            _allowClose = true;
            Close();
        }
        catch (Exception ex)
        {
            TitleText.Text = $"保存失败：{ex.Message}";
        }
    }

    private void MarkError(params TextBox[] boxes)
    {
        var red = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F"));
        foreach (var box in boxes)
        {
            box.BorderBrush = red;
        }
    }

    private static void SetDateBoxes(TextBox year, TextBox month, TextBox day, DateTime date)
    {
        year.Text = date.Year.ToString(CultureInfo.InvariantCulture);
        month.Text = date.Month.ToString(CultureInfo.InvariantCulture);
        day.Text = date.Day.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>解析年/月/日输入框为一个真实存在的日期；不合法返回 false。</summary>
    private static bool TryGetDate(TextBox yearBox, TextBox monthBox, TextBox dayBox, out DateTime date)
    {
        date = default;

        if (!int.TryParse(yearBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            || year is < 1 or > 9999)
        {
            return false;
        }

        if (!int.TryParse(monthBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var month)
            || month is < 1 or > 12)
        {
            return false;
        }

        if (!int.TryParse(dayBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day)
            || day is < 1 or > 31)
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

    /// <summary>解析 "HH:mm" 文本；非法返回 null。</summary>
    private static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var time))
        {
            return null;
        }

        return time.TotalHours < 24 && time.TotalHours >= 0 ? time : null;
    }

    private void RefreshPreview() => SimpleMarkdown.Render(ContentBox.Text ?? string.Empty, PreviewPanel);

    private void UpdateDerivedTitle()
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            var derived = DeriveTitleFromContent(ContentBox.Text ?? string.Empty);
            TitleText.Text = string.IsNullOrEmpty(derived) ? "编辑事项" : derived;
        }
    }

    /// <summary>取内容前 15 个字符作为标题（净化换行/空白后）。</summary>
    private static string DeriveTitleFromContent(string content)
    {
        var cleaned = content.Replace("\r", " ").Replace("\n", " ").Trim();
        if (cleaned.Length == 0)
        {
            return string.Empty;
        }

        var take = Math.Min(15, cleaned.Length);
        var title = cleaned[..take].Trim();
        return FolderItemStore.SanitizeSegment(title);
    }

    private static DateTime Combine(DateTime date, TimeSpan time) =>
        date.Date.Add(time);

    private static double Clamp(double value, double min, double max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>Enter in the title box = save.</summary>
    private void OnTitleBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnSaveClicked(sender, e);
            e.Handled = true;
        }
    }
}
