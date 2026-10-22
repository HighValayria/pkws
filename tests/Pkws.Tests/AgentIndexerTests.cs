using Pkws.Agent;
using Pkws.Core;
using Microsoft.Data.Sqlite;

namespace Pkws.Tests;

public sealed class AgentIndexerTests
{
    [Fact]
    public void Parser_ReadsControlledEntriesAndManualRegion()
    {
        var serializer = new MarkdownEntrySerializer();
        var id = Guid.CreateVersion7().ToString();
        var markdown = "# 2026-10-22\n\n" + serializer.Render(id, new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero), "Asia/Shanghai", "desktop", "受控正文") + "\n用户手工补充";

        var result = new DailyMarkdownParser().Parse(markdown, "Daily/2026/2026-10-22.md");

        Assert.Equal(2, result.Sources.Count);
        Assert.Equal(id, result.Sources[0].EntryId);
        Assert.Equal("受控正文", result.Sources[0].Content);
        Assert.Equal("manual", result.Sources[1].SourceType);
        Assert.Equal("用户手工补充", result.Sources[1].Content);
    }

    [Fact]
    public async Task Scan_IsIncrementalSearchableAndIdempotent()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), ("我在思考因果推断和曝光偏差。", "desktop"), ("另一条零散记录", "weread"));

        var first = await environment.CreateIndexer().ScanAsync();
        var second = await environment.CreateIndexer().ScanAsync();
        var hits = await environment.Database.SearchAsync("因果推断");

        Assert.Equal(2, first.AddedRevisions);
        Assert.True(first.FragmentsCreated >= 2);
        Assert.Equal(0, second.AddedRevisions);
        Assert.Equal(2, second.UnchangedSources);
        Assert.Single(hits);
        Assert.Equal("desktop", hits[0].SourceType);
    }

    [Fact]
    public async Task Scan_OldEditCreatesOneSuccessorWithoutDuplicateActiveSource()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 20), ("旧观点", "desktop"));
        await environment.CreateIndexer().ScanAsync();
        var file = Directory.GetFiles(Path.Combine(environment.Paths.DocumentRoot, "Daily"), "*.md", SearchOption.AllDirectories).Single();
        var before = (await environment.Database.GetActiveSourcesAsync()).Single();
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("旧观点", "修正后的观点", StringComparison.Ordinal));

        var result = await environment.CreateIndexer().ScanAsync();
        var after = (await environment.Database.GetActiveSourcesAsync()).Single();

        Assert.Equal(1, result.AddedRevisions);
        Assert.Equal(before.RevisionId, after.PredecessorId);
        Assert.Equal("修正后的观点", after.Content);
    }

    [Fact]
    public async Task Scan_MoveAndDeleteUpdateSourceState()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 20), ("移动测试", "desktop"));
        await environment.CreateIndexer().ScanAsync();
        var old = Directory.GetFiles(Path.Combine(environment.Paths.DocumentRoot, "Daily"), "*.md", SearchOption.AllDirectories).Single();
        var movedDirectory = Path.Combine(environment.Paths.DocumentRoot, "Daily", "Archive");
        Directory.CreateDirectory(movedDirectory);
        var moved = Path.Combine(movedDirectory, Path.GetFileName(old));
        File.Move(old, moved);

        var moveResult = await environment.CreateIndexer().ScanAsync();
        Assert.Equal(1, moveResult.AddedRevisions);
        Assert.Single(await environment.Database.GetActiveSourcesAsync());

        File.Delete(moved);
        var deleteResult = await environment.CreateIndexer().ScanAsync();
        Assert.Equal(1, deleteResult.RemovedSources);
        Assert.Empty(await environment.Database.GetActiveSourcesAsync());
    }

    [Fact]
    public void Fragmenter_UsesSeveralSemanticCandidatesWithoutChangingSource()
    {
        var fragments = new SemanticFragmenter().Split(Guid.CreateVersion7().ToString(), "第一段讨论因果推断的识别假设，内容足够长以独立保留，并说明混杂变量问题。\n\n第二段转向推荐系统曝光偏差，以及如何用反事实方法理解。内容同样形成另一条论述。");

        Assert.Equal(2, fragments.Count);
        Assert.Equal(0, fragments[0].Ordinal);
        Assert.True(fragments[0].EndOffset <= fragments[1].StartOffset);
    }

    [Fact]
    public async Task Search_ShortChineseTermFallsBackWithoutLosingResult()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), ("讨论因果方法", "desktop"));
        await environment.CreateIndexer().ScanAsync();

        var hits = await environment.Database.SearchAsync("因果");

        Assert.Single(hits);
    }

    [Fact]
    public async Task Scan_TemporarilyUnreadableFileDoesNotDeactivateKnownSources()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), ("暂时占用", "desktop"));
        await environment.CreateIndexer().ScanAsync();
        var file = Directory.GetFiles(Path.Combine(environment.Paths.DocumentRoot, "Daily"), "*.md", SearchOption.AllDirectories).Single();

        await using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await environment.CreateIndexer().ScanAsync();
            Assert.Equal(1, result.Diagnostics);
            Assert.Equal(0, result.RemovedSources);
        }

        Assert.Single(await environment.Database.GetActiveSourcesAsync());
    }

    [Fact]
    public async Task DeletedDerivedIndex_RebuildsWithoutLosingControlState()
    {
        using var environment = new AgentTestEnvironment();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), ("可重建的因果索引", "desktop"));
        await environment.CreateIndexer().ScanAsync();
        await environment.Database.UpsertTopicAsync("保留主题", "长期维护", "user", true);
        SqliteConnection.ClearAllPools();
        File.Delete(environment.Paths.IndexDatabasePath);

        var reopened = new AgentDatabase(environment.Paths.DatabasePath, environment.Paths.IndexDatabasePath);
        await reopened.InitializeAsync();
        await reopened.RebuildFtsAsync();

        Assert.Single(await reopened.SearchAsync("因果"));
        Assert.Single(await reopened.ListTopicsAsync());
        Assert.True(File.Exists(environment.Paths.IndexDatabasePath));
    }

    [Fact]
    public async Task Scan_LoadsWereadSourceContextFromCaptureLedgerReadOnly()
    {
        using var environment = new AgentTestEnvironment();
        var entryId = (await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), ("**我的笔记**\n\n读书评论", "weread"))).Single();
        var captureDb = Path.Combine(environment.Root, "capture.db");
        await using (var connection = new SqliteConnection($"Data Source={captureDb}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE captures(entry_id TEXT, source_context_json TEXT); INSERT INTO captures VALUES($id,$context);";
            command.Parameters.AddWithValue("$id", entryId);
            command.Parameters.AddWithValue("$context", "{\"provider\":\"weread\",\"work_title\":\"测试书\"}");
            await command.ExecuteNonQueryAsync();
        }

        await new DailyNoteIndexer(environment.Paths, environment.Database, captureDb, environment.Clock).ScanAsync();

        var source = (await environment.Database.GetActiveSourcesAsync()).Single();
        Assert.Equal("weread", source.SourceType);
        Assert.Contains("测试书", source.SourceContextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scan_LargerBatchRemainsIncrementalAndFinite()
    {
        using var environment = new AgentTestEnvironment();
        var entries = Enumerable.Range(1, 120).Select(index => ($"批量记录 {index}，用于确认增量扫描。", "desktop")).ToArray();
        await environment.WriteDailyAsync(new DateOnly(2026, 10, 22), entries);

        var first = await environment.CreateIndexer().ScanAsync();
        var second = await environment.CreateIndexer().ScanAsync();

        Assert.Equal(120, first.AddedRevisions);
        Assert.Equal(120, second.UnchangedSources);
        Assert.Equal(0, second.AddedRevisions);
    }
}
