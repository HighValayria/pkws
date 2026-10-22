using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pkws.Core;

namespace Pkws.Api;

public sealed record PairingCodeInfo(string Code, DateTimeOffset ExpiresAt);

public sealed record PairingRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("extension_id")] string ExtensionId);

public sealed record PairingResponse(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("extension_id")] string ExtensionId);

public sealed class ExtensionCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();

    public ExtensionCredentialStore(string path) => _path = Path.GetFullPath(path);

    public string Issue(string extensionId)
    {
        if (!ExtensionOrigin.IsValidId(extensionId))
        {
            throw new PairingException("PAIRING_INVALID_CLIENT", "扩展标识无效。", 400);
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tokenHash = Hash(token);
        lock (_gate)
        {
            var clients = Load();
            clients.RemoveAll(client => string.Equals(client.ExtensionId, extensionId, StringComparison.Ordinal));
            clients.Add(new ExtensionClient(extensionId, tokenHash, DateTimeOffset.UtcNow));
            if (File.Exists(_path))
            {
                File.SetAttributes(_path, File.GetAttributes(_path) & ~FileAttributes.Hidden);
            }

            try
            {
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(clients, JsonOptions));
            }
            finally
            {
                if (File.Exists(_path))
                {
                    File.SetAttributes(_path, File.GetAttributes(_path) | FileAttributes.Hidden);
                }
            }
        }

        return token;
    }

    public bool Validate(string origin, string token)
    {
        if (!ExtensionOrigin.TryGetId(origin, out var extensionId) || string.IsNullOrEmpty(token))
        {
            return false;
        }

        var actualHash = Hash(token);
        lock (_gate)
        {
            var expected = Load().FirstOrDefault(client =>
                string.Equals(client.ExtensionId, extensionId, StringComparison.Ordinal));
            return expected is not null && FixedTimeEquals(actualHash, expected.TokenHash);
        }
    }

    private List<ExtensionClient> Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ExtensionClient>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new PairingException("PAIRING_STORE_INVALID", "插件凭证存储损坏，请删除后重新配对。", 500, exception);
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private sealed record ExtensionClient(string ExtensionId, string TokenHash, DateTimeOffset CreatedAt);
}

public sealed class ExtensionPairingService
{
    private readonly ExtensionCredentialStore _credentials;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private PairingAttempt? _current;

    public ExtensionPairingService(ExtensionCredentialStore credentials, TimeProvider? clock = null)
    {
        _credentials = credentials;
        _clock = clock ?? TimeProvider.System;
    }

    public PairingCodeInfo CreateCode()
    {
        var now = _clock.GetUtcNow();
        var attempt = new PairingAttempt(RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"), now.AddMinutes(5), 0);
        lock (_gate)
        {
            _current = attempt;
        }

        return new PairingCodeInfo(attempt.Code, attempt.ExpiresAt);
    }

    public PairingResponse Exchange(PairingRequest request, string origin)
    {
        if (!ExtensionOrigin.TryGetId(origin, out var originId) ||
            !string.Equals(originId, request.ExtensionId, StringComparison.Ordinal))
        {
            throw new PairingException("PAIRING_ORIGIN_MISMATCH", "配对请求必须来自当前 Chrome 插件。", 403);
        }

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_current is null || now >= _current.ExpiresAt)
            {
                _current = null;
                throw new PairingException("PAIRING_CODE_EXPIRED", "配对码不存在或已过期，请在桌面端重新生成。", 401);
            }

            if (string.IsNullOrWhiteSpace(request.Code) || !FixedTimeEquals(request.Code.Trim(), _current.Code))
            {
                var attempts = _current.FailedAttempts + 1;
                _current = attempts >= 5 ? null : _current with { FailedAttempts = attempts };
                throw new PairingException("PAIRING_CODE_INVALID", "配对码不正确。", 401);
            }

            _current = null;
            return new PairingResponse(1, _credentials.Issue(request.ExtensionId), request.ExtensionId);
        }
    }

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private sealed record PairingAttempt(string Code, DateTimeOffset ExpiresAt, int FailedAttempts);
}

public static partial class ExtensionOrigin
{
    [GeneratedRegex("^[a-p]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionIdPattern();

    public static bool IsValidId(string? value) => value is not null && ExtensionIdPattern().IsMatch(value);

    public static bool TryGetId(string origin, out string extensionId)
    {
        const string prefix = "chrome-extension://";
        extensionId = string.Empty;
        if (!origin.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = origin[prefix.Length..];
        if (!IsValidId(candidate))
        {
            return false;
        }

        extensionId = candidate;
        return true;
    }
}

public sealed class PairingException : Exception
{
    public PairingException(string code, string message, int httpStatus, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    public string Code { get; }
    public int HttpStatus { get; }
}
