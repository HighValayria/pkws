using System.Security.Cryptography;
using System.Text.Json;

namespace Pkws.Core;

public sealed record PkwsConfig
{
    public string DocumentRoot { get; init; } = string.Empty;
    public string DailyPathPattern { get; init; } = "Daily/{yyyy}/{yyyy-MM-dd}.md";
    public string TimeZoneId { get; init; } = "Asia/Shanghai";
    public int ApiPort { get; init; } = 27123;
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
}

public sealed class AppPaths
{
    public AppPaths(string? dataRoot = null, string? documentRoot = null)
    {
        DataRoot = Path.GetFullPath(dataRoot ??
            Environment.GetEnvironmentVariable("PKWS_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PKWS"));
        DocumentRoot = Path.GetFullPath(documentRoot ?? Path.Combine(AppContext.BaseDirectory, "doc"));
    }

    public string DataRoot { get; }
    public string DocumentRoot { get; }
    public string ConfigFile => Path.Combine(DataRoot, "config.json");
    public string StateDirectory => Path.Combine(DataRoot, "state");
    public string CaptureDatabase => Path.Combine(StateDirectory, "capture.db");
    public string SecretsDirectory => Path.Combine(DataRoot, "secrets");
    public string ApiTokenFile => Path.Combine(SecretsDirectory, "api-token");
    public string ExtensionClientsFile => Path.Combine(SecretsDirectory, "extension-clients.json");
    public string DraftFile => Path.Combine(DataRoot, "draft.json");
    public string WindowStateFile => Path.Combine(DataRoot, "window.json");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(DocumentRoot);
        Directory.CreateDirectory(StateDirectory);
        Directory.CreateDirectory(SecretsDirectory);
    }
}

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppPaths _paths;

    public ConfigStore(AppPaths paths) => _paths = paths;

    public PkwsConfig Load()
    {
        _paths.EnsureDirectories();
        if (!File.Exists(_paths.ConfigFile))
        {
            return new PkwsConfig();
        }

        var config = JsonSerializer.Deserialize<PkwsConfig>(File.ReadAllText(_paths.ConfigFile), JsonOptions);
        return config ?? new PkwsConfig();
    }

    public void Save(PkwsConfig config)
    {
        Validate(config);
        _paths.EnsureDirectories();
        AtomicFile.WriteAllText(_paths.ConfigFile, JsonSerializer.Serialize(config, JsonOptions));
    }

    public static void Validate(PkwsConfig config)
    {
        if (config.ApiPort is < 1024 or > 65535)
        {
            throw new CaptureException("CONFIG_INVALID", "API 端口必须在 1024–65535 之间。", false, 400);
        }

        if (Path.IsPathRooted(config.DailyPathPattern) || config.DailyPathPattern.Contains("..", StringComparison.Ordinal))
        {
            throw new CaptureException("CONFIG_INVALID", "每日笔记路径必须是文档目录内不含 '..' 的相对路径。", false, 400);
        }

        _ = TimeZoneResolver.Resolve(config.TimeZoneId);
    }
}

public sealed class TokenStore
{
    private readonly AppPaths _paths;

    public TokenStore(AppPaths paths) => _paths = paths;

    public string GetOrCreate()
    {
        _paths.EnsureDirectories();
        if (File.Exists(_paths.ApiTokenFile))
        {
            var existing = File.ReadAllText(_paths.ApiTokenFile).Trim();
            if (existing.Length >= 32)
            {
                return existing;
            }
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        AtomicFile.WriteAllText(_paths.ApiTokenFile, token);
        File.SetAttributes(_paths.ApiTokenFile, FileAttributes.Hidden);
        return token;
    }
}

public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("文件路径缺少目录。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }
}

public static class TimeZoneResolver
{
    public static TimeZoneInfo Resolve(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException) when (string.Equals(timeZoneId, "Asia/Shanghai", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
        }
    }
}
