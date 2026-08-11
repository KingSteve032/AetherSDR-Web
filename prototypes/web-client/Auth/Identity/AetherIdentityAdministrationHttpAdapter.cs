using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace AetherSDR.Web.Auth.Identity;

internal static class AetherIdentityAdministrationDefaults
{
    internal const string RateLimitPolicy = "identity-administration";
}

internal sealed record AetherIdentityAdministrationHttpAdapterReport(
    IReadOnlyList<string> EndpointPaths);

internal static class AetherIdentityAdministrationHttpAdapter
{
    internal const string AccountsPath = "/api/admin/identity/accounts";
    internal const string EnrollmentsPath =
        "/api/admin/identity/accounts/enrollments";
    internal const string LocalPasswordReauthenticationPath =
        "/api/admin/identity/reauthenticate/local/password";
    internal const string LocalMfaReauthenticationPath =
        "/api/admin/identity/reauthenticate/local/mfa";
    internal const string ExternalReauthenticationPath =
        "/api/admin/identity/reauthenticate/external";
    internal const string ExternalIdentityLinkPath =
        AccountsPath + "/{userId:guid}/external-identities/link";
    internal const string ExternalIdentityProviderPath =
        AccountsPath + "/{userId:guid}/external-identities/{providerId}";
    internal const string ExternalAccountProvisioningPath =
        AccountsPath + "/external-provisioning";
    internal const string AccountEnabledPath =
        AccountsPath + "/{userId:guid}/enabled";
    internal const string AccountSessionRevocationPath =
        AccountsPath + "/{userId:guid}/sessions/revoke";
    internal const int MaximumRequestBodyBytes = 8192;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    internal static AetherIdentityAdministrationHttpAdapterReport Map(
        WebApplication app,
        AetherAuthenticationTopology topology)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(topology);
        List<string> paths = [];
        if (topology.Mode == AetherAuthenticationMode.Development)
        {
            return new(paths.AsReadOnly());
        }

        paths.Add(AccountsPath);
        app.MapGet(AccountsPath, HandleListAsync)
            .RequireAuthorization(AetherPolicies.Admin)
            .RequireRateLimiting(
                AetherIdentityAdministrationDefaults.RateLimitPolicy);

        if (topology.LocalAccountsEnabled)
        {
            paths.Add(EnrollmentsPath);
            paths.Add(LocalPasswordReauthenticationPath);
            paths.Add(LocalMfaReauthenticationPath);
            app.MapPost(
                    LocalPasswordReauthenticationPath,
                    HandleLocalPasswordReauthenticationAsync)
                .RequireAuthorization(AetherPolicies.Admin)
                .RequireRateLimiting(
                    AetherIdentityAdministrationDefaults.RateLimitPolicy)
                .RequireAetherAntiforgery();
            app.MapPost(
                    LocalMfaReauthenticationPath,
                    HandleLocalMfaReauthenticationAsync)
                .RequireAuthorization(AetherPolicies.Admin)
                .RequireRateLimiting(
                    AetherIdentityAdministrationDefaults.RateLimitPolicy)
                .RequireAetherAntiforgery();
            app.MapPost(EnrollmentsPath, HandleBeginEnrollmentAsync)
                .RequireAuthorization(AetherPolicies.Admin)
                .RequireRateLimiting(
                    AetherIdentityAdministrationDefaults.RateLimitPolicy)
                .RequireAetherAntiforgery();
            app.MapPost(
                    AccountsPath +
                    "/{userId:guid}/enrollment-confirmation",
                    HandleConfirmEnrollmentAsync)
                .RequireAuthorization(AetherPolicies.Admin)
                .RequireRateLimiting(
                    AetherIdentityAdministrationDefaults.RateLimitPolicy)
                .RequireAetherAntiforgery();
            app.MapPost(
                    AccountsPath + "/{userId:guid}/password-reset",
                    HandlePasswordResetAsync)
                .RequireAuthorization(AetherPolicies.Admin)
                .RequireRateLimiting(
                    AetherIdentityAdministrationDefaults.RateLimitPolicy)
                .RequireAetherAntiforgery();
            paths.Add(
                AccountsPath +
                "/{userId:guid}/enrollment-confirmation");
            paths.Add(AccountsPath + "/{userId:guid}/password-reset");
        }

