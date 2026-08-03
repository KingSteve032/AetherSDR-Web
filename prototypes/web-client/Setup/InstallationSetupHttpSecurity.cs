using System.Security.Cryptography;
using System.Text;

namespace AetherSDR.Web.Setup;

public enum InstallationSetupHttpOperation
{
    PageRead = 1,
    BootstrapClaim = 2,
    SessionRead = 3,
    SessionMutation = 4
}

public enum InstallationSetupHttpRejectionCode
{
    UnsupportedOperation = 1,
    MethodMismatch = 2,
    HttpsRequired = 3,
    CanonicalHostMismatch = 4,
    CanonicalOriginRequired = 5,
    FetchMetadataRejected = 6,
    QueryStringForbidden = 7,
    RequestBodyForbidden = 8,
    RequestBodyLengthRequired = 9,
    RequestBodyTooLarge = 10,
    JsonContentTypeRequired = 11,
    SessionCookieRequired = 12,
    CsrfTokenRequired = 13,
    CsrfTokenMalformed = 14,
    CsrfTokenMismatch = 15
}

public sealed record InstallationSetupHttpSecuritySettings
{
    public const string SectionName = "InstallationSetupHttpSecurity";

    public int BootstrapClaimMaximumBodyBytes { get; init; } = 4096;
    public int SessionMutationMaximumBodyBytes { get; init; } = 16384;
    public int RateLimitWindowSeconds { get; init; } = 60;
    public int PageReadPermitLimit { get; init; } = 30;
    public int BootstrapClaimPermitLimit { get; init; } = 5;
    public int SessionReadPermitLimit { get; init; } = 60;
    public int SessionMutationPermitLimit { get; init; } = 30;
}

public sealed record InstallationSetupHttpCookieContract(
    string Name,
    bool Secure,
    bool HttpOnly,
    string SameSite,
    string Path,
    bool DomainAllowed,
    TimeSpan MaximumAge);

public sealed record InstallationSetupHttpRateLimitContract(
    string PolicyName,
    int PermitLimit,
    TimeSpan Window,
    int QueueLimit,
    bool AutoReplenishment);

public sealed record InstallationSetupHttpResponseSecurityContract(
    string ContentSecurityPolicy,
    string CacheControl,
    string ReferrerPolicy,
    string PermissionsPolicy,
    string CrossOriginOpenerPolicy,
    string CrossOriginResourcePolicy,
    string XContentTypeOptions);

public sealed record InstallationSetupHttpSecurityContract(
    string CanonicalOrigin,
    InstallationSetupHttpCookieContract SessionCookie,
    InstallationSetupHttpCookieContract CsrfCookie,
    InstallationSetupHttpResponseSecurityContract ResponseHeaders,
    IReadOnlyList<InstallationSetupHttpRateLimitContract> RateLimits);

public sealed class InstallationSetupHttpRequest
{
    public InstallationSetupHttpRequest(
        InstallationSetupHttpOperation operation,
        string method,
        string scheme,
        string host,
        string? origin,
        string? secFetchSite,
        string? secFetchMode,
        string? contentType,
        long? contentLength,
        bool hasQueryString,
        bool sessionCookiePresent,
        string? csrfCookie,
        string? csrfHeader)
    {
        Operation = operation;
        Method = method ?? string.Empty;
        Scheme = scheme ?? string.Empty;
        Host = host ?? string.Empty;
        Origin = origin;
        SecFetchSite = secFetchSite;
        SecFetchMode = secFetchMode;
        ContentType = contentType;
        ContentLength = contentLength;
        HasQueryString = hasQueryString;
        SessionCookiePresent = sessionCookiePresent;
        CsrfCookie = csrfCookie;
        CsrfHeader = csrfHeader;
    }

    public InstallationSetupHttpOperation Operation { get; }

    public string Method { get; }

    public string Scheme { get; }

    public string Host { get; }

    public string? Origin { get; }

    public string? SecFetchSite { get; }

    public string? SecFetchMode { get; }

    public string? ContentType { get; }

    public long? ContentLength { get; }

    public bool HasQueryString { get; }

    public bool SessionCookiePresent { get; }

    public string? CsrfCookie { get; }

    public string? CsrfHeader { get; }

    public override string ToString() =>
        $"{nameof(InstallationSetupHttpRequest)} " +
        $"{{ Operation = {Operation}, Method = {Method}, Scheme = {Scheme}, " +
        $"Host = {Host}, Origin = {Origin ?? "[missing]"}, " +
        $"SessionCookiePresent = {SessionCookiePresent}, " +
        "CsrfCookie = [redacted], CsrfHeader = [redacted] }}";
}

