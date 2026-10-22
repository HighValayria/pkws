using Pkws.Core;

namespace Pkws.Tests;

public sealed class DailyNoteStoreTests
{
    [Fact]
    public async Task Append_CreatesNestedDailyFileAndReadableHeader()
    {
        using var environment = new TestEnvironment();
        var store = new DailyNoteStore(environment.Config);
        var serializer = new MarkdownEntrySerializer();
        var id = Guid.CreateVersion7().ToString();
        var relative = store.GetRelativePath(new DateOnly(2026, 10, 21));

        await store.AppendAndVerifyAsync(
            relative,
            new DateOnly(2026, 10, 21),
            id,
            serializer.Render(id, environment.Clock.GetUtcNow(), "Asia/Shanghai", "desktop", "第一条"));

        var content = await store.ReadAsync(relative);
        Assert.StartsWith("# 2026-10-21", content, StringComparison.Ordinal);
        Assert.Contains("第一条", content, StringComparison.Ordinal);
        Assert.Equal("Daily/2026/2026-10-21.md", relative);
    }

    [Fact]
    public async Task Append_PreservesExternalEditsAndExistingContent()
    {
        using var environment = new TestEnvironment();
        var service = environment.CreateService();
        await service.CaptureAsync(environment.Request("第一条"));
        var (relative, _) = await service.ReadTodayAsync();
        var fullPath = new DailyNoteStore(environment.Config).GetFullPath(relative);
        await File.AppendAllTextAsync(fullPath, "\n用户在 Obsidian 中加入的文字\n");

        await service.CaptureAsync(environment.Request("第二条"));

        var final = await File.ReadAllTextAsync(fullPath);
        Assert.Contains("第一条", final, StringComparison.Ordinal);
        Assert.Contains("用户在 Obsidian 中加入的文字", final, StringComparison.Ordinal);
        Assert.Contains("第二条", final, StringComparison.Ordinal);
    }

    [Fact]
    public void GetRelativePath_RejectsTraversal()
    {
        using var environment = new TestEnvironment();
        environment.Config = environment.Config with { DailyPathPattern = "../outside/{yyyy-MM-dd}.md" };
        var store = new DailyNoteStore(environment.Config);
        var exception = Assert.Throws<CaptureException>(() => store.GetRelativePath(new DateOnly(2026, 10, 21)));
        Assert.Equal("PATH_INVALID", exception.Code);
    }
}
