using System.Net.Sockets;
using System.Security.Claims;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class ReleaseUpdateCompletionSurfaceTests
{
    [Fact]
    public void OfflineInstallCommandRequiresExactInteractiveApprovalInputs()
    {
        string bundle = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "aethersdr-bundle"));
        ReleaseUpdateConsoleCommandLine command =
            ReleaseUpdateConsoleCommandParser.Parse(
            [
                ReleaseUpdateConsoleCommandParser.InstallOfflineReleaseSwitch,
                bundle,
                ReleaseUpdateConsoleCommandParser.InstalledReleaseIdentitySwitch,
                "aethersdr-8.1.0",
                ReleaseUpdateConsoleCommandParser.InstalledVersionSwitch,
                "8.1.0",
                ReleaseUpdateConsoleCommandParser.ConfigurationSchemaVersionSwitch,
                "1",
                ReleaseUpdateConsoleCommandParser.ProtocolVersionSwitch,
                "3",
                ReleaseUpdateConsoleCommandParser.ApprovalSwitch
            ]);

        Assert.Equal(
            ReleaseUpdateConsoleCommandKind.InstallOfflineRelease,
            command.Command);
        Assert.Equal(bundle, command.BundleDirectory);
        Assert.Equal("aethersdr-8.1.0", command.InstalledReleaseIdentity);
        Assert.Equal("8.1.0", command.InstalledVersion);
        Assert.Equal(1, command.ConfigurationSchemaVersion);
        Assert.Equal(3, command.ProtocolVersion);
        Assert.True(command.ApprovalRequested);
        Assert.Null(command.UpdateChannel);
        Assert.Empty(command.PinnedReleaseIdentity);
    }

    [Fact]
    public void OfflineInstallCommandRejectsMissingApproval()
    {
        string bundle = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "aethersdr-bundle"));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ReleaseUpdateConsoleCommandParser.Parse(
            [
                ReleaseUpdateConsoleCommandParser.InstallOfflineReleaseSwitch,
                bundle,
                ReleaseUpdateConsoleCommandParser.InstalledReleaseIdentitySwitch,
                "aethersdr-8.1.0",
                ReleaseUpdateConsoleCommandParser.InstalledVersionSwitch,
                "8.1.0",
                ReleaseUpdateConsoleCommandParser.ConfigurationSchemaVersionSwitch,
                "1",
                ReleaseUpdateConsoleCommandParser.ProtocolVersionSwitch,
                "3"
            ]));

        Assert.Contains(
            ReleaseUpdateConsoleCommandParser.ApprovalSwitch,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RollbackCommandCarriesOnlyExactTransactionAndApproval()
    {
        const string transactionId = "0123456789abcdef0123456789abcdef";
        ReleaseUpdateConsoleCommandLine command =
            ReleaseUpdateConsoleCommandParser.Parse(
            [
                ReleaseUpdateConsoleCommandParser.RollbackTransactionSwitch,
                transactionId,
                ReleaseUpdateConsoleCommandParser.ApprovalSwitch
            ]);

        Assert.Equal(
            ReleaseUpdateConsoleCommandKind.RollbackTransaction,
            command.Command);
        Assert.Equal(transactionId, command.TransactionId);
        Assert.True(command.ApprovalRequested);
        Assert.Empty(command.BundleDirectory);
        Assert.Empty(command.InstalledVersion);
    }

    [Fact]
    public void SupervisorCommandAcceptsNoReleaseArguments()
    {
        ReleaseUpdateConsoleCommandLine command =
            ReleaseUpdateConsoleCommandParser.Parse(
            [ReleaseUpdateConsoleCommandParser.TransactionSupervisorSwitch]);

        Assert.Equal(
            ReleaseUpdateConsoleCommandKind.TransactionSupervisor,
            command.Command);
        Assert.Empty(command.ApplicationArguments);
    }

    [Fact]
    public void ServerAuthenticationDerivesFreshBoundedApprovalEvidence()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ClaimsIdentity identity = new(
        [
            new Claim("sub", "administrator-subject"),
            new Claim("iss", "https://issuer.example"),
            new Claim(ClaimTypes.Role, AetherRoles.Admin),
            new Claim(
                "auth_time",
                now.ToUnixTimeSeconds().ToString(
                    System.Globalization.CultureInfo.InvariantCulture))
        ],
        authenticationType: "test");
        ReleaseUpdateOperatorAuthenticationEvidenceFactory factory = new(
            Options.Create(
                new ReleaseActivationOperatorApprovalSettings
                {
                    AuthorityEnabled = true,
                    MaximumApprovalAgeSeconds = 300
                }),
            new FixedTimeProvider(now));

        ReleaseUpdateOperatorAuthenticationReport report =
            factory.Create(new ClaimsPrincipal(identity));

        Assert.True(report.Succeeded, report.Message);
        Assert.True(report.Authenticated);
        Assert.True(report.AdministratorAuthorized);
        Assert.True(report.ReauthenticationCurrent);
        Assert.NotNull(report.Evidence);
        Assert.Equal(64, report.Evidence!.SubjectBinding.Length);
        Assert.DoesNotContain(
            "administrator-subject",
            report.Evidence.SubjectBinding,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ServerAuthenticationRejectsStaleAuthTime()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ClaimsIdentity identity = new(
        [
            new Claim("sub", "administrator-subject"),
            new Claim(ClaimTypes.Role, AetherRoles.Admin),
            new Claim(
                "auth_time",
                now.AddMinutes(-10).ToUnixTimeSeconds().ToString(
                    System.Globalization.CultureInfo.InvariantCulture))
        ],
        authenticationType: "test");
        ReleaseUpdateOperatorAuthenticationEvidenceFactory factory = new(
            Options.Create(
                new ReleaseActivationOperatorApprovalSettings
                {
                    AuthorityEnabled = true,
                    MaximumApprovalAgeSeconds = 300
                }),
            new FixedTimeProvider(now));

        ReleaseUpdateOperatorAuthenticationReport report =
            factory.Create(new ClaimsPrincipal(identity));

        Assert.False(report.Succeeded);
        Assert.Equal(
            ReleaseUpdateOperatorAuthenticationFailureCode.ReauthenticationStale,
            report.FailureCode);
        Assert.Null(report.Evidence);
    }

    [Fact]
    public async Task SupervisorProtocolIsBoundedLengthPrefixedAndRoundTrips()
    {
        ReleaseUpdateTransactionReport report =
            ReleaseUpdateTransactionReport.Create(
                null,
                succeeded: true,
                ReleaseUpdateTransactionFailureCode.None,
                "No transaction.");
        ReleaseUpdateSupervisorResponse expected = new(
            ReleaseUpdateSupervisorProtocol.Version,
            report);
        using MemoryStream stream = new();

        await ReleaseUpdateSupervisor.WriteAsync(
            stream,
            expected,
            CancellationToken.None);
        stream.Position = 0;
        ReleaseUpdateSupervisorResponse actual =
            await ReleaseUpdateSupervisor.ReadAsync<
                ReleaseUpdateSupervisorResponse>(
                    stream,
                    CancellationToken.None);

        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Report, actual.Report);
    }

    [Fact]
    public void SupervisorReloadsOnlyWhenRollbackAuthorityIsRelinquishedOrConsumed()
    {
        ReleaseUpdateTransactionReport activated =
            ReleaseUpdateTransactionReport.Create(
                null,
                succeeded: true,
                ReleaseUpdateTransactionFailureCode.None,
                "Activated.",
                phase: ReleaseUpdateTransactionPhase.Completed,
                pointerChanged: true) with
            {
                RollbackReady = true
            };
        ReleaseUpdateTransactionReport prepareReload = activated with
        {
            Succeeded = false,
            FailureCode = ReleaseUpdateTransactionFailureCode.TransactionAlreadyActive,
            Message = "Reload before prepare."
        };
        ReleaseUpdateTransactionReport rolledBack =
            ReleaseUpdateTransactionReport.Create(
                null,
                succeeded: false,
                ReleaseUpdateTransactionFailureCode.HealthVerificationFailed,
                "Rolled back.",
                phase: ReleaseUpdateTransactionPhase.RolledBack,
                pointerChanged: true,
                rollbackPerformed: true);
        ReleaseUpdateTransactionReport reconciliation =
            ReleaseUpdateTransactionReport.Create(
                null,
                succeeded: false,
                ReleaseUpdateTransactionFailureCode.ReconciliationRequired,
                "Reconciliation required.",
                phase: ReleaseUpdateTransactionPhase.ReconciliationRequired,
                pointerChanged: true,
                rollbackPerformed: true,
                reconciliationRequired: true);

        Assert.False(ReleaseUpdateSupervisor.RequiresProcessReload(
            ReleaseUpdateSupervisorProtocol.Activate,
            activated));
        Assert.True(ReleaseUpdateSupervisor.RequiresProcessReload(
            ReleaseUpdateSupervisorProtocol.Prepare,
            prepareReload));
        Assert.True(ReleaseUpdateSupervisor.IsPrepareReloadBoundary(
            prepareReload));
        Assert.False(ReleaseUpdateSupervisor.IsPrepareReloadBoundary(
            activated));
        Assert.True(ReleaseUpdateSupervisor.RequiresProcessReload(
            ReleaseUpdateSupervisorProtocol.Activate,
            rolledBack));
        Assert.True(ReleaseUpdateSupervisor.RequiresProcessReload(
            ReleaseUpdateSupervisorProtocol.Rollback,
            rolledBack));
        Assert.False(ReleaseUpdateSupervisor.RequiresProcessReload(
            ReleaseUpdateSupervisorProtocol.Activate,
            reconciliation));
        Assert.False(ReleaseUpdateSupervisor.RequiresProcessReload(
            ReleaseUpdateSupervisorProtocol.Status,
            activated));
    }

    [Fact]
    public async Task PrepareRetriesUnchangedRequestAfterCompletedRollbackWindowReload()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string shortRoot = Path.Combine(
            Path.GetTempPath(),
            $"as-{Guid.NewGuid():N}");
        InstallationPaths paths = new(
            Path.Combine(shortRoot, "c"),
            Path.Combine(shortRoot, "s"),
            Path.Combine(shortRoot, "k"),
            Path.Combine(shortRoot, "r"),
            Path.Combine(shortRoot, "b"),
            Path.Combine(shortRoot, "l"));
        string socketDirectory = Path.Combine(
            paths.StateDirectory,
            ReleaseUpdateSupervisor.DirectoryName);
        Directory.CreateDirectory(socketDirectory);
        string socketPath = Path.Combine(
            socketDirectory,
            ReleaseUpdateSupervisor.SocketFileName);
        using Socket listener = new(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(4);

        ReleaseUpdateInstallRequest install = new(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "aethersdr-next-bundle")),
            "aethersdr-8.2.0",
            "8.2.0",
            1,
            3);
        ReleaseUpdateTransactionReport completed = new(
            Succeeded: true,
            ReleaseUpdateTransactionFailureCode.None,
            "Completed.",
            "0123456789abcdef0123456789abcdef",
            ReleaseUpdateTransactionPhase.Completed,
            SetupRevision: 7,
            InstalledReleaseIdentity: "aethersdr-8.1.0",
            TargetReleaseIdentity: "aethersdr-8.2.0",
            TargetVersion: "8.2.0",
            PackageCount: 4,
            FileCount: 100,
            PublishedBytes: 1000,
            InactiveReleasePublished: true,
            ConfigurationBackupReady: true,
            MigrationReady: true,
            OperatorApproved: true,
            TxLeaseAdmissionClosed: false,
            CurrentPointerChanged: true,
            ServiceControlCompleted: true,
            HealthVerified: true,
            RollbackReady: true,
            RollbackPerformed: false,
            RestartPending: false,
            ReconciliationRequired: false,
            ActivationCompleted: true);
        ReleaseUpdateTransactionReport reloadBoundary = completed with
        {
            Succeeded = false,
            FailureCode = ReleaseUpdateTransactionFailureCode.TransactionAlreadyActive,
            Message = "Reload before prepare."
        };
        ReleaseUpdateTransactionReport recovered = completed with
        {
            OperatorApproved = false,
            RollbackReady = false,
            Message = "Recovered without rollback authority."
        };
        ReleaseUpdateTransactionReport prepared = completed with
        {
            TransactionId = "abcdef0123456789abcdef0123456789",
            Phase = ReleaseUpdateTransactionPhase.AwaitingApproval,
            InstalledReleaseIdentity = "aethersdr-8.2.0",
            TargetReleaseIdentity = "aethersdr-8.3.0",
            TargetVersion = "8.3.0",
            OperatorApproved = false,
            CurrentPointerChanged = false,
            ServiceControlCompleted = false,
            HealthVerified = false,
            RollbackReady = true,
            ActivationCompleted = false,
            Message = "Prepared."
        };

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = Task.Run(async () =>
        {
            ReleaseUpdateSupervisorRequest? firstPrepare = null;
            for (int index = 0; index < 3; index++)
            {
                using Socket connection = await listener.AcceptAsync(timeout.Token);
                await using NetworkStream stream = new(connection, ownsSocket: false);
                ReleaseUpdateSupervisorRequest request =
                    await ReleaseUpdateSupervisor.ReadAsync<ReleaseUpdateSupervisorRequest>(
                        stream,
                        timeout.Token);
                ReleaseUpdateTransactionReport response;
                if (index == 0)
                {
                    Assert.Equal(ReleaseUpdateSupervisorProtocol.Prepare, request.Operation);
                    firstPrepare = request;
                    response = reloadBoundary;
                }
                else if (index == 1)
                {
                    Assert.Equal(ReleaseUpdateSupervisorProtocol.Status, request.Operation);
                    response = recovered;
                }
                else
                {
                    Assert.Equal(ReleaseUpdateSupervisorProtocol.Prepare, request.Operation);
                    Assert.Equal(firstPrepare, request);
                    response = prepared;
                }
                await ReleaseUpdateSupervisor.WriteAsync(
                    stream,
                    new ReleaseUpdateSupervisorResponse(
                        ReleaseUpdateSupervisorProtocol.Version,
                        response),
                    timeout.Token);
            }
        }, timeout.Token);

        ReleaseUpdateSupervisorClient client = new(paths);
        ReleaseUpdateTransactionReport result =
            await client.PrepareOfflineAsync(install, timeout.Token);
        await server;

        Assert.True(result.Succeeded);
        Assert.Equal(ReleaseUpdateTransactionPhase.AwaitingApproval, result.Phase);
        Assert.Equal(prepared.TransactionId, result.TransactionId);
    }

    [Fact]
    public async Task SupervisorProtocolRejectsOversizedLengthBeforeAllocation()
    {
        using MemoryStream stream = new();
        byte[] length = new byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(
            length,
            ReleaseUpdateSupervisorProtocol.MaximumMessageBytes + 1);
        await stream.WriteAsync(length);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReleaseUpdateSupervisor.ReadAsync<object>(
                stream,
                CancellationToken.None));
    }

    [Fact]
    public void HostRestartTransportIsDisabledByDefaultAndHasNoShellSurface()
    {
        InstallationPaths paths = CreatePaths();
        VerifiedReleaseActivationHostRestartTransport transport = new(
            Options.Create(new ReleaseActivationHostRestartSettings()),
            paths);

        VerifiedReleaseActivationHostRestartDiagnostics snapshot =
            transport.Snapshot;
        Assert.True(snapshot.Registered);
        Assert.False(snapshot.ExecutionEnabled);
        Assert.True(snapshot.ExactHostRestartPlanInputRegistered);
        Assert.True(snapshot.ExactPointerEvidenceInputRegistered);
        Assert.True(snapshot.DurablePreRestartMarkerRegistered);
        Assert.True(snapshot.DirectSystemctlRegistered);
        Assert.False(snapshot.ShellRegistered);
        Assert.False(snapshot.ArbitraryCommandRegistered);
        Assert.True(snapshot.PostBootVerificationRequired);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public void RecoveredHostRestartRemainsPendingWithoutAuthorityReconstruction()
    {
        ReleaseUpdateJournalDocument document = new(
            ReleaseUpdateTransactionJournal.SchemaVersion,
            "0123456789abcdef0123456789abcdef",
            ReleaseUpdateTransactionPhase.RestartPending,
            SetupRevision: 7,
            InstalledReleaseIdentity: "aethersdr-8.1.0",
            TargetReleaseIdentity: "aethersdr-8.2.0",
            TargetVersion: "8.2.0",
            UpdatedAt: DateTimeOffset.UtcNow,
            CurrentPointerChanged: true,
            RollbackPerformed: false,
            RestartPending: true,
            ReconciliationRequired: false);

        ReleaseUpdateTransactionReport report =
            ReleaseUpdateTransactionCoordinator.RecoveredStatus(document);

        Assert.True(report.Succeeded);
        Assert.Equal(ReleaseUpdateTransactionFailureCode.None, report.FailureCode);
        Assert.Equal(
            ReleaseUpdateTransactionPhase.RestartPending,
            report.Phase);
        Assert.True(report.RestartPending);
        Assert.False(report.ReconciliationRequired);
        Assert.True(report.CurrentPointerChanged);
        Assert.False(report.HealthVerified);
        Assert.False(report.RollbackReady);
        Assert.False(report.ActivationCompleted);
        Assert.False(report.OperatorApproved);
        Assert.False(report.TxLeaseAdmissionClosed);
    }

    [Fact]
    public void TransactionDiagnosticsKeepRadioAndTxCallersAbsent()
    {
        ReleaseUpdateTransactionDiagnostics diagnostics = new(
            Registered: true,
            ExecutionEnabled: false,
            LeaseDrainSeconds: 30,
            OfflinePreflightRegistered: true,
            VerifiedStagingRegistered: true,
            VerifiedExtractionRegistered: true,
            AtomicInactivePublicationRegistered: true,
            ActivationPlanAdaptationRegistered: true,
            ConfigurationBackupExecutionRegistered: true,
            MigrationExecutionRegistered: true,
            TxLeaseAdmissionClosureRegistered: true,
            RadioAuthoritativeSafetyEvidenceRegistered: true,
            ServiceControlExecutionRegistered: true,
            AtomicCurrentPointerSwitchRegistered: true,
            HealthVerificationRegistered: true,
            HostRestartExecutionRegistered: true,
            AutomaticRollbackRegistered: true,
            ManualRollbackRegistered: true,
            AuthenticatedApprovalRegistered: true,
            DurableJournalRegistered: true,
            CliCallerRegistered: true,
            AdminCallerRegistered: true,
            BrowserCallerRegistered: true,
            RadioCommandRegistered: false,
            TxCallerRegistered: false);

        Assert.True(diagnostics.HostRestartExecutionRegistered);
        Assert.False(diagnostics.RadioCommandRegistered);
        Assert.False(diagnostics.TxCallerRegistered);
    }

    private static InstallationPaths CreatePaths()
    {
        string root = Path.GetFullPath(
            Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-release-update-surfaces-{Guid.NewGuid():N}"));
        return new InstallationPaths(
            Path.Combine(root, "config"),
            Path.Combine(root, "state"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "releases"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "logs"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
