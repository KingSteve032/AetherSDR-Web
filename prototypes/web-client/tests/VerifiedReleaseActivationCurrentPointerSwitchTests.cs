using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Configuration;

namespace AetherSDR.Web.Tests;

[SupportedOSPlatform("linux")]
public sealed class VerifiedReleaseActivationCurrentPointerSwitchTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsAndStateOnly()
    {
        string[] methods =
            typeof(VerifiedReleaseActivationCurrentPointerSwitchService)
                .GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(["get_Snapshot", "get_State"], methods);
        Assert.DoesNotContain(
            methods,
            name => name.Contains("Execute", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Switch", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Observe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownConfigurationPropertiesFailClosed()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{ReleaseActivationCurrentPointerSwitchSettings.SectionName}:" +
                    "ExecutonEnabled"] = "true"
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => configuration
                .GetSection(
                    ReleaseActivationCurrentPointerSwitchSettings.SectionName)
                .Get<ReleaseActivationCurrentPointerSwitchSettings>(options =>
                    options.ErrorOnUnknownConfiguration = true));

        Assert.Contains("ExecutonEnabled", exception.Message);
    }

    [Fact]
    public void DiagnosticsExposeOneCallerlessAtomicBoundary()
    {
        using Fixture fixture = new();
        VerifiedReleaseActivationCurrentPointerSwitchDiagnostics snapshot =
            fixture.PointerService.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.ConfigurationRegistered);
        Assert.True(snapshot.ExecutionEnabled);
        Assert.True(snapshot.ExecutionAvailable);
        Assert.True(snapshot.ExactServiceControlPlanInputRegistered);
        Assert.True(snapshot.ExactServiceControlPlanBindingRegistered);
        Assert.True(snapshot.ExactActivationPlanBindingRegistered);
        Assert.True(snapshot.ExactPreSwitchEvidenceRegistered);
        Assert.True(snapshot.ReleaseStatusDoubleReadRegistered);
        Assert.True(snapshot.SetupStateDoubleReadRegistered);
        Assert.True(snapshot.InstalledActiveRequirementRegistered);
        Assert.True(snapshot.TargetActiveVerificationRegistered);
        Assert.True(snapshot.ImmutableTargetRevalidationRegistered);
        Assert.True(snapshot.ExactInstalledLinkTargetRegistered);
        Assert.True(snapshot.ExactTargetLinkTargetRegistered);
        Assert.True(snapshot.SameDirectoryTemporaryLinkRegistered);
        Assert.True(snapshot.AtomicLinkReplacementRegistered);
        Assert.True(snapshot.PostSwitchObservationRegistered);
        Assert.True(snapshot.ExactPlanEvidenceRegistered);
        Assert.True(snapshot.PartialFailureReconciliationRegistered);
        Assert.False(snapshot.AutomaticRetryRegistered);
        Assert.False(snapshot.ServiceStartRegistered);
        Assert.False(snapshot.HostRestartRegistered);
        Assert.False(snapshot.RemoteServiceControlRegistered);
        Assert.False(snapshot.HealthProbeRegistered);
        Assert.False(snapshot.RollbackRegistered);
        Assert.False(snapshot.ActivationAuthorityRegistered);
        Assert.False(snapshot.OperationalCallerRegistered);
        Assert.False(snapshot.CliCallerRegistered);
        Assert.False(snapshot.AdminCallerRegistered);
        Assert.False(snapshot.BrowserCallerRegistered);
        Assert.False(snapshot.HttpCallerRegistered);
        Assert.False(snapshot.WebSocketCallerRegistered);
        Assert.False(snapshot.HostedServiceCallerRegistered);
        Assert.False(snapshot.TimerCallerRegistered);
        Assert.False(snapshot.AetherRemoteCommandCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public async Task DisabledDefaultFailsBeforeAnyObservation()
    {
        using Fixture fixture = new(pointerEnabled: false);
        await fixture.CompletePreSwitchAsync();

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .ExecutionDisabled);
        Assert.Equal(0, fixture.PointerStatusReads);
        Assert.Equal(0, fixture.PointerSetupReads);
        Assert.Equal(0, fixture.PointerRuntime.CreateCount);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);
        AssertFalseState(fixture.PointerService.State);
    }

    [Fact]
    public async Task ExactPreSwitchEvidenceAtomicallySwitchesAndRetainsEvidence()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode.None,
            report.FailureCode);
        Assert.True(report.ExactServiceControlPlanBound);
        Assert.True(report.ExactActivationPlanBound);
        Assert.True(report.PreSwitchServiceControlReady);
        Assert.True(report.InstalledReleaseActiveBeforeSwitch);
        Assert.True(report.TargetReleaseActiveAfterSwitch);
        Assert.True(report.SetupStable);
        Assert.True(report.TargetReleaseImmutable);
        Assert.True(report.ExactInstalledPointerBound);
        Assert.True(report.ExactTargetPointerBound);
        Assert.True(report.TemporaryPointerCreated);
        Assert.True(report.TemporaryPointerCleaned);
        Assert.True(report.AtomicSwitchAttempted);
        Assert.True(report.AtomicSwitchCompleted);
        Assert.True(report.CurrentPointerChanged);
        Assert.False(report.ReconciliationRequired);
        Assert.False(report.PostSwitchServiceControlReady);
        Assert.False(report.HealthVerificationReady);
        Assert.False(report.RollbackPerformed);
        Assert.False(report.ActivationAuthorized);
        Assert.Equal(3, fixture.PointerStatusReads);
        Assert.Equal(3, fixture.PointerSetupReads);
        Assert.Equal(1, fixture.PointerRuntime.CreateCount);
        Assert.Equal(1, fixture.PointerRuntime.ReplaceCount);
        Assert.Equal(
            fixture.ActivationPlan.TargetCurrentLinkTarget,
            fixture.PointerRuntime.Current.LinkTarget);

        VerifiedReleaseActivationCurrentPointerSwitchStateDiagnostics state =
            fixture.PointerService.State;
        Assert.True(state.PointerSwitchReady);
        Assert.True(state.ExactServiceControlPlanBound);
        Assert.True(state.ExactActivationPlanBound);
        Assert.True(state.PreSwitchServiceControlReady);
        Assert.True(state.CurrentPointerChanged);
        Assert.True(state.TargetReleaseActive);
        Assert.True(state.SetupStable);
        Assert.True(state.TargetReleaseImmutable);
        Assert.True(state.AtomicSwitchCompleted);
        Assert.False(state.ReconciliationRequired);
        Assert.False(state.PostSwitchServiceControlReady);
        Assert.False(state.HealthVerificationReady);
        Assert.False(state.RollbackPerformed);
        Assert.False(state.ActivationAuthorized);

        VerifiedReleaseActivationCurrentPointerSwitchObservation observation =
            fixture.PointerService.Observe(fixture.ActivationPlan);
        Assert.True(observation.PointerSwitchReady);
        Assert.True(observation.ExactServiceControlPlanBound);
        Assert.True(observation.ExactActivationPlanBound);
        Assert.True(observation.PreSwitchServiceControlReady);
        Assert.NotNull(observation.CompletedAt);
        Assert.False(observation.ReconciliationRequired);
        Assert.NotNull(
            fixture.PointerService.GetEvidence(fixture.ServiceControlPlan));
    }

    [Fact]
    public async Task HostRestartPlanSwitchesWithoutServiceStopEvidence()
    {
        using Fixture fixture = new(restartHost: true);

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        Assert.True(report.Succeeded, report.Message);
        Assert.True(report.PreSwitchServiceControlReady);
        Assert.True(report.CurrentPointerChanged);
        Assert.True(report.TargetReleaseActiveAfterSwitch);
        Assert.Equal(1, fixture.PointerRuntime.CreateCount);
        Assert.Equal(1, fixture.PointerRuntime.ReplaceCount);
        Assert.Null(
            fixture.PointerService.GetEvidence(fixture.ServiceControlPlan)!
                .PreSwitchEvidence);
        VerifiedReleaseActivationServiceControlObservation serviceObservation =
            fixture.ServiceControl.ObservePlan(fixture.ServiceControlPlan);
        Assert.False(serviceObservation.ServiceControlReady);
        Assert.Equal(0, serviceObservation.ExecutedStopActionCount);
        Assert.False(report.ActivationAuthorized);
    }

    [Fact]
    public async Task HostRestartTransportWritesTransactionBoundMarkerAndUsesFixedSystemctl()
    {
        const string transactionId =
            "0123456789abcdef0123456789abcdef";
        using Fixture fixture = new(restartHost: true);
        VerifiedReleaseActivationCurrentPointerSwitchReport pointer =
            await fixture.ExecutePointerAsync();
        Assert.True(pointer.Succeeded, pointer.Message);
        ProcessStartInfo? captured = null;
        VerifiedReleaseActivationHostRestartTransport transport = new(
            new ReleaseActivationHostRestartSettings
            {
                ExecutionEnabled = true
            },
            fixture.Paths,
            (start, _) =>
            {
                captured = start;
                return Task.FromResult(0);
            },
            fixture.Time);

        VerifiedReleaseActivationHostRestartReport report =
            await transport.RequestAsync(
                transactionId,
                fixture.ServiceControlPlanReport,
                pointer);

        Assert.True(report.Succeeded, report.Message);
        Assert.True(report.HostRestartRequested);
        Assert.True(report.DurableRestartMarkerWritten);
        Assert.True(report.PostBootVerificationRequired);
        Assert.False(report.ShellUsed);
        Assert.False(report.ActivationAuthorized);
        Assert.False(report.RadioCommandIssued);
        Assert.False(report.TxActionPerformed);
        Assert.NotNull(captured);
        Assert.Equal("/usr/bin/systemctl", captured!.FileName);
        Assert.False(captured.UseShellExecute);
        Assert.Equal(
            ["--no-ask-password", "--no-pager", "--no-block", "reboot"],
            captured.ArgumentList.ToArray());
        HostRestartContinuationPaths storage =
            HostRestartContinuationStorage.Resolve(fixture.Paths);
        Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(storage.Marker));
        HostRestartMarker? marker = JsonSerializer.Deserialize<HostRestartMarker>(
            File.ReadAllBytes(storage.Marker),
            HostRestartContinuationStorage.JsonOptions);
        Assert.NotNull(marker);
        Assert.Equal(transactionId, marker!.TransactionId);
        Assert.Equal(fixture.ActivationPlan.SetupRevision, marker.SetupRevision);
        Assert.Equal(fixture.InstalledIdentity, marker.InstalledReleaseIdentity);
        Assert.Equal(fixture.TargetIdentity, marker.TargetReleaseIdentity);
        Assert.True(marker.PostBootVerificationRequired);
    }

    [Fact]
    public async Task MissingPreSwitchEvidenceFailsBeforeFilesystemMutation()
    {
        using Fixture fixture = new();

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .PreSwitchServiceControlUnavailable);
        Assert.Equal(0, fixture.PointerStatusReads);
        Assert.Equal(0, fixture.PointerRuntime.CreateCount);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public async Task EquivalentPlanCannotReusePreSwitchEvidence()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        VerifiedReleaseActivationServiceControlPlanReport equivalent =
            fixture.ComposeEquivalentServiceControlPlan();

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.PointerService.ExecuteAsync(equivalent);

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .PreSwitchServiceControlUnavailable);
        Assert.Equal(0, fixture.PointerRuntime.CreateCount);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public async Task UnexpectedTargetEntryFailsBeforeTemporaryPointerCreation()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        File.SetUnixFileMode(
            fixture.ActivationPlan.TargetReleasePath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        string unexpected = Path.Combine(
            fixture.ActivationPlan.TargetReleasePath,
            "unexpected.txt");
        File.WriteAllText(unexpected, "unexpected");
        File.SetUnixFileMode(unexpected, UnixFileMode.UserRead);
        File.SetUnixFileMode(
            fixture.ActivationPlan.TargetReleasePath,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .TargetReleaseUnsafe);
        Assert.Equal(0, fixture.PointerRuntime.CreateCount);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public async Task SymbolicLinkInTargetFailsBeforeTemporaryPointerCreation()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        File.SetUnixFileMode(
            fixture.ActivationPlan.TargetReleasePath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        string link = Path.Combine(
            fixture.ActivationPlan.TargetReleasePath,
            "unexpected-link");
        File.CreateSymbolicLink(
            link,
            fixture.ActivationPlan.Packages[0].PublishedPath);
        File.SetUnixFileMode(
            fixture.ActivationPlan.TargetReleasePath,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .TargetReleaseUnsafe);
        Assert.Equal(0, fixture.PointerRuntime.CreateCount);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public async Task SameLengthManifestDigestDriftFailsBeforeTemporaryPointerCreation()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        string manifest = Path.Combine(
            fixture.ActivationPlan.TargetReleasePath,
            LocalOfflineReleaseBundleVerificationService.ManifestFileName);
        File.SetUnixFileMode(
            manifest,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        byte[] drifted = File.ReadAllBytes(manifest);
        drifted[0] ^= 0x01;
        File.WriteAllBytes(manifest, drifted);
        File.SetUnixFileMode(manifest, UnixFileMode.UserRead);

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .TargetReleaseUnsafe);
        Assert.Equal(0, fixture.PointerRuntime.CreateCount);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public async Task SameLengthPackageDigestDriftFailsBeforeTemporaryPointerCreation()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        string packagePath = fixture.ActivationPlan.Packages[0].PublishedPath;
        File.SetUnixFileMode(
            packagePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        byte[] drifted = File.ReadAllBytes(packagePath);
        drifted[^1] ^= 0x01;
        File.WriteAllBytes(packagePath, drifted);
        File.SetUnixFileMode(packagePath, UnixFileMode.UserRead);

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .TargetReleaseUnsafe);
        Assert.Equal(0, fixture.PointerRuntime.CreateCount);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public async Task MutableTargetFailsBeforeTemporaryPointerCreation()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        File.SetUnixFileMode(
            fixture.ActivationPlan.TargetReleasePath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .TargetReleaseUnsafe);
        Assert.Equal(0, fixture.PointerRuntime.CreateCount);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public async Task CurrentPointerMismatchFailsClosed()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        fixture.PointerRuntime.Current = new CurrentPointerRuntimeSnapshot(
            EntryPresent: true,
            IsSymbolicLink: true,
            "releases/other");

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .CurrentPointerMismatch);
        Assert.Equal(0, fixture.PointerRuntime.CreateCount);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public async Task SetupDriftBeforeAtomicMoveCleansTemporaryPointer()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        fixture.PointerSetupQueue.Enqueue(fixture.Setup);
        fixture.PointerSetupQueue.Enqueue(fixture.Setup with
        {
            UpdatedAt = fixture.Setup.UpdatedAt.AddSeconds(1)
        });

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .ObservationDrift);
        Assert.True(report.TemporaryPointerCreated);
        Assert.True(report.TemporaryPointerCleaned);
        Assert.False(report.AtomicSwitchAttempted);
        Assert.False(report.CurrentPointerChanged);
        Assert.Equal(1, fixture.PointerRuntime.CreateCount);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);
        Assert.Equal(1, fixture.PointerRuntime.DeleteCount);
        Assert.False(fixture.PointerRuntime.Temporary.EntryPresent);
    }

    [Fact]
    public async Task TemporaryCleanupFailureRequiresReconciliationAndNoRetry()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        fixture.PointerSetupQueue.Enqueue(fixture.Setup);
        fixture.PointerSetupQueue.Enqueue(fixture.Setup with
        {
            UpdatedAt = fixture.Setup.UpdatedAt.AddSeconds(1)
        });
        fixture.PointerRuntime.DeleteException = new IOException("cleanup failed");

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .ObservationDrift);
        Assert.True(report.TemporaryPointerCreated);
        Assert.False(report.TemporaryPointerCleaned);
        Assert.True(report.ReconciliationRequired);
        Assert.True(fixture.PointerService.State.ReconciliationRequired);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);

        VerifiedReleaseActivationCurrentPointerSwitchReport retry =
            await fixture.ExecutePointerAsync();
        AssertFailure(
            retry,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .ReconciliationRequired);
        Assert.Equal(0, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public async Task AtomicReplacementFailureRequiresReconciliationAndNoRetry()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        fixture.PointerRuntime.ReplaceException = new IOException("ambiguous");

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .AtomicSwitchFailed);
        Assert.True(report.AtomicSwitchAttempted);
        Assert.True(report.ReconciliationRequired);
        Assert.True(fixture.PointerService.State.ReconciliationRequired);
        Assert.Equal(1, fixture.PointerRuntime.ReplaceCount);

        VerifiedReleaseActivationCurrentPointerSwitchReport retry =
            await fixture.ExecutePointerAsync();
        AssertFailure(
            retry,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .ReconciliationRequired);
        Assert.Equal(1, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public async Task PostSwitchStatusDriftRequiresReconciliation()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        fixture.ForceInstalledStatusAfterSwitch = true;

        VerifiedReleaseActivationCurrentPointerSwitchReport report =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            report,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .ObservationDrift);
        Assert.True(report.AtomicSwitchCompleted);
        Assert.True(report.CurrentPointerChanged);
        Assert.True(report.ReconciliationRequired);
        Assert.True(fixture.PointerService.State.ReconciliationRequired);
    }

    [Fact]
    public async Task CancellationAfterAtomicLaunchRequiresReconciliation()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        fixture.PointerRuntime.ReplaceException =
            new OperationCanceledException("cancelled after launch");

        await Assert.ThrowsAsync<OperationCanceledException>(
            fixture.ExecutePointerAsync);

        Assert.True(fixture.PointerService.State.ReconciliationRequired);
        Assert.Equal(1, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public async Task SuccessfulSwitchCannotRepeat()
    {
        using Fixture fixture = new();
        await fixture.CompletePreSwitchAsync();
        Assert.True((await fixture.ExecutePointerAsync()).Succeeded);

        VerifiedReleaseActivationCurrentPointerSwitchReport second =
            await fixture.ExecutePointerAsync();

        AssertFailure(
            second,
            VerifiedReleaseActivationCurrentPointerSwitchFailureCode
                .PointerAlreadySwitched);
        Assert.Equal(1, fixture.PointerRuntime.ReplaceCount);
    }

    [Fact]
    public void LinuxRuntimeAtomicallyReplacesDirectorySymlink()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"pointer-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string releases = Path.Combine(root, "releases");
        string installed = Path.Combine(releases, "aethersdr-8.1.0");
        string target = Path.Combine(releases, "aethersdr-8.2.0");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(target);
        string current = Path.Combine(root, "current");
        string temporary = Path.Combine(root, ".current-switch-test");
        LinuxVerifiedReleaseActivationCurrentPointerRuntime runtime = new();
        try
        {
            Directory.CreateSymbolicLink(
                current,
                Path.Combine("releases", "aethersdr-8.1.0"));
            runtime.CreateSymbolicLink(
                temporary,
                Path.Combine("releases", "aethersdr-8.2.0"));

            runtime.ReplaceAtomically(temporary, current);

            CurrentPointerRuntimeSnapshot snapshot = runtime.Read(current);
            Assert.True(snapshot.EntryPresent);
            Assert.True(snapshot.IsSymbolicLink);
            Assert.Equal(
                Path.Combine("releases", "aethersdr-8.2.0"),
                snapshot.LinkTarget);
            Assert.False(runtime.Read(temporary).EntryPresent);
            Assert.True(Directory.Exists(current));
        }
        finally
        {
            if (new DirectoryInfo(current).LinkTarget is not null)
            {
                File.Delete(current);
            }
            if (new DirectoryInfo(temporary).LinkTarget is not null)
            {
                File.Delete(temporary);
            }
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertFailure(
        VerifiedReleaseActivationCurrentPointerSwitchReport report,
        VerifiedReleaseActivationCurrentPointerSwitchFailureCode code)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(code, report.FailureCode);
        Assert.False(report.PostSwitchServiceControlReady);
        Assert.False(report.HealthVerificationReady);
        Assert.False(report.RollbackPerformed);
        Assert.False(report.ActivationAuthorized);
    }

    private static void AssertFalseState(
        VerifiedReleaseActivationCurrentPointerSwitchStateDiagnostics state)
    {
        Assert.False(state.PointerSwitchReady);
        Assert.False(state.ExactServiceControlPlanBound);
        Assert.False(state.ExactActivationPlanBound);
        Assert.False(state.PreSwitchServiceControlReady);
        Assert.False(state.CurrentPointerChanged);
        Assert.False(state.TargetReleaseActive);
        Assert.False(state.SetupStable);
        Assert.False(state.TargetReleaseImmutable);
        Assert.False(state.AtomicSwitchCompleted);
        Assert.False(state.ReconciliationRequired);
        Assert.False(state.PostSwitchServiceControlReady);
        Assert.False(state.HealthVerificationReady);
        Assert.False(state.RollbackPerformed);
        Assert.False(state.ActivationAuthorized);
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly byte[] ManifestBytes =
            "{\"schemaVersion\":1}"u8.ToArray();

        private readonly ManualTimeProvider m_time =
            new(new DateTimeOffset(2026, 8, 4, 18, 30, 0, TimeSpan.Zero));
        private readonly bool m_restartGateway;
        private readonly bool m_restartBroker;
        private readonly bool m_restartAgent;
        private readonly bool m_restartEngine;
        private readonly bool m_restartHost;

        internal Fixture(
            bool pointerEnabled = true,
            bool restartGateway = true,
            bool restartBroker = true,
            bool restartAgent = true,
            bool restartEngine = true,
            bool restartHost = false)
        {
            m_restartGateway = restartGateway;
            m_restartBroker = restartBroker;
            m_restartAgent = restartAgent;
            m_restartEngine = restartEngine;
            m_restartHost = restartHost;
            Root = Path.Combine(
                Path.GetTempPath(),
                $"pointer-switch-{Guid.NewGuid():N}");
            Paths = new InstallationPaths(
                Path.Combine(Root, "config"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(Root, "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            Setup = new InstallationSetupState
            {
                SchemaVersion = InstallationSetupState.CurrentSchemaVersion,
                Revision = 7,
                CreatedAt = m_time.GetUtcNow().AddMinutes(-10),
                UpdatedAt = m_time.GetUtcNow().AddMinutes(-1),
                LastCompletedStep = InstallationSetupStep.Administrator,
                Lock = new InstallationSetupLock
                {
                    Mode = InstallationSetupLockMode.Complete,
                    ClaimedAt = m_time.GetUtcNow().AddMinutes(-9),
                    CompletedAt = m_time.GetUtcNow().AddMinutes(-1)
                },
                Topology = InstallationTopologyKind.PersonalSingleStation,
                CanonicalPublicUrl = "https://radio.example.org",
                Paths = Paths,
                UpdateChannel = InstallationUpdateChannel.Stable,
                InstallTransmitSupport = false
            };
            ActivationPlanReport = ComposeActivation();
            ActivationPlan = Assert.IsType<VerifiedReleaseActivationPlan>(
                ActivationPlanReport.Plan);
            ServiceControlPlanReport =
                new VerifiedReleaseActivationServiceControlPlanComposer().Compose(
                    ActivationPlanReport);
            Assert.True(ServiceControlPlanReport.Succeeded);
            ServiceControlPlan =
                Assert.IsType<VerifiedReleaseActivationServiceControlPlan>(
                    ServiceControlPlanReport.Plan);

            CreateImmutableReleaseTree();

            ServiceControl =
                new VerifiedReleaseActivationServiceControlExecutionService(
                    _ => Task.FromResult(CreateStatus(InstalledIdentity)),
                    _ => Task.FromResult(Setup),
                    new SuccessfulServiceRuntime(),
                    new ReleaseActivationServiceControlSettings
                    {
                        ExecutionEnabled = true
                    },
                    m_time);
            PointerRuntime = new FakePointerRuntime(
                ActivationPlan.CurrentPointerPath,
                ActivationPlan.InstalledCurrentLinkTarget);
            PointerService =
                new VerifiedReleaseActivationCurrentPointerSwitchService(
                    _ =>
                    {
                        PointerStatusReads++;
                        string active = ForceInstalledStatusAfterSwitch
                            ? InstalledIdentity
                            : string.Equals(
                                PointerRuntime.Current.LinkTarget,
                                ActivationPlan.TargetCurrentLinkTarget,
                                StringComparison.Ordinal)
                                ? TargetIdentity
                                : InstalledIdentity;
                        return Task.FromResult(CreateStatus(active));
                    },
                    _ =>
                    {
                        PointerSetupReads++;
                        InstallationSetupState setup =
                            PointerSetupQueue.Count > 0
                                ? PointerSetupQueue.Dequeue()
                                : Setup;
                        return Task.FromResult(setup);
                    },
                    ServiceControl,
                    PointerRuntime,
                    new ReleaseActivationCurrentPointerSwitchSettings
                    {
                        ExecutionEnabled = pointerEnabled
                    },
                    m_time,
                    () => "0123456789abcdef0123456789abcdef");
        }

        internal string Root { get; }
        internal string InstalledIdentity => "aethersdr-8.1.0";
        internal string TargetIdentity => "aethersdr-8.2.0";
        internal InstallationPaths Paths { get; }
        internal InstallationSetupState Setup { get; }
        internal VerifiedReleaseActivationPlanCompositionResult ActivationPlanReport
        {
            get;
        }
        internal VerifiedReleaseActivationPlan ActivationPlan { get; }
        internal VerifiedReleaseActivationServiceControlPlanReport
            ServiceControlPlanReport
        {
            get;
        }
        internal VerifiedReleaseActivationServiceControlPlan ServiceControlPlan
        {
            get;
        }
        internal VerifiedReleaseActivationServiceControlExecutionService
            ServiceControl
        {
            get;
        }
        internal FakePointerRuntime PointerRuntime { get; }
        internal VerifiedReleaseActivationCurrentPointerSwitchService PointerService
        {
            get;
        }
        internal Queue<InstallationSetupState> PointerSetupQueue { get; } = new();
        internal int PointerStatusReads { get; private set; }
        internal int PointerSetupReads { get; private set; }
        internal bool ForceInstalledStatusAfterSwitch { get; set; }
        internal TimeProvider Time => m_time;

        internal async Task CompletePreSwitchAsync()
        {
            VerifiedReleaseActivationServiceControlExecutionReport report =
                await ServiceControl.ExecutePreSwitchStopAsync(
                    ServiceControlPlanReport);
            Assert.True(report.Succeeded);
        }

        internal Task<VerifiedReleaseActivationCurrentPointerSwitchReport>
            ExecutePointerAsync() =>
            PointerService.ExecuteAsync(ServiceControlPlanReport);

        internal VerifiedReleaseActivationServiceControlPlanReport
            ComposeEquivalentServiceControlPlan()
        {
            VerifiedReleaseActivationPlanCompositionResult equivalent =
                ComposeActivation();
            VerifiedReleaseActivationServiceControlPlanReport report =
                new VerifiedReleaseActivationServiceControlPlanComposer().Compose(
                    equivalent);
            Assert.True(report.Succeeded);
            return report;
        }

        internal ReleaseStatusReadResult CreateStatus(string activeIdentity) =>
            ReleaseStatusReadResult.Success(
                Setup,
                releaseDirectoryPresent: true,
                [InstalledIdentity, TargetIdentity],
                currentPointerPresent: true,
                activeIdentity);

        private VerifiedReleaseActivationPlanCompositionResult ComposeActivation()
        {
            string targetPath = Path.Combine(Paths.ReleaseDirectory, TargetIdentity);
            VerifiedReleaseInstallationPackagePlan[] packages =
                CreatePackages(targetPath);
            VerifiedReleaseInstallationPlan installation = new(
                setupRevision: Setup.Revision,
                installedReleaseIdentity: InstalledIdentity,
                targetReleaseIdentity: TargetIdentity,
                targetVersion: "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                InstallationUpdateChannel.Stable,
                pinnedReleaseIdentity: string.Empty,
                installTransmitSupport: false,
                bundleDirectory: Path.Combine(Paths.StateDirectory, "bundle"),
                manifestLength: ManifestBytes.Length,
                manifestSha256: SHA256.HashData(ManifestBytes),
                releaseRootPath: Paths.ReleaseDirectory,
                deploymentRootPath: Path.GetDirectoryName(Paths.ReleaseDirectory)!,
                targetReleasePath: targetPath,
                packages,
                targetConfigurationSchemaVersion: 1,
                ReleaseMigrationKind.None,
                migrationFromConfigurationSchemaVersion: null,
                migrationToConfigurationSchemaVersion: null,
                migrationIdentity: string.Empty,
                restartGatewayWeb: m_restartGateway,
                restartBroker: m_restartBroker,
                restartAetherRemoteAgent: m_restartAgent,
                restartStationEngine: m_restartEngine,
                restartHost: m_restartHost,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary:
                    "Exact current-pointer switch test release.");
            long publishedBytes = checked(
                installation.ManifestLength +
                packages.Sum(package => package.Length));
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(
                        installation,
                        installation.TargetReleasePath,
                        publishedBytes));
            VerifiedReleaseActivationPlanCompositionResult activation =
                new VerifiedReleaseActivationPlanComposer().Compose(publication);
            Assert.True(activation.Succeeded);
            return activation;
        }

        private void CreateImmutableReleaseTree()
        {
            Directory.CreateDirectory(ActivationPlan.InstalledReleasePath);
            Directory.CreateDirectory(ActivationPlan.TargetReleasePath);
            string manifestPath = Path.Combine(
                ActivationPlan.TargetReleasePath,
                LocalOfflineReleaseBundleVerificationService.ManifestFileName);
            File.WriteAllBytes(manifestPath, ManifestBytes);
            File.SetUnixFileMode(manifestPath, UnixFileMode.UserRead);
            foreach (VerifiedReleaseActivationPackagePlan package in
                ActivationPlan.Packages)
            {
                string? parent = Path.GetDirectoryName(package.PublishedPath);
                Assert.NotNull(parent);
                Directory.CreateDirectory(parent);
                File.WriteAllBytes(
                    package.PublishedPath,
                    PackageContent(package.Role, package.Length));
                File.SetUnixFileMode(
                    package.PublishedPath,
                    UnixFileMode.UserRead);
            }
            foreach (string directory in Directory
                .EnumerateDirectories(
                    ActivationPlan.TargetReleasePath,
                    "*",
                    SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length))
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            File.SetUnixFileMode(
                ActivationPlan.TargetReleasePath,
                UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        private static VerifiedReleaseInstallationPackagePlan[] CreatePackages(
            string targetPath)
        {
            (string Identity, ReleasePackageRole Role, string Relative, long Length)[]
                inputs =
                [
                    ("gateway", ReleasePackageRole.GatewayWeb,
                        "packages/gateway.tar", 11),
                    ("broker", ReleasePackageRole.Broker,
                        "packages/broker.tar", 12),
                    ("agent", ReleasePackageRole.AetherRemoteAgent,
                        "packages/agent.tar", 13),
                    ("engine", ReleasePackageRole.StationEngine,
                        "packages/engine.tar", 14)
                ];
            return inputs.Select((input, index) =>
            {
                SignedReleasePackage package = new()
                {
                    PackageIdentity = input.Identity,
                    Role = input.Role,
                    FileName = input.Relative,
                    Length = input.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(
                            PackageContent(input.Role, input.Length)))
                };
                return new VerifiedReleaseInstallationPackagePlan(
                    new VerifiedReleasePackageSnapshot(package),
                    Path.GetFullPath(
                        Path.Combine(
                            targetPath,
                            input.Relative.Replace(
                                '/',
                                Path.DirectorySeparatorChar))));
            }).ToArray();
        }

        private static byte[] PackageContent(
            ReleasePackageRole role,
            long length) =>
            Enumerable.Repeat(
                    checked((byte)(0x41 + (int)role)),
                    checked((int)length))
                .ToArray();

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }
            foreach (string file in Directory.EnumerateFiles(
                Root,
                "*",
                SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(
                    file,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            foreach (string directory in Directory
                .EnumerateDirectories(Root, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length))
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
            File.SetUnixFileMode(
                Root,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakePointerRuntime(
        string currentPath,
        string installedLinkTarget) :
        IVerifiedReleaseActivationCurrentPointerRuntime
    {
        private readonly string m_currentPath = currentPath;
        private string m_temporaryPath = string.Empty;

        internal CurrentPointerRuntimeSnapshot Current { get; set; } =
            new(true, true, installedLinkTarget);
        internal CurrentPointerRuntimeSnapshot Temporary { get; private set; } =
            new(false, false, string.Empty);
        internal Exception? ReplaceException { get; set; }
        internal Exception? DeleteException { get; set; }
        internal int CreateCount { get; private set; }
        internal int ReplaceCount { get; private set; }
        internal int DeleteCount { get; private set; }

        public CurrentPointerRuntimeSnapshot Read(string path) =>
            string.Equals(path, m_currentPath, StringComparison.Ordinal)
                ? Current
                : string.Equals(path, m_temporaryPath, StringComparison.Ordinal)
                    ? Temporary
                    : new CurrentPointerRuntimeSnapshot(
                        EntryPresent: false,
                        IsSymbolicLink: false,
                        string.Empty);

        public void CreateSymbolicLink(string path, string linkTarget)
        {
            CreateCount++;
            m_temporaryPath = path;
            Temporary = new CurrentPointerRuntimeSnapshot(true, true, linkTarget);
        }

        public void ReplaceAtomically(string temporaryPath, string currentPath)
        {
            ReplaceCount++;
            if (ReplaceException is not null)
            {
                throw ReplaceException;
            }
            Assert.Equal(m_temporaryPath, temporaryPath);
            Assert.Equal(m_currentPath, currentPath);
            Current = Temporary;
            Temporary = new CurrentPointerRuntimeSnapshot(
                EntryPresent: false,
                IsSymbolicLink: false,
                string.Empty);
        }

        public void DeleteTemporary(string path)
        {
            DeleteCount++;
            Assert.Equal(m_temporaryPath, path);
            if (DeleteException is not null)
            {
                throw DeleteException;
            }
            Temporary = new CurrentPointerRuntimeSnapshot(
                EntryPresent: false,
                IsSymbolicLink: false,
                string.Empty);
        }
    }

    private sealed class SuccessfulServiceRuntime :
        IVerifiedReleaseActivationServiceControlRuntime
    {
        public Task<ServiceControlAttemptResult> ControlUnitAsync(
            VerifiedReleaseActivationServiceControlAction action,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(ServiceControlAttemptResult.Success());
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        internal void Advance(TimeSpan duration) => m_now += duration;
    }
}
