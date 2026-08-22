using System.IO;
using CalendarApp.Data;
using CalendarApp.Models;
using Xunit;

namespace CalendarApp.Tests;

public class FolderItemStoreTests : IDisposable
{
    private readonly string _dir;

    public FolderItemStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "folder-store-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    [Fact]
    public void InsertCreatesFileWithDesignedNamingRule()
    {
        var store = new FolderItemStore();
        store.Open(_dir);

        var start = new DateTimeOffset(2026, 8, 21, 9, 30, 15, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 21, 9, 30, 15)));
        var fileName = store.Insert(new ScheduleEvent
        {
            Title = "完成项目设计文档",
            StartsAt = start,
            EndsAt = start.AddHours(6),
            Priority = 1,
            Tags = "工作-项目",
            Notes = "# 设计文档\n\n- 条目一\n- 条目二",
        });

        Assert.Equal("20260821-093015_20260821-153015_P1_完成项目设计文档_工作-项目.md", fileName);
        Assert.True(File.Exists(Path.Combine(_dir, "items", fileName)));
        Assert.Equal("# 设计文档\n\n- 条目一\n- 条目二",
            File.ReadAllText(Path.Combine(_dir, "items", fileName)));
    }

    [Fact]
    public void QueryRoundTripsMetadataAndContent()
    {
        var store = new FolderItemStore();
        store.Open(_dir);
        store.Insert(new ScheduleEvent
        {
            Title = "买牛奶",
            StartsAt = new DateTimeOffset(2026, 8, 21, 15, 30, 22, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 21, 15, 30, 22))),
            EndsAt = new DateTimeOffset(2026, 8, 21, 15, 30, 22, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 21, 15, 30, 22))),
            Priority = 3,
            Tags = "",
            Notes = "备注内容",
        });

        var all = store.GetItems();
        Assert.Single(all);

        var item = all[0];
        Assert.Equal("买牛奶", item.Title);
        Assert.Equal(3, item.Priority);
        Assert.Equal("备注内容", item.Notes);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 21, 15, 30, 22, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 21, 15, 30, 22))),
            item.StartsAt);
        Assert.NotNull(item.FileName);
    }

    [Fact]
    public void DateRangeAndDayQueriesWork()
    {
        var store = new FolderItemStore();
        store.Open(_dir);

        var day = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 21, 0, 0, 0)));
        store.Insert(new ScheduleEvent { Title = "今天早上", StartsAt = day.AddHours(9) });
        store.Insert(new ScheduleEvent { Title = "今天下午", StartsAt = day.AddHours(15) });
        store.Insert(new ScheduleEvent { Title = "明天事项", StartsAt = day.AddDays(1).AddHours(10) });

        Assert.Equal(2, store.GetEventsOnDay(day).Count);
        Assert.Equal(3, store.GetUpcoming(day.AddDays(-1), limit: 10).Count);
        Assert.Single(store.GetUpcoming(day.AddDays(1).AddHours(-1), limit: 10));
    }

    [Fact]
    public void SanitizeRemovesIllegalFileNameCharacters()
    {
        Assert.Equal("本周要完成A事项", FolderItemStore.SanitizeSegment("本周要完成A事项"));
        Assert.Equal("a b c", FolderItemStore.SanitizeSegment("a/b\\c"));
        Assert.Equal("a b c", FolderItemStore.SanitizeSegment("a:b*c?\"< >|"));
        Assert.Equal("工作-生活", FolderItemStore.SanitizeSegment("工作-生活"));
        Assert.Equal("", FolderItemStore.SanitizeSegment("   "));
    }
}
