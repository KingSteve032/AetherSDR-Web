using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Releases;

public sealed class ReleaseActivationHostRestartSettings
{
    public const string SectionName = "ReleaseActivationHostRestart";

    public bool ExecutionEnabled { get; init; }
}

public enum VerifiedReleaseActivationHostRestartFailureCode
{
    None = 0,
    ExecutionDisabled = 1,
    UnsupportedPlatform = 2,
    PlanNotEligible = 3,
    PointerEvidenceUnavailable = 4,
    MarkerWriteFailed = 5,
    RestartProcessUnavailable = 6,
    RestartRequestRejected = 7,
    RestartOutcomeUnknown = 8
}

public sealed record VerifiedReleaseActivationHostRestartReport(
    bool Succeeded,
    VerifiedReleaseActivationHostRestartFailureCode FailureCode,
    string Message,
    long? SetupRevision,
    string InstalledReleaseIdentity,
    string TargetReleaseIdentity,
    bool ExactPlanBound,
    bool ExactPointerEvidenceBound,
    bool DurableRestartMarkerWritten,
    bool DirectProcessStarted,
    bool ShellUsed,
    bool HostRestartRequested,
    bool PostBootVerificationRequired,
    bool CurrentPointerChanged,
    bool ActivationAuthorized,
    bool RadioCommandIssued,
    bool TxActionPerformed);

public sealed record VerifiedReleaseActivationHostRestartDiagnostics(
    bool Registered,
    bool ExecutionEnabled,
    bool ExactHostRestartPlanInputRegistered,
    bool ExactPointerEvidenceInputRegistered,
    bool DurablePreRestartMarkerRegistered,
    bool DirectSystemctlRegistered,
    bool ShellRegistered,
    bool ArbitraryCommandRegistered,
    bool PostBootVerificationRequired,
    bool RadioCallerRegistered,
    bool CommandCallerRegistered,
    bool TxCallerRegistered);

/// <summary>
/// Fixed host-restart transport for a signed activation plan that explicitly
/// supersedes every individual service restart. It first writes one owner-only
/// durable continuation marker, then invokes only /usr/bin/systemctl reboot
/// through a direct argument list. It does not perform post-boot health itself,
/// grant activation authority, operate a radio, alter a lease, or transmit.
/// </summary>
public sealed class VerifiedReleaseActivationHostRestartTransport
{
    private const string SystemctlPath = "/usr/bin/systemctl";
    private readonly ReleaseActivationHostRestartSettings m_settings;
    private readonly HostRestartContinuationPaths m_storage;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> m_runner;
    private readonly TimeProvider m_timeProvider;

    public VerifiedReleaseActivationHostRestartTransport(
        IOptions<ReleaseActivationHostRestartSettings> settings,
        InstallationPaths paths)
        : this(
            settings?.Value ?? throw new ArgumentNullException(nameof(settings)),
            paths,
            RunProcessAsync,
            TimeProvider.System)
    {
    }

    internal VerifiedReleaseActivationHostRestartTransport(
        ReleaseActivationHostRestartSettings settings,
        InstallationPaths paths,
        Func<ProcessStartInfo, CancellationToken, Task<int>> runner,
        TimeProvider timeProvider)
    {
        m_settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(paths);
        InstallationPaths.Validate(paths);
        m_runner = runner ?? throw new ArgumentNullException(nameof(runner));
        m_timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        m_storage = HostRestartContinuationStorage.Resolve(paths);

        Snapshot = new VerifiedReleaseActivationHostRestartDiagnostics(
            Registered: true,
            m_settings.ExecutionEnabled,
            ExactHostRestartPlanInputRegistered: true,
            ExactPointerEvidenceInputRegistered: true,
            DurablePreRestartMarkerRegistered: true,
            DirectSystemctlRegistered: true,
            ShellRegistered: false,
            ArbitraryCommandRegistered: false,
            PostBootVerificationRequired: true,
            RadioCallerRegistered: false,
            CommandCallerRegistered: false,
            TxCallerRegistered: false);
    }

    public VerifiedReleaseActivationHostRestartDiagnostics Snapshot { get; }

