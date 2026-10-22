using System.Text.Json;
using System.Text.RegularExpressions;
using Pkws.Core;

namespace Pkws.Tests;

public sealed partial class CaptureServiceTests
{
    [Fact]
    public async Task Capture_OneHundredIdenticalRetriesWriteOneEntry()
    {
        using var environment = new TestEnvironment();
        var service = environment.CreateService();
        var request = environment.Request("幂等测试");

        var results = new List<CaptureResponse>();
        for (var index = 0; index < 100; index++)
        {
            results.Add(await service.CaptureAsync(request));
        }

        var (_, markdown) = await service.ReadTodayAsync();
        Assert.Single(results.Select(result => result.EntryId).Distinct());
        Assert.Equal(1, OpeningMarkerRegex().Matches(markdown).Count);
        Assert.False(results[0].Duplicate);
        Assert.All(results.Skip(1), result => Assert.True(result.Duplicate));
    }

    [Fact]
    public async Task Capture_SameRequestIdWithDifferentPayloadReturnsConflict()
    {
        using var environment = new TestEnvironment();
        var service = environment.CreateService();
        var id = Guid.CreateVersion7().ToString();
        await service.CaptureAsync(environment.Request("原内容", id));

        var exception = await Assert.ThrowsAsync<CaptureException>(() =>
            service.CaptureAsync(environment.Request("不同内容", id)));

        Assert.Equal("IDEMPOTENCY_CONFLICT", exception.Code);
    }

    [Fact]
    public async Task Capture_SameEntryIdAcrossDifferentRequestsReturnsConflict()
    {
        using var environment = new TestEnvironment();
        var service = environment.CreateService();
        var entryId = Guid.CreateVersion7().ToString();
        await service.CaptureAsync(environment.Request("first", entryId: entryId));

        var exception = await Assert.ThrowsAsync<CaptureException>(() =>
            service.CaptureAsync(environment.Request("second", entryId: entryId)));

        Assert.Equal("ENTRY_ID_CONFLICT", exception.Code);
    }

