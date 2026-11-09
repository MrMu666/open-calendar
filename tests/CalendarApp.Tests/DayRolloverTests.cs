using CalendarApp.Models;
using Xunit;

namespace CalendarApp.Tests;

/// <summary>
/// 跨零点规则：只有面板仍停留在“今天”（选中日=今天且显示月=本月）时才跟随
/// 新的一天，浏览其他日期/月份时不受影响。
/// </summary>
public class DayRolloverTests
{
    private static readonly DateTime Today = new(2026, 11, 9);
    private static readonly DateTime ThisMonth = new(2026, 11, 1);

    [Fact]
    public void FollowsWhenSelectedDayAndMonthAreToday()
    {
        Assert.True(DayRollover.IsFollowingToday(Today, ThisMonth, Today));
    }

    [Fact]
    public void FollowsEvenWhenUserExplicitlyClickedTodaysCell()
    {
        // 点击今天会把 _hasExplicitSelection 置 true，但选中日仍是今天 → 零点跟随。
        Assert.True(DayRollover.IsFollowingToday(Today.AddHours(13), ThisMonth.AddHours(8), Today));
    }

    [Fact]
    public void DoesNotFollowWhenSelectedDateIsNotToday()
    {
        Assert.False(DayRollover.IsFollowingToday(Today.AddDays(-1), ThisMonth, Today));
    }

    [Fact]
    public void DoesNotFollowWhenBrowsingAnotherMonth()
    {
        Assert.False(DayRollover.IsFollowingToday(Today, new DateTime(2026, 10, 1), Today));
    }

    [Fact]
    public void DoesNotFollowWhenBrowsingSameMonthOfAnotherYear()
    {
        Assert.False(DayRollover.IsFollowingToday(new DateTime(2025, 11, 9), new DateTime(2025, 11, 1), Today));
    }

    [Fact]
    public void DelayLandsJustAfterNextMidnight()
    {
        var now = new DateTime(2026, 11, 9, 23, 59, 30);
        var delay = DayRollover.DelayUntilNextMidnight(now);

        Assert.True(delay > TimeSpan.FromSeconds(30));
        Assert.True(delay <= TimeSpan.FromSeconds(32));
        Assert.Equal(new DateTime(2026, 11, 10), (now + delay).Date);
    }

    [Fact]
    public void DelayFromMidnightIsOneDayPlusOneSecond()
    {
        var now = new DateTime(2026, 11, 9, 0, 0, 0);
        Assert.Equal(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1), DayRollover.DelayUntilNextMidnight(now));
    }

    [Fact]
    public void DelayAlwaysPositive()
    {
        Assert.True(DayRollover.DelayUntilNextMidnight(new DateTime(2026, 11, 9, 23, 59, 59, 900)) > TimeSpan.Zero);
    }
}
