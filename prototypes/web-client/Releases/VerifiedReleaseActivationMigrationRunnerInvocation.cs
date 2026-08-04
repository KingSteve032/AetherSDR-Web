using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherSDR.Web.Releases;

public enum VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
{
    None = 0,
    RunnerSelectionNotEligible = 1,
    RunnerSelectionUnavailable = 2,
    RunnerSelectionMismatch = 3,
    RunnerArtifactChanged = 4,
    RunnerStartFailed = 5,
    RunnerTimedOut = 6,
    RunnerOutputTooLarge = 7,
    RunnerProcessFailed = 8,
    RunnerResponseInvalid = 9,
    RunnerProbeRejected = 10
}

public sealed record VerifiedReleaseActivationMigrationRunnerInvocationReport(
    bool Succeeded,
    VerifiedReleaseActivationMigrationRunnerInvocationFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    ReleaseMigrationKind? MigrationKind,
    int? FromConfigurationSchemaVersion,
    int? ToConfigurationSchemaVersion,
    bool MigrationRequired,
    bool NoOpMigrationResolved,
    bool ExactRunnerSelectionBound,
    bool RunnerArtifactRevalidated,
    bool ShellInvocationDisabled,
    bool EnvironmentCleared,
    bool ProbeRequestSent,
    bool RunnerInvoked,
    bool ProbeResponseAccepted,
    int? RunnerProtocolVersion,
    bool MigrationSourcePathProvided,
    bool MigrationSourceReadPerformed,
    bool FileWritePerformed,
    bool DirectoryMutationPerformed,
    bool MigrationExecutionPerformed,
    bool MigrationReady,
    bool CurrentPointerChanged,
    bool ActivationAuthorized)
{
    internal VerifiedReleaseActivationMigrationRunnerInvocation? Invocation
    {
        get;
        init;
    }

    internal static VerifiedReleaseActivationMigrationRunnerInvocationReport Failure(
        VerifiedReleaseActivationMigrationRunnerInvocationFailureCode failureCode,
        string message,
        VerifiedReleaseActivationMigrationRunnerSelectionReport? selectionReport = null,
        bool exactSelectionBound = false,
        bool artifactRevalidated = false,
        bool probeRequestSent = false,
        bool runnerInvoked = false,
        int? runnerProtocolVersion = null) =>
        new(
            false,
            failureCode,
            message,
            selectionReport?.SetupRevision,
            selectionReport?.InstalledReleaseIdentity ?? string.Empty,
            selectionReport?.TargetReleaseIdentity ?? string.Empty,
            selectionReport?.MigrationKind,
            selectionReport?.FromConfigurationSchemaVersion,
            selectionReport?.ToConfigurationSchemaVersion,
            selectionReport?.MigrationRequired ?? false,
            NoOpMigrationResolved: false,
            exactSelectionBound,
            artifactRevalidated,
            ShellInvocationDisabled: true,
            EnvironmentCleared: true,
            probeRequestSent,
            runnerInvoked,
            ProbeResponseAccepted: false,
            runnerProtocolVersion,
            MigrationSourcePathProvided: false,
            MigrationSourceReadPerformed: false,
            FileWritePerformed: false,
            DirectoryMutationPerformed: false,
            MigrationExecutionPerformed: false,
            MigrationReady: false,
            CurrentPointerChanged: false,
            ActivationAuthorized: false);

    internal static VerifiedReleaseActivationMigrationRunnerInvocationReport Success(
        VerifiedReleaseActivationMigrationRunnerSelection selection,
        bool runnerInvoked,
        bool artifactRevalidated,
        int? runnerProtocolVersion) =>
        new(
            true,
            VerifiedReleaseActivationMigrationRunnerInvocationFailureCode.None,
            selection.Plan.MigrationRequired
                ? "The exact locally pinned migration runner accepted a bounded probe-only request without receiving migration paths or executing migration work."
                : "The exact signed no-migration declaration requires no runner invocation.",
            selection.Plan.ActivationPlan.SetupRevision,
            selection.Plan.ActivationPlan.InstalledReleaseIdentity,
            selection.Plan.ActivationPlan.TargetReleaseIdentity,
            selection.Plan.MigrationKind,
            selection.Plan.FromConfigurationSchemaVersion,
            selection.Plan.ToConfigurationSchemaVersion,
            selection.Plan.MigrationRequired,
            NoOpMigrationResolved: !selection.Plan.MigrationRequired,
            ExactRunnerSelectionBound: true,
            artifactRevalidated,
            ShellInvocationDisabled: true,
            EnvironmentCleared: true,
            ProbeRequestSent: runnerInvoked,
            runnerInvoked,
            ProbeResponseAccepted: runnerInvoked,
            runnerProtocolVersion,
            MigrationSourcePathProvided: false,
            MigrationSourceReadPerformed: false,
            FileWritePerformed: false,
            DirectoryMutationPerformed: false,
            MigrationExecutionPerformed: false,
            MigrationReady: !selection.Plan.MigrationRequired,
            CurrentPointerChanged: false,
            ActivationAuthorized: false)
        {
            Invocation = new VerifiedReleaseActivationMigrationRunnerInvocation(
                selection,
                runnerInvoked,
                artifactRevalidated)
        };
}

