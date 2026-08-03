using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherSDR.Web.Setup;

public enum InstallationSetupConsoleCommandKind
{
    None = 0,
    Status = 1,
    IssueBootstrapToken = 2,
    ClaimBootstrapToken = 3,
    ConfigureTopology = 4,
    ConfigurePublicUrl = 5,
    ConfigurePaths = 6,
    ConfigureUpdateChannel = 7,
    ConfirmBackupLocation = 8,
    ConfigureTransmitSupport = 9,
    Preflight = 10
}

public sealed record InstallationSetupConsoleCommandLine(
    InstallationSetupConsoleCommandKind Command,
    InstallationTopologyKind? Topology,
    string PublicUrl,
    InstallationUpdateChannel? UpdateChannel,
    string PinnedRelease,
    bool? InstallTransmitSupport,
    IReadOnlyList<string> ApplicationArguments)
{
    public static InstallationSetupConsoleCommandLine For(
        InstallationSetupConsoleCommandKind command) =>
        new(
            command,
            Topology: null,
            PublicUrl: string.Empty,
            UpdateChannel: null,
            PinnedRelease: string.Empty,
            InstallTransmitSupport: null,
            ApplicationArguments: []);
}

public static class InstallationSetupConsoleCommandParser
{
    public const string StatusSwitch = "--installation-setup-status";
    public const string IssueBootstrapTokenSwitch =
        "--issue-installation-bootstrap-token";
    public const string ClaimBootstrapTokenSwitch =
        "--claim-installation-bootstrap-token";
    public const string ConfigureTopologySwitch =
        "--configure-installation-topology";
    public const string ConfigurePublicUrlSwitch =
        "--configure-installation-public-url";
    public const string ConfigurePathsSwitch =
        "--configure-installation-paths";
    public const string ConfigureUpdateChannelSwitch =
        "--configure-installation-update-channel";
    public const string PinnedReleaseSwitch =
        "--installation-pinned-release";
    public const string ConfirmBackupLocationSwitch =
        "--confirm-installation-backup-location";
    public const string ConfigureTransmitSupportSwitch =
        "--configure-installation-transmit-support";
    public const string PreflightSwitch =
        "--installation-setup-preflight";