public sealed record InstallationSetupHttpSecurityDecision(
    bool Allowed,
    InstallationSetupHttpOperation Operation,
    InstallationSetupHttpRateLimitContract RateLimit,
    int MaximumRequestBodyBytes,
    IReadOnlyList<InstallationSetupHttpRejectionCode> Rejections);

public sealed class InstallationSetupHttpCsrfIssue
{
    internal InstallationSetupHttpCsrfIssue(string token)
    {
        Token = token;
    }

    public string Token { get; }

    public override string ToString() =>
        $"{nameof(InstallationSetupHttpCsrfIssue)} {{ Token = [redacted] }}";
}

public sealed class InstallationSetupHttpSecurityPolicy
{
    public const string SessionCookieName = "__Host-AetherSdrSetup";
    public const string CsrfCookieName = "__Host-AetherSdrSetupCsrf";
    public const string CsrfHeaderName = "X-Aether-Setup-Csrf";

    private const int CsrfTokenLength = 43;
    private const int MaximumConfiguredBodyBytes = 1024 * 1024;
    private const int MaximumConfiguredPermits = 1000;
    private const int MaximumConfiguredWindowSeconds = 3600;

    private readonly CanonicalPublicUrl m_publicUrl;
    private readonly InstallationSetupHttpSecuritySettings m_settings;
    private readonly IReadOnlyList<InstallationSetupHttpRateLimitContract> m_rateLimits;

    public InstallationSetupHttpSecurityPolicy(
        string canonicalPublicUrl,
        InstallationSetupHttpSecuritySettings? settings = null)
    {
        m_publicUrl = CanonicalPublicUrl.Parse(canonicalPublicUrl);
        m_settings = settings ?? new InstallationSetupHttpSecuritySettings();
        ValidateSettings(m_settings);
        m_rateLimits = CreateRateLimits(m_settings);
        Contract = new InstallationSetupHttpSecurityContract(
            m_publicUrl.Value,
            new InstallationSetupHttpCookieContract(
                SessionCookieName,
                Secure: true,
                HttpOnly: true,
                SameSite: "Strict",
                Path: "/",
                DomainAllowed: false,
                InstallationSetupClaimSessionService.MaximumLifetime),
            new InstallationSetupHttpCookieContract(
                CsrfCookieName,
                Secure: true,
                HttpOnly: false,
                SameSite: "Strict",
                Path: "/",
                DomainAllowed: false,
                InstallationSetupClaimSessionService.MaximumLifetime),
            new InstallationSetupHttpResponseSecurityContract(
                "default-src 'none'; script-src 'self'; style-src 'self'; " +
                "connect-src 'self'; img-src 'self'; base-uri 'none'; " +
                "form-action 'self'; frame-ancestors 'none'; object-src 'none'",
                "no-store, max-age=0",
                "no-referrer",
                "camera=(), geolocation=(), microphone=(), payment=(), usb=()",
                "same-origin",
                "same-origin",
                "nosniff"),
            m_rateLimits);
    }

    public InstallationSetupHttpSecurityContract Contract { get; }

    public static InstallationSetupHttpCsrfIssue IssueCsrfToken()
    {
        byte[] entropy = RandomNumberGenerator.GetBytes(32);
        try
        {
            return new InstallationSetupHttpCsrfIssue(Base64UrlEncode(entropy));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public InstallationSetupHttpSecurityDecision Evaluate(
        InstallationSetupHttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<InstallationSetupHttpRejectionCode> rejections = [];
        InstallationSetupHttpRateLimitContract rateLimit =
            SelectRateLimit(request.Operation, rejections);
        int maximumBodyBytes = MaximumBodyBytes(request.Operation);

        ValidateMethod(request, rejections);
        ValidateAuthority(request, rejections);
        ValidateOriginAndFetchMetadata(request, rejections);
        if (request.HasQueryString)
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.QueryStringForbidden);
        }
        ValidateBody(request, maximumBodyBytes, rejections);
        ValidateSessionAndCsrf(request, rejections);

        return new InstallationSetupHttpSecurityDecision(
            rejections.Count == 0,
            request.Operation,
            rateLimit,
            maximumBodyBytes,
            rejections.AsReadOnly());
    }