public sealed record VerifiedReleaseActivationMigrationRunnerInvocationDiagnostics(
    bool Registered,
    bool RunnerSelectionInputRegistered,
    bool ExactRunnerSelectionBindingRegistered,
    bool NoOpResolutionRegistered,
    bool ImmediateRunnerArtifactRevalidationRegistered,
    bool DirectProcessInvocationRegistered,
    bool ShellInvocationRegistered,
    bool ClearedEnvironmentRegistered,
    bool BoundedJsonStdinRegistered,
    bool BoundedStdoutRegistered,
    bool BoundedStderrRegistered,
    bool HardTimeoutRegistered,
    bool ProcessTreeTerminationRegistered,
    bool ProbeOnlyProtocolRegistered,
    bool MigrationSourcePathInputRegistered,
    bool MigrationSourceReadRegistered,
    bool FileWriteRegistered,
    bool DirectoryMutationRegistered,
    bool MigrationExecutionRegistered,
    bool MigrationEvidenceRegistered,
    bool CurrentPointerMutationRegistered,
    bool ActivationAuthorityRegistered,
    bool OperationalCallerRegistered,
    bool CliCallerRegistered,
    bool AdminCallerRegistered,
    bool BrowserCallerRegistered,
    bool HttpCallerRegistered,
    bool WebSocketCallerRegistered,
    bool HostedServiceCallerRegistered,
    bool TimerCallerRegistered,
    bool AetherRemoteCallerRegistered,
    bool ServiceControlCallerRegistered,
    bool HealthProbeCallerRegistered,
    bool RollbackCallerRegistered,
    bool RadioCallerRegistered,
    bool WatchdogCallerRegistered,
    bool CommandCallerRegistered,
    bool LeaseCallerRegistered,
    bool TxCallerRegistered);

internal sealed record ReleaseMigrationRunnerProbeRequest(
    int ProtocolVersion,
    string Type,
    string RequestId,
    long SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    string RunnerIdentity,
    string MigrationIdentity,
    int FromConfigurationSchemaVersion,
    int ToConfigurationSchemaVersion,
    bool MigrationExecutionRequested,
    bool MigrationSourcePathsProvided);

internal sealed record ReleaseMigrationRunnerProbeResponse(
    int ProtocolVersion,
    string Type,
    string RequestId,
    string RunnerIdentity,
    string MigrationIdentity,
    int FromConfigurationSchemaVersion,
    int ToConfigurationSchemaVersion,
    bool ProbeAccepted,
    bool MigrationExecutionPerformed,
    bool FilesystemMutationPerformed,
    bool MigrationSourcePathsReceived);

