using System.Text.Json;
using Pkws.Core;

namespace Pkws.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void LegacyUiFields_AreIgnoredAndRemovedOnNextSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "pkws-config-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureDirectories();
            File.WriteAllText(paths.ConfigFile, """
                {
                  "vaultPath": "",
                  "dailyPathPattern": "Daily/{yyyy}/{yyyy-MM-dd}.md",
                  "timeZoneId": "Asia/Shanghai",
                  "hotkey": "Ctrl+Space",
                  "apiPort": 27123,
                  "allowedOrigins": []
                }
                """);

            var store = new ConfigStore(paths);
            var config = store.Load();
            store.Save(config);

            using var document = JsonDocument.Parse(File.ReadAllText(paths.ConfigFile));
            Assert.False(document.RootElement.TryGetProperty("hotkey", out _));
            Assert.False(document.RootElement.TryGetProperty("vaultPath", out _));
            Assert.True(document.RootElement.TryGetProperty("documentRoot", out _));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