    [Fact]
    public async Task Capture_ConcurrentRequestsAreAllPresentAndUnique()
    {
        using var environment = new TestEnvironment();
        var service = environment.CreateService();
        var tasks = Enumerable.Range(0, 30)
            .Select(index => service.CaptureAsync(environment.Request($"并发记录 {index:D2}")))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        var (_, markdown) = await service.ReadTodayAsync();

        Assert.Equal(30, results.Select(result => result.EntryId).Distinct().Count());
        Assert.Equal(30, OpeningMarkerRegex().Matches(markdown).Count);
        for (var index = 0; index < 30; index++)
        {
            Assert.Contains($"并发记录 {index:D2}", markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Capture_ResponseWindowFailureRecoversWithoutDuplicate()
    {
        using var environment = new TestEnvironment();
        var request = environment.Request("崩溃恢复");
        var failingService = environment.CreateService(new ThrowAfterVerifyFaultInjector());
        var first = await Assert.ThrowsAsync<CaptureException>(() => failingService.CaptureAsync(request));
        Assert.Equal("WRITE_FAILED", first.Code);

        var recovered = await environment.CreateService().CaptureAsync(request);
        var (_, markdown) = await environment.CreateService().ReadTodayAsync();

        Assert.True(recovered.Duplicate);
        Assert.Equal(1, OpeningMarkerRegex().Matches(markdown).Count);
        Assert.Equal("committed", (await environment.Ledger.GetAsync(request.RequestId))!.Status);
    }

    [Fact]
    public async Task RecoverPending_CompletesPreviouslyVerifiedWrite()
    {
        using var environment = new TestEnvironment();
        var request = environment.Request("启动恢复");
        await Assert.ThrowsAsync<CaptureException>(() =>
            environment.CreateService(new ThrowAfterVerifyFaultInjector()).CaptureAsync(request));

        var recoveredCount = await environment.CreateService().RecoverPendingAsync();

        Assert.Equal(1, recoveredCount);
        Assert.Equal("committed", (await environment.Ledger.GetAsync(request.RequestId))!.Status);
    }

    [Fact]
    public async Task Capture_CrossesConfiguredLocalDate()
    {
        using var environment = new TestEnvironment(new DateTimeOffset(2026, 10, 21, 15, 59, 59, TimeSpan.Zero));
        var service = environment.CreateService();
        var before = await service.CaptureAsync(environment.Request("午夜前"));
        environment.Clock.Set(new DateTimeOffset(2026, 10, 21, 16, 0, 1, TimeSpan.Zero));
        var after = await service.CaptureAsync(environment.Request("午夜后"));

        Assert.Equal(new DateOnly(2026, 10, 21), before.LocalDate);
        Assert.Equal(new DateOnly(2026, 10, 22), after.LocalDate);
        Assert.NotEqual(before.RelativePath, after.RelativePath);
    }

    [Fact]
    public async Task Capture_EmptyContentIsRejectedWithoutLedgerRow()
    {
        using var environment = new TestEnvironment();
        var service = environment.CreateService();
        var request = environment.Request("   \r\n");

        var exception = await Assert.ThrowsAsync<CaptureException>(() => service.CaptureAsync(request));

        Assert.Equal("VALIDATION_FAILED", exception.Code);
        Assert.Null(await environment.Ledger.GetAsync(request.RequestId));
    }

    [Fact]
    public async Task Capture_WriteFailureNeverReturnsSuccessAndRemainsRecoverable()
    {
        using var environment = new TestEnvironment();
        var service = environment.CreateService(store: new ThrowingNoteStore());
        var request = environment.Request("不能虚报成功");

        var exception = await Assert.ThrowsAsync<CaptureException>(() => service.CaptureAsync(request));

        Assert.Equal("VAULT_NOT_WRITABLE", exception.Code);
        var ledger = await environment.Ledger.GetAsync(request.RequestId);
        Assert.Equal("recoverable", ledger!.Status);
        Assert.Equal("VAULT_NOT_WRITABLE", ledger.ErrorCode);
    }

    [Fact]
    public async Task ReadToday_ReflectsExternalEditInsteadOfCachedText()
    {
        using var environment = new TestEnvironment();
        var service = environment.CreateService();
        await service.CaptureAsync(environment.Request("初始"));
        var (relative, _) = await service.ReadTodayAsync();
        var path = new DailyNoteStore(environment.Config).GetFullPath(relative);
        await File.AppendAllTextAsync(path, "\n外部实时修改");

        var (_, refreshed) = await service.ReadTodayAsync();

        Assert.Contains("外部实时修改", refreshed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_SourceContextIsStoredForFutureP1Contract()
    {
        using var environment = new TestEnvironment();
        var request = environment.Request("带上下文") with
        {
            SourceType = "weread",
            SourceContext = new SourceContext
            {
                Provider = "weread",
                WorkTitle = "示例书",
                Authors = ["作者"],
                ChapterTitle = "第一章",
                Url = "https://weread.qq.com/web/reader/example"
            }
        };

        await environment.CreateService().CaptureAsync(request);
        var record = await environment.Ledger.GetAsync(request.RequestId);

        var storedContext = JsonSerializer.Deserialize<SourceContext>(record!.SourceContextJson!);
        Assert.Equal("示例书", storedContext!.WorkTitle);
        Assert.Equal(["作者"], storedContext.Authors);
    }

    [Fact]
    public async Task Capture_WereadRendersSourceQuoteAndMarkdownComment()
    {
        using var environment = new TestEnvironment();
        var request = environment.Request("**我的判断**") with
        {
            SourceType = "weread",
            SourceContext = new SourceContext
            {
                Provider = "weread",
                WorkId = "book-1",
                WorkTitle = "示例[书]",
                Authors = ["作者甲"],
                ChapterId = "chapter-2",
                ChapterTitle = "第二章",
                Url = "https://weread.qq.com/web/reader/book-1?chapterUid=chapter-2",
                SelectedText = "第一行\n第二行"
            }
        };

        await environment.CreateService().CaptureAsync(request);
        var (_, markdown) = await environment.CreateService().ReadTodayAsync();
        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("**来源：微信读书**", normalized, StringComparison.Ordinal);
        Assert.Contains("[示例\\[书\\]](https://weread.qq.com/web/reader/book-1?chapterUid=chapter-2)", normalized, StringComparison.Ordinal);
        Assert.Contains("> 第一行\n> 第二行", normalized, StringComparison.Ordinal);
        Assert.Contains("**我的笔记**\n\n**我的判断**", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_WereadAllowsQuoteWithoutCommentButRejectsInvalidSource()
    {
        using var environment = new TestEnvironment();
        var quoteOnly = environment.Request(string.Empty) with
        {
            SourceType = "weread",
            SourceContext = new SourceContext
            {
                Provider = "weread",
                Authors = [],
                Url = "https://weread.qq.com/web/reader/book-1",
                SelectedText = "仅保存引用"
            }
        };

        await environment.CreateService().CaptureAsync(quoteOnly);
        var invalid = quoteOnly with
        {
            RequestId = Guid.CreateVersion7().ToString(),
            SourceContext = quoteOnly.SourceContext with { Url = "https://example.com/reader/book-1" }
        };
        var exception = await Assert.ThrowsAsync<CaptureException>(() => environment.CreateService().CaptureAsync(invalid));

        Assert.Equal("VALIDATION_FAILED", exception.Code);
    }

    [GeneratedRegex("<!-- pkws:entry:v1 ")]
    private static partial Regex OpeningMarkerRegex();
}
