using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AetherSDR.Web.Setup;

internal enum InstallationInstallerUbuntuPrimitiveInspectionOutcome
{
    Converged = 1,
    Missing = 2,
    Drift = 3,
    Rejected = 4,
    Unknown = 5
}

internal sealed record InstallationInstallerUbuntuPrimitiveInspection(
    InstallationInstallerUbuntuPrimitiveInspectionOutcome Outcome,
    string Code,
    string Summary);

internal sealed record InstallationInstallerUbuntuDirectProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal delegate Task<InstallationInstallerUbuntuDirectProcessResult>
    InstallationInstallerUbuntuDirectProcessRunner(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken);

internal interface IInstallationInstallerUbuntuPrimitiveInspector
{
    Task<InstallationInstallerUbuntuPrimitiveInspection> InspectAsync(
        InstallationInstallerUbuntuMutationRequest request,
        InstallationInstallerUbuntuPrimitiveOperation operation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes only the fixed absolute Ubuntu primitives emitted by
/// <see cref="InstallationInstallerUbuntuPrimitivePlanner"/>. Complete-plan
/// preflight rejects every unsupported typed operation before any process can
/// start. Processes use direct argument lists with an empty environment; no
/// shell, command string, PATH lookup, or arbitrary executable input exists.
/// </summary>
public sealed class LocalInstallationInstallerUbuntuMutationPrimitives :
    IInstallationInstallerUbuntuMutationPrimitives,
    IInstallationInstallerUbuntuPlanInspector,
    IInstallationInstallerUbuntuMutationRollback
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(2);

    private static readonly IReadOnlySet<
        InstallationInstallerUbuntuPrimitiveKind> ImplementedKinds =
        new HashSet<InstallationInstallerUbuntuPrimitiveKind>
        {
            InstallationInstallerUbuntuPrimitiveKind.EnsureSystemUser,
            InstallationInstallerUbuntuPrimitiveKind.EnsureDirectory,
            InstallationInstallerUbuntuPrimitiveKind.InstallSystemdUnit,
            InstallationInstallerUbuntuPrimitiveKind.ReloadSystemd,
            InstallationInstallerUbuntuPrimitiveKind.ActivateSystemdUnit
        };

    private readonly IInstallationInstallerUbuntuPrimitiveInspector m_inspector;
    private readonly InstallationInstallerUbuntuDirectProcessRunner m_runner;
    private readonly Func<bool> m_isRoot;
    private readonly IInstallationInstallerUbuntuManagedPrimitiveHandler m_managed;

    public LocalInstallationInstallerUbuntuMutationPrimitives()
        : this(
            new LocalInstallationInstallerUbuntuPrimitiveInspector(
                RunDirectProcessAsync),
            RunDirectProcessAsync,
            IsEffectiveRoot)
    {
    }

    internal LocalInstallationInstallerUbuntuMutationPrimitives(
        IInstallationInstallerUbuntuPrimitiveInspector inspector,
        InstallationInstallerUbuntuDirectProcessRunner runner,
        Func<bool> isRoot,
        IInstallationInstallerUbuntuManagedPrimitiveHandler? managed = null)
    {
        m_inspector = inspector ??
            throw new ArgumentNullException(nameof(inspector));
        m_runner = runner ?? throw new ArgumentNullException(nameof(runner));
        m_isRoot = isRoot ?? throw new ArgumentNullException(nameof(isRoot));
        m_managed = managed ??
            new LocalInstallationInstallerUbuntuManagedPrimitiveHandler(runner);
    }

    public async Task<InstallationInstallerHostInspectionResult>
        InspectPlanAsync(
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            return InstallationInstallerHostInspectionResult.Unknown(
                "ubuntu-platform-unsupported",
                "Exact Ubuntu installer plan inspection requires Linux.");
        }
        if (!m_isRoot())
        {
            return InstallationInstallerHostInspectionResult.Unknown(
                "ubuntu-root-required",
                "Exact Ubuntu installer plan inspection requires effective root.");
        }

        InstallationInstallerArchitecture hostArchitecture =
            RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 =>
                    InstallationInstallerArchitecture.LinuxX64,
                Architecture.Arm64 =>
                    InstallationInstallerArchitecture.LinuxArm64,
                _ => 0
            };
        if (hostArchitecture != request.Architecture)
        {
            return InstallationInstallerHostInspectionResult.Drift(
                "architecture-drift",
                "The Ubuntu host architecture does not match the exact installer plan.");
        }

