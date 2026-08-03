using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationSetupHttpSecurityTests
{
    private const string CanonicalUrl = "https://radio.example.org";
    private const string CsrfToken =
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string OtherCsrfToken =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void ContractPublishesStrictCookiesHeadersAndRateLimits()
    {
        InstallationSetupHttpSecurityPolicy policy = new(CanonicalUrl);
        InstallationSetupHttpSecurityContract contract = policy.Contract;

        Assert.Equal(CanonicalUrl, contract.CanonicalOrigin);
        Assert.Equal(
            InstallationSetupHttpSecurityPolicy.SessionCookieName,
            contract.SessionCookie.Name);
        Assert.True(contract.SessionCookie.Secure);
        Assert.True(contract.SessionCookie.HttpOnly);
        Assert.Equal("Strict", contract.SessionCookie.SameSite);
        Assert.Equal("/", contract.SessionCookie.Path);
        Assert.False(contract.SessionCookie.DomainAllowed);
        Assert.Equal(
            InstallationSetupClaimSessionService.MaximumLifetime,
            contract.SessionCookie.MaximumAge);

        Assert.Equal(
            InstallationSetupHttpSecurityPolicy.CsrfCookieName,
            contract.CsrfCookie.Name);
        Assert.True(contract.CsrfCookie.Secure);
        Assert.False(contract.CsrfCookie.HttpOnly);
        Assert.Equal("Strict", contract.CsrfCookie.SameSite);
        Assert.Equal("/", contract.CsrfCookie.Path);
        Assert.False(contract.CsrfCookie.DomainAllowed);

        Assert.Contains("default-src 'none'", contract.ResponseHeaders.ContentSecurityPolicy);
        Assert.Contains("frame-ancestors 'none'", contract.ResponseHeaders.ContentSecurityPolicy);
        Assert.Equal("no-store, max-age=0", contract.ResponseHeaders.CacheControl);
        Assert.Equal("no-referrer", contract.ResponseHeaders.ReferrerPolicy);
        Assert.Equal("same-origin", contract.ResponseHeaders.CrossOriginOpenerPolicy);
        Assert.Equal("same-origin", contract.ResponseHeaders.CrossOriginResourcePolicy);
        Assert.Equal("nosniff", contract.ResponseHeaders.XContentTypeOptions);

        Assert.Equal(4, contract.RateLimits.Count);
        Assert.All(contract.RateLimits, value =>
        {
            Assert.Equal(TimeSpan.FromMinutes(1), value.Window);
            Assert.Equal(0, value.QueueLimit);
            Assert.True(value.AutoReplenishment);
        });
        Assert.Equal(30, contract.RateLimits[0].PermitLimit);
        Assert.Equal(5, contract.RateLimits[1].PermitLimit);
        Assert.Equal(60, contract.RateLimits[2].PermitLimit);
        Assert.Equal(30, contract.RateLimits[3].PermitLimit);
    }

    [Fact]
    public void InitialPageReadAllowsOnlyCanonicalHttpsNavigation()
    {
        InstallationSetupHttpSecurityPolicy policy = new(CanonicalUrl);
        InstallationSetupHttpRequest request = CreateRequest(
            InstallationSetupHttpOperation.PageRead);

        InstallationSetupHttpSecurityDecision decision =
            policy.Evaluate(request);

        Assert.True(decision.Allowed);
        Assert.Empty(decision.Rejections);
        Assert.Equal(0, decision.MaximumRequestBodyBytes);
        Assert.Equal("installation-setup-page", decision.RateLimit.PolicyName);
    }

    [Fact]
    public void PageReadRejectsInsecureForeignOrBodyBearingRequests()
    {
        InstallationSetupHttpSecurityPolicy policy = new(CanonicalUrl);
        InstallationSetupHttpRequest request = new(
            InstallationSetupHttpOperation.PageRead,
            "POST",
            "http",
            "attacker.example",
            "https://attacker.example",
            "cross-site",
            "cors",
            "application/json",
            2,
            hasQueryString: true,
            sessionCookiePresent: false,
            csrfCookie: null,
            csrfHeader: null);

        InstallationSetupHttpSecurityDecision decision =
            policy.Evaluate(request);

        Assert.False(decision.Allowed);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.MethodMismatch,
            decision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.HttpsRequired,
            decision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.CanonicalHostMismatch,
            decision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.CanonicalOriginRequired,
            decision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.FetchMetadataRejected,
            decision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.QueryStringForbidden,
            decision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.RequestBodyForbidden,
            decision.Rejections);
    }

    [Fact]
    public void BootstrapClaimRequiresBoundedJsonAndMatchingCsrf()
    {
        InstallationSetupHttpSecurityPolicy policy = new(CanonicalUrl);
        InstallationSetupHttpRequest request = CreateRequest(
            InstallationSetupHttpOperation.BootstrapClaim);

        InstallationSetupHttpSecurityDecision decision =
            policy.Evaluate(request);

        Assert.True(decision.Allowed);
        Assert.Empty(decision.Rejections);
        Assert.Equal(4096, decision.MaximumRequestBodyBytes);
        Assert.Equal("installation-setup-claim", decision.RateLimit.PolicyName);
        Assert.Equal(5, decision.RateLimit.PermitLimit);
    }

    [Fact]
    public void BootstrapClaimRejectsEveryWeakenedBoundary()
    {
        InstallationSetupHttpSecurityPolicy policy = new(CanonicalUrl);
        InstallationSetupHttpRequest request = new(
            InstallationSetupHttpOperation.BootstrapClaim,
            "POST",
            "https",
            "radio.example.org",
            null,
            "cross-site",
            "navigate",
            "text/plain",
            4097,
            hasQueryString: true,
            sessionCookiePresent: false,
            csrfCookie: "short",
            csrfHeader: OtherCsrfToken);

        InstallationSetupHttpSecurityDecision decision =
            policy.Evaluate(request);

        Assert.False(decision.Allowed);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.CanonicalOriginRequired,
            decision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.FetchMetadataRejected,
            decision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.QueryStringForbidden,
            decision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.RequestBodyTooLarge,
            decision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.JsonContentTypeRequired,
            decision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.CsrfTokenMalformed,
            decision.Rejections);
    }

    [Theory]
    [InlineData(null, null, InstallationSetupHttpRejectionCode.CsrfTokenRequired)]
    [InlineData(CsrfToken, OtherCsrfToken, InstallationSetupHttpRejectionCode.CsrfTokenMismatch)]
    public void BootstrapClaimFailsClosedForMissingOrMismatchedCsrf(
        string? csrfCookie,
        string? csrfHeader,
        InstallationSetupHttpRejectionCode expected)
    {
        InstallationSetupHttpSecurityPolicy policy = new(CanonicalUrl);
        InstallationSetupHttpRequest valid = CreateRequest(
            InstallationSetupHttpOperation.BootstrapClaim);
        InstallationSetupHttpRequest request = new(
            valid.Operation,
            valid.Method,
            valid.Scheme,
            valid.Host,
            valid.Origin,
            valid.SecFetchSite,
            valid.SecFetchMode,
            valid.ContentType,
            valid.ContentLength,
            valid.HasQueryString,
            valid.SessionCookiePresent,
            csrfCookie,
            csrfHeader);

        InstallationSetupHttpSecurityDecision decision =
            policy.Evaluate(request);

        Assert.False(decision.Allowed);
        Assert.Contains(expected, decision.Rejections);
    }

    [Fact]
    public void SessionReadRequiresExactOriginFetchMetadataAndCookie()
    {
        InstallationSetupHttpSecurityPolicy policy = new(CanonicalUrl);
        InstallationSetupHttpRequest allowed = CreateRequest(
            InstallationSetupHttpOperation.SessionRead);
        InstallationSetupHttpRequest denied = new(
            allowed.Operation,
            allowed.Method,
            allowed.Scheme,
            allowed.Host,
            allowed.Origin,
            allowed.SecFetchSite,
            allowed.SecFetchMode,
            allowed.ContentType,
            allowed.ContentLength,
            allowed.HasQueryString,
            sessionCookiePresent: false,
            allowed.CsrfCookie,
            allowed.CsrfHeader);

        InstallationSetupHttpSecurityDecision allowedDecision =
            policy.Evaluate(allowed);
        InstallationSetupHttpSecurityDecision deniedDecision =
            policy.Evaluate(denied);

        Assert.True(allowedDecision.Allowed);
        Assert.Equal(
            "installation-setup-session-read",
            allowedDecision.RateLimit.PolicyName);
        Assert.False(deniedDecision.Allowed);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.SessionCookieRequired,
            deniedDecision.Rejections);
    }

    [Fact]
    public void SessionMutationRequiresCookieBoundedJsonAndCsrf()
    {
        InstallationSetupHttpSecurityPolicy policy = new(CanonicalUrl);
        InstallationSetupHttpRequest allowed = CreateRequest(
            InstallationSetupHttpOperation.SessionMutation);
        InstallationSetupHttpRequest denied = new(
            allowed.Operation,
            allowed.Method,
            allowed.Scheme,
            allowed.Host,
            allowed.Origin,
            allowed.SecFetchSite,
            allowed.SecFetchMode,
            allowed.ContentType,
            contentLength: null,
            allowed.HasQueryString,
            sessionCookiePresent: false,
            allowed.CsrfCookie,
            OtherCsrfToken);

        InstallationSetupHttpSecurityDecision allowedDecision =
            policy.Evaluate(allowed);
        InstallationSetupHttpSecurityDecision deniedDecision =
            policy.Evaluate(denied);

        Assert.True(allowedDecision.Allowed);
        Assert.Equal(16384, allowedDecision.MaximumRequestBodyBytes);
        Assert.Equal(
            "installation-setup-session-mutation",
            allowedDecision.RateLimit.PolicyName);
        Assert.False(deniedDecision.Allowed);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.RequestBodyLengthRequired,
            deniedDecision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.SessionCookieRequired,
            deniedDecision.Rejections);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.CsrfTokenMismatch,
            deniedDecision.Rejections);
    }

    [Fact]
    public void CanonicalAuthorityNormalizesDefaultHttpsPort()
    {
        InstallationSetupHttpSecurityPolicy policy =
            new("https://radio.example.org:443");
        InstallationSetupHttpRequest request = new(
            InstallationSetupHttpOperation.PageRead,
            "GET",
            "HTTPS",
            "RADIO.EXAMPLE.ORG:443",
            null,
            "none",
            "navigate",
            null,
            null,
            hasQueryString: false,
            sessionCookiePresent: false,
            csrfCookie: null,
            csrfHeader: null);

        Assert.True(policy.Evaluate(request).Allowed);
        Assert.Equal(CanonicalUrl, policy.Contract.CanonicalOrigin);
    }

    [Fact]
    public void UnsupportedOperationAndInvalidConfigurationFailClosed()
    {
        InstallationSetupHttpSecurityPolicy policy = new(CanonicalUrl);
        InstallationSetupHttpRequest request = new(
            (InstallationSetupHttpOperation)999,
            "GET",
            "https",
            "radio.example.org",
            CanonicalUrl,
            "same-origin",
            "cors",
            null,
            null,
            hasQueryString: false,
            sessionCookiePresent: false,
            csrfCookie: null,
            csrfHeader: null);

        InstallationSetupHttpSecurityDecision decision =
            policy.Evaluate(request);

        Assert.False(decision.Allowed);
        Assert.Contains(
            InstallationSetupHttpRejectionCode.UnsupportedOperation,
            decision.Rejections);
        Assert.Throws<InvalidOperationException>(
            () => new InstallationSetupHttpSecurityPolicy(
                CanonicalUrl,
                new InstallationSetupHttpSecuritySettings
                {
                    BootstrapClaimPermitLimit = 0
                }));
    }

    [Fact]
    public void CsrfIssueUsesIndependentCanonicalEntropyAndRedactedDiagnostics()
    {
        InstallationSetupHttpCsrfIssue first =
            InstallationSetupHttpSecurityPolicy.IssueCsrfToken();
        InstallationSetupHttpCsrfIssue second =
            InstallationSetupHttpSecurityPolicy.IssueCsrfToken();

        Assert.Equal(43, first.Token.Length);
        Assert.All(
            first.Token,
            value => Assert.True(
                value is >= 'A' and <= 'Z' or
                    >= 'a' and <= 'z' or
                    >= '0' and <= '9' or '-' or '_'));
        Assert.NotEqual(first.Token, second.Token);
        Assert.DoesNotContain(
            first.Token,
            first.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("Token = [redacted]", first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RequestStringRedactsCsrfValues()
    {
        InstallationSetupHttpRequest request = CreateRequest(
            InstallationSetupHttpOperation.SessionMutation);

        string rendered = request.ToString();

        Assert.DoesNotContain(CsrfToken, rendered, StringComparison.Ordinal);
        Assert.Contains("CsrfCookie = [redacted]", rendered, StringComparison.Ordinal);
        Assert.Contains("CsrfHeader = [redacted]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void SecurityPolicyRemainsUnwiredFromProductionProgram()
    {
        string programPath = Path.Combine(
            FindRepositoryRoot(),
            "prototypes",
            "web-client",
            "Program.cs");
        string source = File.ReadAllText(programPath);

        Assert.DoesNotContain(
            "InstallationSetupHttpSecurityPolicy",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            InstallationSetupHttpSecuritySettings.SectionName,
            source,
            StringComparison.Ordinal);
    }

    private static InstallationSetupHttpRequest CreateRequest(
        InstallationSetupHttpOperation operation) =>
        operation switch
        {
            InstallationSetupHttpOperation.PageRead => new(
                operation,
                "GET",
                "https",
                "radio.example.org",
                origin: null,
                "none",
                "navigate",
                contentType: null,
                contentLength: null,
                hasQueryString: false,
                sessionCookiePresent: false,
                csrfCookie: null,
                csrfHeader: null),
            InstallationSetupHttpOperation.BootstrapClaim => new(
                operation,
                "POST",
                "https",
                "radio.example.org",
                CanonicalUrl,
                "same-origin",
                "cors",
                "application/json; charset=utf-8",
                128,
                hasQueryString: false,
                sessionCookiePresent: false,
                CsrfToken,
                CsrfToken),
            InstallationSetupHttpOperation.SessionRead => new(
                operation,
                "GET",
                "https",
                "radio.example.org",
                CanonicalUrl,
                "same-origin",
                "cors",
                contentType: null,
                contentLength: null,
                hasQueryString: false,
                sessionCookiePresent: true,
                csrfCookie: null,
                csrfHeader: null),
            InstallationSetupHttpOperation.SessionMutation => new(
                operation,
                "POST",
                "https",
                "radio.example.org",
                CanonicalUrl,
                "same-origin",
                "cors",
                "application/json",
                256,
                hasQueryString: false,
                sessionCookiePresent: true,
                CsrfToken,
                CsrfToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root was not found.");
    }
}