    private static void ValidateSettings(
        InstallationSetupHttpSecuritySettings settings)
    {
        if (settings.BootstrapClaimMaximumBodyBytes is < 1 or >
                MaximumConfiguredBodyBytes ||
            settings.SessionMutationMaximumBodyBytes is < 1 or >
                MaximumConfiguredBodyBytes ||
            settings.RateLimitWindowSeconds is < 1 or >
                MaximumConfiguredWindowSeconds ||
            settings.PageReadPermitLimit is < 1 or >
                MaximumConfiguredPermits ||
            settings.BootstrapClaimPermitLimit is < 1 or >
                MaximumConfiguredPermits ||
            settings.SessionReadPermitLimit is < 1 or >
                MaximumConfiguredPermits ||
            settings.SessionMutationPermitLimit is < 1 or >
                MaximumConfiguredPermits)
        {
            throw new InvalidOperationException(
                "Installation setup HTTP security settings contain an unsupported " +
                "body or rate-limit value.");
        }
    }

    private static IReadOnlyList<InstallationSetupHttpRateLimitContract>
        CreateRateLimits(InstallationSetupHttpSecuritySettings settings)
    {
        TimeSpan window = TimeSpan.FromSeconds(settings.RateLimitWindowSeconds);
        return Array.AsReadOnly(
        [
            CreateRateLimit(
                "installation-setup-page",
                settings.PageReadPermitLimit,
                window),
            CreateRateLimit(
                "installation-setup-claim",
                settings.BootstrapClaimPermitLimit,
                window),
            CreateRateLimit(
                "installation-setup-session-read",
                settings.SessionReadPermitLimit,
                window),
            CreateRateLimit(
                "installation-setup-session-mutation",
                settings.SessionMutationPermitLimit,
                window)
        ]);
    }

    private static InstallationSetupHttpRateLimitContract CreateRateLimit(
        string name,
        int permitLimit,
        TimeSpan window) =>
        new(
            name,
            permitLimit,
            window,
            QueueLimit: 0,
            AutoReplenishment: true);