        paths.Add(AccountsPath + "/{userId:guid}/roles");
        paths.Add(AccountEnabledPath);
        paths.Add(AccountSessionRevocationPath);
        app.MapPut(
                AccountsPath + "/{userId:guid}/roles",
                HandleReplaceRolesAsync)
            .RequireAuthorization(AetherPolicies.Admin)
            .RequireRateLimiting(
                AetherIdentityAdministrationDefaults.RateLimitPolicy)
            .RequireAetherAntiforgery();
        app.MapPut(AccountEnabledPath, HandleSetEnabledAsync)
            .RequireAuthorization(AetherPolicies.Admin)
            .RequireRateLimiting(
                AetherIdentityAdministrationDefaults.RateLimitPolicy)
            .RequireAetherAntiforgery();
        app.MapPost(
                AccountSessionRevocationPath,
                HandleRevokeSessionsAsync)
            .RequireAuthorization(AetherPolicies.Admin)
            .RequireRateLimiting(
                AetherIdentityAdministrationDefaults.RateLimitPolicy)
            .RequireAetherAntiforgery();

        if (topology.ExternalProvider is not null)
        {
            paths.Add(ExternalReauthenticationPath);
            paths.Add(ExternalIdentityLinkPath);
            paths.Add(ExternalIdentityProviderPath);
            paths.Add(ExternalAccountProvisioningPath);
            app.MapPost(
                    ExternalReauthenticationPath,
                    HandleExternalReauthenticationAsync)
                .RequireAuthorization(AetherPolicies.Admin)
                .RequireRateLimiting(
                    AetherIdentityAdministrationDefaults.RateLimitPolicy)
                .RequireAetherAntiforgery();
            app.MapPost(
                    ExternalAccountProvisioningPath,
                    HandleProvisionExternalAccountAsync)
                .RequireAuthorization(AetherPolicies.Admin)
                .RequireRateLimiting(
                    AetherIdentityAdministrationDefaults.RateLimitPolicy)
                .RequireAetherAntiforgery();
            app.MapPost(
                    ExternalIdentityLinkPath,
                    HandleAuthorizeExternalIdentityLinkAsync)
                .RequireAuthorization(AetherPolicies.Admin)
                .RequireRateLimiting(
                    AetherIdentityAdministrationDefaults.RateLimitPolicy)
                .RequireAetherAntiforgery();
            app.MapDelete(
                    ExternalIdentityProviderPath,
                    HandleUnlinkExternalIdentityAsync)
                .RequireAuthorization(AetherPolicies.Admin)
                .RequireRateLimiting(
                    AetherIdentityAdministrationDefaults.RateLimitPolicy)
                .RequireAetherAntiforgery();
        }

