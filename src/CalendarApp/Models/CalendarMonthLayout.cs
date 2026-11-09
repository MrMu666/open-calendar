namespace CalendarApp.Models;

/// <summary>
/// 月历网格布局：行数按当月实际占用的周数决定（4–6 行），首行/末行由相邻月份
/// 日期填满，不出现空白格。
/// </summary>
public static class CalendarMonthLayout
{
    /// <summary>
    /// 首行中位于 1 号之前的格子数（周日 = 0，周一 = 1 … 周六 = 6）。
    /// 这些格子显示上个月的日期（更淡）。
    /// </summary>
    public static int LeadingBlanks(DateTime month) =>
        (int)new DateTime(month.Year, month.Month, 1).DayOfWeek;

    /// <summary>
    /// 当月需要几行格子：前导空格 + 当月天数，向上取整到 7 的倍数。
    /// 结果只会是 4、5 或 6（28 天且 1 号是周日时正好 4 行）。
    /// </summary>
    public static int Weeks(DateTime month)
    {
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        return (LeadingBlanks(month) + days + 6) / 7;
    }
}
