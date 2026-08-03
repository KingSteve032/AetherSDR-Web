namespace AetherSDR.Web.Setup;

public static class InstallationReleaseIdentity
{
    public const int MaximumLength = 96;

    public static string Parse(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > MaximumLength)
        {
            throw new InvalidOperationException(
                "A release identity must contain between 1 and 96 characters.");
        }

        foreach (char character in normalized)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '_' and not '-')
            {
                throw new InvalidOperationException(
                    "A release identity may contain only ASCII letters, digits, " +
                    "periods, underscores, and hyphens.");
            }
        }

        return normalized;
    }
}