    public static InstallationSetupConsoleCommandLine Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        InstallationSetupConsoleCommandKind command =
            InstallationSetupConsoleCommandKind.None;
        InstallationTopologyKind? topology = null;
        string publicUrl = string.Empty;
        InstallationUpdateChannel? updateChannel = null;
        string pinnedRelease = string.Empty;
        bool? installTransmitSupport = null;
        List<string> applicationArguments = [];

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case StatusSwitch:
                    SetCommand(
                        ref command,
                        InstallationSetupConsoleCommandKind.Status);
                    break;
                case IssueBootstrapTokenSwitch:
                    SetCommand(
                        ref command,
                        InstallationSetupConsoleCommandKind.IssueBootstrapToken);
                    break;
                case ClaimBootstrapTokenSwitch:
                    SetCommand(
                        ref command,
                        InstallationSetupConsoleCommandKind.ClaimBootstrapToken);
                    break;
                case ConfigureTopologySwitch:
                    SetCommand(
                        ref command,
                        InstallationSetupConsoleCommandKind.ConfigureTopology);
                    topology = ParseTopology(
                        RequireValue(arguments, ref index, argument));
                    break;
                case ConfigurePublicUrlSwitch:
                    SetCommand(
                        ref command,
                        InstallationSetupConsoleCommandKind.ConfigurePublicUrl);
                    publicUrl = RequireValue(arguments, ref index, argument);
                    _ = CanonicalPublicUrl.Parse(publicUrl);
                    break;
                case ConfigurePathsSwitch:
                    SetCommand(
                        ref command,
                        InstallationSetupConsoleCommandKind.ConfigurePaths);
                    break;
                case ConfigureUpdateChannelSwitch:
                    SetCommand(
                        ref command,
                        InstallationSetupConsoleCommandKind.ConfigureUpdateChannel);
                    updateChannel = ParseUpdateChannel(
                        RequireValue(arguments, ref index, argument));
                    break;
                case PinnedReleaseSwitch:
                    if (!string.IsNullOrEmpty(pinnedRelease))
                    {
                        throw new InvalidOperationException(
                            "The installation pinned release was provided more than once.");
                    }
                    pinnedRelease = RequireValue(arguments, ref index, argument);
                    break;
                case ConfirmBackupLocationSwitch:
                    SetCommand(
                        ref command,
                        InstallationSetupConsoleCommandKind.ConfirmBackupLocation);
                    break;
                case ConfigureTransmitSupportSwitch:
                    SetCommand(
                        ref command,
                        InstallationSetupConsoleCommandKind.ConfigureTransmitSupport);
                    installTransmitSupport = ParseBoolean(
                        RequireValue(arguments, ref index, argument),
                        argument);
                    break;
                case PreflightSwitch:
                    SetCommand(
                        ref command,
                        InstallationSetupConsoleCommandKind.Preflight);
                    break;
                default:
                    applicationArguments.Add(argument);
                    break;
            }
        }

        if (command !=
                InstallationSetupConsoleCommandKind.ConfigureUpdateChannel &&
            !string.IsNullOrEmpty(pinnedRelease))
        {
            throw new InvalidOperationException(
                "The installation pinned release is valid only with the update-channel command.");
        }
        if (command ==
                InstallationSetupConsoleCommandKind.ConfigureUpdateChannel)
        {
            InstallationUpdateChannel selectedChannel = updateChannel ??
                throw new InvalidOperationException(
                    "The installation update-channel command requires a channel.");
            if (selectedChannel == InstallationUpdateChannel.Pinned)
            {
                pinnedRelease = InstallationReleaseIdentity.Parse(pinnedRelease);
            }
            else if (!string.IsNullOrEmpty(pinnedRelease))
            {
                throw new InvalidOperationException(
                    "Only the pinned update channel may include a release identity.");
            }
        }

        return new InstallationSetupConsoleCommandLine(
            command,
            topology,
            publicUrl,
            updateChannel,
            pinnedRelease,
            installTransmitSupport,
            [.. applicationArguments]);
    }

    private static void SetCommand(
        ref InstallationSetupConsoleCommandKind command,
        InstallationSetupConsoleCommandKind candidate)
    {
        if (command != InstallationSetupConsoleCommandKind.None)
        {
            throw new InvalidOperationException(
                "Only one installation setup console command may be requested.");
        }
        command = candidate;
    }

    private static string RequireValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option)
    {
        if (index + 1 >= arguments.Count ||
            string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            throw new InvalidOperationException(
                $"Installation setup option '{option}' requires one value.");
        }
        return arguments[++index].Trim();
    }

    private static InstallationTopologyKind ParseTopology(string value) =>
        value.ToLowerInvariant() switch
        {
            "personal-single-station" =>
                InstallationTopologyKind.PersonalSingleStation,
            "local-station-gateway" =>
                InstallationTopologyKind.LocalStationGateway,
            "remote-station-gateway" =>
                InstallationTopologyKind.RemoteStationGateway,
            "hybrid-gateway" =>
                InstallationTopologyKind.HybridGateway,
            "remote-station-node" =>
                InstallationTopologyKind.RemoteStationNode,
            _ => throw new InvalidOperationException(
                $"Unsupported installation topology '{value}'.")
        };

    private static InstallationUpdateChannel ParseUpdateChannel(string value) =>
        value.ToLowerInvariant() switch
        {
            "stable" => InstallationUpdateChannel.Stable,
            "beta" => InstallationUpdateChannel.Beta,
            "pinned" => InstallationUpdateChannel.Pinned,
            _ => throw new InvalidOperationException(
                $"Unsupported installation update channel '{value}'.")
        };

    private static bool ParseBoolean(string value, string option) =>
        value.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException(
                $"Installation setup option '{option}' requires 'true' or 'false'.")
        };
}

public static class InstallationSetupConsoleSecretReader
{
    public const int MaximumSecretLength = 512;