internal sealed class VerifiedReleaseActivationMigrationRunnerInvocation
{
    internal VerifiedReleaseActivationMigrationRunnerInvocation(
        VerifiedReleaseActivationMigrationRunnerSelection selection,
        bool runnerInvoked,
        bool artifactRevalidated)
    {
        Selection = selection ??
            throw new ArgumentNullException(nameof(selection));
        RunnerInvoked = runnerInvoked;
        ArtifactRevalidated = artifactRevalidated;
    }

    internal VerifiedReleaseActivationMigrationRunnerSelection Selection { get; }
    internal bool RunnerInvoked { get; }
    internal bool ArtifactRevalidated { get; }
}

/// <summary>
/// Callerless probe-only process boundary for one exact locally pinned migration
/// runner selection. The runner artifact is rehashed immediately before direct
/// no-shell invocation. One bounded JSON request is sent over stdin and one
/// bounded JSON response is accepted from stdout under a hard timeout. The
/// request contains no backup, staging, secret, configuration, deployment, or
/// publication path and explicitly forbids migration execution. The boundary
/// produces no migration evidence, changes no files or current pointer, grants no
/// activation authority, and has no operational, service, radio, watchdog,
/// command, lease, or TX caller.
/// </summary>
public sealed class VerifiedReleaseActivationMigrationRunnerInvocationService
{
    internal const int CurrentProbeProtocolVersion = 1;
    internal const int MaximumRequestCharacters = 4096;
    internal const int MaximumStandardOutputCharacters = 16 * 1024;
    internal const int MaximumStandardErrorCharacters = 8 * 1024;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private const string ProbeRequestType =
        "aethersdr.release-migration.probe.v1";
    private const string ProbeResponseType =
        "aethersdr.release-migration.probe-result.v1";
    private const UnixFileMode AnyWritableUnixModes =
        UnixFileMode.UserWrite |
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;
    private const UnixFileMode ForbiddenSharedWritableUnixModes =
        UnixFileMode.GroupWrite |
        UnixFileMode.OtherWrite;

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly TimeSpan m_timeout;

    public VerifiedReleaseActivationMigrationRunnerInvocationService()
        : this(DefaultTimeout)
    {
    }

