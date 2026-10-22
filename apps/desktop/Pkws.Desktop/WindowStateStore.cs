using System.IO;
using System.Text.Json;
using Pkws.Core;

namespace Pkws.Desktop;

public sealed record WindowLayoutState(double Width, double Height);

public sealed class WindowStateStore
{
    private readonly string _path;

    public WindowStateStore(string path) => _path = path;

    public WindowLayoutState? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<WindowLayoutState>(File.ReadAllText(_path));
            return state is { Width: >= 390 and <= 1600, Height: >= 560 and <= 2200 } ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(WindowLayoutState state) =>
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(state));
}
