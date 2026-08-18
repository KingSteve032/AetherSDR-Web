namespace AetherSDR.Web.Setup;

public static class InstallationInstallerUbuntuPathPolicy
{
    public static void RequireCanonicalLinuxSystemPaths(
        InstallationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        InstallationPaths.Validate(paths);

        InstallationPaths expected = InstallationPaths.Resolve(
            "/unused-content-root",
            InstallationPathLayout.LinuxSystem);
        if (paths != expected)
        {
            throw new InvalidOperationException(
                "Standalone Ubuntu installer commands require the canonical " +
                "Linux system installation paths.");
        }
    }
}
