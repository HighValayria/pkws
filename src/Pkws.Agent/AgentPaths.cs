using System.Text.Json;

namespace Pkws.Agent;

public sealed class AgentPaths
{
    public AgentPaths(string documentRoot, string dataRoot)
    {
        DocumentRoot = Path.GetFullPath(documentRoot);
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DocumentRoot { get; }
    public string DataRoot { get; }
    public string DatabasePath => Path.Combine(DataRoot, "agent.db");
    public string IndexDatabasePath => Path.Combine(DataRoot, "index.db");
    public string LockPath => Path.Combine(DataRoot, "agent.lock");
    public string ConfigPath => Path.Combine(DataRoot, "agent-config.json");
    public string ArticlesDirectory => Path.Combine(DocumentRoot, "Writing", "Articles");
    public string WeeklyReviewsDirectory => Path.Combine(DocumentRoot, "Reviews", "Weekly");
    public string MonthlyReviewsDirectory => Path.Combine(DocumentRoot, "Reviews", "Monthly");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DocumentRoot);
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ArticlesDirectory);
        Directory.CreateDirectory(WeeklyReviewsDirectory);
        Directory.CreateDirectory(MonthlyReviewsDirectory);
    }

    public string ResolveDocumentPath(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(DocumentRoot, normalized));
        var root = DocumentRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("路径超出 PKWS 文档目录。");
        }

        return full;
    }

    public string ToRelativeDocumentPath(string fullPath) =>
        Path.GetRelativePath(DocumentRoot, Path.GetFullPath(fullPath)).Replace('\\', '/');
}

public static class AgentConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static AgentConfig Load(string? documentRoot = null, string? dataRoot = null)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var resolvedData = Path.GetFullPath(dataRoot ?? Environment.GetEnvironmentVariable("PKWS_AGENT_DATA_DIR") ?? Path.Combine(local, "PKWS", "agent"));
        var resolvedDocument = Path.GetFullPath(documentRoot ?? Environment.GetEnvironmentVariable("PKWS_DOC_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "doc"));
        var paths = new AgentPaths(resolvedDocument, resolvedData);
        Directory.CreateDirectory(paths.DataRoot);
        AgentConfig saved = File.Exists(paths.ConfigPath)
            ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(paths.ConfigPath), JsonOptions) ?? new AgentConfig()
            : new AgentConfig();

        return saved with
        {
            DocumentRoot = resolvedDocument,
            DataRoot = resolvedData,
            CaptureDatabasePath = Environment.GetEnvironmentVariable("PKWS_CAPTURE_DB") ?? saved.CaptureDatabasePath
        };
    }

    public static void SavePublicSettings(AgentConfig config)
    {
        var paths = new AgentPaths(config.DocumentRoot, config.DataRoot);
        paths.EnsureDirectories();
        var publicConfig = config with { DocumentRoot = string.Empty, DataRoot = string.Empty, CaptureDatabasePath = string.Empty };
        Pkws.Core.AtomicFile.WriteAllText(paths.ConfigPath, JsonSerializer.Serialize(publicConfig, JsonOptions));
    }
}
