using CalendarApp.Models;
using Xunit;

namespace CalendarApp.Tests;

/// <summary>
/// 月历只排本月日期，行数按当月实际占用周数（4–6 行）决定：
/// 不需要第 6 行时不能白占高度。
/// </summary>
public class CalendarMonthLayoutTests
{
    [Theory]
    [InlineData(2026, 11, 0, 5)]  // 2026-11-01 周日，30 天 → 5 行
    [InlineData(2026, 8, 6, 6)]   // 2026-08-01 周六，31 天 → 6 行
    [InlineData(2026, 2, 0, 4)]   // 2026-02-01 周日，28 天 → 正好 4 行
    [InlineData(2027, 2, 1, 5)]   // 2027-02-01 周一，28 天 → 5 行
    public void ComputesLeadingBlanksAndWeekCount(int year, int month, int expectedLeading, int expectedWeeks)
    {
        var first = new DateTime(year, month, 1);

        Assert.Equal(expectedLeading, CalendarMonthLayout.LeadingBlanks(first));
        Assert.Equal(expectedWeeks, CalendarMonthLayout.Weeks(first));
    }

    [Theory]
    [InlineData(2026, 2)]
    [InlineData(2026, 8)]
    [InlineData(2026, 11)]
    [InlineData(2027, 2)]
    [InlineData(2027, 5)]
    public void WeeksCoverEveryDayWithoutSpillingIntoAnExtraRow(int year, int month)
    {
        var first = new DateTime(year, month, 1);
        var days = DateTime.DaysInMonth(year, month);
        var leading = CalendarMonthLayout.LeadingBlanks(first);
        var weeks = CalendarMonthLayout.Weeks(first);

        Assert.InRange(weeks, 4, 6);
        Assert.True(weeks * 7 >= leading + days, "行数不够，月末日期会被截断");
        Assert.True(weeks * 7 < leading + days + 7, "多排了一整行");
    }
}
