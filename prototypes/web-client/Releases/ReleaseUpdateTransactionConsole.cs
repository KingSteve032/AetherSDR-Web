using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherSDR.Web.Releases;

public sealed class ReleaseUpdateTransactionConsole
{
    public const int SuccessExitCode = 0;
    public const int RejectedExitCode = 2;
    public const int InteractiveApprovalRequiredExitCode = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ReleaseUpdateSupervisorClient m_client;

    public ReleaseUpdateTransactionConsole(
        ReleaseUpdateSupervisorClient client)
    {
        m_client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<int> ExecuteAsync(
        ReleaseUpdateConsoleCommandLine commandLine,
        TextReader input,
        TextWriter output,
        bool interactive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        if (commandLine.Command is not ReleaseUpdateConsoleCommandKind.TransactionStatus &&
            !OperatingSystem.IsLinux())
        {
            await WriteErrorAsync(output, "Release update execution requires Linux.");
            return RejectedExitCode;
        }

        switch (commandLine.Command)
        {
            case ReleaseUpdateConsoleCommandKind.TransactionStatus:
                await WriteAsync(
                    output,
                    await m_client.StatusAsync(cancellationToken));
                return SuccessExitCode;

            case ReleaseUpdateConsoleCommandKind.InstallOfflineRelease:
                if (!interactive || !commandLine.ApprovalRequested)
                {
                    await WriteErrorAsync(
                        output,
                        "Offline release installation requires an owner-controlled interactive terminal and --approve-release-transaction.");
                    return InteractiveApprovalRequiredExitCode;
                }
                ReleaseUpdateTransactionReport prepared =
                    await m_client.PrepareOfflineAsync(
                        new ReleaseUpdateInstallRequest(
                            commandLine.BundleDirectory,
                            commandLine.InstalledReleaseIdentity,
                            commandLine.InstalledVersion,
                            commandLine.ConfigurationSchemaVersion ?? 0,
                            commandLine.ProtocolVersion ?? 0),
                        cancellationToken);
                await WriteAsync(output, prepared);
                if (!prepared.Succeeded)
                {
                    return RejectedExitCode;
                }
                await output.WriteAsync(
                    $"Type {prepared.TargetReleaseIdentity} to activate this exact release: ");
                string confirmation = (await input.ReadLineAsync(cancellationToken)) ??
                    string.Empty;
                if (!string.Equals(
                        confirmation,
                        prepared.TargetReleaseIdentity,
                        StringComparison.Ordinal))
                {
                    await WriteErrorAsync(output, "Release activation confirmation did not match.");
                    return InteractiveApprovalRequiredExitCode;
                }
                ReleaseUpdateTransactionReport installed =
                    await m_client.ApproveAndActivateAsync(
                        prepared.TransactionId,
                        ReleaseUpdateOperatorAuthenticationEvidenceFactory
                            .CreateLocalCliEvidence(),
                        cancellationToken);
                await WriteAsync(output, installed);
                return installed.Succeeded
                    ? SuccessExitCode
                    : RejectedExitCode;

            case ReleaseUpdateConsoleCommandKind.RollbackTransaction:
                if (!interactive || !commandLine.ApprovalRequested)
                {
                    await WriteErrorAsync(
                        output,
                        "Release rollback requires an owner-controlled interactive terminal and --approve-release-transaction.");
                    return InteractiveApprovalRequiredExitCode;
                }
                await output.WriteAsync(
                    $"Type {commandLine.TransactionId} to roll back this exact transaction: ");
                string rollbackConfirmation =
                    (await input.ReadLineAsync(cancellationToken)) ?? string.Empty;
                if (!string.Equals(
                        rollbackConfirmation,
                        commandLine.TransactionId,
                        StringComparison.Ordinal))
                {
                    await WriteErrorAsync(output, "Release rollback confirmation did not match.");
                    return InteractiveApprovalRequiredExitCode;
                }
                ReleaseUpdateTransactionReport rollback =
                    await m_client.ApproveAndRollbackAsync(
                        commandLine.TransactionId,
                        ReleaseUpdateOperatorAuthenticationEvidenceFactory
                            .CreateLocalCliEvidence(),
                        cancellationToken);
                await WriteAsync(output, rollback);
                return rollback.Succeeded
                    ? SuccessExitCode
                    : RejectedExitCode;

            default:
                throw new InvalidOperationException(
                    "The release transaction console requires an install, rollback, or transaction-status command.");
        }
    }

    private static Task WriteAsync(
        TextWriter output,
        ReleaseUpdateTransactionReport report) =>
        output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOptions));

    private static Task WriteErrorAsync(TextWriter output, string message) =>
        output.WriteLineAsync(JsonSerializer.Serialize(new { error = message }, JsonOptions));
}
