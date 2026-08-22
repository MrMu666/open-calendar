using System.Globalization;

namespace CalendarApp.Models;

/// <summary>
/// Converts Gregorian dates to Chinese lunar dates using the built-in
/// <see cref="ChineseLunisolarCalendar"/> (no third-party dependency).
/// </summary>
public static class LunarCalendar
{
    // Shared instance: ChineseLunisolarCalendar is thread-safe for reads
    // (all query methods are pure functions of the date).
    private static readonly ChineseLunisolarCalendar Calendar = new();

    private static readonly string[] MonthNames =
    [
        "正月", "二月", "三月", "四月", "五月", "六月",
        "七月", "八月", "九月", "十月", "冬月", "腊月",
    ];

    private static readonly string[] DayNames =
    [
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十",
    ];

    /// <summary>The lunar month number (1-12). Positive, even for leap months; use <see cref="IsLeapMonth"/>.</summary>
    public static int GetMonth(DateTime date) => Math.Abs(Calendar.GetMonth(date));

    /// <summary>Whether the given date falls in a leap lunar month.</summary>
    public static bool IsLeapMonth(DateTime date) => Calendar.GetMonth(date) < 0;

    /// <summary>Lunar day of month, 1-30.</summary>
    public static int GetDay(DateTime date) => Calendar.GetDayOfMonth(date);

    /// <summary>Lunar month name, e.g. "正月" or "闰四月".</summary>
    public static string GetMonthName(DateTime date)
    {
        var month = GetMonth(date);
        return (IsLeapMonth(date) ? "闰" : string.Empty) + MonthNames[month - 1];
    }

    /// <summary>Lunar day name, e.g. "初一" ... "三十".</summary>
    public static string GetDayName(DateTime date) => DayNames[GetDay(date) - 1];

    /// <summary>Full text, e.g. "正月初一" or "闰四月十五".</summary>
    public static string GetMonthDayText(DateTime date) => GetMonthName(date) + GetDayName(date);
}
