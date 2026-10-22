using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pkws.Api;
using Pkws.Core;

namespace Pkws.Tests;

public sealed class LocalApiHostTests
{
    [Fact]
    public async Task CaptureEndpoint_RejectsMissingTokenAndWrongOrigin()
    {
        using var environment = new TestEnvironment();
        var config = environment.Config with
        {
            ApiPort = 0,
            AllowedOrigins = ["chrome-extension://allowed-id"]
        };
        await using var host = await LocalApiHost.StartAsync(environment.CreateService(), config, "test-secret-token");
        using var client = CreateClient(host);
        var request = environment.Request("API 安全");

        var unauthenticated = await client.PostAsJsonAsync("v1/captures", request);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret-token");
        client.DefaultRequestHeaders.Add("Origin", "https://malicious.example");
        var wrongOrigin = await client.PostAsJsonAsync("v1/captures", request);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode);
        Assert.Null(await environment.Ledger.GetAsync(request.RequestId));
    }

    [Fact]
    public async Task CaptureEndpoint_AuthorizedRequestWritesAndCanQueryStatus()
    {
        using var environment = new TestEnvironment();
        var config = environment.Config with { ApiPort = 0 };
        await using var host = await LocalApiHost.StartAsync(environment.CreateService(), config, "test-secret-token");
        using var client = CreateClient(host);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-secret-token");
        var request = environment.Request("API 写入");

        var response = await client.PostAsJsonAsync("v1/captures", request);
        var status = await client.GetAsync($"v1/captures/{request.RequestId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var (_, markdown) = await environment.CreateService().ReadTodayAsync();
        Assert.Contains("API 写入", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthEndpoint_IsLoopbackAndDoesNotLeakVaultPath()
    {
        using var environment = new TestEnvironment();
        await using var host = await LocalApiHost.StartAsync(
            environment.CreateService(),
            environment.Config with { ApiPort = 0 },
            "test-secret-token");
        using var client = CreateClient(host);

        var body = await client.GetStringAsync("v1/health");

        Assert.True(IPAddress.IsLoopback(IPAddress.Parse(host.BaseAddress.Host)));
        Assert.DoesNotContain(environment.Vault, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Pairing_IssuesOriginBoundCredentialOnceAndAuthorizesCapture()
    {
        using var environment = new TestEnvironment();
        const string extensionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var credentialPath = Path.Combine(environment.Data, "extension-clients.json");
        var credentials = new ExtensionCredentialStore(credentialPath);
        var pairing = new ExtensionPairingService(credentials, environment.Clock);
        var code = pairing.CreateCode();
        await using var host = await LocalApiHost.StartAsync(
            environment.CreateService(),
            environment.Config with { ApiPort = 0 },
            "desktop-secret-token",
            pairingService: pairing,
            credentialStore: credentials);
        using var client = CreateClient(host);
        client.DefaultRequestHeaders.Add("Origin", $"chrome-extension://{extensionId}");

        var paired = await client.PostAsJsonAsync("v1/pairings", new PairingRequest(code.Code, extensionId));
        var response = await paired.Content.ReadFromJsonAsync<PairingResponse>();
        var replay = await client.PostAsJsonAsync("v1/pairings", new PairingRequest(code.Code, extensionId));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response!.AccessToken);
        var capture = await client.PostAsJsonAsync("v1/captures", new CaptureRequest
        {
            RequestId = Guid.CreateVersion7().ToString(),
            SourceType = "weread",
            Content = "插件笔记",
            SourceContext = new SourceContext
            {
                Provider = "weread",
                WorkTitle = "测试书",
                Authors = [],
                Url = "https://weread.qq.com/web/reader/test-book"
            }
        });

        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        Assert.DoesNotContain(response.AccessToken, File.ReadAllText(credentialPath), StringComparison.Ordinal);
        var replacement = credentials.Issue(extensionId);
        Assert.False(credentials.Validate($"chrome-extension://{extensionId}", response.AccessToken));
        Assert.True(credentials.Validate($"chrome-extension://{extensionId}", replacement));
    }

    [Fact]
    public async Task Pairing_RejectsWebOriginAndMismatchedExtensionId()
    {
        using var environment = new TestEnvironment();
        var credentials = new ExtensionCredentialStore(Path.Combine(environment.Data, "extension-clients.json"));
        var pairing = new ExtensionPairingService(credentials, environment.Clock);
        var code = pairing.CreateCode();
        await using var host = await LocalApiHost.StartAsync(
            environment.CreateService(),
            environment.Config with { ApiPort = 0 },
            "desktop-secret-token",
            pairingService: pairing,
            credentialStore: credentials);
        using var client = CreateClient(host);
        client.DefaultRequestHeaders.Add("Origin", "https://malicious.example");

        var response = await client.PostAsJsonAsync(
            "v1/pairings",
            new PairingRequest(code.Code, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static HttpClient CreateClient(LocalApiHost host) => new(new HttpClientHandler { UseProxy = false })
    {
        BaseAddress = host.BaseAddress,
        Timeout = TimeSpan.FromSeconds(5)
    };
}