    internal VerifiedReleaseActivationMigrationRunnerInvocationService(
        TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        m_timeout = timeout;
        Snapshot = new VerifiedReleaseActivationMigrationRunnerInvocationDiagnostics(
            Registered: true,
            RunnerSelectionInputRegistered: true,
            ExactRunnerSelectionBindingRegistered: true,
            NoOpResolutionRegistered: true,
            ImmediateRunnerArtifactRevalidationRegistered: true,
            DirectProcessInvocationRegistered: true,
            ShellInvocationRegistered: false,
            ClearedEnvironmentRegistered: true,
            BoundedJsonStdinRegistered: true,
            BoundedStdoutRegistered: true,
            BoundedStderrRegistered: true,
            HardTimeoutRegistered: true,
            ProcessTreeTerminationRegistered: true,
            ProbeOnlyProtocolRegistered: true,
            MigrationSourcePathInputRegistered: false,
            MigrationSourceReadRegistered: false,
            FileWriteRegistered: false,
            DirectoryMutationRegistered: false,
            MigrationExecutionRegistered: false,
            MigrationEvidenceRegistered: false,
            CurrentPointerMutationRegistered: false,
            ActivationAuthorityRegistered: false,
            OperationalCallerRegistered: false,
            CliCallerRegistered: false,
            AdminCallerRegistered: false,
            BrowserCallerRegistered: false,
            HttpCallerRegistered: false,
            WebSocketCallerRegistered: false,
            HostedServiceCallerRegistered: false,
            TimerCallerRegistered: false,
            AetherRemoteCallerRegistered: false,
            ServiceControlCallerRegistered: false,
            HealthProbeCallerRegistered: false,
            RollbackCallerRegistered: false,
            RadioCallerRegistered: false,
            WatchdogCallerRegistered: false,
            CommandCallerRegistered: false,
            LeaseCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationMigrationRunnerInvocationDiagnostics Snapshot
    {
        get;
    }

    internal async Task<
        VerifiedReleaseActivationMigrationRunnerInvocationReport> InvokeAsync(
        VerifiedReleaseActivationMigrationRunnerSelectionReport selectionReport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectionReport);
        if (!IsEligibleSelectionReport(selectionReport))
        {
            return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                    .RunnerSelectionNotEligible,
                "A successful exact migration-runner selection is required.",
                selectionReport);
        }

        VerifiedReleaseActivationMigrationRunnerSelection? selection =
            selectionReport.Selection;
        if (selection is null)
        {
            return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                    .RunnerSelectionUnavailable,
                "The successful runner-selection report does not retain its exact internal selection.",
                selectionReport);
        }
        if (!MatchesSelectionReport(selectionReport, selection))
        {
            return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                    .RunnerSelectionMismatch,
                "Runner-selection metadata does not match its exact internal selection.",
                selectionReport);
        }

        if (!selection.Plan.MigrationRequired)
        {
            return VerifiedReleaseActivationMigrationRunnerInvocationReport.Success(
                selection,
                runnerInvoked: false,
                artifactRevalidated: false,
                runnerProtocolVersion: null);
        }

        ReleaseMigrationTrustedRunner runner = selection.Runner!;
        if (!RevalidateRunnerArtifact(runner))
        {
            return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                    .RunnerArtifactChanged,
                "The selected migration runner changed after startup validation.",
                selectionReport,
                exactSelectionBound: true,
                runnerProtocolVersion: runner.RunnerProtocolVersion);
        }

        string requestId = Guid.NewGuid().ToString("N");
        string requestJson = JsonSerializer.Serialize(
            new ReleaseMigrationRunnerProbeRequest(
                CurrentProbeProtocolVersion,
                ProbeRequestType,
                requestId,
                selection.Plan.ActivationPlan.SetupRevision,
                selection.Plan.ActivationPlan.InstalledReleaseIdentity,
                selection.Plan.ActivationPlan.TargetReleaseIdentity,
                runner.RunnerIdentity,
                selection.Plan.MigrationIdentity,
                selection.Plan.FromConfigurationSchemaVersion!.Value,
                selection.Plan.ToConfigurationSchemaVersion!.Value,
                MigrationExecutionRequested: false,
                MigrationSourcePathsProvided: false),
            StrictJson);
        if (requestJson.Length is 0 or > MaximumRequestCharacters)
        {
            return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                    .RunnerSelectionMismatch,
                "The exact runner probe request exceeded its bounded protocol envelope.",
                selectionReport,
                exactSelectionBound: true,
                artifactRevalidated: true,
                runnerProtocolVersion: runner.RunnerProtocolVersion);
        }

        ProcessStartInfo startInfo = CreateStartInfo(runner);
        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                    VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                        .RunnerStartFailed,
                    "The exact migration runner process did not start.",
                    selectionReport,
                    exactSelectionBound: true,
                    artifactRevalidated: true,
                    runnerProtocolVersion: runner.RunnerProtocolVersion);
            }
        }
        catch (Exception exception)
            when (exception is Win32Exception or InvalidOperationException or
                IOException or UnauthorizedAccessException or SecurityException)
        {
            return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                    .RunnerStartFailed,
                "The exact migration runner process could not be started directly.",
                selectionReport,
                exactSelectionBound: true,
                artifactRevalidated: true,
                runnerProtocolVersion: runner.RunnerProtocolVersion);
        }

        using CancellationTokenSource operation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(m_timeout);
        Task<string> standardOutput = ReadBoundedAsync(
            process.StandardOutput,
            MaximumStandardOutputCharacters,
            operation.Token);
        Task<string> standardError = ReadBoundedAsync(
            process.StandardError,
            MaximumStandardErrorCharacters,
            operation.Token);
        bool requestSent = false;
        try
        {
            await process.StandardInput.WriteLineAsync(
                requestJson.AsMemory(),
                operation.Token);
            await process.StandardInput.FlushAsync(operation.Token);
            process.StandardInput.Close();
            requestSent = true;

            Task exit = process.WaitForExitAsync(operation.Token);
            Task first = await Task.WhenAny(exit, standardOutput, standardError);
            if (first != exit && first.IsFaulted)
            {
                await TerminateAsync(process);
                await first;
            }
            await exit;
            string output = await standardOutput;
            string errors = await standardError;

            if (process.ExitCode != 0)
            {
                return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                    VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                        .RunnerProcessFailed,
                    "The exact migration runner probe process returned a nonzero exit code.",
                    selectionReport,
                    exactSelectionBound: true,
                    artifactRevalidated: true,
                    probeRequestSent: requestSent,
                    runnerInvoked: true,
                    runnerProtocolVersion: runner.RunnerProtocolVersion);
            }
            if (!string.IsNullOrWhiteSpace(errors))
            {
                return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                    VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                        .RunnerResponseInvalid,
                    "The exact migration runner wrote unexpected standard-error output.",
                    selectionReport,
                    exactSelectionBound: true,
                    artifactRevalidated: true,
                    probeRequestSent: requestSent,
                    runnerInvoked: true,
                    runnerProtocolVersion: runner.RunnerProtocolVersion);
            }

            ReleaseMigrationRunnerProbeResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<
                    ReleaseMigrationRunnerProbeResponse>(output, StrictJson);
            }
            catch (JsonException)
            {
                response = null;
            }
            if (response is null ||
                !MatchesProbeResponse(response, requestId, selection, runner))
            {
                return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                    VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                        .RunnerResponseInvalid,
                    "The exact migration runner returned an invalid or mismatched bounded probe response.",
                    selectionReport,
                    exactSelectionBound: true,
                    artifactRevalidated: true,
                    probeRequestSent: requestSent,
                    runnerInvoked: true,
                    runnerProtocolVersion: runner.RunnerProtocolVersion);
            }
            if (!response.ProbeAccepted)
            {
                return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                    VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                        .RunnerProbeRejected,
                    "The exact migration runner rejected the probe-only request.",
                    selectionReport,
                    exactSelectionBound: true,
                    artifactRevalidated: true,
                    probeRequestSent: requestSent,
                    runnerInvoked: true,
                    runnerProtocolVersion: runner.RunnerProtocolVersion);
            }

            return VerifiedReleaseActivationMigrationRunnerInvocationReport.Success(
                selection,
                runnerInvoked: true,
                artifactRevalidated: true,
                runner.RunnerProtocolVersion);
        }
        catch (InvalidDataException)
        {
            await TerminateAsync(process);
            return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                    .RunnerOutputTooLarge,
                "The exact migration runner exceeded a bounded output channel.",
                selectionReport,
                exactSelectionBound: true,
                artifactRevalidated: true,
                probeRequestSent: requestSent,
                runnerInvoked: true,
                runnerProtocolVersion: runner.RunnerProtocolVersion);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateAsync(process);
            return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                    .RunnerTimedOut,
                "The exact migration runner probe exceeded its hard timeout.",
                selectionReport,
                exactSelectionBound: true,
                artifactRevalidated: true,
                probeRequestSent: requestSent,
                runnerInvoked: true,
                runnerProtocolVersion: runner.RunnerProtocolVersion);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process);
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or ObjectDisposedException or
                InvalidOperationException)
        {
            await TerminateAsync(process);
            return VerifiedReleaseActivationMigrationRunnerInvocationReport.Failure(
                VerifiedReleaseActivationMigrationRunnerInvocationFailureCode
                    .RunnerProcessFailed,
                "The exact migration runner probe process failed before a valid response.",
                selectionReport,
                exactSelectionBound: true,
                artifactRevalidated: true,
                probeRequestSent: requestSent,
                runnerInvoked: true,
                runnerProtocolVersion: runner.RunnerProtocolVersion);
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        ReleaseMigrationTrustedRunner runner)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = runner.RunnerPath,
            WorkingDirectory = Path.GetDirectoryName(runner.RunnerPath) ??
                throw new InvalidOperationException(
                    "The exact migration runner has no working directory."),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment.Clear();
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["AETHERSDR_MIGRATION_RUNNER_PROTOCOL"] =
            CurrentProbeProtocolVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        return startInfo;
    }

    private static bool IsEligibleSelectionReport(
        VerifiedReleaseActivationMigrationRunnerSelectionReport report) =>
        report.Succeeded &&
        report.FailureCode ==
            VerifiedReleaseActivationMigrationRunnerSelectionFailureCode.None &&
        report.SetupRevision is > 0 &&
        !string.IsNullOrEmpty(report.InstalledReleaseIdentity) &&
        !string.IsNullOrEmpty(report.TargetReleaseIdentity) &&
        report.MigrationKind is ReleaseMigrationKind.None or
            ReleaseMigrationKind.Required &&
        report.ExactMigrationPlanBound &&
        !report.MigrationSourceReadPerformed &&
        !report.RunnerInvoked &&
        !report.MigrationExecutionPerformed &&
        !report.CurrentPointerChanged &&
        !report.ActivationAuthorized;

    private static bool MatchesSelectionReport(
        VerifiedReleaseActivationMigrationRunnerSelectionReport report,
        VerifiedReleaseActivationMigrationRunnerSelection selection)
    {
        VerifiedReleaseActivationMigrationPlan plan = selection.Plan;
        if (report.SetupRevision != plan.ActivationPlan.SetupRevision ||
            !string.Equals(
                report.InstalledReleaseIdentity,
                plan.ActivationPlan.InstalledReleaseIdentity,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.TargetReleaseIdentity,
                plan.ActivationPlan.TargetReleaseIdentity,
                StringComparison.Ordinal) ||
            report.MigrationKind != plan.MigrationKind ||
            report.FromConfigurationSchemaVersion !=
                plan.FromConfigurationSchemaVersion ||
            report.ToConfigurationSchemaVersion !=
                plan.ToConfigurationSchemaVersion ||
            report.MigrationRequired != plan.MigrationRequired ||
            report.NoOpMigrationResolved == plan.MigrationRequired ||
            report.MigrationRunnerRequired != plan.MigrationRequired ||
            report.MigrationReady != !plan.MigrationRequired)
        {
            return false;
        }

        if (!plan.MigrationRequired)
        {
            return !report.MigrationRunnerSelected &&
                !report.RunnerArtifactValidatedAtStartup &&
                report.RunnerProtocolVersion is null &&
                selection.Runner is null &&
                selection.Mapping is null;
        }

        ReleaseMigrationTrustedRunner? runner = selection.Runner;
        ReleaseMigrationRunnerMapping? mapping = selection.Mapping;
        return report.RunnerTrustEnabled &&
            report.MigrationRunnerSelected &&
            report.RunnerArtifactValidatedAtStartup &&
            report.RunnerProtocolVersion ==
                ReleaseMigrationRunnerTrustRegistry.CurrentRunnerProtocolVersion &&
            runner is not null &&
            mapping is not null &&
            runner.RunnerProtocolVersion == report.RunnerProtocolVersion &&
            runner.RunnerLength is > 0 and <=
                ReleaseMigrationRunnerTrustRegistry.MaximumRunnerFileBytes &&
            runner.Sha256.Count == 32 &&
            runner.LastWriteTimeUtc != default &&
            string.Equals(
                mapping.MigrationIdentity,
                plan.MigrationIdentity,
                StringComparison.Ordinal) &&
            mapping.FromConfigurationSchemaVersion ==
                plan.FromConfigurationSchemaVersion &&
            mapping.ToConfigurationSchemaVersion ==
                plan.ToConfigurationSchemaVersion;
    }

    internal static bool RevalidateRunnerArtifact(
        ReleaseMigrationTrustedRunner runner)
    {
        byte[]? digest = null;
        try
        {
            string path = runner.RunnerPath;
            if (!Path.IsPathFullyQualified(path) ||
                !string.Equals(
                    Path.GetFullPath(path),
                    path,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                return false;
            }

            ValidateContainingDirectory(path);
            FileInfo before = new(path);
            before.Refresh();
            if (!ValidateRunnerFile(before) ||
                before.Length != runner.RunnerLength ||
                before.LastWriteTimeUtc != runner.LastWriteTimeUtc)
            {
                return false;
            }
            UnixFileMode? beforeMode = OperatingSystem.IsLinux()
                ? File.GetUnixFileMode(path)
                : null;

            using (FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                FileOptions.SequentialScan))
            {
                if (stream.Length != runner.RunnerLength)
                {
                    return false;
                }
                digest = SHA256.HashData(stream);
            }

            ValidateContainingDirectory(path);
            FileInfo after = new(path);
            after.Refresh();
            UnixFileMode? afterMode = OperatingSystem.IsLinux()
                ? File.GetUnixFileMode(path)
                : null;
            if (!ValidateRunnerFile(after) ||
                after.Length != runner.RunnerLength ||
                after.LastWriteTimeUtc != runner.LastWriteTimeUtc ||
                afterMode != beforeMode ||
                runner.Sha256.Count != digest.Length)
            {
                return false;
            }

            return CryptographicOperations.FixedTimeEquals(
                digest,
                runner.Sha256.ToArray());
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or NotSupportedException or
                PathTooLongException or CryptographicException or
                InvalidOperationException)
        {
            return false;
        }
        finally
        {
            if (digest is not null)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }

    private static bool ValidateRunnerFile(FileInfo file)
    {
        if (!file.Exists ||
            (file.Attributes & FileAttributes.Directory) != 0 ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.LinkTarget is not null ||
            file.Length is < 1 or >
                ReleaseMigrationRunnerTrustRegistry.MaximumRunnerFileBytes)
        {
            return false;
        }
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        UnixFileMode mode = File.GetUnixFileMode(file.FullName);
        return (mode & UnixFileMode.UserRead) != 0 &&
            (mode & UnixFileMode.UserExecute) != 0 &&
            (mode & AnyWritableUnixModes) == 0;
    }

    private static void ValidateContainingDirectory(string path)
    {
        DirectoryInfo? directory = new FileInfo(path).Directory;
        if (directory is null)
        {
            throw new InvalidOperationException(
                "The exact migration runner has no containing directory.");
        }
        directory.Refresh();
        if (!directory.Exists ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                "The exact migration runner containing directory changed.");
        }
        if (OperatingSystem.IsLinux() &&
            (File.GetUnixFileMode(directory.FullName) &
                ForbiddenSharedWritableUnixModes) != 0)
        {
            throw new InvalidOperationException(
                "The exact migration runner containing directory became shared-writable.");
        }
    }

    private static bool MatchesProbeResponse(
        ReleaseMigrationRunnerProbeResponse response,
        string requestId,
        VerifiedReleaseActivationMigrationRunnerSelection selection,
        ReleaseMigrationTrustedRunner runner) =>
        response.ProtocolVersion == CurrentProbeProtocolVersion &&
        string.Equals(response.Type, ProbeResponseType, StringComparison.Ordinal) &&
        string.Equals(response.RequestId, requestId, StringComparison.Ordinal) &&
        string.Equals(
            response.RunnerIdentity,
            runner.RunnerIdentity,
            StringComparison.Ordinal) &&
        string.Equals(
            response.MigrationIdentity,
            selection.Plan.MigrationIdentity,
            StringComparison.Ordinal) &&
        response.FromConfigurationSchemaVersion ==
            selection.Plan.FromConfigurationSchemaVersion &&
        response.ToConfigurationSchemaVersion ==
            selection.Plan.ToConfigurationSchemaVersion &&
        !response.MigrationExecutionPerformed &&
        !response.FilesystemMutationPerformed &&
        !response.MigrationSourcePathsReceived;

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[1024];
        StringBuilder output = new();
        while (true)
        {
            int read = await reader.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);
            if (read == 0)
            {
                return output.ToString();
            }
            if (output.Length > maximumCharacters - read)
            {
                throw new InvalidDataException(
                    "The migration runner output exceeded its bound.");
            }
            _ = output.Append(buffer, 0, read);
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or Win32Exception or
                NotSupportedException)
        {
        }

        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or TimeoutException or
                Win32Exception)
        {
        }
    }
}