    public static ValueTask<string> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        StringBuilder value = new();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }
                continue;
            }
            if (char.IsControl(key.KeyChar))
            {
                continue;
            }
            if (value.Length >= MaximumSecretLength)
            {
                throw new InvalidOperationException(
                    "The installation bootstrap token exceeds the supported length.");
            }
            value.Append(key.KeyChar);
        }
        return ValueTask.FromResult(value.ToString());
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
    private readonly InstallationSetupWorkflow m_workflow;
    private readonly InstallationSetupPreflight m_preflight;

    public InstallationSetupConsole(
        InstallationSetupStore store,
        InstallationBootstrapTokenService tokenService,
        InstallationSetupWorkflow? workflow = null,
        InstallationSetupPreflight? preflight = null)
    {
        m_store = store ?? throw new ArgumentNullException(nameof(store));
        m_tokenService = tokenService ??
            throw new ArgumentNullException(nameof(tokenService));
        m_workflow = workflow ?? new InstallationSetupWorkflow(store);
        m_preflight = preflight ?? new InstallationSetupPreflight(store);
    }

    public Task ExecuteAsync(
        InstallationSetupConsoleCommandKind command,
        TextWriter output,
        bool interactiveTokenOutput = false,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            InstallationSetupConsoleCommandLine.For(command),
            installationPaths: null,
            output,
            interactiveTokenOutput,
            interactiveSecretInput: false,
            secretReader: null,
            cancellationToken);

    public async Task ExecuteAsync(
        InstallationSetupConsoleCommandLine commandLine,
        InstallationPaths? installationPaths,
        TextWriter output,
        bool interactiveTokenOutput = false,
        bool interactiveSecretInput = false,
        Func<CancellationToken, ValueTask<string>>? secretReader = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(output);
        if (commandLine.Command ==
                InstallationSetupConsoleCommandKind.IssueBootstrapToken &&
            !interactiveTokenOutput)
        {
            throw new InvalidOperationException(
                "Bootstrap token output requires an interactive local terminal.");
        }
        if (commandLine.Command ==
                InstallationSetupConsoleCommandKind.ClaimBootstrapToken &&
            (!interactiveSecretInput || secretReader is null))
        {
            throw new InvalidOperationException(
                "Bootstrap token claim requires interactive local terminal input.");
        }

        switch (commandLine.Command)
        {
            case InstallationSetupConsoleCommandKind.Status:
            {
                InstallationSetupState state =
                    await m_store.LoadOrCreateAsync(cancellationToken);
                await WriteStatusAsync(state, output);
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
            case InstallationSetupConsoleCommandKind.ClaimBootstrapToken:
            {
                InstallationSetupState state =
                    await m_store.LoadAsync(cancellationToken);
                await output.WriteAsync("Bootstrap token: ");
                string token = await secretReader!(cancellationToken);
                await output.WriteLineAsync();
                InstallationSetupState claimed =
                    await m_tokenService.ClaimAsync(
                        state.Revision,
                        token,
                        cancellationToken);
                await WriteStatusAsync(claimed, output);
                break;
            }
            case InstallationSetupConsoleCommandKind.ConfigureTopology:
            {
                InstallationSetupState state =
                    await m_store.LoadAsync(cancellationToken);
                InstallationSetupState updated =
                    await m_workflow.ConfigureTopologyAsync(
                        state.Revision,
                        commandLine.Topology ??
                            throw MissingValue("topology"),
                        cancellationToken);
                await WriteStatusAsync(updated, output);
                break;
            }
            case InstallationSetupConsoleCommandKind.ConfigurePublicUrl:
            {
                InstallationSetupState state =
                    await m_store.LoadAsync(cancellationToken);
                InstallationSetupState updated =
                    await m_workflow.ConfigurePublicUrlAsync(
                        state.Revision,
                        commandLine.PublicUrl,
                        cancellationToken);
                await WriteStatusAsync(updated, output);
                break;
            }
            case InstallationSetupConsoleCommandKind.ConfigurePaths:
            {
                InstallationPaths paths = installationPaths ??
                    throw MissingValue("resolved installation paths");
                InstallationSetupState state =
                    await m_store.LoadAsync(cancellationToken);
                InstallationSetupState updated =
                    await m_workflow.ConfigurePathsAsync(
                        state.Revision,
                        paths,
                        cancellationToken);
                await WriteStatusAsync(updated, output);
                break;
            }
            case InstallationSetupConsoleCommandKind.ConfigureUpdateChannel:
            {
                InstallationSetupState state =
                    await m_store.LoadAsync(cancellationToken);
                InstallationSetupState updated =
                    await m_workflow.ConfigureUpdateChannelAsync(
                        state.Revision,
                        commandLine.UpdateChannel ??
                            throw MissingValue("update channel"),
                        commandLine.PinnedRelease,
                        cancellationToken);
                await WriteStatusAsync(updated, output);
                break;
            }
            case InstallationSetupConsoleCommandKind.ConfirmBackupLocation:
            {
                InstallationSetupState state =
                    await m_store.LoadAsync(cancellationToken);
                InstallationSetupState updated =
                    await m_workflow.ConfirmBackupLocationAsync(
                        state.Revision,
                        cancellationToken);
                await WriteStatusAsync(updated, output);
                break;
            }
            case InstallationSetupConsoleCommandKind.ConfigureTransmitSupport:
            {
                InstallationSetupState state =
                    await m_store.LoadAsync(cancellationToken);
                InstallationSetupState updated =
                    await m_workflow.ConfigureTransmitSupportAsync(
                        state.Revision,
                        commandLine.InstallTransmitSupport ??
                            throw MissingValue("transmit-support choice"),
                        cancellationToken);
                await WriteStatusAsync(updated, output);
                break;
            }
            case InstallationSetupConsoleCommandKind.Preflight:
            {
                InstallationSetupPreflightReport report =
                    await m_preflight.CreateAsync(cancellationToken);
                await output.WriteLineAsync(
                    JsonSerializer.Serialize(report, JsonOptions));
                break;
            }
            case InstallationSetupConsoleCommandKind.None:
                throw new InvalidOperationException(
                    "No installation setup console command was requested.");
            default:
                throw new InvalidOperationException(
                    $"Unsupported installation setup console command " +
                    $"'{commandLine.Command}'.");
        }
    }

    private static InvalidOperationException MissingValue(string name) =>
        new($"The installation setup command is missing its {name}.");

    private static Task WriteStatusAsync(
        InstallationSetupState state,
        TextWriter output)
    {
        InstallationSetupStatusReport report =
            InstallationSetupStatusReport.From(state);
        return output.WriteLineAsync(
            JsonSerializer.Serialize(report, JsonOptions));
    }
}