    private InstallationSetupHttpRateLimitContract SelectRateLimit(
        InstallationSetupHttpOperation operation,
        List<InstallationSetupHttpRejectionCode> rejections)
    {
        int index = operation switch
        {
            InstallationSetupHttpOperation.PageRead => 0,
            InstallationSetupHttpOperation.BootstrapClaim => 1,
            InstallationSetupHttpOperation.SessionRead => 2,
            InstallationSetupHttpOperation.SessionMutation => 3,
            _ => -1
        };
        if (index < 0)
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.UnsupportedOperation);
            return m_rateLimits[0];
        }
        return m_rateLimits[index];
    }

    private int MaximumBodyBytes(InstallationSetupHttpOperation operation) =>
        operation switch
        {
            InstallationSetupHttpOperation.BootstrapClaim =>
                m_settings.BootstrapClaimMaximumBodyBytes,
            InstallationSetupHttpOperation.SessionMutation =>
                m_settings.SessionMutationMaximumBodyBytes,
            _ => 0
        };

    private static void ValidateMethod(
        InstallationSetupHttpRequest request,
        List<InstallationSetupHttpRejectionCode> rejections)
    {
        string expected = request.Operation switch
        {
            InstallationSetupHttpOperation.PageRead => "GET",
            InstallationSetupHttpOperation.BootstrapClaim => "POST",
            InstallationSetupHttpOperation.SessionRead => "GET",
            InstallationSetupHttpOperation.SessionMutation => "POST",
            _ => string.Empty
        };
        if (expected.Length == 0 ||
            !string.Equals(request.Method, expected, StringComparison.Ordinal))
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.MethodMismatch);
        }
    }

    private void ValidateAuthority(
        InstallationSetupHttpRequest request,
        List<InstallationSetupHttpRejectionCode> rejections)
    {
        if (!string.Equals(
                request.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.HttpsRequired);
        }

        if (!TryNormalizeAuthority(request.Host, out string? authority) ||
            !string.Equals(
                authority,
                m_publicUrl.Uri.Authority,
                StringComparison.OrdinalIgnoreCase))
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.CanonicalHostMismatch);
        }
    }

    private void ValidateOriginAndFetchMetadata(
        InstallationSetupHttpRequest request,
        List<InstallationSetupHttpRejectionCode> rejections)
    {
        bool pageRead =
            request.Operation == InstallationSetupHttpOperation.PageRead;
        bool originPresent = !string.IsNullOrWhiteSpace(request.Origin);
        if ((!pageRead || originPresent) &&
            !OriginMatches(request.Origin))
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.CanonicalOriginRequired);
        }

        bool fetchAllowed = pageRead
            ? IsOneOf(request.SecFetchSite, "none", "same-origin") &&
              string.Equals(
                  request.SecFetchMode,
                  "navigate",
                  StringComparison.OrdinalIgnoreCase)
            : string.Equals(
                  request.SecFetchSite,
                  "same-origin",
                  StringComparison.OrdinalIgnoreCase) &&
              IsOneOf(request.SecFetchMode, "cors", "same-origin");
        if (!fetchAllowed)
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.FetchMetadataRejected);
        }
    }

    private bool OriginMatches(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? origin) ||
            !string.IsNullOrEmpty(origin.UserInfo) ||
            !string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(origin.Query) ||
            !string.IsNullOrEmpty(origin.Fragment))
        {
            return false;
        }
        return string.Equals(
            origin.GetLeftPart(UriPartial.Authority),
            m_publicUrl.Value,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeAuthority(
        string value,
        out string? authority)
    {
        authority = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(
                $"{Uri.UriSchemeHttps}://{value.Trim()}",
                UriKind.Absolute,
                out Uri? parsed) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.Equals(parsed.AbsolutePath, "/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }
        authority = parsed.Authority;
        return true;
    }

    private static void ValidateBody(
        InstallationSetupHttpRequest request,
        int maximumBodyBytes,
        List<InstallationSetupHttpRejectionCode> rejections)
    {
        if (maximumBodyBytes == 0)
        {
            if (request.ContentLength is > 0 ||
                !string.IsNullOrWhiteSpace(request.ContentType))
            {
                rejections.Add(
                    InstallationSetupHttpRejectionCode.RequestBodyForbidden);
            }
            return;
        }

        if (request.ContentLength is null or <= 0)
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.RequestBodyLengthRequired);
        }
        else if (request.ContentLength > maximumBodyBytes)
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.RequestBodyTooLarge);
        }
        if (!IsJsonContentType(request.ContentType))
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.JsonContentTypeRequired);
        }
    }

    private static bool IsJsonContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        string[] parts = value.Split(';', StringSplitOptions.TrimEntries);
        if (!string.Equals(
                parts[0],
                "application/json",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return parts.Length == 1 ||
            (parts.Length == 2 &&
             string.Equals(
                 parts[1],
                 "charset=utf-8",
                 StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateSessionAndCsrf(
        InstallationSetupHttpRequest request,
        List<InstallationSetupHttpRejectionCode> rejections)
    {
        bool sessionRequired =
            request.Operation is InstallationSetupHttpOperation.SessionRead or
                InstallationSetupHttpOperation.SessionMutation;
        if (sessionRequired && !request.SessionCookiePresent)
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.SessionCookieRequired);
        }

        bool csrfRequired =
            request.Operation is InstallationSetupHttpOperation.BootstrapClaim or
                InstallationSetupHttpOperation.SessionMutation;
        if (!csrfRequired)
        {
            return;
        }
        if (request.CsrfCookie is null || request.CsrfHeader is null)
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.CsrfTokenRequired);
            return;
        }
        if (!IsCanonicalCsrfToken(request.CsrfCookie) ||
            !IsCanonicalCsrfToken(request.CsrfHeader))
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.CsrfTokenMalformed);
            return;
        }
        if (!FixedTimeEquals(request.CsrfCookie, request.CsrfHeader))
        {
            rejections.Add(
                InstallationSetupHttpRejectionCode.CsrfTokenMismatch);
        }
    }

    private static bool IsCanonicalCsrfToken(string value) =>
        value.Length == CsrfTokenLength &&
        value.All(
            character =>
                character is >= 'A' and <= 'Z' or
                    >= 'a' and <= 'z' or
                    >= '0' and <= '9' or '-' or '_');

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool FixedTimeEquals(string left, string right)
    {
        Span<byte> leftBytes = stackalloc byte[CsrfTokenLength];
        Span<byte> rightBytes = stackalloc byte[CsrfTokenLength];
        int leftWritten = Encoding.ASCII.GetBytes(left, leftBytes);
        int rightWritten = Encoding.ASCII.GetBytes(right, rightBytes);
        return leftWritten == CsrfTokenLength &&
            rightWritten == CsrfTokenLength &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static bool IsOneOf(
        string? value,
        string first,
        string second) =>
        string.Equals(value, first, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, second, StringComparison.OrdinalIgnoreCase);
}
