namespace AetherSDR.Web.Auth;

/// <summary>
/// Normalizes an untrusted login return target to one local absolute path.
/// Backslashes, control characters, and encoded authority-like paths fail
/// closed to the application root.
/// </summary>
public static class LocalReturnUrl
{
    public static string Normalize(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) ||
            returnUrl[0] != '/' ||
            ContainsUnsafeCharacters(returnUrl))
        {
            return "/";
        }

        string decoded = returnUrl;
        bool fullyDecoded = false;
        try
        {
            for (int pass = 0; pass < 16; pass++)
            {
                string next = Uri.UnescapeDataString(decoded);
                if (string.Equals(next, decoded, StringComparison.Ordinal))
                {
                    fullyDecoded = true;
                    break;
                }
                decoded = next;
            }
        }
        catch (UriFormatException)
        {
            return "/";
        }

        if (!fullyDecoded ||
            decoded.Length == 0 ||
            decoded[0] != '/' ||
            decoded.StartsWith("//", StringComparison.Ordinal) ||
            ContainsUnsafeCharacters(decoded) ||
            !Uri.TryCreate(returnUrl, UriKind.Relative, out _))
        {
            return "/";
        }

        return returnUrl;
    }

    private static bool ContainsUnsafeCharacters(string value) =>
        value.Contains('\\') || value.Any(char.IsControl);
}
