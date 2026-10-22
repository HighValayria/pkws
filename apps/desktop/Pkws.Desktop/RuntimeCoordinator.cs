using System.Diagnostics;
using System.IO;
using Pkws.Api;
using Pkws.Core;

namespace Pkws.Desktop;

public sealed class RuntimeCoordinator : IAsyncDisposable
{
    private readonly AppPaths _paths = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConfigStore? _configStore;
    private CaptureLedger? _ledger;
    private LocalApiHost? _apiHost;
    private string? _apiToken;
    private ExtensionCredentialStore? _extensionCredentials;
    private ExtensionPairingService? _extensionPairing;

    public PkwsConfig Config { get; private set; } = new();
    public CaptureService CaptureService { get; private set; } = null!;
    public AppPaths Paths => _paths;
    public string ApiStatus { get; private set; } = "尚未启动";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        _configStore = new ConfigStore(_paths);
        Config = _configStore.Load() with
        {
            DocumentRoot = _paths.DocumentRoot,
            DailyPathPattern = "Daily/{yyyy}/{yyyy-MM-dd}.md"
        };
        ValidateDocumentRoot(Config);
        _configStore.Save(Config);
        _ledger = new CaptureLedger(_paths.CaptureDatabase);
        await _ledger.InitializeAsync(cancellationToken);
        _apiToken = new TokenStore(_paths).GetOrCreate();
        _extensionCredentials = new ExtensionCredentialStore(_paths.ExtensionClientsFile);
        _extensionPairing = new ExtensionPairingService(_extensionCredentials);
        await RebuildRuntimeAsync(cancellationToken);
    }

    public async Task ApplyConfigAsync(PkwsConfig config, CancellationToken cancellationToken = default)
    {
        ConfigStore.Validate(config);
        ValidateDocumentRoot(config);
        _configStore!.Save(config);
        Config = config;
        await RebuildRuntimeAsync(cancellationToken);
    }

    public Task<CaptureResponse> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default) =>
        CaptureService.CaptureAsync(request, cancellationToken);

    public Task<(string RelativePath, string Content)> ReadTodayAsync(CancellationToken cancellationToken = default) =>
        CaptureService.ReadTodayAsync(cancellationToken);

    public void OpenDocumentFolder()
    {
        Directory.CreateDirectory(Config.DocumentRoot);
        Process.Start(new ProcessStartInfo(Config.DocumentRoot) { UseShellExecute = true });
    }

    public PairingCodeInfo CreateExtensionPairingCode() =>
        _extensionPairing?.CreateCode() ?? throw new InvalidOperationException("插件配对服务尚未启动。");

    public async ValueTask DisposeAsync()
    {
        if (_apiHost is not null)
        {
            await _apiHost.DisposeAsync();
        }

        _gate.Dispose();
    }

    private async Task RebuildRuntimeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_apiHost is not null)
            {
                await _apiHost.DisposeAsync();
                _apiHost = null;
            }

            CaptureService = new CaptureService(
                Config,
                _ledger!,
                new DailyNoteStore(Config),
                new MarkdownEntrySerializer());
            await CaptureService.RecoverPendingAsync(cancellationToken);

            try
            {
                _apiHost = await LocalApiHost.StartAsync(
                    CaptureService,
                    Config,
                    _apiToken!,
                    cancellationToken,
                    _extensionPairing,
                    _extensionCredentials);
                ApiStatus = $"{_apiHost.BaseAddress}（仅本机）";
            }
            catch (Exception exception)
            {
                ApiStatus = $"未启动：{exception.Message}";
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateDocumentRoot(PkwsConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DocumentRoot) || !Directory.Exists(config.DocumentRoot))
        {
            throw new CaptureException("VAULT_UNAVAILABLE", "程序目录下的 doc 文档目录不可用。", false, 400);
        }

        var store = new DailyNoteStore(config);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneResolver.Resolve(config.TimeZoneId)).DateTime);
        var target = store.GetFullPath(store.GetRelativePath(today));
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".pkws-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "PKWS write test");
            File.Delete(probe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CaptureException("VAULT_NOT_WRITABLE", "程序目录下的 doc 文档目录不可写。", false, 400, exception);
        }
    }
}
