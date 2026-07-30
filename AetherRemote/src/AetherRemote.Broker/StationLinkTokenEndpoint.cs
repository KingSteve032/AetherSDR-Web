using System.Text.Json;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

namespace AetherRemote.Broker;

public sealed class StationLinkTokenEndpoint(
    IOptions<StationLinkSettings> settings,
    StationCredentialVerifier credentials,
    StationLinkTokenService tokens,
    ILogger<StationLinkTokenEndpoint> logger)
{
    private const int MaximumRequestBytes = 4 * 1024;
    private readonly StationLinkSettings m_settings = settings.Value;

    public async Task HandleAsync(HttpContext context)
    {
        if (!m_settings.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (m_settings.RequireForwardedHttps &&
            !context.Request.IsHttps &&
            !ForwardedAsHttps(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        string stationId =
            context.Request.Headers["X-Aether-Station-Id"]
                .FirstOrDefault()?
                .Trim() ??
            string.Empty;
        string credential = ReadBearerCredential(context.Request);
        if (!credentials.VerifyStation(stationId, credential))
        {
            logger.LogWarning(
                "Rejected a station token request from {RemoteAddress}",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        StationLinkTokenRequest? request;
        try
        {
            request = await ReadBoundedRequestAsync(
                context.Request,
                context.RequestAborted);
        }
        catch (RequestTooLargeException)
        {
            context.Response.StatusCode =
                StatusCodes.Status413PayloadTooLarge;
            return;
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        string? validation =
            StationProtocolValidator.ValidateLinkTokenRequest(
                request,
                stationId);
        if (validation is not null || request?.Capabilities is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        try
        {
            StationLinkTokenResponse response = tokens.Issue(
                stationId,
                request.Capabilities);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(
                response,
                StationProtocol.JsonOptions,
                context.RequestAborted);
        }
        catch (StationLinkTokenException exception)
        {
            int status = exception.Code == "token_capacity"
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status400BadRequest;
            await Results.Json(
                new
                {
                    error = exception.Message,
                    code = exception.Code
                },
                statusCode: status)
                .ExecuteAsync(context);
        }
    }

    private static async Task<StationLinkTokenRequest?> ReadBoundedRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaximumRequestBytes)
        {
            throw new RequestTooLargeException();
        }

        using MemoryStream payload = new();
        byte[] buffer = new byte[1024];
        while (true)
        {
            int read = await request.Body.ReadAsync(
                buffer,
                cancellationToken);
            if (read == 0)
            {
                break;
            }
            if (payload.Length + read > MaximumRequestBytes)
            {
                throw new RequestTooLargeException();
            }
            await payload.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
        payload.Position = 0;
        return await JsonSerializer.DeserializeAsync<
            StationLinkTokenRequest>(
                payload,
                StationProtocol.JsonOptions,
                cancellationToken);
    }

    private static string ReadBearerCredential(HttpRequest request)
    {
        string authorization =
            request.Headers.Authorization.FirstOrDefault() ?? string.Empty;
        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.Ordinal)
            ? authorization[prefix.Length..].Trim()
            : string.Empty;
    }

    private static bool ForwardedAsHttps(HttpRequest request)
    {
        string forwarded =
            request.Headers["X-Forwarded-Proto"].FirstOrDefault() ??
            string.Empty;
        string firstValue = forwarded
            .Split(',', 2, StringSplitOptions.TrimEntries)[0];
        return string.Equals(
            firstValue,
            "https",
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RequestTooLargeException : Exception;
}
