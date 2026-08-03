using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace AetherSDR.Web.Setup;

public sealed record InstallationSetupOnlyHttpAdapterReport(
    IReadOnlyList<string> EndpointPaths,
    IReadOnlyList<string> RateLimitPolicies);

public sealed record InstallationSetupHttpSessionMetadata(
    int SetupSchemaVersion,
    long SetupRevision,
    DateTimeOffset SetupCreatedAt,
    DateTimeOffset ClaimedAt,
    DateTimeOffset ExpiresAt,
    InstallationSetupStep LastCompletedStep);

public sealed record InstallationSetupHttpPageResponse(
    InstallationSetupStatusReport Status,
    InstallationSetupHttpSecurityContract SecurityContract);

public sealed record InstallationSetupHttpClaimResponse(
    InstallationSetupStatusReport Status,
    InstallationSetupHttpSessionMetadata Session);

public sealed record InstallationSetupHttpSessionResponse(
    InstallationSetupStatusReport Status,
    InstallationSetupHttpSessionMetadata Session);

public sealed record InstallationSetupHttpPreflightResponse(
    InstallationSetupStatusReport Status,
    InstallationSetupHttpSessionMetadata Session,
    InstallationSetupPreflightReport Preflight);

public sealed record InstallationSetupHttpMutationResponse(
    InstallationSetupStatusReport Status,
    InstallationSetupHttpSessionMetadata Session,
    InstallationSetupCenterMutationKind MutationKind);

public sealed record InstallationSetupHttpErrorResponse(
    string Code,
    IReadOnlyList<InstallationSetupHttpRejectionCode> Rejections,
    long? ExpectedRevision = null,
    long? ActualRevision = null);

public sealed record InstallationSetupHttpClaimBody(
    long ExpectedRevision,
    string BootstrapToken);

public sealed record InstallationSetupHttpTopologyBody(
    long ExpectedRevision,
    InstallationTopologyKind Topology);

public sealed record InstallationSetupHttpPublicUrlBody(
    long ExpectedRevision,
    string CanonicalPublicUrl);

public sealed record InstallationSetupHttpPathsBody(
    long ExpectedRevision,
    string ConfigurationDirectory,
    string StateDirectory,
    string SecretDirectory,
    string ReleaseDirectory,
    string BackupDirectory,
    string LogDirectory);

public sealed record InstallationSetupHttpUpdateChannelBody(
    long ExpectedRevision,
    InstallationUpdateChannel UpdateChannel,
    string? PinnedRelease);

public sealed record InstallationSetupHttpBackupBody(
    long ExpectedRevision,
    bool Confirmed);

public sealed record InstallationSetupHttpTransmitSupportBody(
    long ExpectedRevision,
    bool InstallTransmitSupport,
    bool AcknowledgedInstallationDoesNotEnableTransmit);

public sealed record InstallationSetupHttpRevokeBody(long ExpectedRevision);

public static class InstallationSetupOnlyHttpAdapter
{
    public const string RevisionHeaderName = "X-Aether-Setup-Revision";
    public const string PagePath = "/setup";
    public const string ClaimPath = "/setup/api/claim";
    public const string SessionPath = "/setup/api/session";
    public const string PreflightPath = "/setup/api/preflight";
    public const string TopologyPath = "/setup/api/topology";
    public const string PublicUrlPath = "/setup/api/public-url";
    public const string PathsPath = "/setup/api/paths";
    public const string UpdateChannelPath = "/setup/api/update-channel";
    public const string BackupPath = "/setup/api/backup";
    public const string TransmitSupportPath = "/setup/api/transmit-support";
    public const string RevokePath = "/setup/api/revoke";

