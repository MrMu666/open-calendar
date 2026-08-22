using CalendarApp.Models;
using Xunit;

namespace CalendarApp.Tests;

public class LunarCalendarTests
{
    [Theory]
    [InlineData(2026, 2, 17)] // 丙午年正月初一（2026 春节）
    [InlineData(2025, 1, 29)] // 乙巳年正月初一（2025 春节）
    [InlineData(2024, 2, 10)] // 甲辰年正月初一（2024 春节）
    public void ChineseNewYearIsFirstDayOfFirstMonth(int year, int month, int day)
    {
        var date = new DateTime(year, month, day);

        Assert.Equal(1, LunarCalendar.GetMonth(date));
        Assert.Equal(1, LunarCalendar.GetDay(date));
        Assert.Equal("正月", LunarCalendar.GetMonthName(date));
        Assert.Equal("初一", LunarCalendar.GetDayName(date));
        Assert.Equal("正月初一", LunarCalendar.GetMonthDayText(date));
    }

    [Fact]
    public void DayNamesCoverFullMonthRange()
    {
        // The last day of a lunar month is "三十" or earlier; the helper must
        // never throw and always return a non-empty name across a long range.
        var start = new DateTime(2020, 1, 1);
        for (var i = 0; i < 3650; i++) // 10 years
        {
            var date = start.AddDays(i);
            var dayName = LunarCalendar.GetDayName(date);
            Assert.False(string.IsNullOrWhiteSpace(dayName));
            Assert.InRange(LunarCalendar.GetDay(date), 1, 30);
        }
    }
}
