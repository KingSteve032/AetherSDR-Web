namespace AetherSDR.Web.Setup;

public enum InstallationServiceHostRole
{
    Gateway = 1,
    StationEngine = 2
}

public sealed record InstallationServiceHostSettings
{
    public const string SectionName = "InstallationServiceHost";

    public InstallationServiceHostRole Role { get; init; } =
        InstallationServiceHostRole.Gateway;
}

public static class InstallationServiceHost
{
    public static InstallationServiceHostRole Validate(
        InstallationServiceHostSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Role))
        {
            throw new InvalidOperationException(
                "The installation service host role is unsupported.");
        }

        return settings.Role;
    }
}
