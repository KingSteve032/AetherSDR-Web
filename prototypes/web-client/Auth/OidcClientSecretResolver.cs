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
            FileInfo file = new(Path.GetFullPath(path));
            file.Refresh();
            if (!file.Exists ||
                file.LinkTarget is not null ||
                (file.Attributes &
                    (FileAttributes.Directory |
                     FileAttributes.ReparsePoint)) != 0 ||
                file.Length is < 1 or > 4096)
            {
                throw new InvalidOperationException(
                    "Auth:ClientSecretFile must be one safe bounded regular file.");
            }
            if (!OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(file.FullName) &
                    ForbiddenUnixModes) != 0)
            {
                throw new InvalidOperationException(
                    "Auth:ClientSecretFile must not be accessible by group " +
                    "or other users.");
            }

            string secret = File.ReadAllText(file.FullName).Trim();
            if (secret.Length is < 1 or > 2048 ||
                secret.Any(character =>
                    character is '\r' or '\n' or '\0'))
            {
                throw new InvalidOperationException(
                    "Auth:ClientSecretFile is empty or malformed.");
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
