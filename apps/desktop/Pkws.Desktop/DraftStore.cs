using System.Text.Json;
using System.IO;
using Pkws.Core;

namespace Pkws.Desktop;

public sealed record DraftState(string Content, string StartedLocalDate, string RequestId);

public sealed class DraftStore
{
    private readonly string _path;

    public DraftStore(string path) => _path = path;

    public DraftState? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DraftState>(File.ReadAllText(_path));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(DraftState state) => AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(state));

    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