    [SupportedOSPlatform("linux")]
    internal async Task<VerifiedReleaseActivationHostRestartReport> RequestAsync(
        string transactionId,
        VerifiedReleaseActivationServiceControlPlanReport serviceControl,
        VerifiedReleaseActivationCurrentPointerSwitchReport pointer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceControl);
        ArgumentNullException.ThrowIfNull(pointer);
        if (transactionId is not { Length: 32 } ||
            transactionId.Any(character =>
                character is not (>= '0' and <= '9') and
                    not (>= 'a' and <= 'f')))
        {
            return Failure(
                VerifiedReleaseActivationHostRestartFailureCode.PlanNotEligible,
                "One exact release transaction identity is required.",
                serviceControl);
        }
        if (!m_settings.ExecutionEnabled)
        {
            return Failure(
                VerifiedReleaseActivationHostRestartFailureCode.ExecutionDisabled,
                "Host-restart execution is disabled.",
                serviceControl);
        }
        if (!OperatingSystem.IsLinux())
        {
            return Failure(
                VerifiedReleaseActivationHostRestartFailureCode.UnsupportedPlatform,
                "Host restart requires Linux.",
                serviceControl);
        }
        VerifiedReleaseActivationServiceControlPlan? plan = serviceControl.Plan;
        if (plan is null ||
            !serviceControl.Succeeded ||
            serviceControl.FailureCode !=
                VerifiedReleaseActivationServiceControlPlanFailureCode.None ||
            !plan.HostRestartRequired ||
            plan.StopActions.Count != 0 ||
            plan.StartActions.Count != 0 ||
            plan.HostRestartActions.Count != 1 ||
            !string.Equals(
                plan.HostRestartActions[0].UnitIdentity,
                VerifiedReleaseActivationServiceControlPlanComposer
                    .HostRestartIdentity,
                StringComparison.Ordinal))
        {
            return Failure(
                VerifiedReleaseActivationHostRestartFailureCode.PlanNotEligible,
                "One exact signed host-restart plan is required.",
                serviceControl);
        }
        if (!VerifiedReleaseActivationCurrentPointerSwitchService
                .ValidateEvidenceReport(pointer, plan))
        {
            return Failure(
                VerifiedReleaseActivationHostRestartFailureCode
                    .PointerEvidenceUnavailable,
                "Exact completed current-pointer switch evidence is required before host restart.",
                serviceControl,
                exactPlanBound: true);
        }

        try
        {
            await WriteMarkerAsync(transactionId, plan, cancellationToken);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                JsonException or InvalidOperationException or NotSupportedException)
        {
            return Failure(
                VerifiedReleaseActivationHostRestartFailureCode.MarkerWriteFailed,
                "The durable host-restart continuation marker could not be written.",
                serviceControl,
                exactPlanBound: true,
                pointerBound: true);
        }

