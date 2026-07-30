namespace AetherSDR.Web.Auth;

public static class OidcClientSecretResolver
{
    private const UnixFileMode ForbiddenUnixModes =
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;

    public static string Resolve(AuthSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool hasInlineSecret = !string.IsNullOrWhiteSpace(settings.ClientSecret);
        bool hasSecretFile =
            !string.IsNullOrWhiteSpace(settings.ClientSecretFile);

        if (hasInlineSecret && hasSecretFile)
        {
            throw new InvalidOperationException(
                "Configure only one of Auth:ClientSecret or " +
                "Auth:ClientSecretFile.");
        }

        if (hasInlineSecret)
        {
            return settings.ClientSecret;
        }

        if (!hasSecretFile)
        {
            return string.Empty;
        }

        string path = settings.ClientSecretFile;
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException(
                "Auth:ClientSecretFile must be an absolute path.");
        }

        try
        {
            if (!OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(path) & ForbiddenUnixModes) != 0)
            {
                throw new InvalidOperationException(
                    "Auth:ClientSecretFile must not be accessible by group " +
                    "or other users.");
            }

            string secret = File.ReadAllText(path).Trim();
            if (secret.Length == 0)
            {
                throw new InvalidOperationException(
                    "Auth:ClientSecretFile is empty.");
            }

            return secret;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Auth:ClientSecretFile could not be read.",
                exception);
        }
    }
}
