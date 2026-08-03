using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherSDR.Web.Setup;

public enum InstallationSetupConsoleCommandKind
{
    None = 0,
    Status = 1,
    IssueBootstrapToken = 2
}

public sealed record InstallationSetupConsoleCommandLine(
    InstallationSetupConsoleCommandKind Command,
    IReadOnlyList<string> ApplicationArguments);

public static class InstallationSetupConsoleCommandParser
{
    public const string StatusSwitch = "--installation-setup-status";
    public const string IssueBootstrapTokenSwitch =
        "--issue-installation-bootstrap-token";

    public static InstallationSetupConsoleCommandLine Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        InstallationSetupConsoleCommandKind command =
            InstallationSetupConsoleCommandKind.None;
        List<string> applicationArguments = [];
        foreach (string argument in arguments)
        {
            InstallationSetupConsoleCommandKind candidate = argument switch
            {
                StatusSwitch => InstallationSetupConsoleCommandKind.Status,
                IssueBootstrapTokenSwitch =>
                    InstallationSetupConsoleCommandKind.IssueBootstrapToken,
                _ => InstallationSetupConsoleCommandKind.None
            };
            if (candidate == InstallationSetupConsoleCommandKind.None)
            {
                applicationArguments.Add(argument);
                continue;
            }

            if (command != InstallationSetupConsoleCommandKind.None)
            {
                throw new InvalidOperationException(
                    "Only one installation setup console command may be requested.");
            }
            command = candidate;
        }

        return new InstallationSetupConsoleCommandLine(
            command,
            [.. applicationArguments]);
    }
}

public sealed record InstallationSetupStatusReport(
    int SchemaVersion,
    long Revision,
    InstallationSetupLockMode LockMode,
    InstallationSetupStep LastCompletedStep,
    bool SetupComplete,
    bool BootstrapTokenPresent,
    DateTimeOffset? BootstrapTokenExpiresAt,
    InstallationTopologyKind? Topology,
    bool CanonicalPublicUrlConfigured,
    bool InstallationPathsConfigured,
    InstallationUpdateChannel UpdateChannel,
    bool InstallTransmitSupport)
{
    public static InstallationSetupStatusReport From(
        InstallationSetupState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        InstallationSetupStateValidator.Validate(state);
        return new InstallationSetupStatusReport(
            state.SchemaVersion,
            state.Revision,
            state.Lock.Mode,
            state.LastCompletedStep,
            state.Lock.Mode == InstallationSetupLockMode.Complete,
            !string.IsNullOrWhiteSpace(state.Lock.BootstrapTokenHash),
            state.Lock.BootstrapTokenExpiresAt,
            state.Topology,
            !string.IsNullOrWhiteSpace(state.CanonicalPublicUrl),
            state.Paths is not null,
            state.UpdateChannel,
            state.InstallTransmitSupport);
    }
}

public sealed class InstallationSetupConsole
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly InstallationSetupStore m_store;
    private readonly InstallationBootstrapTokenService m_tokenService;

    public InstallationSetupConsole(
        InstallationSetupStore store,
        InstallationBootstrapTokenService tokenService)
    {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_tokenService = tokenService ??
            throw new ArgumentNullException(nameof(tokenService));
    }

    public async Task ExecuteAsync(
        InstallationSetupConsoleCommandKind command,
        TextWriter output,
        bool interactiveTokenOutput = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (command ==
                InstallationSetupConsoleCommandKind.IssueBootstrapToken &&
            !interactiveTokenOutput)
        {
            throw new InvalidOperationException(
                "Bootstrap token output requires an interactive local terminal.");
        }

        switch (command)
        {
            case InstallationSetupConsoleCommandKind.Status:
            {
                InstallationSetupState state =
                    await m_store.LoadOrCreateAsync(cancellationToken);
                InstallationSetupStatusReport report =
                    InstallationSetupStatusReport.From(state);
                await output.WriteLineAsync(
                    JsonSerializer.Serialize(report, JsonOptions));
                break;
            }
            case InstallationSetupConsoleCommandKind.IssueBootstrapToken:
            {
                InstallationSetupState state =
                    await m_store.LoadOrCreateAsync(cancellationToken);
                InstallationBootstrapTokenIssue issue =
                    await m_tokenService.IssueAsync(
                        state.Revision,
                        cancellationToken: cancellationToken);
                await output.WriteLineAsync(
                    "AetherSDR first-administrator bootstrap token");
                await output.WriteLineAsync($"Token: {issue.Token}");
                await output.WriteLineAsync(
                    $"Expires at: {issue.ExpiresAt:O}");
                await output.WriteLineAsync(
                    "This token is shown once. Do not redirect or retain this output.");
                break;
            }
            case InstallationSetupConsoleCommandKind.None:
                throw new InvalidOperationException(
                    "No installation setup console command was requested.");
            default:
                throw new InvalidOperationException(
                    $"Unsupported installation setup console command '{command}'.");
        }
    }
}