        ProcessStartInfo start = new()
        {
            FileName = SystemctlPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        start.ArgumentList.Add("--no-ask-password");
        start.ArgumentList.Add("--no-pager");
        start.ArgumentList.Add("--no-block");
        start.ArgumentList.Add("reboot");
        try
        {
            int exitCode = await m_runner(start, cancellationToken);
            if (exitCode != 0)
            {
                return Failure(
                    VerifiedReleaseActivationHostRestartFailureCode
                        .RestartRequestRejected,
                    "systemd rejected the fixed host-restart request.",
                    serviceControl,
                    exactPlanBound: true,
                    pointerBound: true,
                    markerWritten: true,
                    processStarted: true);
            }
            return new VerifiedReleaseActivationHostRestartReport(
                true,
                VerifiedReleaseActivationHostRestartFailureCode.None,
                "The fixed host restart was requested; post-boot health verification must consume the durable marker before activation is considered complete.",
                plan.ActivationPlan.SetupRevision,
                plan.ActivationPlan.InstalledReleaseIdentity,
                plan.ActivationPlan.TargetReleaseIdentity,
                ExactPlanBound: true,
                ExactPointerEvidenceBound: true,
                DurableRestartMarkerWritten: true,
                DirectProcessStarted: true,
                ShellUsed: false,
                HostRestartRequested: true,
                PostBootVerificationRequired: true,
                CurrentPointerChanged: true,
                ActivationAuthorized: false,
                RadioCommandIssued: false,
                TxActionPerformed: false);
        }
        catch (Exception exception)
            when (exception is IOException or InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException or
                OperationCanceledException)
        {
            return Failure(
                VerifiedReleaseActivationHostRestartFailureCode
                    .RestartOutcomeUnknown,
                "The fixed host-restart request outcome is unknown and requires reconciliation.",
                serviceControl,
                exactPlanBound: true,
                pointerBound: true,
                markerWritten: true,
                processStarted: true);
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task WriteMarkerAsync(
        string transactionId,
        VerifiedReleaseActivationServiceControlPlan plan,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(m_storage.Root);
        ValidateStorageRoot();
        File.SetUnixFileMode(
            m_storage.Root,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        if (PathEntryExists(m_storage.Marker))
        {
            throw new InvalidOperationException(
                "An unconsumed host-restart continuation marker already exists.");
        }
        DeletePriorTerminalResultIfSafe();
        string temporary = Path.Combine(
            m_storage.Root,
            $".{HostRestartContinuationStorage.MarkerFileName}.{Guid.NewGuid():N}");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new HostRestartMarker(
                HostRestartContinuationStorage.MarkerSchemaVersion,
                transactionId,
                plan.ActivationPlan.SetupRevision,
                plan.ActivationPlan.InstalledReleaseIdentity,
                plan.ActivationPlan.TargetReleaseIdentity,
                plan.ActivationPlan.ReleaseRootPath,
                plan.ActivationPlan.CurrentPointerPath,
                plan.ActivationPlan.UpdateChannel,
                plan.ActivationPlan.PinnedReleaseIdentity,
                plan.ActivationPlan.InstallTransmitSupport,
                m_timeProvider.GetUtcNow(),
                PostBootVerificationRequired: true),
            HostRestartContinuationStorage.JsonOptions);
        try
        {
            await using FileStream stream = new(
                temporary,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough
                });
            File.SetUnixFileMode(
                temporary,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
            File.Move(temporary, m_storage.Marker, overwrite: false);
            File.SetUnixFileMode(m_storage.Marker, UnixFileMode.UserRead);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                }
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private void ValidateStorageRoot()
    {
        DirectoryInfo root = new(m_storage.Root);
        root.Refresh();
        if (!root.Exists ||
            root.LinkTarget is not null ||
            (root.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The host-restart continuation directory is unsafe.");
        }
    }

    [SupportedOSPlatform("linux")]
    private void DeletePriorTerminalResultIfSafe()
    {
        if (!PathEntryExists(m_storage.Result))
        {
            return;
        }
        FileInfo file = new(m_storage.Result);
        file.Refresh();
        if (!file.Exists ||
            file.LinkTarget is not null ||
            (file.Attributes &
                (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            file.Length is < 2 or >
                HostRestartContinuationStorage.MaximumDocumentBytes ||
            File.GetUnixFileMode(m_storage.Result) != UnixFileMode.UserRead)
        {
            throw new InvalidOperationException(
                "The prior host-restart continuation result is unsafe.");
        }
        File.SetUnixFileMode(
            m_storage.Result,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Delete(m_storage.Result);
    }

    private static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static VerifiedReleaseActivationHostRestartReport Failure(
        VerifiedReleaseActivationHostRestartFailureCode code,
        string message,
        VerifiedReleaseActivationServiceControlPlanReport report,
        bool exactPlanBound = false,
        bool pointerBound = false,
        bool markerWritten = false,
        bool processStarted = false) =>
        new(
            false,
            code,
            message,
            report.SetupRevision,
            report.InstalledReleaseIdentity,
            report.TargetReleaseIdentity,
            exactPlanBound,
            pointerBound,
            markerWritten,
            processStarted,
            ShellUsed: false,
            HostRestartRequested: false,
            PostBootVerificationRequired: true,
            CurrentPointerChanged: pointerBound,
            ActivationAuthorized: false,
            RadioCommandIssued: false,
            TxActionPerformed: false);

    private static async Task<int> RunProcessAsync(
        ProcessStartInfo start,
        CancellationToken cancellationToken)
    {
        using Process process = new() { StartInfo = start };
        if (!process.Start())
        {
            throw new IOException("systemctl did not start.");
        }
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        _ = await stdout;
        _ = await stderr;
        return process.ExitCode;
    }
}
