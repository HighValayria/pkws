using Pkws.Core;

namespace Pkws.Tests;

public sealed class MarkdownEntrySerializerTests
{
    [Fact]
    public void Render_PreservesComplexMarkdownVerbatim()
    {
        var serializer = new MarkdownEntrySerializer();
        var entryId = Guid.CreateVersion7().ToString();
        var content = "中文 😀\n\n```csharp\nvar x = \"<!-- comment -->\";\n```\n> 引用\n-- 分隔";

        var result = serializer.Render(
            entryId,
            new DateTimeOffset(2026, 10, 21, 6, 35, 12, TimeSpan.Zero),
            "Asia/Shanghai",
            "desktop",
            content);

        Assert.Contains(content, result, StringComparison.Ordinal);
        Assert.True(MarkdownEntrySerializer.ContainsCompleteEntry(result, entryId));
        Assert.Contains("### 14:00", result, StringComparison.Ordinal);
        Assert.Contains("\"content_bytes\"", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("没有结尾换行")]
    [InlineData("保留一个结尾换行\n")]
    [InlineData("中文 😀\n\n- Markdown 列表")]
    public void ParseEntries_RestoresContentAndUsesHourlyDisplay(string content)
    {
        var serializer = new MarkdownEntrySerializer();
        var entryId = Guid.CreateVersion7().ToString();
        var markdown = serializer.Render(
            entryId,
            new DateTimeOffset(2026, 10, 21, 6, 35, 12, TimeSpan.Zero),
            "Asia/Shanghai",
            "desktop",
            content);

        var entry = Assert.Single(serializer.ParseEntries(markdown));

        Assert.Equal(entryId, entry.EntryId);
        Assert.Equal("14:00", entry.Hour);
        Assert.Equal(content, entry.Content);
    }

    [Fact]
    public void Render_RejectsExactReservedClosingMarker()
    {
        var serializer = new MarkdownEntrySerializer();
        var entryId = Guid.CreateVersion7().ToString();
        var content = $"text {MarkdownEntrySerializer.ClosingMarker(entryId)} text";

        var exception = Assert.Throws<CaptureException>(() => serializer.Render(
            entryId,
            DateTimeOffset.UtcNow,
            "Asia/Shanghai",
            "desktop",
            content));

        Assert.Equal("RESERVED_MARKER", exception.Code);
    }
}
