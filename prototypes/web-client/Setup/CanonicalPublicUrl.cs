namespace AetherSDR.Web.Setup;

public sealed record CanonicalPublicUrl
{
    private CanonicalPublicUrl(Uri uri)
    {
        Uri = uri;
        Value = uri.GetLeftPart(UriPartial.Authority);
    }

    public Uri Uri { get; }

    public string Value { get; }

    public static CanonicalPublicUrl Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !System.Uri.TryCreate(
                value.Trim(),
                UriKind.Absolute,
                out Uri? parsed))
        {
            throw new InvalidOperationException(
                "The canonical public AetherSDR URL must be an absolute HTTPS URL.");
        }

        if (!string.Equals(
                parsed.Scheme,
                System.Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.Equals(parsed.AbsolutePath, "/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            throw new InvalidOperationException(
                "The canonical public AetherSDR URL must contain only an HTTPS " +
                "scheme, host, and optional non-default port.");
        }

        UriBuilder normalized = new(parsed)
        {
            Scheme = System.Uri.UriSchemeHttps,
            Host = parsed.IdnHost.ToLowerInvariant(),
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        if (parsed.IsDefaultPort)
        {
            normalized.Port = -1;
        }

        return new CanonicalPublicUrl(normalized.Uri);
    }

    public override string ToString() => Value;
}
