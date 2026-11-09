namespace CalendarApp.Models;

/// <summary>
/// 跨零点（day rollover）相关的纯逻辑，便于单元测试，也把“什么情况下才跟随
/// 新的一天”这条规则集中在一处。
///
/// 面板标题、日历网格和当日事项都绑定在 <c>_selectedDate</c> 上。零点到来时
/// 只有“面板确实还停在今天”才应该把选中日推进到新的一天；用户正在浏览别的
/// 日期或别的月份时不能被拽走。
/// </summary>
public static class DayRollover
{
    /// <summary>
    /// 面板是否仍在展示“今天”：选中日就是 <paramref name="today"/>，且显示的
    /// 月份也是今天所在月份（按了上/下月翻页就不算）。
    /// </summary>
    public static bool IsFollowingToday(DateTime selectedDate, DateTime displayMonth, DateTime today) =>
        selectedDate.Date == today.Date
        && displayMonth.Year == today.Year
        && displayMonth.Month == today.Month;

    /// <summary>
    /// 距离下一个本地零点之后 1 秒的延时。
    ///
    /// 多等的这 1 秒是刻意的：定时器可能提前几毫秒触发，而 <see cref="DateTime.Today"/>
    /// 要到零点整才翻页，提前触发会白跑一次检查。返回值保证为正数。
    /// </summary>
    public static TimeSpan DelayUntilNextMidnight(DateTime now)
    {
        var nextMidnight = now.Date.AddDays(1).AddSeconds(1);
        var delay = nextMidnight - now;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
    }
}
