using System.Runtime.InteropServices;
using System.Security.Claims;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Radio;
using AetherSDR.Web.Setup;
using Microsoft.AspNetCore.Antiforgery;

namespace AetherSDR.Web.Releases;

public sealed record PrepareReleaseUpdateHttpRequest(
    string ReleaseIdentity,
    string InstalledReleaseIdentity,
    string InstalledVersion,
    int ConfigurationSchemaVersion,
    int ProtocolVersion);

/// <summary>
/// Authenticated Admin-only HTTP surface for one exact release transaction. The
/// browser selects only a canonical release identity; the server derives the
/// immutable downloaded-bundle path and all approval evidence. Every mutation
/// validates an antiforgery token. No route accepts a shell command, service
/// name, station command, filesystem path, radio identifier, or TX authority.
/// </summary>
public static class ReleaseUpdateHttpAdapter
{
    public const string AntiforgeryHeaderName = AetherAntiforgery.HeaderName;

    public static RouteGroupBuilder Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        RouteGroupBuilder group = app.MapGroup("/api/admin/releases")
            .RequireAuthorization(AetherPolicies.Admin);

        group.MapGet(
            "/antiforgery",
            (HttpContext context, IAntiforgery antiforgery) =>
            {
                AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
                return Results.Ok(new
                {
                    headerName = AntiforgeryHeaderName,
                    requestToken = tokens.RequestToken ?? string.Empty
                });
            });

        group.MapGet(
            "/transaction",
            async (
                ReleaseUpdateSupervisorClient client,
                CancellationToken cancellationToken) =>
                Results.Ok(await client.StatusAsync(cancellationToken)));

