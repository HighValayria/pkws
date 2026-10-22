using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pkws.Core;

namespace Pkws.Api;

public sealed class LocalApiHost : IAsyncDisposable
{
    private readonly WebApplication _application;

    private LocalApiHost(WebApplication application, Uri baseAddress)
    {
        _application = application;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<LocalApiHost> StartAsync(
        CaptureService captureService,
        PkwsConfig config,
        string apiToken,
        CancellationToken cancellationToken = default,
        ExtensionPairingService? pairingService = null,
        ExtensionCredentialStore? credentialStore = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LocalApiHost).Assembly.FullName,
            EnvironmentName = Environments.Production
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = CaptureService.MaxContentBytes + 64 * 1024;
            options.Listen(IPAddress.Loopback, config.ApiPort);
        });

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";

            var origin = context.Request.Headers.Origin.ToString();
            var isExtensionOrigin = ExtensionOrigin.TryGetId(origin, out _);
            if (isExtensionOrigin)
            {
                context.Response.Headers.AccessControlAllowOrigin = origin;
                context.Response.Headers.Vary = "Origin";
            }

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                if (!isExtensionOrigin)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                context.Response.Headers.AccessControlAllowHeaders = "authorization, content-type";
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            if (context.Request.Path == "/v1/health" || context.Request.Path == "/v1/pairings")
            {
                await next().ConfigureAwait(false);
                return;
            }

            var guard = ApiRequestGuard.Authorize(context.Request, apiToken, config.AllowedOrigins, credentialStore);
            if (guard is not null)
            {
                context.Response.StatusCode = guard.Value.StatusCode;
                await context.Response.WriteAsJsonAsync(new ApiError(guard.Value.Code, guard.Value.Message, false)).ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
        });

        app.MapGet("/v1/health", () => Results.Ok(new
        {
            status = "ok",
            service = "pkws-capture",
            schema_version = 1
        }));

        app.MapGet("/v1/context", () => Results.Ok(new
        {
            schema_version = 1,
            time_zone = config.TimeZoneId,
            capabilities = new[] { "desktop", "weread", "idempotency", "status-query", "extension-pairing" }
        }));

        app.MapPost("/v1/pairings", (PairingRequest request, HttpRequest httpRequest) =>
        {
            if (pairingService is null)
            {
                return Results.Json(new ApiError("PAIRING_UNAVAILABLE", "桌面端未启用插件配对。", false), statusCode: 503);
            }

            try
            {
                return Results.Ok(pairingService.Exchange(request, httpRequest.Headers.Origin.ToString()));
            }
            catch (PairingException exception)
            {
                return Results.Json(new ApiError(exception.Code, exception.Message, false), statusCode: exception.HttpStatus);
            }
        });

        app.MapGet("/v1/captures/{requestId}", async (string requestId, CancellationToken token) =>
        {
            var status = await captureService.GetStatusAsync(requestId, token).ConfigureAwait(false);
            return status is null ? Results.NotFound(new ApiError("NOT_FOUND", "未找到该请求。", false)) : Results.Ok(status);
        });

        app.MapPost("/v1/captures", async (CaptureRequest request, CancellationToken token) =>
        {
            try
            {
                var response = await captureService.CaptureAsync(request, token).ConfigureAwait(false);
                return Results.Ok(response);
            }
            catch (CaptureException exception)
            {
                return Results.Json(
                    new ApiError(exception.Code, exception.Message, exception.Retryable),
                    statusCode: exception.HttpStatus);
            }
        });

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.SingleOrDefault() ?? $"http://127.0.0.1:{config.ApiPort}";
        if (address.Contains("[::]", StringComparison.Ordinal))
        {
            address = address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal);
        }
        else if (address.Contains("0.0.0.0", StringComparison.Ordinal))
        {
            address = address.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
        }

        return new LocalApiHost(app, new Uri(address));
    }

    public async ValueTask DisposeAsync()
    {
        await _application.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
    }
}

public static class ApiRequestGuard
{
    public static GuardFailure? Authorize(
        HttpRequest request,
        string expectedToken,
        IReadOnlyList<string> allowedOrigins,
        ExtensionCredentialStore? extensionCredentials = null)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return new GuardFailure(StatusCodes.Status401Unauthorized, "AUTH_FAILED", "缺少或使用了无效的本地 API 令牌。");
        }

        var token = authorization[7..];
        var origin = request.Headers.Origin.ToString();
        if (extensionCredentials?.Validate(origin, token) == true)
        {
            return null;
        }

        if (!FixedTimeEquals(token, expectedToken))
        {
            return new GuardFailure(StatusCodes.Status401Unauthorized, "AUTH_FAILED", "缺少或使用了无效的本地 API 令牌。");
        }

        if (origin.Length > 0 && !allowedOrigins.Contains(origin, StringComparer.Ordinal))
        {
            return new GuardFailure(StatusCodes.Status403Forbidden, "ORIGIN_FORBIDDEN", "该浏览器来源未获准访问本地记录服务。");
        }

        return null;
    }

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var actualBytes = System.Text.Encoding.UTF8.GetBytes(actual);
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }
}

public readonly record struct GuardFailure(int StatusCode, string Code, string Message);
public sealed record ApiError(string Code, string Message, bool Retryable);