        IReadOnlyList<InstallationInstallerUbuntuPrimitiveOperation> operations =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request);
        foreach (InstallationInstallerUbuntuPrimitiveOperation operation in
            operations.Where(operation =>
                operation.Kind !=
                    InstallationInstallerUbuntuPrimitiveKind.ReloadSystemd))
        {
            if (!ImplementedKinds.Contains(operation.Kind) &&
                !m_managed.Supports(operation.Kind))
            {
                return InstallationInstallerHostInspectionResult.Unknown(
                    "ubuntu-inspection-unimplemented",
                    "The exact installer plan contains an unimplemented inspection primitive.");
            }

            InstallationInstallerUbuntuPrimitiveInspection inspection =
                m_managed.Supports(operation.Kind)
                    ? await m_managed.InspectAsync(
                        request,
                        operation,
                        cancellationToken)
                    : await m_inspector.InspectAsync(
                        request,
                        operation,
                        cancellationToken);
            if (inspection.Outcome ==
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged)
            {
                continue;
            }
            if (inspection.Outcome ==
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Unknown)
            {
                return InstallationInstallerHostInspectionResult.Unknown(
                    inspection.Code,
                    inspection.Summary);
            }
            return InstallationInstallerHostInspectionResult.Drift(
                inspection.Code,
                inspection.Summary);
        }

        return InstallationInstallerHostInspectionResult.Converged(
            "ubuntu-host-converged",
            "The exact Ubuntu installer plan is fully converged.");
    }

    public async Task<InstallationInstallerUbuntuStepResult> PrepareAsync(
        InstallationInstallerUbuntuMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-platform-unsupported",
                "The fixed Ubuntu installer primitives require Linux.");
        }
        if (!m_isRoot())
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-root-required",
                "The fixed Ubuntu installer primitives require effective root.");
        }
        if (request.VerifiedStaging is null &&
            request.VerifiedInstallationPlan is null)
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-verified-release-unavailable",
                "Ubuntu mutation requires one exact retained verified release binding.");
        }

        IReadOnlyList<InstallationInstallerUbuntuPrimitiveOperation> operations =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request);
        InstallationInstallerUbuntuPrimitiveOperation? unsupported =
            operations.FirstOrDefault(operation =>
                !ImplementedKinds.Contains(operation.Kind) &&
                !m_managed.Supports(operation.Kind));
        if (unsupported is not null)
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-primitive-unimplemented",
                "The exact installer plan contains a typed Ubuntu primitive that is not implemented.");
        }

        foreach (string executable in operations
            .Select(operation => operation.Executable)
            .Where(executable => executable.Length > 0)
            .Append(LocalInstallationInstallerUbuntuPrimitiveInspector
                .StatExecutable)
            .Distinct(StringComparer.Ordinal))
        {
            if (!SafeExecutable(executable))
            {
                return InstallationInstallerUbuntuStepResult.Rejected(
                    "ubuntu-executable-unsafe",
                    "A fixed Ubuntu primitive executable is unavailable or unsafe.");
            }
        }

        foreach (InstallationInstallerUbuntuPrimitiveOperation operation in
            operations.Where(operation =>
                operation.Kind !=
                    InstallationInstallerUbuntuPrimitiveKind.ReloadSystemd))
        {
            InstallationInstallerUbuntuPrimitiveInspection inspection =
                m_managed.Supports(operation.Kind)
                    ? await m_managed.InspectAsync(
                        request,
                        operation,
                        cancellationToken)
                    : await m_inspector.InspectAsync(
                        request,
                        operation,
                        cancellationToken);
            InstallationInstallerUbuntuStepResult? failure =
                TranslatePreflightInspection(
                    request,
                    operation,
                    inspection);
            if (failure is not null)
            {
                return failure;
            }
        }

        return InstallationInstallerUbuntuStepResult.Converged(
            "ubuntu-primitives-prepared",
            "The exact fixed Ubuntu primitive inventory passed read-only preflight.");
    }

    public async Task<InstallationInstallerUbuntuStepResult> ExecuteAsync(
        InstallationInstallerUbuntuMutationRequest request,
        InstallationInstallerPlanAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !m_isRoot())
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-execution-ineligible",
                "The fixed Ubuntu primitive is not eligible for execution.");
        }

        IReadOnlyList<InstallationInstallerUbuntuPrimitiveOperation> operations =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request);
        if (action.Order < 1 ||
            action.Order > request.Actions.Count ||
            action != request.Actions[action.Order - 1])
        {
            throw new InvalidOperationException(
                "The Ubuntu primitive action is not part of the exact request.");
        }
        InstallationInstallerUbuntuPrimitiveOperation operation =
            operations[action.Order - 1];
        if (m_managed.Supports(operation.Kind))
        {
            return await m_managed.ExecuteAsync(
                request,
                operation,
                cancellationToken);
        }
        if (!ImplementedKinds.Contains(operation.Kind) ||
            operation.Executable.Length == 0 ||
            !SafeExecutable(operation.Executable) ||
            !SafeExecutable(
                LocalInstallationInstallerUbuntuPrimitiveInspector
                    .StatExecutable))
        {
            return InstallationInstallerUbuntuStepResult.Rejected(
                "ubuntu-primitive-unavailable",
                "The fixed Ubuntu primitive is unavailable for execution.");
        }

        if (operation.Kind !=
            InstallationInstallerUbuntuPrimitiveKind.ReloadSystemd)
        {
            InstallationInstallerUbuntuPrimitiveInspection before =
                await m_inspector.InspectAsync(
                    request,
                    operation,
                    cancellationToken);
            if (before.Outcome ==
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged)
            {
                return InstallationInstallerUbuntuStepResult.Converged();
            }
            InstallationInstallerUbuntuStepResult? failure =
                TranslateExecutionInspection(
                    request,
                    operation,
                    before);
            if (failure is not null)
            {
                return failure;
            }
        }

        InstallationInstallerUbuntuDirectProcessResult process;
        try
        {
            process = await m_runner(
                CreateStartInfo(operation.Executable, operation.Arguments),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-process-outcome-unknown",
                "The fixed Ubuntu primitive process outcome is unknown.");
        }
        if (process.ExitCode != 0)
        {
            return InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-process-rejected-unknown",
                "The fixed Ubuntu primitive returned nonzero after execution began.");
        }

        if (operation.Kind ==
            InstallationInstallerUbuntuPrimitiveKind.ReloadSystemd)
        {
            return InstallationInstallerUbuntuStepResult.Applied(
                "ubuntu-systemd-reloaded",
                "The fixed systemd manager reload completed.");
        }

        InstallationInstallerUbuntuPrimitiveInspection after =
            await m_inspector.InspectAsync(
                request,
                operation,
                cancellationToken);
        return after.Outcome ==
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged
            ? InstallationInstallerUbuntuStepResult.Applied()
            : InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-postcondition-unknown",
                "The fixed Ubuntu primitive completed without an exact converged postcondition.");
    }

    public async Task<InstallationInstallerUbuntuStepResult> RollbackAsync(
        InstallationInstallerUbuntuMutationRequest request,
        IReadOnlyList<InstallationInstallerPlanAction> rollbackCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rollbackCandidates);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || !m_isRoot())
        {
            return InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-rollback-ineligible",
                "The bounded Ubuntu activation rollback is not eligible for execution.");
        }

        IReadOnlyList<InstallationInstallerUbuntuPrimitiveOperation> operations =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request);
        bool changed = false;
        foreach (InstallationInstallerPlanAction action in
            rollbackCandidates.Reverse())
        {
            if (action.Order < 1 ||
                action.Order > request.Actions.Count ||
                action != request.Actions[action.Order - 1])
            {
                throw new InvalidOperationException(
                    "An Ubuntu rollback candidate is not part of the exact request.");
            }

            InstallationInstallerUbuntuPrimitiveOperation operation =
                operations[action.Order - 1];
            InstallationInstallerUbuntuStepResult result =
                operation.Kind switch
                {
                    InstallationInstallerUbuntuPrimitiveKind
                        .ActivateSystemdUnit =>
                        await RollbackActiveUnitAsync(
                            operation,
                            cancellationToken),
                    InstallationInstallerUbuntuPrimitiveKind
                        .ActivateInitialRelease =>
                        await m_managed.RollbackInitialReleaseAsync(
                            request,
                            cancellationToken),
                    _ => throw new InvalidOperationException(
                        "Only exact initial activation actions can be rolled back.")
                };
            if (result.Outcome is
                InstallationInstallerUbuntuStepOutcome.Rejected or
                InstallationInstallerUbuntuStepOutcome.Unknown)
            {
                return InstallationInstallerUbuntuStepResult.Unknown(
                    "ubuntu-rollback-step-unknown",
                    "A bounded Ubuntu activation rollback step requires reconciliation.");
            }
            changed |= result.Outcome ==
                InstallationInstallerUbuntuStepOutcome.Applied;
        }

        return changed
            ? InstallationInstallerUbuntuStepResult.Applied(
                "ubuntu-activation-rolled-back",
                "The transaction-applied initial runtime activation was rolled back.")
            : InstallationInstallerUbuntuStepResult.Converged(
                "ubuntu-activation-rollback-converged",
                "The transaction-applied initial runtime activation was already absent.");
    }

    private async Task<InstallationInstallerUbuntuStepResult>
        RollbackActiveUnitAsync(
            InstallationInstallerUbuntuPrimitiveOperation operation,
            CancellationToken cancellationToken)
    {
        if (operation.Kind !=
                InstallationInstallerUbuntuPrimitiveKind.ActivateSystemdUnit ||
            !string.Equals(
                operation.Executable,
                "/usr/bin/systemctl",
                StringComparison.Ordinal) ||
            !SafeExecutable(operation.Executable))
        {
            return InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-service-rollback-unavailable",
                "The fixed systemd activation rollback is unavailable.");
        }

        InstallationInstallerUbuntuPrimitiveInspection before =
            await InspectRollbackUnitAsync(operation.Target, cancellationToken);
        if (before.Outcome ==
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged)
        {
            return InstallationInstallerUbuntuStepResult.Converged(
                "ubuntu-service-rollback-converged",
                "The transaction-applied service activation is already disabled and inactive.");
        }
        if (before.Outcome !=
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing)
        {
            return InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-service-rollback-inspection-unknown",
                "The transaction-applied service activation could not be classified before rollback.");
        }

        InstallationInstallerUbuntuDirectProcessResult process;
        try
        {
            process = await m_runner(
                CreateStartInfo(
                    "/usr/bin/systemctl",
                    ["disable", "--now", "--", operation.Target]),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-service-rollback-process-unknown",
                "The fixed systemd activation rollback process outcome is unknown.");
        }
        if (process.ExitCode != 0)
        {
            return InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-service-rollback-process-unknown",
                "The fixed systemd activation rollback returned nonzero.");
        }

        InstallationInstallerUbuntuPrimitiveInspection after =
            await InspectRollbackUnitAsync(operation.Target, cancellationToken);
        return after.Outcome ==
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged
            ? InstallationInstallerUbuntuStepResult.Applied(
                "ubuntu-service-rolled-back",
                "The transaction-applied service activation is disabled and inactive.")
            : InstallationInstallerUbuntuStepResult.Unknown(
                "ubuntu-service-rollback-postcondition-unknown",
                "The fixed systemd activation rollback did not reach its exact postcondition.");
    }

    private async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectRollbackUnitAsync(
            string unit,
            CancellationToken cancellationToken)
    {
        InstallationInstallerUbuntuDirectProcessResult enabled;
        InstallationInstallerUbuntuDirectProcessResult active;
        try
        {
            enabled = await m_runner(
                CreateStartInfo(
                    "/usr/bin/systemctl",
                    ["is-enabled", "--quiet", "--", unit]),
                cancellationToken);
            active = await m_runner(
                CreateStartInfo(
                    "/usr/bin/systemctl",
                    ["is-active", "--quiet", "--", unit]),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Unknown,
                "ubuntu-service-rollback-inspection-unknown",
                "The fixed systemd rollback state could not be inspected.");
        }

        if (enabled.ExitCode == 1 && active.ExitCode == 3)
        {
            return new(
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged,
                "ubuntu-service-rollback-converged",
                "The fixed systemd unit is disabled and inactive.");
        }
        if (enabled.ExitCode is 0 or 1 &&
            active.ExitCode is 0 or 3)
        {
            return new(
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing,
                "ubuntu-service-rollback-required",
                "The fixed systemd unit still has runtime activation.");
        }
        return new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Unknown,
            "ubuntu-service-rollback-inspection-unknown",
            "The fixed systemd rollback state could not be classified.");
    }

    private static InstallationInstallerUbuntuStepResult?
        TranslatePreflightInspection(
            InstallationInstallerUbuntuMutationRequest request,
            InstallationInstallerUbuntuPrimitiveOperation operation,
            InstallationInstallerUbuntuPrimitiveInspection inspection)
    {
        return inspection.Outcome switch
        {
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged =>
                null,
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing =>
                null,
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Drift
                when Repairable(operation.Kind) &&
                    (request.Repair || ConvergesOnApply(operation.Kind)) =>
                null,
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Drift =>
                InstallationInstallerUbuntuStepResult.Rejected(
                    inspection.Code,
                    inspection.Summary),
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Rejected =>
                InstallationInstallerUbuntuStepResult.Rejected(
                    inspection.Code,
                    inspection.Summary),
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Unknown =>
                InstallationInstallerUbuntuStepResult.Unknown(
                    inspection.Code,
                    inspection.Summary),
            _ => throw new InvalidOperationException(
                "The Ubuntu primitive inspection outcome is unsupported.")
        };
    }

    private static InstallationInstallerUbuntuStepResult?
        TranslateExecutionInspection(
            InstallationInstallerUbuntuMutationRequest request,
            InstallationInstallerUbuntuPrimitiveOperation operation,
            InstallationInstallerUbuntuPrimitiveInspection inspection)
    {
        if (inspection.Outcome ==
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing)
        {
            return null;
        }
        if (inspection.Outcome ==
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Drift &&
            Repairable(operation.Kind) &&
            (request.Repair || ConvergesOnApply(operation.Kind)))
        {
            return null;
        }
        return inspection.Outcome switch
        {
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Drift or
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Rejected =>
                InstallationInstallerUbuntuStepResult.Rejected(
                    inspection.Code,
                    inspection.Summary),
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Unknown =>
                InstallationInstallerUbuntuStepResult.Unknown(
                    inspection.Code,
                    inspection.Summary),
            _ => throw new InvalidOperationException(
                "The Ubuntu primitive inspection outcome is unsupported.")
        };
    }

    private static bool ConvergesOnApply(
        InstallationInstallerUbuntuPrimitiveKind kind) =>
        kind == InstallationInstallerUbuntuPrimitiveKind.EnsureDirectory;

    private static bool Repairable(
        InstallationInstallerUbuntuPrimitiveKind kind) =>
        kind is InstallationInstallerUbuntuPrimitiveKind.EnsureDirectory or
            InstallationInstallerUbuntuPrimitiveKind.InstallSystemdUnit or
            InstallationInstallerUbuntuPrimitiveKind.ConfigureReverseProxy or
            InstallationInstallerUbuntuPrimitiveKind.TrustInternalCertificate or
            InstallationInstallerUbuntuPrimitiveKind.WriteFirewallGuidance;

    private static bool SafeExecutable(string executable)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }
        try
        {
            FileInfo file = new(executable);
            file.Refresh();
            if (!file.Exists ||
                file.LinkTarget is not null ||
                (file.Attributes &
                    (FileAttributes.Directory |
                     FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }
            UnixFileMode mode = File.GetUnixFileMode(executable);
            return (mode &
                (UnixFileMode.GroupWrite |
                 UnixFileMode.OtherWrite)) == 0 &&
                (mode &
                    (UnixFileMode.UserExecute |
                     UnixFileMode.GroupExecute |
                     UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments)
    {
        ProcessStartInfo start = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    private static bool IsEffectiveRoot() =>
        OperatingSystem.IsLinux() && GetEffectiveUserId() == 0;

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    internal static async Task<InstallationInstallerUbuntuDirectProcessResult>
        RunDirectProcessAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new IOException("The fixed Ubuntu primitive did not start.");
        }
        process.StandardInput.Close();
        using CancellationTokenSource operation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(ProcessTimeout);
        Task<string> stdout =
            process.StandardOutput.ReadToEndAsync(operation.Token);
        Task<string> stderr =
            process.StandardError.ReadToEndAsync(operation.Token);
        try
        {
            await process.WaitForExitAsync(operation.Token);
            return new(
                process.ExitCode,
                await stdout,
                await stderr);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateAsync(process);
            throw new TimeoutException(
                "The fixed Ubuntu primitive exceeded its execution bound.");
        }
        catch
        {
            await TerminateAsync(process);
            throw;
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }
}

internal sealed class LocalInstallationInstallerUbuntuPrimitiveInspector :
    IInstallationInstallerUbuntuPrimitiveInspector
{
    private const int MaximumUnitBytes = 64 * 1024;
    internal const string StatExecutable = "/usr/bin/stat";

    private readonly InstallationInstallerUbuntuDirectProcessRunner m_runner;

    internal LocalInstallationInstallerUbuntuPrimitiveInspector(
        InstallationInstallerUbuntuDirectProcessRunner runner)
    {
        m_runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task<InstallationInstallerUbuntuPrimitiveInspection> InspectAsync(
        InstallationInstallerUbuntuMutationRequest request,
        InstallationInstallerUbuntuPrimitiveOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        return operation.Kind switch
        {
            InstallationInstallerUbuntuPrimitiveKind.EnsureSystemUser =>
                Task.FromResult(InspectUser(operation)),
            InstallationInstallerUbuntuPrimitiveKind.EnsureDirectory =>
                InspectDirectoryAsync(operation, cancellationToken),
            InstallationInstallerUbuntuPrimitiveKind.InstallSystemdUnit =>
                InspectUnitAsync(operation, cancellationToken),
            InstallationInstallerUbuntuPrimitiveKind.ActivateSystemdUnit =>
                InspectActiveUnitAsync(operation, cancellationToken),
            _ => Task.FromResult(Unknown(
                "ubuntu-inspection-unsupported",
                "The fixed Ubuntu primitive cannot be inspected."))
        };
    }

    private static InstallationInstallerUbuntuPrimitiveInspection InspectUser(
        InstallationInstallerUbuntuPrimitiveOperation operation)
    {
        try
        {
            string? passwd = File.ReadLines("/etc/passwd")
                .FirstOrDefault(line =>
                    line.StartsWith(
                        operation.Target + ":",
                        StringComparison.Ordinal));
            string? group = File.ReadLines("/etc/group")
                .FirstOrDefault(line =>
                    line.StartsWith(
                        operation.Target + ":",
                        StringComparison.Ordinal));
            if (passwd is null && group is null)
            {
                return Missing();
            }
            if (passwd is null || group is null)
            {
                return Drift(
                    "ubuntu-service-user-drift",
                    "The fixed service user or matching primary group is incomplete.");
            }

            string[] userFields = passwd.Split(':');
            string[] groupFields = group.Split(':');
            return userFields.Length == 7 &&
                groupFields.Length >= 3 &&
                string.Equals(
                    userFields[3],
                    groupFields[2],
                    StringComparison.Ordinal) &&
                string.Equals(
                    userFields[5],
                    "/nonexistent",
                    StringComparison.Ordinal) &&
                string.Equals(
                    userFields[6],
                    "/usr/sbin/nologin",
                    StringComparison.Ordinal)
                ? Converged()
                : Drift(
                    "ubuntu-service-user-drift",
                    "The existing service user does not match the fixed non-login identity.");
        }
        catch
        {
            return Unknown(
                "ubuntu-service-user-inspection-unknown",
                "The fixed service-user inspection did not complete.");
        }
    }

    private async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectDirectoryAsync(
            InstallationInstallerUbuntuPrimitiveOperation operation,
            CancellationToken cancellationToken)
    {
        PathInspection path = InspectPath(operation.Target);
        if (path == PathInspection.Missing)
        {
            return Missing();
        }
        if (path == PathInspection.Unknown)
        {
            return Unknown(
                "ubuntu-directory-inspection-unknown",
                "The planned directory path could not be inspected.");
        }
        if (path != PathInspection.Directory)
        {
            return Rejected(
                "ubuntu-directory-unsafe",
                "The planned directory path is not a real directory.");
        }

        string owner = ArgumentAfter(operation.Arguments, "-o");
        string group = ArgumentAfter(operation.Arguments, "-g");
        string mode = ArgumentAfter(operation.Arguments, "-m").TrimStart('0');
        return await InspectMetadataAsync(
            operation.Target,
            owner,
            group,
            mode,
            cancellationToken);
    }

    private async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectActiveUnitAsync(
            InstallationInstallerUbuntuPrimitiveOperation operation,
            CancellationToken cancellationToken)
    {
        InstallationInstallerUbuntuDirectProcessResult enabled =
            await RunSystemctlInspectionAsync(
                "is-enabled",
                operation.Target,
                cancellationToken);
        InstallationInstallerUbuntuDirectProcessResult active =
            await RunSystemctlInspectionAsync(
                "is-active",
                operation.Target,
                cancellationToken);
        if (enabled.ExitCode == 0 && active.ExitCode == 0)
        {
            return Converged();
        }
        if ((enabled.ExitCode is 0 or 1 &&
             active.ExitCode is 0 or 3) ||
            (enabled.ExitCode == 4 && active.ExitCode == 4))
        {
            return Missing();
        }
        return Unknown(
            "ubuntu-service-state-inspection-unknown",
            "The fixed systemd unit state could not be classified.");
    }

    private async Task<InstallationInstallerUbuntuDirectProcessResult>
        RunSystemctlInspectionAsync(
            string command,
            string unit,
            CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new()
        {
            FileName = "/usr/bin/systemctl",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        start.ArgumentList.Add(command);
        start.ArgumentList.Add("--quiet");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(unit);
        try
        {
            return await m_runner(start, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new(
                ExitCode: int.MinValue,
                StandardOutput: string.Empty,
                StandardError: string.Empty);
        }
    }

    private async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectUnitAsync(
            InstallationInstallerUbuntuPrimitiveOperation operation,
            CancellationToken cancellationToken)
    {
        string source = operation.Arguments[^2];
        string target = operation.Arguments[^1];
        if (!SafeUnitFile(source))
        {
            return Rejected(
                "ubuntu-unit-source-unsafe",
                "The reviewed installer systemd unit source is missing or unsafe.");
        }

        PathInspection targetPath = InspectPath(target);
        if (targetPath == PathInspection.Missing)
        {
            return Missing();
        }
        if (targetPath == PathInspection.Unknown)
        {
            return Unknown(
                "ubuntu-unit-target-inspection-unknown",
                "The installed systemd unit target could not be inspected.");
        }
        if (targetPath != PathInspection.File || !SafeUnitFile(target))
        {
            return Rejected(
                "ubuntu-unit-target-unsafe",
                "The installed systemd unit target is not a safe regular file.");
        }

        byte[] sourceBytes = await File.ReadAllBytesAsync(
            source,
            cancellationToken);
        byte[] targetBytes = await File.ReadAllBytesAsync(
            target,
            cancellationToken);
        if (!sourceBytes.AsSpan().SequenceEqual(targetBytes))
        {
            return Drift(
                "ubuntu-unit-content-drift",
                "The installed systemd unit differs from the reviewed release asset.");
        }
        return await InspectMetadataAsync(
            target,
            "root",
            "root",
            "644",
            cancellationToken);
    }

    private async Task<InstallationInstallerUbuntuPrimitiveInspection>
        InspectMetadataAsync(
            string path,
            string owner,
            string group,
            string mode,
            CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new()
        {
            FileName = StatExecutable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        start.ArgumentList.Add("--format=%U:%G:%a");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(path);
        try
        {
            InstallationInstallerUbuntuDirectProcessResult result =
                await m_runner(start, cancellationToken);
            if (result.ExitCode != 0)
            {
                return Unknown(
                    "ubuntu-metadata-inspection-unknown",
                    "The fixed Ubuntu ownership and mode inspection failed.");
            }
            string expected = $"{owner}:{group}:{mode}";
            return string.Equals(
                result.StandardOutput.Trim(),
                expected,
                StringComparison.Ordinal)
                ? Converged()
                : Drift(
                    "ubuntu-metadata-drift",
                    "The fixed Ubuntu ownership or mode does not match.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Unknown(
                "ubuntu-metadata-inspection-unknown",
                "The fixed Ubuntu ownership and mode inspection did not complete.");
        }
    }

    private static bool SafeUnitFile(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }
        try
        {
            FileInfo file = new(path);
            file.Refresh();
            if (!file.Exists ||
                file.LinkTarget is not null ||
                (file.Attributes &
                    (FileAttributes.Directory |
                     FileAttributes.ReparsePoint)) != 0 ||
                file.Length is < 1 or > MaximumUnitBytes)
            {
                return false;
            }
            UnixFileMode mode = File.GetUnixFileMode(path);
            return (mode &
                (UnixFileMode.GroupWrite |
                 UnixFileMode.OtherWrite)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static PathInspection InspectPath(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return PathInspection.Unsafe;
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                DirectoryInfo directory = new(path);
                directory.Refresh();
                return directory.LinkTarget is null
                    ? PathInspection.Directory
                    : PathInspection.Unsafe;
            }
            FileInfo file = new(path);
            file.Refresh();
            return file.LinkTarget is null
                ? PathInspection.File
                : PathInspection.Unsafe;
        }
        catch (FileNotFoundException)
        {
            return PathInspection.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return PathInspection.Missing;
        }
        catch
        {
            return PathInspection.Unknown;
        }
    }

    private static string ArgumentAfter(
        IReadOnlyList<string> arguments,
        string name)
    {
        for (int index = 0; index + 1 < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }
        throw new InvalidOperationException(
            "The fixed Ubuntu primitive arguments are invalid.");
    }

    private static InstallationInstallerUbuntuPrimitiveInspection Converged() =>
        new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged,
            "ubuntu-primitive-converged",
            "The fixed Ubuntu primitive is already converged.");

    private static InstallationInstallerUbuntuPrimitiveInspection Missing() =>
        new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing,
            "ubuntu-primitive-missing",
            "The fixed Ubuntu primitive target is absent.");

    private static InstallationInstallerUbuntuPrimitiveInspection Drift(
        string code,
        string summary) =>
        new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Drift,
            code,
            summary);

    private static InstallationInstallerUbuntuPrimitiveInspection Rejected(
        string code,
        string summary) =>
        new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Rejected,
            code,
            summary);

    private static InstallationInstallerUbuntuPrimitiveInspection Unknown(
        string code,
        string summary) =>
        new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Unknown,
            code,
            summary);

    private enum PathInspection
    {
        Missing = 1,
        File = 2,
        Directory = 3,
        Unsafe = 4,
        Unknown = 5
    }
}