        return new(paths.AsReadOnly());
    }

    private static async Task<IResult> HandleListAsync(
        HttpContext context,
        ClaimsPrincipal user,
        [FromServices] AetherLocalAccountAdministrationService accounts,
        int offset = 0,
        int limit = 50)
    {
        ApplyNoStore(context.Response);
        try
        {
            AetherIdentityAccountPage page = await accounts.ListAsync(
                user,
                offset,
                limit,
                context.RequestAborted);
            return Json(page);
        }
        catch (AetherAdministratorReauthenticationRequiredException)
        {
            return ReauthenticationRequired();
        }
        catch (ArgumentOutOfRangeException)
        {
            return InvalidRequest();
        }
    }

    private static async Task<IResult>
        HandleLocalPasswordReauthenticationAsync(
            HttpContext context,
            ClaimsPrincipal user,
            [FromServices]
            AetherLocalAdministratorReauthenticationService reauthentication)
    {
        ApplyNoStore(context.Response);
        LocalPasswordReauthenticationRequest? body =
            await ReadJsonAsync<LocalPasswordReauthenticationRequest>(
                context);
        if (body is null)
        {
            return InvalidRequest();
        }

        AetherLocalAdministratorReauthenticationChallenge challenge =
            await reauthentication.BeginAsync(
                user,
                body.Password,
                CorrelationId("admin-reauth-password"),
                context.RequestAborted);
        if (!challenge.ReadyForSecondFactor ||
            string.IsNullOrEmpty(challenge.ChallengeToken))
        {
            return ReauthenticationRejected();
        }
        return Json(
            new
            {
                code = challenge.Code,
                challengeToken = challenge.ChallengeToken
            });
    }

    private static async Task<IResult> HandleLocalMfaReauthenticationAsync(
        HttpContext context,
        ClaimsPrincipal user,
        [FromServices]
        AetherLocalAdministratorReauthenticationService reauthentication)
    {
        ApplyNoStore(context.Response);
        LocalMfaReauthenticationRequest? body =
            await ReadJsonAsync<LocalMfaReauthenticationRequest>(context);
        if (body is null)
        {
            return InvalidRequest();
        }

        AetherAdministratorReauthenticationResult result =
            await reauthentication.CompleteAsync(
                user,
                body.ChallengeToken,
                body.VerificationCode,
                CorrelationId("admin-reauth-mfa"),
                context.RequestAborted);
        if (!result.Succeeded ||
            result.Principal is null ||
            result.SessionId is null ||
            result.AbsoluteExpiresAtUtc is null)
        {
            return ReauthenticationRejected();
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
        return Json(new { code = result.Code });
    }

    private static async Task<IResult> HandleExternalReauthenticationAsync(
        HttpContext context,
        ClaimsPrincipal user)
    {
        ApplyNoStore(context.Response);
        ExternalReauthenticationRequest? body =
            await ReadJsonAsync<ExternalReauthenticationRequest>(context);
        if (body is null ||
            body.ReturnUrl is { Length: > 2048 } ||
            !AetherAuthenticationSessionService.TryReadCanonicalIdentity(
                user,
                out Guid userId,
                out _,
                out _) ||
            !user.IsInRole(AetherRoles.Admin))
        {
            return ReauthenticationRejected();
        }

        AuthenticationProperties properties = new()
        {
            RedirectUri = LocalReturnUrl.Normalize(body.ReturnUrl)
        };
        properties.Items[
            AetherOpenIdConnectEvents.FreshAuthenticationItem] =
            AetherOpenIdConnectEvents.RequiredValue;
        properties.Items[
            AetherOpenIdConnectEvents.ExpectedUserIdItem] =
            userId.ToString("D");
        properties.SetParameter(
            OpenIdConnectParameterNames.Prompt,
            "login");
        properties.SetParameter(
            OpenIdConnectParameterNames.MaxAge,
            "0");
        return Results.Challenge(
            properties,
            [OpenIdConnectDefaults.AuthenticationScheme]);
    }

    private static async Task<IResult>
        HandleAuthorizeExternalIdentityLinkAsync(
            HttpContext context,
            ClaimsPrincipal user,
            [FromServices]
            AetherExternalIdentityAdministrationService externalIdentities,
            Guid userId)
    {
        ApplyNoStore(context.Response);
        ExternalIdentityLinkRequest? body =
            await ReadJsonAsync<ExternalIdentityLinkRequest>(context);
        if (body is null || body.ReturnUrl is { Length: > 2048 })
        {
            return InvalidRequest();
        }

        try
        {
            AetherExternalIdentityLinkAuthorization authorization =
                await externalIdentities.AuthorizeLinkAsync(
                    user,
                    userId,
                    context.RequestAborted);
            AuthenticationProperties properties = new()
            {
                RedirectUri = LocalReturnUrl.Normalize(body.ReturnUrl)
            };
            properties.Items[
                AetherOpenIdConnectEvents.ExternalIdentityLinkItem] =
                AetherOpenIdConnectEvents.RequiredValue;
            properties.Items[
                AetherOpenIdConnectEvents.LinkActorUserIdItem] =
                authorization.ActorUserId.ToString("D");
            properties.Items[
                AetherOpenIdConnectEvents.LinkActorSessionIdItem] =
                authorization.ActorSessionId.ToString("D");
            properties.Items[
                AetherOpenIdConnectEvents.LinkTargetUserIdItem] =
                authorization.TargetUserId.ToString("D");
            properties.Items[
                AetherOpenIdConnectEvents.LinkProviderIdItem] =
                authorization.ProviderId;
            properties.SetParameter(
                OpenIdConnectParameterNames.Prompt,
                "login");
            properties.SetParameter(
                OpenIdConnectParameterNames.MaxAge,
                "0");
            return Results.Challenge(
                properties,
                [OpenIdConnectDefaults.AuthenticationScheme]);
        }
        catch (AetherAdministratorReauthenticationRequiredException)
        {
            return ReauthenticationRequired();
        }
        catch (InvalidOperationException)
        {
            return AdministrationRejected();
        }
    }

    private static async Task<IResult> HandleUnlinkExternalIdentityAsync(
        HttpContext context,
        ClaimsPrincipal user,
        [FromServices]
        AetherExternalIdentityAdministrationService externalIdentities,
        Guid userId,
        string providerId)
    {
        ApplyNoStore(context.Response);
        try
        {
            AetherExternalIdentityMutationResult result =
                await externalIdentities.UnlinkAsync(
                    user,
                    userId,
                    providerId,
                    CorrelationId("external-identity-unlink"),
                    context.RequestAborted);
            return result.Succeeded
                ? Json(result)
                : AdministrationRejected(result.Code);
        }
        catch (AetherAdministratorReauthenticationRequiredException)
        {
            return ReauthenticationRequired();
        }
        catch (InvalidOperationException)
        {
            return AdministrationRejected();
        }
    }

    private static async Task<IResult> HandleProvisionExternalAccountAsync(
        HttpContext context,
        ClaimsPrincipal user,
        [FromServices] AetherLocalAccountAdministrationService accounts)
    {
        ApplyNoStore(context.Response);
        ExternalAccountProvisioningRequest? body =
            await ReadJsonAsync<ExternalAccountProvisioningRequest>(context);
        if (body?.Roles is null)
        {
            return InvalidRequest();
        }

        try
        {
            AetherIdentityAccountMutationResult result =
                await accounts.ProvisionExternalAccountAsync(
                    user,
                    new(
                        body.UserName ?? string.Empty,
                        body.DisplayName ?? string.Empty,
                        body.Email,
                        body.Roles,
                        CorrelationId("external-account-provisioning")),
                    context.RequestAborted);
            return Json(result);
        }
        catch (AetherAdministratorReauthenticationRequiredException)
        {
            return ReauthenticationRequired();
        }
        catch (InvalidOperationException)
        {
            return AdministrationRejected();
        }
    }

    private static async Task<IResult> HandleSetEnabledAsync(
        HttpContext context,
        ClaimsPrincipal user,
        [FromServices] AetherLocalAccountAdministrationService accounts,
        Guid userId)
    {
        ApplyNoStore(context.Response);
        SetEnabledRequest? body =
            await ReadJsonAsync<SetEnabledRequest>(context);
        if (body?.Enabled is not bool enabled)
        {
            return InvalidRequest();
        }

        try
        {
            AetherIdentityAccountMutationResult result =
                await accounts.SetEnabledAsync(
                    user,
                    userId,
                    enabled,
                    CorrelationId("account-enabled-state"),
                    context.RequestAborted);
            return Json(result);
        }
        catch (AetherAdministratorReauthenticationRequiredException)
        {
            return ReauthenticationRequired();
        }
        catch (InvalidOperationException)
        {
            return AdministrationRejected();
        }
    }

    private static async Task<IResult> HandleRevokeSessionsAsync(
        HttpContext context,
        ClaimsPrincipal user,
        [FromServices] AetherLocalAccountAdministrationService accounts,
        Guid userId)
    {
        ApplyNoStore(context.Response);
        RevokeSessionsRequest? body =
            await ReadJsonAsync<RevokeSessionsRequest>(context);
        if (body is null)
        {
            return InvalidRequest();
        }

        try
        {
            AetherIdentityAccountMutationResult result =
                await accounts.RevokeSessionsAsync(
                    user,
                    userId,
                    CorrelationId("account-session-revocation"),
                    context.RequestAborted);
            return Json(result);
        }
        catch (AetherAdministratorReauthenticationRequiredException)
        {
            return ReauthenticationRequired();
        }
        catch (InvalidOperationException)
        {
            return AdministrationRejected();
        }
    }

    private static async Task<IResult> HandleBeginEnrollmentAsync(
        HttpContext context,
        ClaimsPrincipal user,
        [FromServices] AetherLocalAccountAdministrationService accounts)
    {
        ApplyNoStore(context.Response);
        BeginEnrollmentRequest? body =
            await ReadJsonAsync<BeginEnrollmentRequest>(context);
        if (body?.Roles is null)
        {
            return InvalidRequest();
        }

        try
        {
            AetherLocalAccountEnrollmentIssue issue =
                await accounts.BeginEnrollmentAsync(
                    user,
                    new(
                        body.UserName ?? string.Empty,
                        body.DisplayName ?? string.Empty,
                        body.Email,
                        body.Password ?? string.Empty,
                        body.Roles,
                        CorrelationId("account-enrollment")),
                    context.RequestAborted);
            return Json(
                new
                {
                    code = "local-account-enrollment-begun",
                    userId = issue.UserId,
                    enrollmentId = issue.EnrollmentId,
                    sharedSecretBase32 = issue.SharedSecretBase32,
                    recoveryCodes = issue.RecoveryCodes
                });
        }
        catch (AetherAdministratorReauthenticationRequiredException)
        {
            return ReauthenticationRequired();
        }
        catch (InvalidOperationException)
        {
            return AdministrationRejected();
        }
    }

    private static async Task<IResult> HandleConfirmEnrollmentAsync(
        HttpContext context,
        ClaimsPrincipal user,
        [FromServices] AetherLocalAccountAdministrationService accounts,
        Guid userId)
    {
        ApplyNoStore(context.Response);
        ConfirmEnrollmentRequest? body =
            await ReadJsonAsync<ConfirmEnrollmentRequest>(context);
        if (body is null || body.EnrollmentId == Guid.Empty)
        {
            return InvalidRequest();
        }

        try
        {
            AetherIdentityAccountMutationResult result =
                await accounts.ConfirmEnrollmentAsync(
                    user,
                    userId,
                    body.EnrollmentId,
                    body.TotpCode,
                    CorrelationId("account-enrollment-confirmation"),
                    context.RequestAborted);
            return result.Succeeded
                ? Json(result)
                : AdministrationRejected();
        }
        catch (AetherAdministratorReauthenticationRequiredException)
        {
            return ReauthenticationRequired();
        }
        catch (InvalidOperationException)
        {
            return AdministrationRejected();
        }
    }

    private static async Task<IResult> HandlePasswordResetAsync(
        HttpContext context,
        ClaimsPrincipal user,
        [FromServices] AetherLocalAccountAdministrationService accounts,
        Guid userId)
    {
        ApplyNoStore(context.Response);
        PasswordResetRequest? body =
            await ReadJsonAsync<PasswordResetRequest>(context);
        if (body is null)
        {
            return InvalidRequest();
        }

        try
        {
            AetherIdentityAccountMutationResult result =
                await accounts.ResetPasswordAsync(
                    user,
                    new(
                        userId,
                        body.Password ?? string.Empty,
                        CorrelationId("account-password-reset")),
                    context.RequestAborted);
            return Json(result);
        }
        catch (AetherAdministratorReauthenticationRequiredException)
        {
            return ReauthenticationRequired();
        }
        catch (InvalidOperationException)
        {
            return AdministrationRejected();
        }
    }

    private static async Task<IResult> HandleReplaceRolesAsync(
        HttpContext context,
        ClaimsPrincipal user,
        [FromServices] AetherLocalAccountAdministrationService accounts,
        Guid userId)
    {
        ApplyNoStore(context.Response);
        ReplaceRolesRequest? body =
            await ReadJsonAsync<ReplaceRolesRequest>(context);
        if (body?.Roles is null)
        {
            return InvalidRequest();
        }

        try
        {
            AetherIdentityAccountMutationResult result =
                await accounts.ReplaceRolesAsync(
                    user,
                    userId,
                    body.Roles,
                    CorrelationId("account-role-replacement"),
                    context.RequestAborted);
            return Json(result);
        }
        catch (AetherAdministratorReauthenticationRequiredException)
        {
            return ReauthenticationRequired();
        }
        catch (InvalidOperationException)
        {
            return AdministrationRejected();
        }
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

    private static void RejectDuplicateProperties(
        ReadOnlyMemory<byte> payload)
    {
        using JsonDocument document = JsonDocument.Parse(
            payload,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = JsonOptions.MaxDepth
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
                    throw new JsonException(
                        "Duplicate JSON properties are not permitted.");
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

    private static string CorrelationId(string operation)
    {
        byte[] random = RandomNumberGenerator.GetBytes(12);
        try
        {
            return $"{operation}-{Convert.ToHexStringLower(random)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(random);
        }
    }

    private static IResult Json(object value) =>
        Results.Json(
            value,
            statusCode: StatusCodes.Status200OK,
            contentType: "application/json; charset=utf-8");

    private static IResult InvalidRequest() =>
        Results.Json(
            new { code = "identity-administration-request-invalid" },
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult ReauthenticationRejected() =>
        Results.Json(
            new { code = "administrator-reauthentication-rejected" },
            statusCode: StatusCodes.Status401Unauthorized);

    private static IResult ReauthenticationRequired() =>
        Results.Json(
            new { code = "administrator-reauthentication-required" },
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult AdministrationRejected() =>
        AdministrationRejected("identity-administration-rejected");

    private static IResult AdministrationRejected(string code) =>
        Results.Json(
            new { code },
            statusCode: StatusCodes.Status409Conflict);

    private static void ApplyNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, max-age=0";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }

    private sealed class LocalPasswordReauthenticationRequest
    {
        public string? Password { get; init; }
    }

    private sealed class LocalMfaReauthenticationRequest
    {
        public string? ChallengeToken { get; init; }

        public string? VerificationCode { get; init; }
    }

    private sealed class ExternalReauthenticationRequest
    {
        public string? ReturnUrl { get; init; }
    }

    private sealed class ExternalIdentityLinkRequest
    {
        public string? ReturnUrl { get; init; }
    }

    private sealed class ExternalAccountProvisioningRequest
    {
        public string? UserName { get; init; }

        public string? DisplayName { get; init; }

        public string? Email { get; init; }

        public string[]? Roles { get; init; }
    }

    private sealed class SetEnabledRequest
    {
        public bool? Enabled { get; init; }
    }

    private sealed class RevokeSessionsRequest
    {
    }

    private sealed class BeginEnrollmentRequest
    {
        public string? UserName { get; init; }

        public string? DisplayName { get; init; }

        public string? Email { get; init; }

        public string? Password { get; init; }

        public string[]? Roles { get; init; }
    }

    private sealed class ConfirmEnrollmentRequest
    {
        public Guid EnrollmentId { get; init; }

        public string? TotpCode { get; init; }
    }

    private sealed class PasswordResetRequest
    {
        public string? Password { get; init; }
    }

    private sealed class ReplaceRolesRequest
    {
        public string[]? Roles { get; init; }
    }
}
