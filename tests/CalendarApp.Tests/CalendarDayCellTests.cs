using System.Windows.Media;
using CalendarApp.ViewModels;
using Xunit;

namespace CalendarApp.Tests;

/// <summary>
/// 月历文字透明度优先级：相邻月份最淡 &lt; 历史日期 &lt; 今天/未来；
/// 选中日期用白色数字。
/// </summary>
public class CalendarDayCellTests
{
    private static byte Alpha(Brush brush) => ((SolidColorBrush)brush).Color.A;

    private static CalendarDayCell Cell(bool inMonth, bool past, bool selected = false) => new()
    {
        Date = new DateTime(2026, 11, 5),
        IsInMonth = inMonth,
        IsPast = past,
        IsSelected = selected,
    };

    [Fact]
    public void AdjacentMonthTextIsFainterThanPastDayText()
    {
        var adjacent = Cell(inMonth: false, past: true);
        var past = Cell(inMonth: true, past: true);

        Assert.True(Alpha(adjacent.Foreground) < Alpha(past.Foreground));
        Assert.True(Alpha(adjacent.LunarForeground) < Alpha(past.LunarForeground));
    }

    [Fact]
    public void PastTextIsFainterThanFutureText()
    {
        var past = Cell(inMonth: true, past: true);
        var future = Cell(inMonth: true, past: false);

        Assert.True(Alpha(past.Foreground) < Alpha(future.Foreground));
        Assert.True(Alpha(past.LunarForeground) < Alpha(future.LunarForeground));
    }

    [Fact]
    public void SelectedDayKeepsWhiteNumeral()
    {
        var cell = Cell(inMonth: true, past: true, selected: true);

        Assert.Equal(Colors.White, ((SolidColorBrush)cell.Foreground).Color);
    }
}
