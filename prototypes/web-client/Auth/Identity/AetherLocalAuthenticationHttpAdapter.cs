using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace AetherSDR.Web.Auth.Identity;

internal sealed record AetherLocalAuthenticationHttpAdapterReport(
    IReadOnlyList<string> EndpointPaths);

internal static class AetherLocalAuthenticationHttpAdapter
{
    internal const string OptionsPath = "/api/auth/options";
    internal const string PasswordPath = "/api/auth/local/password";
    internal const string MfaPath = "/api/auth/local/mfa";
    internal const int MaximumRequestBodyBytes = 4096;
    internal const string RejectedCode = "local-authentication-rejected";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    internal static AetherLocalAuthenticationHttpAdapterReport Map(
        WebApplication app,
        AetherAuthenticationTopology topology)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(topology);

        if (topology.Mode == AetherAuthenticationMode.ServiceBoundary)
        {
            return new([]);
        }

        List<string> paths = [OptionsPath];
        app.MapGet(
                OptionsPath,
                (HttpContext context, IAntiforgery antiforgery, string? returnUrl) =>
                    Options(context, antiforgery, topology, returnUrl))
            .AllowAnonymous();

        if (topology.LocalAccountsEnabled)
        {
            paths.Add(PasswordPath);
            paths.Add(MfaPath);
            app.MapPost(PasswordPath, HandlePasswordAsync)
                .AllowAnonymous()
                .RequireRateLimiting(
                    AetherLocalAuthenticationDefaults.RateLimitPolicy)
                .RequireAetherAntiforgery();
            app.MapPost(MfaPath, HandleMfaAsync)
                .AllowAnonymous()
                .RequireRateLimiting(
                    AetherLocalAuthenticationDefaults.RateLimitPolicy)
                .RequireAetherAntiforgery();
        }

        return new(paths.AsReadOnly());
    }

    private static IResult Options(
        HttpContext context,
        IAntiforgery antiforgery,
        AetherAuthenticationTopology topology,
        string? returnUrl)
    {
        ApplyNoStore(context.Response);
        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
        AetherExternalProviderDescriptor? provider = topology.ExternalProvider;
        return Json(new
        {
            developmentMode =
                topology.Mode == AetherAuthenticationMode.Development,
            localAccountsEnabled = topology.LocalAccountsEnabled,
            externalProvider = provider is null
                ? null
                : new
                {
                    id = provider.ProviderId,
                    kind = provider.Kind == AetherExternalProviderKind.MicrosoftEntraId
                        ? "microsoftEntraId"
                        : "openIdConnect",
                    displayName =
                        provider.Kind == AetherExternalProviderKind.MicrosoftEntraId
                            ? "Microsoft Entra ID"
                            : "OpenID Connect"
                },
            returnUrl = LocalReturnUrl.Normalize(returnUrl),
            antiforgery = new
            {
                headerName = AetherAntiforgery.HeaderName,
                requestToken = tokens.RequestToken ?? string.Empty
            }
        });
    }

    private static async Task<IResult> HandlePasswordAsync(
        HttpContext context,
        AetherLocalPasswordAuthenticationService passwords)
    {
        ApplyNoStore(context.Response);
        AetherLocalPasswordRequest? body =
            await ReadJsonAsync<AetherLocalPasswordRequest>(context);
        if (body is null)
        {
            return InvalidRequest();
        }

        AetherLocalPasswordVerificationResult result =
            await passwords.VerifyAsync(
                body.UserName,
                body.Password,
                CorrelationId("password"),
                context.RequestAborted);
        if (!result.ReadyForSecondFactor ||
            string.IsNullOrEmpty(result.ChallengeToken))
        {
            return Rejected();
        }

        return Json(new
        {
            code = result.Code,
            challengeToken = result.ChallengeToken
        });
    }

    private static async Task<IResult> HandleMfaAsync(
        HttpContext context,
        AetherLocalMfaAuthenticationService mfa,
        AetherAuthenticationTopology topology)
    {
        ApplyNoStore(context.Response);
        AetherLocalMfaRequest? body =
            await ReadJsonAsync<AetherLocalMfaRequest>(context);
        if (body is null ||
            body.ReturnUrl is { Length: > 2048 })
        {
            return InvalidRequest();
        }

        AetherLocalMfaAuthenticationResult result =
            await mfa.AuthenticateAsync(
                body.ChallengeToken,
                body.VerificationCode,
                CorrelationId("mfa"),
                topology.SessionAbsoluteLifetime,
                context.RequestAborted);
        if (!result.Succeeded ||
            result.Principal is null ||
            result.SessionId is null ||
            result.AbsoluteExpiresAtUtc is null)
        {
            return Rejected();
        }

        await context.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            result.Principal,
            new AuthenticationProperties
            {
                AllowRefresh = false,
                IsPersistent = false,
                ExpiresUtc = result.AbsoluteExpiresAtUtc
            });

        return Json(new
        {
            code = result.Code,
            redirectUrl = LocalReturnUrl.Normalize(body.ReturnUrl)
        });
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context)
        where T : class
    {
        string? contentType = context.Request.ContentType;
        int separator = contentType?.IndexOf(';') ?? -1;
        string mediaType = separator >= 0
            ? contentType![..separator].Trim()
            : contentType?.Trim() ?? string.Empty;
        long? contentLength = context.Request.ContentLength;
        if (!string.Equals(
                mediaType,
                "application/json",
                StringComparison.OrdinalIgnoreCase) ||
            contentLength is null or <= 0 or > MaximumRequestBodyBytes)
        {
            return null;
        }

        byte[] payload =
            GC.AllocateUninitializedArray<byte>((int)contentLength.Value);
        try
        {
            int total = 0;
            while (total < payload.Length)
            {
                int read = await context.Request.Body.ReadAsync(
                    payload.AsMemory(total),
                    context.RequestAborted);
                if (read == 0)
                {
                    return null;
                }
                total += read;
            }

            byte[] extra = new byte[1];
            try
            {
                if (await context.Request.Body.ReadAsync(
                        extra,
                        context.RequestAborted) != 0)
                {
                    return null;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(extra);
            }

            RejectDuplicateProperties(payload);
            return JsonSerializer.Deserialize<T>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void RejectDuplicateProperties(ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(
            payload,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        RejectDuplicateProperties(document.RootElement);
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException("Duplicate JSON property.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static string CorrelationId(string operation) =>
        $"local-auth-{operation}-{Guid.NewGuid():N}";

    private static void ApplyNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, max-age=0";
        response.Headers.Pragma = "no-cache";
    }

    private static IResult InvalidRequest() =>
        Json(
            new { code = "invalid-authentication-request" },
            StatusCodes.Status400BadRequest);

    private static IResult Rejected() =>
        Json(
            new { code = RejectedCode },
            StatusCodes.Status401Unauthorized);

    private static IResult Json(
        object value,
        int statusCode = StatusCodes.Status200OK) =>
        Results.Json(value, JsonOptions, statusCode: statusCode);

    private sealed record AetherLocalPasswordRequest(
        string? UserName,
        string? Password);

    private sealed record AetherLocalMfaRequest(
        string? ChallengeToken,
        string? VerificationCode,
        string? ReturnUrl);
}
