namespace CalendarApp.Models;

/// <summary>
/// 月历网格布局：只渲染本月日期，月初/月末用空格补位（不显示邻月日期），
/// 行数按当月实际占用的周数决定（4–6 行），这样不需要第 6 行时不会白占高度。
/// </summary>
public static class CalendarMonthLayout
{
    /// <summary>当月 1 号之前要留几个空格（周日 = 0，周一 = 1 … 周六 = 6）。</summary>
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
