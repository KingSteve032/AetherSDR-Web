using AetherSDR.Web.Auth;

namespace AetherSDR.Web.Tests;

public sealed class OidcClientSecretResolverTests : IDisposable
{
    private readonly string m_directory =
        Path.Combine(
            Path.GetTempPath(),
            $"aethersdr-web-secret-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ResolveReturnsInlineSecret()
    {
        AuthSettings settings = new() { ClientSecret = "inline-value" };

        Assert.Equal(
            "inline-value",
            OidcClientSecretResolver.Resolve(settings));
    }

    [Fact]
    public void ResolveReadsOwnerOnlySecretFile()
    {
        string path = WriteSecretFile("  file-value\r\n");
        AuthSettings settings = new() { ClientSecretFile = path };

        Assert.Equal(
            "file-value",
            OidcClientSecretResolver.Resolve(settings));
    }

    [Fact]
    public void ResolveRejectsAmbiguousConfiguration()
    {
        AuthSettings settings = new()
        {
            ClientSecret = "inline-value",
            ClientSecretFile = Path.Combine(m_directory, "secret")
        };

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => OidcClientSecretResolver.Resolve(settings));

        Assert.Contains("only one", exception.Message);
    }

    [Fact]
    public void ResolveRejectsOversizedOrMultilineSecretFile()
    {
        string oversized = WriteSecretFile(new string('x', 4097));
        Assert.Throws<InvalidOperationException>(
            () => OidcClientSecretResolver.Resolve(
                new AuthSettings { ClientSecretFile = oversized }));

        File.Delete(oversized);
        string multiline = WriteSecretFile("first\nsecond");
        Assert.Throws<InvalidOperationException>(
            () => OidcClientSecretResolver.Resolve(
                new AuthSettings { ClientSecretFile = multiline }));
    }

    [Fact]
    public void ResolveRejectsSecretFileSymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(m_directory);
        string target = Path.Combine(m_directory, "target");
        File.WriteAllText(target, "secret");
        File.SetUnixFileMode(
            target,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string link = Path.Combine(m_directory, "secret");
        File.CreateSymbolicLink(link, target);

        Assert.Throws<InvalidOperationException>(
            () => OidcClientSecretResolver.Resolve(
                new AuthSettings { ClientSecretFile = link }));
    }

    [Fact]
    public void ResolveRejectsMissingSecretFile()
    {
        Directory.CreateDirectory(m_directory);
        AuthSettings settings = new()
        {
            ClientSecretFile = Path.Combine(m_directory, "missing")
        };

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => OidcClientSecretResolver.Resolve(settings));

        Assert.Contains("safe bounded regular file", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(m_directory))
        {
            Directory.Delete(m_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string WriteSecretFile(string value)
    {
        Directory.CreateDirectory(m_directory);
        string path = Path.Combine(m_directory, "secret");
        File.WriteAllText(path, value);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return path;
    }
}
