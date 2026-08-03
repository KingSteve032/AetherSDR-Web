using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace AetherSDR.Web.Setup;

public sealed record InstallationSetupBrowserShellReport(
    IReadOnlyList<string> EndpointPaths);

public static class InstallationSetupBrowserShell
{
    public const string PagePath = "/setup/center";
    public const string StylePath = "/setup/assets/setup.css";
    public const string ScriptPath = "/setup/assets/setup.js";

    private static readonly string[] EndpointPaths =
    [
        PagePath,
        StylePath,
        ScriptPath
    ];

    public static InstallationSetupBrowserShellReport Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        IWebHostEnvironment environment =
            app.Services.GetRequiredService<IWebHostEnvironment>();
        InstallationSetupHttpSecurityContract contract =
            app.Services
                .GetRequiredService<InstallationSetupHttpSecurityPolicy>()
                .Contract;
        InstallationPaths paths =
            app.Services.GetRequiredService<InstallationPaths>();
        string pageTemplate = ReadAsset(environment, "setup.html");
        string stylesheet = ReadAsset(environment, "setup.css");
        string script = ReadAsset(environment, "setup.js");
        string pageRateLimit = contract.RateLimits[0].PolicyName;

        app.MapGet(
                PagePath,
                context => HandlePageAsync(
                    context,
                    pageTemplate,
                    paths,
                    contract))
            .RequireRateLimiting(pageRateLimit);
        app.MapGet(
                StylePath,
                context => HandleAssetAsync(
                    context,
                    stylesheet,
                    "text/css; charset=utf-8",
                    contract))
            .RequireRateLimiting(pageRateLimit);
        app.MapGet(
                ScriptPath,
                context => HandleAssetAsync(
                    context,
                    script,
                    "text/javascript; charset=utf-8",
                    contract))
            .RequireRateLimiting(pageRateLimit);

        return new InstallationSetupBrowserShellReport(EndpointPaths);
    }

    private static async Task HandlePageAsync(
        HttpContext context,
        string pageTemplate,
        InstallationPaths paths,
        InstallationSetupHttpSecurityContract contract)
    {
        InstallationSetupOnlyHttpAdapter.ApplyResponseHeaders(
            context.Response,
            contract.ResponseHeaders);
        try
        {
            InstallationSetupHttpRequest request =
                InstallationSetupOnlyHttpAdapter.CreateRequest(
                    context.Request,
                    InstallationSetupHttpOperation.PageRead);
            InstallationSetupCenterPageResult page =
                await context.RequestServices
                    .GetRequiredService<InstallationSetupCenterApplication>()
                    .ReadPageAsync(request, context.RequestAborted);
            TimeProvider time =
                context.RequestServices.GetRequiredService<TimeProvider>();
            InstallationSetupOnlyHttpAdapter.AppendCsrfCookie(
                context.Response,
                page.Csrf.Token,
                time.GetUtcNow() + contract.CsrfCookie.MaximumAge,
                contract.CsrfCookie,
                time);
            string html = RenderPage(
                pageTemplate,
                page.Status,
                paths,
                contract.CanonicalOrigin);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync(html, context.RequestAborted);
        }
        catch (InstallationSetupCenterSecurityException)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                "The setup page request was rejected.",
                context.RequestAborted);
        }
        catch (InvalidOperationException)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                "The setup page is unavailable.",
                context.RequestAborted);
        }
    }

    private static async Task HandleAssetAsync(
        HttpContext context,
        string content,
        string contentType,
        InstallationSetupHttpSecurityContract contract)
    {
        InstallationSetupOnlyHttpAdapter.ApplyResponseHeaders(
            context.Response,
            contract.ResponseHeaders);
        if (!AssetRequestAllowed(context.Request, contract.CanonicalOrigin))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                "The setup asset request was rejected.",
                context.RequestAborted);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        await context.Response.WriteAsync(content, context.RequestAborted);
    }

    private static bool AssetRequestAllowed(
        HttpRequest request,
        string canonicalOrigin)
    {
        CanonicalPublicUrl canonical = CanonicalPublicUrl.Parse(canonicalOrigin);
        if (!HttpMethods.IsGet(request.Method) ||
            !string.Equals(
                request.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                request.Host.Value,
                canonical.Uri.Authority,
                StringComparison.OrdinalIgnoreCase) ||
            request.QueryString.HasValue)
        {
            return false;
        }

        string? site = SingleHeader(request.Headers["Sec-Fetch-Site"]);
        string? mode = SingleHeader(request.Headers["Sec-Fetch-Mode"]);
        if (!string.Equals(site, "same-origin", StringComparison.OrdinalIgnoreCase) ||
            !(string.Equals(mode, "no-cors", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(mode, "cors", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string? origin = SingleHeader(request.Headers.Origin);
        return origin is null ||
            string.Equals(
                origin,
                canonical.Value,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string RenderPage(
        string template,
        InstallationSetupStatusReport status,
        InstallationPaths paths,
        string canonicalAccessUrl)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["SCHEMA_VERSION"] = status.SchemaVersion.ToString(
                CultureInfo.InvariantCulture),
            ["REVISION"] = status.Revision.ToString(
                CultureInfo.InvariantCulture),
            ["LOCK_MODE"] = EnumValue(status.LockMode),
            ["LAST_STEP"] = EnumValue(status.LastCompletedStep),
            ["SETUP_COMPLETE"] = Bool(status.SetupComplete),
            ["BOOTSTRAP_TOKEN_PRESENT"] = Bool(status.BootstrapTokenPresent),
            ["BOOTSTRAP_TOKEN_EXPIRES_AT"] =
                status.BootstrapTokenExpiresAt?.ToString("O") ?? string.Empty,
            ["TOPOLOGY"] = status.Topology is null
                ? string.Empty
                : EnumValue(status.Topology.Value),
            ["CANONICAL_URL_CONFIGURED"] =
                Bool(status.CanonicalPublicUrlConfigured),
            ["PATHS_CONFIGURED"] = Bool(status.InstallationPathsConfigured),
            ["UPDATE_CHANNEL"] = EnumValue(status.UpdateChannel),
            ["INSTALL_TRANSMIT_SUPPORT"] =
                Bool(status.InstallTransmitSupport),
            ["CANONICAL_ACCESS_URL"] = canonicalAccessUrl,
            ["CONFIGURATION_DIRECTORY"] = paths.ConfigurationDirectory,
            ["STATE_DIRECTORY"] = paths.StateDirectory,
            ["SECRET_DIRECTORY"] = paths.SecretDirectory,
            ["RELEASE_DIRECTORY"] = paths.ReleaseDirectory,
            ["BACKUP_DIRECTORY"] = paths.BackupDirectory,
            ["LOG_DIRECTORY"] = paths.LogDirectory
        };

        string rendered = template;
        foreach ((string key, string value) in values)
        {
            rendered = rendered.Replace(
                $"{{{{{key}}}}}",
                HtmlEncoder.Default.Encode(value),
                StringComparison.Ordinal);
        }
        if (rendered.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The setup browser template contains an unresolved placeholder.");
        }
        return rendered;
    }

    private static string ReadAsset(
        IWebHostEnvironment environment,
        string name)
    {
        string root = environment.WebRootPath ??
            throw new InvalidOperationException(
                "The setup browser shell requires a web root.");
        string path = Path.Combine(root, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Required setup browser asset '{name}' was not found.",
                path);
        }
        return File.ReadAllText(path);
    }

    private static string EnumValue<T>(T value)
        where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static string Bool(bool value) => value ? "true" : "false";

    private static string? SingleHeader(StringValues values) =>
        values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => "\0"
        };
}