        group.MapPost(
            "/prepare",
            async (
                PrepareReleaseUpdateHttpRequest request,
                HttpContext context,
                IAntiforgery antiforgery,
                InstallationPaths paths,
                ClaimsPrincipal user,
                ReleaseUpdateSupervisorClient client,
                AdministrativeAuditStore audit,
                CancellationToken cancellationToken) =>
            {
                IResult? csrfFailure = await ValidateAntiforgeryAsync(
                    context,
                    antiforgery);
                if (csrfFailure is not null)
                {
                    return csrfFailure;
                }
                if (!OperatingSystem.IsLinux())
                {
                    return Results.Json(
                        new { error = "Release update execution requires Linux." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                (string actorId, string actorName) = GetActor(user);
                string target = request.ReleaseIdentity ?? string.Empty;
                try
                {
                    string bundleDirectory = DeriveBundleDirectory(
                        paths,
                        request.ReleaseIdentity ?? string.Empty);
                    ReleaseUpdateTransactionReport report =
                        await client.PrepareOfflineAsync(
                            new ReleaseUpdateInstallRequest(
                                bundleDirectory,
                                request.InstalledReleaseIdentity,
                                request.InstalledVersion,
                                request.ConfigurationSchemaVersion,
                                request.ProtocolVersion),
                            cancellationToken);
                    Record(
                        audit,
                        actorId,
                        actorName,
                        AdministrativeAuditActions.PrepareReleaseUpdate,
                        target,
                        report);
                    return Result(report);
                }
                catch (Exception exception)
                    when (exception is InvalidOperationException or
                        ArgumentException or NotSupportedException or
                        PathTooLongException)
                {
                    audit.Record(
                        actorId,
                        actorName,
                        AdministrativeAuditActions.PrepareReleaseUpdate,
                        "release-update",
                        target,
                        AdministrativeAuditResults.Failed,
                        exception.Message);
                    return Results.BadRequest(new { error = exception.Message });
                }
            });

        group.MapPost(
            "/{transactionId}/activate",
            async (
                string transactionId,
                HttpContext context,
                IAntiforgery antiforgery,
                ClaimsPrincipal user,
                ReleaseUpdateOperatorAuthenticationEvidenceFactory authentication,
                ReleaseUpdateSupervisorClient client,
                AdministrativeAuditStore audit,
                CancellationToken cancellationToken) =>
            {
                IResult? csrfFailure = await ValidateAntiforgeryAsync(
                    context,
                    antiforgery);
                if (csrfFailure is not null)
                {
                    return csrfFailure;
                }
                if (!OperatingSystem.IsLinux())
                {
                    return Results.Json(
                        new { error = "Release update execution requires Linux." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                if (!TryCanonicalTransactionId(
                        transactionId,
                        out string canonicalTransactionId))
                {
                    return Results.BadRequest(
                        new { error = "The release transaction identity is invalid." });
                }
                (string actorId, string actorName) = GetActor(user);
                ReleaseUpdateOperatorAuthenticationReport evidence =
                    authentication.Create(user);
                if (!evidence.Succeeded || evidence.Evidence is null)
                {
                    audit.Record(
                        actorId,
                        actorName,
                        AdministrativeAuditActions.ActivateReleaseUpdate,
                        "release-update",
                        transactionId,
                        AdministrativeAuditResults.Failed,
                        evidence.Message);
                    return Results.Json(
                        new { error = evidence.Message },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                ReleaseUpdateTransactionReport report =
                    await client.ApproveAndActivateAsync(
                        canonicalTransactionId,
                        evidence.Evidence,
                        cancellationToken);
                Record(
                    audit,
                    actorId,
                    actorName,
                    AdministrativeAuditActions.ActivateReleaseUpdate,
                    transactionId,
                    report);
                return Result(report);
            });

        group.MapPost(
            "/{transactionId}/rollback",
            async (
                string transactionId,
                HttpContext context,
                IAntiforgery antiforgery,
                ClaimsPrincipal user,
                ReleaseUpdateOperatorAuthenticationEvidenceFactory authentication,
                ReleaseUpdateSupervisorClient client,
                AdministrativeAuditStore audit,
                CancellationToken cancellationToken) =>
            {
                IResult? csrfFailure = await ValidateAntiforgeryAsync(
                    context,
                    antiforgery);
                if (csrfFailure is not null)
                {
                    return csrfFailure;
                }
                if (!OperatingSystem.IsLinux())
                {
                    return Results.Json(
                        new { error = "Release update execution requires Linux." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
                if (!TryCanonicalTransactionId(
                        transactionId,
                        out string canonicalTransactionId))
                {
                    return Results.BadRequest(
                        new { error = "The release transaction identity is invalid." });
                }
                (string actorId, string actorName) = GetActor(user);
                ReleaseUpdateOperatorAuthenticationReport evidence =
                    authentication.Create(user);
                if (!evidence.Succeeded || evidence.Evidence is null)
                {
                    audit.Record(
                        actorId,
                        actorName,
                        AdministrativeAuditActions.RollbackReleaseUpdate,
                        "release-update",
                        transactionId,
                        AdministrativeAuditResults.Failed,
                        evidence.Message);
                    return Results.Json(
                        new { error = evidence.Message },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                ReleaseUpdateTransactionReport report =
                    await client.ApproveAndRollbackAsync(
                        canonicalTransactionId,
                        evidence.Evidence,
                        cancellationToken);
                Record(
                    audit,
                    actorId,
                    actorName,
                    AdministrativeAuditActions.RollbackReleaseUpdate,
                    transactionId,
                    report);
                return Result(report);
            });

        return group;
    }

    private static async Task<IResult?> ValidateAntiforgeryAsync(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return null;
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest(
                new { error = "The release update request failed antiforgery validation." });
        }
    }

    private static string DeriveBundleDirectory(
        InstallationPaths paths,
        string releaseIdentity)
    {
        ArgumentNullException.ThrowIfNull(paths);
        InstallationPaths.Validate(paths);
        string canonicalIdentity = InstallationReleaseIdentity.Parse(
            releaseIdentity ?? string.Empty);
        if (!string.Equals(
                canonicalIdentity,
                releaseIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected release identity must be canonical.");
        }
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "linux-x64",
            Architecture.Arm64 => "linux-arm64",
            _ => throw new InvalidOperationException(
                "The current runtime architecture is unsupported for release installation.")
        };
        string root = Path.GetFullPath(paths.ReleaseDownloadDirectory);
        string target = Path.GetFullPath(
            Path.Combine(root, $"{canonicalIdentity}-{architecture}"));
        if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The selected release escaped the verified download inventory.");
        }
        return target;
    }

    private static bool TryCanonicalTransactionId(
        string value,
        out string canonical)
    {
        canonical = value ?? string.Empty;
        return canonical.Length == 32 &&
            canonical.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static IResult Result(ReleaseUpdateTransactionReport report)
    {
        if (report.Succeeded)
        {
            return Results.Ok(report);
        }
        int status = report.FailureCode switch
        {
            ReleaseUpdateTransactionFailureCode.ExecutionDisabled =>
                StatusCodes.Status503ServiceUnavailable,
            ReleaseUpdateTransactionFailureCode.InvalidRequest =>
                StatusCodes.Status400BadRequest,
            ReleaseUpdateTransactionFailureCode.TransactionNotFound =>
                StatusCodes.Status404NotFound,
            ReleaseUpdateTransactionFailureCode.TransactionAlreadyActive or
            ReleaseUpdateTransactionFailureCode.TransactionPhaseInvalid or
            ReleaseUpdateTransactionFailureCode.ApprovalFailed or
            ReleaseUpdateTransactionFailureCode.LeaseDrainTimedOut =>
                StatusCodes.Status409Conflict,
            ReleaseUpdateTransactionFailureCode.ReconciliationRequired =>
                StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status422UnprocessableEntity
        };
        return Results.Json(report, statusCode: status);
    }

    private static void Record(
        AdministrativeAuditStore audit,
        string actorId,
        string actorName,
        string action,
        string target,
        ReleaseUpdateTransactionReport report) =>
        audit.Record(
            actorId,
            actorName,
            action,
            "release-update",
            target,
            report.Succeeded
                ? AdministrativeAuditResults.Succeeded
                : AdministrativeAuditResults.Failed,
            $"{report.Phase}: {report.Message}");

    private static (string Id, string Name) GetActor(ClaimsPrincipal user)
    {
        string id =
            user.FindFirstValue("oid") ??
            user.FindFirstValue("sub") ??
            user.FindFirstValue(ClaimTypes.NameIdentifier) ??
            "unknown-administrator";
        string name =
            user.FindFirstValue("name") ??
            user.Identity?.Name ??
            id;
        return (id, name);
    }
}