    private const int MaximumTextFieldLength = 4096;
    private const int MaximumBootstrapTokenLength = 256;
    private const int MaximumReleaseIdentityLength = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false)
        }
    };

    private static readonly string[] EndpointPaths =
    [
        PagePath,
        ClaimPath,
        SessionPath,
        PreflightPath,
        TopologyPath,
        PublicUrlPath,
        PathsPath,
        UpdateChannelPath,
        BackupPath,
        TransmitSupportPath,
        RevokePath
    ];

    public static void ConfigureServices(
        IServiceCollection services,
        InstallationSetupHttpSecurityContract contract)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(contract);
        ValidateContract(contract);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            foreach (InstallationSetupHttpRateLimitContract rateLimit in
                     contract.RateLimits)
            {
                options.AddFixedWindowLimiter(
                    rateLimit.PolicyName,
                    limiter =>
                    {
                        limiter.PermitLimit = rateLimit.PermitLimit;
                        limiter.Window = rateLimit.Window;
                        limiter.QueueLimit = rateLimit.QueueLimit;
                        limiter.AutoReplenishment = rateLimit.AutoReplenishment;
                    });
            }
        });
    }

    public static InstallationSetupOnlyHttpAdapterReport Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        InstallationSetupCenterApplication application =
            app.Services.GetRequiredService<InstallationSetupCenterApplication>();
        InstallationSetupHttpSecurityContract contract =
            application.SecurityContract;
        ValidateContract(contract);

        if (((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Any(endpoint =>
                endpoint.RoutePattern.RawText?.StartsWith(
                    "/setup",
                    StringComparison.Ordinal) == true))
        {
            throw new InvalidOperationException(
                "Setup-only HTTP endpoints were already mapped.");
        }

        app.Use(
            async (context, next) =>
            {
                ApplyResponseHeaders(context.Response, contract.ResponseHeaders);
                await next();
            });
        app.UseRouting();
        app.UseRateLimiter();

        MapGet(app, PagePath, HandlePageAsync, Policy(contract, PagePath));
        MapPost(app, ClaimPath, HandleClaimAsync, Policy(contract, ClaimPath));
        MapGet(app, SessionPath, HandleSessionAsync, Policy(contract, SessionPath));
        MapGet(app, PreflightPath, HandlePreflightAsync, Policy(contract, PreflightPath));
        MapPost(app, TopologyPath, HandleTopologyAsync, Policy(contract, TopologyPath));
        MapPost(app, PublicUrlPath, HandlePublicUrlAsync, Policy(contract, PublicUrlPath));
        MapPost(app, PathsPath, HandlePathsAsync, Policy(contract, PathsPath));
        MapPost(
            app,
            UpdateChannelPath,
            HandleUpdateChannelAsync,
            Policy(contract, UpdateChannelPath));
        MapPost(app, BackupPath, HandleBackupAsync, Policy(contract, BackupPath));
        MapPost(
            app,
            TransmitSupportPath,
            HandleTransmitSupportAsync,
            Policy(contract, TransmitSupportPath));
        MapPost(app, RevokePath, HandleRevokeAsync, Policy(contract, RevokePath));
        app.UseEndpoints(_ => { });

        return new InstallationSetupOnlyHttpAdapterReport(
            Array.AsReadOnly((string[])EndpointPaths.Clone()),
            contract.RateLimits
                .Select(rateLimit => rateLimit.PolicyName)
                .ToArray());
    }

    private static void MapGet(
        WebApplication app,
        string path,
        RequestDelegate handler,
        string rateLimitPolicy) =>
        app.MapGet(path, handler)
            .RequireRateLimiting(rateLimitPolicy);

    private static void MapPost(
        WebApplication app,
        string path,
        RequestDelegate handler,
        string rateLimitPolicy) =>
        app.MapPost(path, handler)
            .RequireRateLimiting(rateLimitPolicy);

    private static string Policy(
        InstallationSetupHttpSecurityContract contract,
        string path)
    {
        int index = path switch
        {
            PagePath => 0,
            ClaimPath => 1,
            SessionPath or PreflightPath => 2,
            TopologyPath or PublicUrlPath or PathsPath or UpdateChannelPath or
                BackupPath or TransmitSupportPath or RevokePath => 3,
            _ => throw new InvalidOperationException(
                $"No setup rate-limit policy is defined for '{path}'.")
        };
        return contract.RateLimits[index].PolicyName;
    }

    private static async Task HandlePageAsync(HttpContext context)
    {
        IResult result = await ExecuteAsync(
            context,
            async () =>
            {
                InstallationSetupCenterApplication application = Application(context);
                InstallationSetupHttpRequest request =
                    RequireSecurity(context, InstallationSetupHttpOperation.PageRead);
                InstallationSetupCenterPageResult page =
                    await application.ReadPageAsync(request, context.RequestAborted);
                TimeProvider time = Time(context);
                AppendCsrfCookie(
                    context.Response,
                    page.Csrf.Token,
                    time.GetUtcNow() +
                        page.SecurityContract.CsrfCookie.MaximumAge,
                    page.SecurityContract.CsrfCookie,
                    time);
                return Json(
                    new InstallationSetupHttpPageResponse(
                        page.Status,
                        page.SecurityContract));
            });
        await result.ExecuteAsync(context);
    }

    private static async Task HandleClaimAsync(HttpContext context)
    {
        IResult result = await ExecuteAsync(
            context,
            async () =>
            {
                InstallationSetupHttpRequest request =
                    RequireSecurity(context, InstallationSetupHttpOperation.BootstrapClaim);
                InstallationSetupHttpClaimBody body =
                    await ReadJsonAsync<InstallationSetupHttpClaimBody>(context);
                RequireRevision(body.ExpectedRevision);
                string bootstrapToken = RequireExactText(
                    body.BootstrapToken,
                    MaximumBootstrapTokenLength,
                    "bootstrap token");
                InstallationSetupCenterClaimResult claim =
                    await Application(context).ClaimAsync(
                        request,
                        body.ExpectedRevision,
                        bootstrapToken,
                        context.RequestAborted);
                AppendSessionCookies(
                    context.Response,
                    claim.Session,
                    claim.Csrf,
                    Application(context).SecurityContract,
                    Time(context));
                return Json(
                    new InstallationSetupHttpClaimResponse(
                        claim.Status,
                        Metadata(claim.Session, claim.Status.LastCompletedStep)));
            });
        await result.ExecuteAsync(context);
    }

    private static async Task HandleSessionAsync(HttpContext context)
    {
        IResult result = await ExecuteAsync(
            context,
            async () =>
            {
                InstallationSetupHttpRequest request =
                    RequireSecurity(context, InstallationSetupHttpOperation.SessionRead);
                long revision = RequireRevisionHeader(context.Request);
                InstallationSetupCenterSessionResult session =
                    await Application(context).ReadSessionAsync(
                        request,
                        RequireSessionToken(context.Request),
                        revision,
                        context.RequestAborted);
                return Json(
                    new InstallationSetupHttpSessionResponse(
                        session.Status,
                        Metadata(session.Session)));
            });
        await result.ExecuteAsync(context);
    }

    private static async Task HandlePreflightAsync(HttpContext context)
    {
        IResult result = await ExecuteAsync(
            context,
            async () =>
            {
                InstallationSetupHttpRequest request =
                    RequireSecurity(context, InstallationSetupHttpOperation.SessionRead);
                long revision = RequireRevisionHeader(context.Request);
                InstallationSetupCenterPreflightResult preflight =
                    await Application(context).ReadPreflightAsync(
                        request,
                        RequireSessionToken(context.Request),
                        revision,
                        context.RequestAborted);
                return Json(
                    new InstallationSetupHttpPreflightResponse(
                        preflight.Status,
                        Metadata(preflight.Session),
                        preflight.Preflight));
            });
        await result.ExecuteAsync(context);
    }

    private static Task HandleTopologyAsync(HttpContext context) =>
        HandleMutationAsync<InstallationSetupHttpTopologyBody>(
            context,
            body =>
            {
                RequireRevision(body.ExpectedRevision);
                if (!Enum.IsDefined(body.Topology) ||
                    !InstallationTopologyProfile.For(body.Topology).GatewayRunsHere)
                {
                    throw InvalidInput();
                }
                return new InstallationSetupCenterTopologyMutation(
                    body.ExpectedRevision,
                    body.Topology);
            });

    private static Task HandlePublicUrlAsync(HttpContext context) =>
        HandleMutationAsync<InstallationSetupHttpPublicUrlBody>(
            context,
            body =>
            {
                RequireRevision(body.ExpectedRevision);
                string value = RequireExactText(
                    body.CanonicalPublicUrl,
                    MaximumTextFieldLength,
                    "canonical public URL");
                CanonicalPublicUrl parsed;
                try
                {
                    parsed = CanonicalPublicUrl.Parse(value);
                }
                catch (InvalidOperationException)
                {
                    throw InvalidInput();
                }
                if (!string.Equals(parsed.Value, value, StringComparison.Ordinal))
                {
                    throw InvalidInput();
                }
                return new InstallationSetupCenterPublicUrlMutation(
                    body.ExpectedRevision,
                    value);
            });

    private static Task HandlePathsAsync(HttpContext context) =>
        HandleMutationAsync<InstallationSetupHttpPathsBody>(
            context,
            body =>
            {
                RequireRevision(body.ExpectedRevision);
                InstallationPaths paths = new(
                    RequireExactText(
                        body.ConfigurationDirectory,
                        MaximumTextFieldLength,
                        "configuration directory"),
                    RequireExactText(
                        body.StateDirectory,
                        MaximumTextFieldLength,
                        "state directory"),
                    RequireExactText(
                        body.SecretDirectory,
                        MaximumTextFieldLength,
                        "secret directory"),
                    RequireExactText(
                        body.ReleaseDirectory,
                        MaximumTextFieldLength,
                        "release directory"),
                    RequireExactText(
                        body.BackupDirectory,
                        MaximumTextFieldLength,
                        "backup directory"),
                    RequireExactText(
                        body.LogDirectory,
                        MaximumTextFieldLength,
                        "log directory"));
                try
                {
                    InstallationPaths.Validate(paths);
                }
                catch (InvalidOperationException)
                {
                    throw InvalidInput();
                }
                return new InstallationSetupCenterPathsMutation(
                    body.ExpectedRevision,
                    paths);
            });

    private static Task HandleUpdateChannelAsync(HttpContext context) =>
        HandleMutationAsync<InstallationSetupHttpUpdateChannelBody>(
            context,
            body =>
            {
                RequireRevision(body.ExpectedRevision);
                if (!Enum.IsDefined(body.UpdateChannel))
                {
                    throw InvalidInput();
                }
                string? pinnedRelease = body.PinnedRelease;
                if (pinnedRelease is not null)
                {
                    pinnedRelease = RequireExactText(
                        pinnedRelease,
                        MaximumReleaseIdentityLength,
                        "pinned release");
                }
                if (body.UpdateChannel == InstallationUpdateChannel.Pinned)
                {
                    try
                    {
                        _ = InstallationReleaseIdentity.Parse(
                            pinnedRelease ?? string.Empty);
                    }
                    catch (InvalidOperationException)
                    {
                        throw InvalidInput();
                    }
                }
                else if (!string.IsNullOrEmpty(pinnedRelease))
                {
                    throw InvalidInput();
                }
                return new InstallationSetupCenterUpdateChannelMutation(
                    body.ExpectedRevision,
                    body.UpdateChannel,
                    pinnedRelease);
            });

    private static Task HandleBackupAsync(HttpContext context) =>
        HandleMutationAsync<InstallationSetupHttpBackupBody>(
            context,
            body =>
            {
                RequireRevision(body.ExpectedRevision);
                if (!body.Confirmed)
                {
                    throw InvalidInput();
                }
                return new InstallationSetupCenterBackupConfirmationMutation(
                    body.ExpectedRevision);
            });

    private static Task HandleTransmitSupportAsync(HttpContext context) =>
        HandleMutationAsync<InstallationSetupHttpTransmitSupportBody>(
            context,
            body =>
            {
                RequireRevision(body.ExpectedRevision);
                if (!body.AcknowledgedInstallationDoesNotEnableTransmit)
                {
                    throw InvalidInput();
                }
                return new InstallationSetupCenterTransmitSupportMutation(
                    body.ExpectedRevision,
                    body.InstallTransmitSupport);
            });

    private static async Task HandleMutationAsync<TBody>(
        HttpContext context,
        Func<TBody, InstallationSetupCenterMutation> createMutation)
        where TBody : class
    {
        IResult result = await ExecuteAsync(
            context,
            async () =>
            {
                InstallationSetupHttpRequest request =
                    RequireSecurity(context, InstallationSetupHttpOperation.SessionMutation);
                TBody body = await ReadJsonAsync<TBody>(context);
                InstallationSetupCenterMutation mutation = createMutation(body);
                InstallationSetupCenterMutationResult updated =
                    await Application(context).MutateAsync(
                        request,
                        RequireSessionToken(context.Request),
                        mutation,
                        context.RequestAborted);
                AppendSessionCookies(
                    context.Response,
                    updated.Session,
                    updated.Csrf,
                    Application(context).SecurityContract,
                    Time(context));
                return Json(
                    new InstallationSetupHttpMutationResponse(
                        updated.Status,
                        Metadata(updated.Session, updated.Status.LastCompletedStep),
                        updated.MutationKind));
            });
        await result.ExecuteAsync(context);
    }

    private static async Task HandleRevokeAsync(HttpContext context)
    {
        IResult result = await ExecuteAsync(
            context,
            async () =>
            {
                InstallationSetupHttpRequest request =
                    RequireSecurity(context, InstallationSetupHttpOperation.SessionMutation);
                InstallationSetupHttpRevokeBody body =
                    await ReadJsonAsync<InstallationSetupHttpRevokeBody>(context);
                RequireRevision(body.ExpectedRevision);
                InstallationSetupCenterApplication application = Application(context);
                await application.RevokeAsync(
                    request,
                    RequireSessionToken(context.Request),
                    body.ExpectedRevision,
                    context.RequestAborted);
                DeleteCookies(
                    context.Response,
                    application.SecurityContract);
                return Results.NoContent();
            });
        await result.ExecuteAsync(context);
    }

    private static InstallationSetupHttpRequest RequireSecurity(
        HttpContext context,
        InstallationSetupHttpOperation operation)
    {
        InstallationSetupHttpRequest request = CreateRequest(context.Request, operation);
        InstallationSetupHttpSecurityDecision decision =
            context.RequestServices
                .GetRequiredService<InstallationSetupHttpSecurityPolicy>()
                .Evaluate(request);
        if (!decision.Allowed)
        {
            throw new InstallationSetupCenterSecurityException(operation, decision);
        }

        IHttpMaxRequestBodySizeFeature? bodySize =
            context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is not null && !bodySize.IsReadOnly)
        {
            bodySize.MaxRequestBodySize = decision.MaximumRequestBodyBytes == 0
                ? 0
                : decision.MaximumRequestBodyBytes;
        }
        return request;
    }

    internal static InstallationSetupHttpRequest CreateRequest(
        HttpRequest request,
        InstallationSetupHttpOperation operation)
    {
        string? session = SingleCookie(
            request,
            InstallationSetupHttpSecurityPolicy.SessionCookieName);
        string? csrf = SingleCookie(
            request,
            InstallationSetupHttpSecurityPolicy.CsrfCookieName);
        return new InstallationSetupHttpRequest(
            operation,
            request.Method,
            request.Scheme,
            request.Host.Value ?? string.Empty,
            SingleHeader(request.Headers.Origin),
            SingleHeader(request.Headers["Sec-Fetch-Site"]),
            SingleHeader(request.Headers["Sec-Fetch-Mode"]),
            request.ContentType,
            request.ContentLength,
            request.QueryString.HasValue,
            !string.IsNullOrEmpty(session),
            csrf,
            SingleHeader(
                request.Headers[
                    InstallationSetupHttpSecurityPolicy.CsrfHeaderName]));
    }

    private static string? SingleHeader(StringValues values) =>
        values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => "\0"
        };

    private static string? SingleCookie(HttpRequest request, string name)
    {
        int matches = 0;
        string? value = null;
        foreach (string? header in request.Headers.Cookie)
        {
            if (header is null)
            {
                continue;
            }
            foreach (string segment in header.Split(';'))
            {
                int separator = segment.IndexOf('=');
                if (separator <= 0 ||
                    !string.Equals(
                        segment[..separator].Trim(),
                        name,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                matches++;
                value = segment[(separator + 1)..].Trim();
            }
        }
        return matches switch
        {
            0 => null,
            1 => value,
            _ => "\0"
        };
    }

    private static async Task<T> ReadJsonAsync<T>(HttpContext context)
        where T : class
    {
        long contentLength = context.Request.ContentLength ?? 0;
        if (contentLength is <= 0 or > int.MaxValue)
        {
            throw InvalidInput();
        }
        byte[] payload = GC.AllocateUninitializedArray<byte>((int)contentLength);
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
                    throw InvalidInput();
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
                    throw InvalidInput();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(extra);
            }

            RejectDuplicateProperties(payload);
            return JsonSerializer.Deserialize<T>(payload, JsonOptions) ??
                throw InvalidInput();
        }
        catch (JsonException)
        {
            throw InvalidInput();
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
                MaxDepth = 16
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
                    throw InvalidInput();
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

    private static long RequireRevisionHeader(HttpRequest request)
    {
        StringValues values = request.Headers[RevisionHeaderName];
        if (values.Count != 1 ||
            !long.TryParse(
                values[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long revision))
        {
            throw InvalidInput();
        }
        RequireRevision(revision);
        return revision;
    }

    private static void RequireRevision(long revision)
    {
        if (revision < 0)
        {
            throw InvalidInput();
        }
    }

    private static string RequireSessionToken(HttpRequest request)
    {
        string value = RequireExactText(
            SingleCookie(
                request,
                InstallationSetupHttpSecurityPolicy.SessionCookieName),
            MaximumBootstrapTokenLength,
            "setup session");
        if (value.Length != 43 ||
            value.Any(character =>
                character is not (>= 'A' and <= 'Z') and
                    not (>= 'a' and <= 'z') and
                    not (>= '0' and <= '9') and
                    not '-' and
                    not '_'))
        {
            throw InvalidInput();
        }
        return value;
    }

    private static string RequireExactText(
        string? value,
        int maximumLength,
        string field)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > maximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new InstallationSetupHttpInputException(
                $"Invalid {field} input.");
        }
        return value;
    }

    private static void AppendSessionCookies(
        HttpResponse response,
        InstallationSetupClaimSessionIssue session,
        InstallationSetupHttpCsrfIssue csrf,
        InstallationSetupHttpSecurityContract contract,
        TimeProvider timeProvider)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (session.ExpiresAt <= now ||
            session.ExpiresAt - now > contract.SessionCookie.MaximumAge)
        {
            throw new InvalidOperationException(
                "The setup session expiry exceeds the HTTP cookie contract.");
        }
        response.Cookies.Append(
            contract.SessionCookie.Name,
            session.Token,
            CookieOptions(
                contract.SessionCookie,
                session.ExpiresAt,
                now));
        AppendCsrfCookie(
            response,
            csrf.Token,
            session.ExpiresAt,
            contract.CsrfCookie,
            timeProvider);
    }

    internal static void AppendCsrfCookie(
        HttpResponse response,
        string csrfToken,
        DateTimeOffset expiresAt,
        InstallationSetupHttpCookieContract contract,
        TimeProvider timeProvider)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (expiresAt <= now || expiresAt - now > contract.MaximumAge)
        {
            throw new InvalidOperationException(
                "The setup CSRF expiry exceeds the HTTP cookie contract.");
        }
        response.Cookies.Append(
            contract.Name,
            csrfToken,
            CookieOptions(contract, expiresAt, now));
    }

    private static CookieOptions CookieOptions(
        InstallationSetupHttpCookieContract contract,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        ValidateCookieContract(contract);
        return new CookieOptions
        {
            Secure = true,
            HttpOnly = contract.HttpOnly,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Domain = null,
            Expires = expiresAt,
            MaxAge = expiresAt - now,
            IsEssential = true
        };
    }

    private static void DeleteCookies(
        HttpResponse response,
        InstallationSetupHttpSecurityContract contract)
    {
        response.Cookies.Delete(
            contract.SessionCookie.Name,
            DeleteCookieOptions(contract.SessionCookie));
        response.Cookies.Delete(
            contract.CsrfCookie.Name,
            DeleteCookieOptions(contract.CsrfCookie));
    }

    private static CookieOptions DeleteCookieOptions(
        InstallationSetupHttpCookieContract contract)
    {
        ValidateCookieContract(contract);
        return new CookieOptions
        {
            Secure = true,
            HttpOnly = contract.HttpOnly,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Domain = null,
            IsEssential = true
        };
    }

    internal static void ApplyResponseHeaders(
        HttpResponse response,
        InstallationSetupHttpResponseSecurityContract contract)
    {
        response.Headers.ContentSecurityPolicy = contract.ContentSecurityPolicy;
        response.Headers.CacheControl = contract.CacheControl;
        response.Headers["Referrer-Policy"] = contract.ReferrerPolicy;
        response.Headers["Permissions-Policy"] = contract.PermissionsPolicy;
        response.Headers["Cross-Origin-Opener-Policy"] =
            contract.CrossOriginOpenerPolicy;
        response.Headers["Cross-Origin-Resource-Policy"] =
            contract.CrossOriginResourcePolicy;
        response.Headers.XContentTypeOptions = contract.XContentTypeOptions;
    }

    private static void ValidateContract(
        InstallationSetupHttpSecurityContract contract)
    {
        _ = CanonicalPublicUrl.Parse(contract.CanonicalOrigin);
        ValidateCookieContract(contract.SessionCookie);
        ValidateCookieContract(contract.CsrfCookie);
        if (!string.Equals(
                contract.SessionCookie.Name,
                InstallationSetupHttpSecurityPolicy.SessionCookieName,
                StringComparison.Ordinal) ||
            !contract.SessionCookie.HttpOnly ||
            !string.Equals(
                contract.CsrfCookie.Name,
                InstallationSetupHttpSecurityPolicy.CsrfCookieName,
                StringComparison.Ordinal) ||
            contract.CsrfCookie.HttpOnly ||
            contract.SessionCookie.MaximumAge >
                InstallationSetupClaimSessionService.MaximumLifetime ||
            contract.CsrfCookie.MaximumAge >
                InstallationSetupClaimSessionService.MaximumLifetime)
        {
            throw new InvalidOperationException(
                "The setup HTTP adapter requires the exact session and CSRF cookie roles.");
        }
        if (contract.RateLimits.Count != 4 ||
            contract.RateLimits.Any(rateLimit =>
                string.IsNullOrWhiteSpace(rateLimit.PolicyName) ||
                rateLimit.PermitLimit <= 0 ||
                rateLimit.Window <= TimeSpan.Zero ||
                rateLimit.QueueLimit != 0 ||
                !rateLimit.AutoReplenishment) ||
            contract.RateLimits
                .Select(rateLimit => rateLimit.PolicyName)
                .Distinct(StringComparer.Ordinal)
                .Count() != 4)
        {
            throw new InvalidOperationException(
                "The setup HTTP adapter requires four exact zero-queue rate-limit policies.");
        }
    }

    private static void ValidateCookieContract(
        InstallationSetupHttpCookieContract contract)
    {
        if (!contract.Name.StartsWith("__Host-", StringComparison.Ordinal) ||
            !contract.Secure ||
            !string.Equals(contract.SameSite, "Strict", StringComparison.Ordinal) ||
            !string.Equals(contract.Path, "/", StringComparison.Ordinal) ||
            contract.DomainAllowed ||
            contract.MaximumAge <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "The setup HTTP adapter requires an exact host-only strict cookie contract.");
        }
    }

    private static InstallationSetupCenterApplication Application(
        HttpContext context) =>
        context.RequestServices
            .GetRequiredService<InstallationSetupCenterApplication>();

    private static TimeProvider Time(HttpContext context) =>
        context.RequestServices.GetRequiredService<TimeProvider>();

    private static InstallationSetupHttpSessionMetadata Metadata(
        InstallationSetupClaimSessionIssue session,
        InstallationSetupStep lastCompletedStep) =>
        new(
            session.SetupSchemaVersion,
            session.SetupRevision,
            session.SetupCreatedAt,
            session.ClaimedAt,
            session.ExpiresAt,
            lastCompletedStep);

    private static InstallationSetupHttpSessionMetadata Metadata(
        InstallationSetupClaimSessionContext session) =>
        new(
            session.SetupSchemaVersion,
            session.SetupRevision,
            session.SetupCreatedAt,
            session.ClaimedAt,
            session.ExpiresAt,
            session.LastCompletedStep);

    private static async Task<IResult> ExecuteAsync(
        HttpContext context,
        Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (InstallationSetupCenterSecurityException exception)
        {
            return Json(
                new InstallationSetupHttpErrorResponse(
                    "requestRejected",
                    exception.Decision?.Rejections ?? []),
                StatusCodes.Status403Forbidden);
        }
        catch (InstallationSetupHttpInputException)
        {
            return Json(
                new InstallationSetupHttpErrorResponse("invalidRequest", []),
                StatusCodes.Status400BadRequest);
        }
        catch (InstallationSetupConcurrencyException exception)
        {
            return Json(
                new InstallationSetupHttpErrorResponse(
                    "revisionConflict",
                    [],
                    exception.ExpectedRevision,
                    exception.ActualRevision),
                StatusCodes.Status409Conflict);
        }
        catch (UnauthorizedAccessException)
        {
            return Json(
                new InstallationSetupHttpErrorResponse("invalidSession", []),
                StatusCodes.Status401Unauthorized);
        }
        catch (InvalidOperationException)
        {
            return Json(
                new InstallationSetupHttpErrorResponse("setupUnavailable", []),
                StatusCodes.Status409Conflict);
        }
        catch (OperationCanceledException)
            when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
    }

    private static IResult Json(object value, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(
            value,
            JsonOptions,
            contentType: "application/json; charset=utf-8",
            statusCode: statusCode);

    private static InstallationSetupHttpInputException InvalidInput() => new();

    private sealed class InstallationSetupHttpInputException(
        string message = "The setup HTTP input is invalid.")
        : InvalidOperationException(message);
}
