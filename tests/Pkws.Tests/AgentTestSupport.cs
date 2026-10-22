using System.Text;
using Microsoft.Data.Sqlite;
using Pkws.Agent;
using Pkws.Core;

namespace Pkws.Tests;

internal sealed class AgentTestEnvironment : IDisposable
{
    private readonly MarkdownEntrySerializer _serializer = new();

    public AgentTestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "pkws-agent-tests", Guid.NewGuid().ToString("N"));
        Paths = new AgentPaths(Path.Combine(Root, "doc"), Path.Combine(Root, "state"));
        Paths.EnsureDirectories();
        Database = new AgentDatabase(Paths.DatabasePath, Paths.IndexDatabasePath);
        Database.InitializeAsync().GetAwaiter().GetResult();
        Clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        Config = new AgentConfig
        {
            DocumentRoot = Paths.DocumentRoot,
            DataRoot = Paths.DataRoot,
            MaxWorksetCharacters = 20_000,
            MaxFragmentsPerWorkset = 50,
            ProposalBudgetPerSubmission = 3,
            AnalysisVersion = "codex-test-v1"
        };
    }

    public string Root { get; }
    public AgentPaths Paths { get; }
    public AgentDatabase Database { get; }
    public MutableTimeProvider Clock { get; }
    public AgentConfig Config { get; }

    public async Task<IReadOnlyList<string>> WriteDailyAsync(DateOnly date, params (string Content, string SourceType)[] entries)
    {
        var directory = Path.Combine(Paths.DocumentRoot, "Daily", date.Year.ToString());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{date:yyyy-MM-dd}.md");
        var builder = new StringBuilder($"# {date:yyyy-MM-dd}\n\n");
        var ids = new List<string>();
        for (var i = 0; i < entries.Length; i++)
        {
            var id = Guid.CreateVersion7(Clock.GetUtcNow().AddMinutes(i)).ToString();
            ids.Add(id);
            builder.Append(_serializer.Render(id, Clock.GetUtcNow().AddMinutes(i), "Asia/Shanghai", entries[i].SourceType, entries[i].Content)).AppendLine();
        }
        await File.WriteAllTextAsync(path, builder.ToString());
        return ids;
    }

    public DailyNoteIndexer CreateIndexer() => new(Paths, Database, timeProvider: Clock);
    public ArticleManager CreateArticles() => new(Paths, Database, Clock);
    public CodexWorksetService CreateWorksets() => new(Config, Database, CreateArticles(), Clock);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
