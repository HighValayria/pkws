using Microsoft.Data.Sqlite;
using Pkws.Core;

namespace Pkws.Tests;

internal sealed class TestEnvironment : IDisposable
{
    public TestEnvironment(DateTimeOffset? now = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "pkws-tests", Guid.NewGuid().ToString("N"));
        Vault = Path.Combine(Root, "vault");
        Data = Path.Combine(Root, "data");
        Directory.CreateDirectory(Vault);
        Directory.CreateDirectory(Data);
        Config = new PkwsConfig
        {
            DocumentRoot = Vault,
            DailyPathPattern = "Daily/{yyyy}/{yyyy-MM-dd}.md",
            TimeZoneId = "Asia/Shanghai",
            ApiPort = 0
        };
        Clock = new MutableTimeProvider(now ?? new DateTimeOffset(2026, 10, 21, 6, 30, 0, TimeSpan.Zero));
        Ledger = new CaptureLedger(Path.Combine(Data, "capture.db"));
        Ledger.InitializeAsync().GetAwaiter().GetResult();
    }

    public string Root { get; }
    public string Vault { get; }
    public string Data { get; }
    public PkwsConfig Config { get; set; }
    public MutableTimeProvider Clock { get; }
    public CaptureLedger Ledger { get; }

    public CaptureService CreateService(ICaptureFaultInjector? faultInjector = null, IDailyNoteStore? store = null) =>
        new(
            Config,
            Ledger,
            store ?? new DailyNoteStore(Config),
            new MarkdownEntrySerializer(),
            Clock,
            faultInjector);

    public CaptureRequest Request(string content, string? requestId = null, string? entryId = null) => new()
    {
        RequestId = requestId ?? Guid.CreateVersion7().ToString(),
        EntryId = entryId,
        SourceType = "desktop",
        Content = content
    };

    public void Dispose()
    {
        // SQLite connections are pooled after disposal. Release the process-wide
        // test pools before removing each isolated temporary database.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }
}

internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Set(DateTimeOffset value) => _now = value;
}

internal sealed class ThrowAfterVerifyFaultInjector : ICaptureFaultInjector
{
    private int _remaining = 1;

    public Task AfterFileVerifiedAsync(CaptureLedgerRecord record, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _remaining, 0) == 1)
        {
            throw new IOException("Simulated response-window crash.");
        }

        return Task.CompletedTask;
    }
}

internal sealed class ThrowingNoteStore : IDailyNoteStore
{
    public string GetRelativePath(DateOnly localDate) => $"Daily/{localDate:yyyy}/{localDate:yyyy-MM-dd}.md";
    public string GetFullPath(string relativePath) => relativePath;
    public Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

    public Task<AppendResult> AppendAndVerifyAsync(
        string relativePath,
        DateOnly localDate,
        string entryId,
        string renderedMarkdown,
        CancellationToken cancellationToken = default) =>
        throw new CaptureException("VAULT_NOT_WRITABLE", "Simulated write failure.", true, 503);
}
