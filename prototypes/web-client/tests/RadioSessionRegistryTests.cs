using System.Security.Claims;
using System.Security.Cryptography;
using AetherSDR.Web.Radio;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class RadioSessionRegistryTests
{
    [Fact]
    public void MobileReconnectGracePreservesTheLiveRadioSession()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(60),
            RadioSessionRegistry.IdleTimeout);
    }

    private const string BrowserA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string BrowserB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BrowserC = "cccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task SessionSupervisesOneDisarmedWatchdogAndRemovesItOnShutdown()
    {
        StationTxIndependentWatchdogRegistry watchdogs =
            CreateIndependentWatchdogRegistry();
        (RadioSessionRegistry registry, _, _) = CreateRegistry(
            independentWatchdogs: watchdogs);
        await registry.StartAsync(CancellationToken.None);
        try
        {
            RadioSession session = await registry.GetDefaultAsync(
                CreateUser("operator-a"),
                BrowserA,
                CancellationToken.None);

            StationTxIndependentWatchdogAggregate aggregate =
                await WaitForWatchdogsAsync(
                    watchdogs,
                    snapshot => snapshot.SessionCount == 1 &&
                        snapshot.RunningProcessCount == 1 &&
                        snapshot.ConnectedProcessCount == 1);
            Assert.True(aggregate.SupervisionRegistered);
            Assert.Equal("supervised-disarmed", aggregate.State);
            Assert.Equal(0, aggregate.RegisteredIdentityCount);
            Assert.False(aggregate.CommandTransportAvailable);
            Assert.False(aggregate.ArmingAvailable);

            StationTxIndependentWatchdogDiagnostics process =
                session.GetDiagnostics().TxLifecycle!.IndependentWatchdog;
            Assert.True(process.SupervisionEnabled);
            Assert.True(process.ProcessRunning);
            Assert.True(process.IpcConnected);
            Assert.Equal("Disarmed", process.State);
            Assert.False(process.Registered);
            Assert.False(process.Connected);
            Assert.False(process.LeaseBound);
            Assert.Equal(0, process.LastSequence);
            Assert.False(process.RadioCommandTransportAvailable);
            Assert.False(process.ArmingAvailable);
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }

        StationTxIndependentWatchdogAggregate stopped =
            await WaitForWatchdogsAsync(
                watchdogs,
                snapshot => snapshot.SessionCount == 0);
        Assert.Equal("supervised-empty-disarmed", stopped.State);
        Assert.Equal(0, stopped.RunningProcessCount);
        Assert.Equal(0, stopped.ConnectedProcessCount);
        Assert.Equal(0, stopped.RegisteredIdentityCount);
    }

    [Fact]
    public async Task LocalSessionInheritsLeaseFoundationWithoutKeyCapability()
    {
        (RadioSessionRegistry registry, _, _) = CreateRegistry(
            browserTxLeaseEnabled: true);
        await registry.StartAsync(CancellationToken.None);
        try
        {
            ClaimsPrincipal user = CreateUser(
                "operator-a",
                "Aether.Transmit");
            RadioSession session = await registry.GetDefaultAsync(
                user,
                BrowserA,
                CancellationToken.None);
            RadioClientConnection connection =
                session.Coordinator.Register(user);
            try
            {
                BrowserTxCapability capability =
                    session.Coordinator.GetBrowserTxCapability(connection);

                Assert.True(session.Coordinator.BrowserTxLeaseEnabled);
                Assert.False(session.Coordinator.AllowTransmit);
                Assert.False(session.Coordinator.Snapshot.CanTransmit);
                Assert.True(capability.LeaseConfigured);
                Assert.True(capability.RoleAuthorized);
                Assert.False(capability.KeyingAvailable);
                Assert.False(capability.MicrophoneAvailable);
                Assert.False(capability.TuneAvailable);
                Assert.False(capability.CwAvailable);

                await session.TxLifecycle.FlushAsync();
                StationTxLifecycleDiagnostics? lifecycle =
                    session.GetDiagnostics().TxLifecycle;
                Assert.NotNull(lifecycle);
                Assert.True(lifecycle.Registered);
                Assert.True(lifecycle.BrowserConnected);
                Assert.True(lifecycle.Authenticated);
                Assert.False(lifecycle.ProductionTransmitEnabled);
                Assert.False(lifecycle.CommandTransportAvailable);
                Assert.False(lifecycle.EmergencyUnkeyTransportAvailable);
                Assert.Equal("Disabled", lifecycle.GateState);
                Assert.Equal("Disarmed", lifecycle.SafetyState);
            }
            finally
            {
                session.Coordinator.Unregister(connection.ClientId);
            }
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SessionInheritsReadyTrustVerifierWithoutCommandCapability()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"aethersdr-session-command-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string keyPath = Path.Combine(directory, "station-command.pem");
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(keyPath, key.ExportSubjectPublicKeyInfoPem());
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
            File.SetUnixFileMode(
                keyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        using StationTxCommandTrustRegistry trust = new(
            Options.Create(new StationTxCommandTrustSettings
            {
                VerificationEnabled = true,
                Keys =
                [
                    new StationTxCommandTrustKeySettings
                    {
                        KeyId = "station-key-a",
                        PublicKeyPath = keyPath
                    }
                ]
            }),
            NullLogger<StationTxCommandTrustRegistry>.Instance);
        (RadioSessionRegistry registry, _, _) = CreateRegistry(
            stationCommandTrust: trust);
        await registry.StartAsync(CancellationToken.None);
        try
        {
            RadioSession session = await registry.GetDefaultAsync(
                CreateUser("operator-a"),
                BrowserA,
                CancellationToken.None);

            await session.TxLifecycle.FlushAsync();
            StationTxLifecycleDiagnostics lifecycle =
                Assert.IsType<StationTxLifecycleDiagnostics>(
                    session.GetDiagnostics().TxLifecycle);
            Assert.True(lifecycle.StationCommandSignatureVerificationAvailable);
            Assert.False(lifecycle.StationCommandBoundaryEnabled);
            Assert.False(lifecycle.StationCommandAdapterRegistered);
            Assert.False(lifecycle.StationCommandArmingAvailable);
            Assert.False(lifecycle.StationCommandSetTransmitAvailable);
            Assert.Equal(0, lifecycle.StationCommandAuditCount);
            Assert.True(lifecycle.StationCommandAdapterComposition.Registered);
            Assert.False(
                lifecycle.StationCommandAdapterComposition.ExecutorAttached);
            Assert.False(
                lifecycle.StationCommandAdapterComposition.CommandAdapterRegistered);
            Assert.Equal(
                "executor-unattached",
                lifecycle.StationCommandAdapterComposition.Reason);
            Assert.Equal("Disabled", lifecycle.GateState);
            Assert.Equal("Disarmed", lifecycle.SafetyState);
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SessionAttachesDisabledAdapterAndCommandCompositionWithoutBrowserIngress()
    {
        using StationTxCommandSigningAuthority signing = new(
            Options.Create(new StationTxCommandSigningSettings()),
            NullLogger<StationTxCommandSigningAuthority>.Instance);
        using StationTxCommandTrustRegistry trust = new(
            Options.Create(new StationTxCommandTrustSettings()),
            NullLogger<StationTxCommandTrustRegistry>.Instance);
        StationTxCommandEnvelopeCoordinator commandCoordinator = new(
            Options.Create(
                new StationTxCommandEnvelopeCoordinatorSettings
                {
                    SubmissionEnabled = false
                }),
            signing,
            trust,
            NullLogger<StationTxCommandEnvelopeCoordinator>.Instance);
        (RadioSessionRegistry registry, _, _) = CreateRegistry(
            stationCommandTrust: trust,
            stationCommandCoordinator: commandCoordinator);
        await registry.StartAsync(CancellationToken.None);
        try
        {
            RadioSession session = await registry.GetDefaultAsync(
                CreateUser("operator-a"),
                BrowserA,
                CancellationToken.None);

            StationTxLifecycleDiagnostics lifecycle =
                Assert.IsType<StationTxLifecycleDiagnostics>(
                    session.GetDiagnostics().TxLifecycle);
            StationTxCommandAdapterCompositionDiagnostics adapterComposition =
                lifecycle.StationCommandAdapterComposition;
            Assert.True(adapterComposition.Registered);
            Assert.False(adapterComposition.ExecutorAttached);
            Assert.False(adapterComposition.ExecutorRegistered);
            Assert.False(adapterComposition.ExecutorArmingAvailable);
            Assert.False(adapterComposition.ExecutorSetTransmitAvailable);
            Assert.False(adapterComposition.AuthoritySnapshotAvailable);
            Assert.False(adapterComposition.CommandAdapterRegistered);
            Assert.False(adapterComposition.ArmingAvailable);
            Assert.False(adapterComposition.SetTransmitAvailable);
            Assert.Equal(0, adapterComposition.AttemptCount);
            Assert.Equal(0, adapterComposition.ForwardedCount);
            Assert.Equal("none", adapterComposition.LastOutcome);
            Assert.Equal("executor-unattached", adapterComposition.Reason);

            StationTxCommandSessionCompositionDiagnostics composition =
                lifecycle.StationCommandSessionComposition;
            Assert.True(composition.Registered);
            Assert.True(composition.CoordinatorAttached);
            Assert.True(composition.BoundaryAttached);
            Assert.False(composition.SubmissionEnabled);
            Assert.False(composition.SigningAvailable);
            Assert.False(composition.SignatureVerificationAvailable);
            Assert.False(composition.BoundaryEnabled);
            Assert.False(composition.BoundarySignatureVerificationAvailable);
            Assert.False(composition.CommandAdapterRegistered);
            Assert.False(composition.ArmingAvailable);
            Assert.False(composition.SetTransmitAvailable);
            Assert.False(composition.AuthoritySnapshotAvailable);
            Assert.False(composition.SubmissionAvailable);
            Assert.Equal(0, composition.AttemptCount);
            Assert.Equal(0, composition.ForwardedCount);
            Assert.Equal("none", composition.LastOutcome);
            Assert.Equal("submission-disabled", composition.Reason);

            Assert.DoesNotContain(
                typeof(RadioSessionRegistry)
                    .GetConstructors(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance)
                    .SelectMany(constructor => constructor.GetParameters()),
                parameter => parameter.ParameterType ==
                    typeof(IStationTxCommandAdapterExecutor));
            Assert.DoesNotContain(
                typeof(RadioCoordinator)
                    .GetMethods(
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.DeclaredOnly),
                method => method.Name.Contains(
                    "StationCommand",
                    StringComparison.Ordinal));
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SameBrowserConnectionAndRadioReuseOneGuiSession()
    {
        (RadioSessionRegistry registry, _, _) = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        try
        {
            ClaimsPrincipal user = CreateUser("operator-a");

            RadioSession first = await registry.GetDefaultAsync(
                user,
                BrowserA,
                CancellationToken.None);
            RadioSession second = await registry.GetDefaultAsync(
                user,
                BrowserA,
                CancellationToken.None);

            Assert.Same(first, second);
            Assert.Equal(first.SessionId, second.SessionId);
            Assert.Same(first.Coordinator, second.Coordinator);
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task BandwidthProfileChangesKeepTheSessionAndLiveStateOwner()
    {
        (RadioSessionRegistry registry, _, _) = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        try
        {
            ClaimsPrincipal user = CreateUser("operator-a");
            RadioSession session = await registry.GetDefaultAsync(
                user,
                BrowserA,
                CancellationToken.None);
            RadioCoordinator coordinator = session.Coordinator;
            RadioSnapshot before = coordinator.Snapshot;

            Assert.True(session.SetLowBandwidth(true));
            Assert.True(session.Selection.LowBandwidth);
            Assert.Same(coordinator, session.Coordinator);
            Assert.Equal(before.SessionId, coordinator.Snapshot.SessionId);
            Assert.Equal(
                before.Slices.Select(slice => slice.FrequencyHz),
                coordinator.Snapshot.Slices.Select(slice => slice.FrequencyHz));

            Assert.True(session.SetLowBandwidth(false));
            Assert.False(session.Selection.LowBandwidth);
            Assert.Same(coordinator, session.Coordinator);
            Assert.Equal(before.SessionId, coordinator.Snapshot.SessionId);
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task InitialBandwidthProfileIsAppliedBeforeTransportStarts()
    {
        (RadioSessionRegistry registry, RadioSelectionManager catalog, _) =
            CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        try
        {
            ClaimsPrincipal user = CreateUser("operator-a");
            RadioSession session = await registry.GetOrCreateAsync(
                user,
                BrowserA,
                catalog.Selected,
                initialLowBandwidth: true,
                cancellationToken: CancellationToken.None);

            Assert.True(session.Selection.LowBandwidth);
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SameUserGetsSeparateGuiSessionsForSeparateBrowserConnections()
    {
        (RadioSessionRegistry registry, _, _) = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        try
        {
            ClaimsPrincipal user = CreateUser("operator-a");
            RadioSession first = await registry.GetDefaultAsync(
                user,
                BrowserA,
                CancellationToken.None);
            RadioSession second = await registry.GetDefaultAsync(
                user,
                BrowserB,
                CancellationToken.None);

            Assert.NotEqual(first.SessionId, second.SessionId);
            Assert.NotEqual(first.BrowserClientId, second.BrowserClientId);
            Assert.NotEqual(first.GuiClientId, second.GuiClientId);
            Assert.NotSame(first.Coordinator, second.Coordinator);
            Assert.Equal(2, registry.GetSnapshots().Count);
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DifferentUsersHaveSeparateStateOnTheSameRadio()
    {
        (RadioSessionRegistry registry, _, _) = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        try
        {
            ClaimsPrincipal firstUser = CreateUser("operator-a");
            ClaimsPrincipal secondUser = CreateUser("operator-b");
            RadioSession first = await registry.GetDefaultAsync(
                firstUser,
                BrowserA,
                CancellationToken.None);
            RadioSession second = await registry.GetDefaultAsync(
                secondUser,
                BrowserA,
                CancellationToken.None);

            Assert.NotEqual(first.SessionId, second.SessionId);
            Assert.NotSame(first.Coordinator, second.Coordinator);
            Assert.NotSame(first.Selection, second.Selection);

            Assert.True(first.SetLowBandwidth(true));
            Assert.True(first.Selection.LowBandwidth);
            Assert.False(second.Selection.LowBandwidth);

            Assert.True(
                registry.TryGetOwned(
                    first.SessionId,
                    firstUser,
                    out RadioSession? owned));
            Assert.Same(first, owned);
            Assert.False(
                registry.TryGetOwned(
                    first.SessionId,
                    secondUser,
                    out _));
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OneUserGetsSeparateSessionsForDifferentRadios()
    {
        (
            RadioSessionRegistry registry,
            RadioSelectionManager catalog,
            _) =
            CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        try
        {
            catalog.Upsert(
                new DiscoveredFlexRadio(
                    "flex:RADIO-B",
                    "FLEX-6600",
                    "FLEX-6600",
                    "RADIO-B",
                    "Backup",
                    "K1ABC",
                    "192.168.7.20",
                    4992,
                    "Available",
                    "4.2.20",
                    false,
                    true,
                    DateTimeOffset.UtcNow));
            Assert.True(
                catalog.TryResolve(
                    "flex:RADIO-B",
                    out SelectedRadioEndpoint secondEndpoint,
                    out string? error),
                error);

            ClaimsPrincipal user = CreateUser("operator-a");
            RadioSession first = await registry.GetDefaultAsync(
                user,
                BrowserA,
                CancellationToken.None);
            RadioSession second = await registry.GetOrCreateAsync(
                user,
                BrowserA,
                secondEndpoint,
                CancellationToken.None);

            Assert.NotEqual(first.SessionId, second.SessionId);
            Assert.NotEqual(first.Endpoint.Host, second.Endpoint.Host);
            Assert.NotSame(first.Coordinator, second.Coordinator);
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExclusivePolicyRejectsASecondAccount()
    {
        (
            RadioSessionRegistry registry,
            RadioSelectionManager catalog,
            RadioAccessPolicyStore policies) =
            CreateRegistry();
        policies.Update(
            catalog.Selected.RadioId,
            RadioAccessModes.Exclusive,
            null,
            "administrator");
        await registry.StartAsync(CancellationToken.None);
        try
        {
            await registry.GetDefaultAsync(
                CreateUser("operator-a"),
                BrowserA,
                CancellationToken.None);

            RadioAccessDeniedException exception =
                await Assert.ThrowsAsync<RadioAccessDeniedException>(
                    () => registry.GetDefaultAsync(
                        CreateUser("operator-b"),
                        BrowserB,
                        CancellationToken.None));

            Assert.Equal(catalog.Selected.RadioId, exception.RadioId);
            Assert.Single(registry.GetSnapshots());
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReservationAllowsOnlyTheNamedAccount()
    {
        (
            RadioSessionRegistry registry,
            RadioSelectionManager catalog,
            RadioAccessPolicyStore policies) =
            CreateRegistry();
        policies.Update(
            catalog.Selected.RadioId,
            RadioAccessModes.Shared,
            "operator-a",
            "administrator");
        await registry.StartAsync(CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<RadioAccessDeniedException>(
                () => registry.GetDefaultAsync(
                    CreateUser("operator-b"),
                    BrowserB,
                    CancellationToken.None));
            RadioSession allowed = await registry.GetDefaultAsync(
                CreateUser("operator-a"),
                BrowserA,
                CancellationToken.None);

            Assert.Equal("operator-a", allowed.UserId);
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AdministratorCanReachAReservedRadio()
    {
        (
            RadioSessionRegistry registry,
            RadioSelectionManager catalog,
            RadioAccessPolicyStore policies) =
            CreateRegistry();
        policies.Update(
            catalog.Selected.RadioId,
            RadioAccessModes.Shared,
            "operator-a",
            "administrator");
        await registry.StartAsync(CancellationToken.None);
        try
        {
            RadioSession allowed = await registry.GetDefaultAsync(
                CreateUser("administrator", AetherSDR.Web.Auth.AetherRoles.Admin),
                BrowserA,
                CancellationToken.None);

            Assert.Equal("administrator", allowed.UserId);
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ForceTerminationReleasesOnlyTheSelectedUsersRadioSession()
    {
        (
            RadioSessionRegistry registry,
            RadioSelectionManager catalog,
            _) =
            CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        try
        {
            RadioSession first = await registry.GetDefaultAsync(
                CreateUser("operator-a"),
                BrowserA,
                CancellationToken.None);
            await registry.GetDefaultAsync(
                CreateUser("operator-a"),
                BrowserB,
                CancellationToken.None);
            await registry.GetDefaultAsync(
                CreateUser("operator-b"),
                BrowserC,
                CancellationToken.None);

            int removed = await registry.TerminateUserSessionsAsync(
                catalog.Selected.RadioId,
                "operator-a");

            Assert.Equal(2, removed);
            Assert.DoesNotContain(
                registry.GetSnapshots(),
                session => session.SessionId == first.SessionId);
            Assert.Single(registry.GetSnapshots());
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OneGuiSessionAllowsOnlyOneActiveWebSocket()
    {
        (RadioSessionRegistry registry, _, _) = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        try
        {
            ClaimsPrincipal user = CreateUser("operator-a");
            RadioSession session = await registry.GetDefaultAsync(
                user,
                BrowserA,
                CancellationToken.None);

            Assert.True(
                registry.TryAcquire(
                    session.SessionId,
                    user,
                    out RadioSession? first));
            Assert.Same(session, first);
            Assert.False(
                registry.TryAcquire(
                    session.SessionId,
                    user,
                    out _));

            session.ReleaseClient();
            Assert.True(
                registry.TryAcquire(
                    session.SessionId,
                    user,
                    out _));
            session.ReleaseClient();
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    [Trait("Category", "Soak")]
    public async Task TenBrowserInterruptionsReuseStateWithoutDuplicateSockets()
    {
        (RadioSessionRegistry registry, _, _) = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        try
        {
            ClaimsPrincipal user = CreateUser("operator-a");
            RadioSession session = await registry.GetDefaultAsync(
                user,
                BrowserA,
                CancellationToken.None);
            RadioCoordinator coordinator = session.Coordinator;
            RadioSnapshot original = coordinator.Snapshot;

            Assert.True(
                registry.TryAcquire(
                    session.SessionId,
                    user,
                    out RadioSession? acquired));
            Assert.Same(session, acquired);

            for (int interruption = 0; interruption < 10; interruption++)
            {
                session.ReleaseClient();
                await Task.Delay(2);

                RadioSession recovered = await registry.GetDefaultAsync(
                    user,
                    BrowserA,
                    CancellationToken.None);
                Assert.Same(session, recovered);
                Assert.Same(coordinator, recovered.Coordinator);
                Assert.Equal(original, recovered.Coordinator.Snapshot);
                Assert.True(
                    registry.TryAcquire(
                        recovered.SessionId,
                        user,
                        out RadioSession? reacquired));
                Assert.Same(session, reacquired);
                Assert.False(
                    registry.TryAcquire(
                        recovered.SessionId,
                        user,
                        out _));
                Assert.Equal(1, session.ClientCount);
                Assert.Single(registry.GetSnapshots());
            }

            RadioBrowserReconnectDiagnostics reconnect =
                session.GetDiagnostics().Reconnect;
            Assert.Equal(21, reconnect.ConnectionAttempts);
            Assert.Equal(11, reconnect.SuccessfulConnections);
            Assert.Equal(10, reconnect.Reconnects);
            Assert.Equal(10, reconnect.RejectedConnections);
            Assert.NotNull(reconnect.LastConnectedAt);
            Assert.NotNull(reconnect.LastDisconnectedAt);
            Assert.NotNull(reconnect.LastRecoveryMilliseconds);
            session.ReleaseClient();
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task MalformedBrowserClientIdIsRejectedAtTheBoundary()
    {
        (RadioSessionRegistry registry, _, _) = CreateRegistry();
        await registry.StartAsync(CancellationToken.None);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => registry.GetDefaultAsync(
                    CreateUser("operator-a"),
                    "not-a-guid",
                    CancellationToken.None));
            Assert.Empty(registry.GetSnapshots());
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    private static (
        RadioSessionRegistry Registry,
        RadioSelectionManager Catalog,
        RadioAccessPolicyStore Policies) CreateRegistry(
            bool browserTxLeaseEnabled = false,
            StationTxIndependentWatchdogRegistry? independentWatchdogs = null,
            StationTxCommandTrustRegistry? stationCommandTrust = null,
            StationTxCommandEnvelopeCoordinator? stationCommandCoordinator = null)
    {
        IOptions<RadioSettings> options = Options.Create(
            new RadioSettings
            {
                Mode = "Simulation",
                Host = "192.168.7.10",
                TcpPort = 4992,
                BrowserTxLeaseEnabled = browserTxLeaseEnabled,
                SessionId = "unused-global-session"
            });
        RadioSelectionManager catalog = new(options);
        RadioAccessPolicyStore policies = new(
            Path.Combine(
                Path.GetTempPath(),
                "aethersdr-web-tests",
                Guid.NewGuid().ToString("N"),
                "policies.json"),
            NullLogger<RadioAccessPolicyStore>.Instance);
        RadioSessionRegistry registry = new(
            catalog,
            policies,
            options,
            new TxLeaseManager(),
            new RadioTxOccupancyRegistry(),
            NullLoggerFactory.Instance,
            NullLogger<RadioSessionRegistry>.Instance,
            remoteSettings: null,
            independentWatchdogs,
            stationCommandTrust,
            stationCommandCoordinator);
        return (registry, catalog, policies);
    }

    private static StationTxIndependentWatchdogRegistry
        CreateIndependentWatchdogRegistry()
    {
        string hostAssembly = typeof(global::AetherSDR.TxWatchdog.Program)
            .Assembly.Location;
        string hostDirectory = Path.GetDirectoryName(hostAssembly) ??
            throw new InvalidOperationException(
                "The watchdog host directory is unavailable.");
        string executable = Path.Combine(
            hostDirectory,
            OperatingSystem.IsWindows()
                ? "AetherSDR.TxWatchdog.exe"
                : "AetherSDR.TxWatchdog");
        Assert.True(File.Exists(executable), executable);
        executable = EnsureExecutableWatchdogHost(executable, hostAssembly);
        return new StationTxIndependentWatchdogRegistry(
            Options.Create(new IndependentTxWatchdogSettings
            {
                Enabled = true,
                ExecutablePath = executable,
                RequestTimeoutMilliseconds = 2000,
                RestartDelayMilliseconds = 100
            }),
            new TestWebHostEnvironment(),
            NullLoggerFactory.Instance);
    }

    private static string EnsureExecutableWatchdogHost(
        string executable,
        string hostAssembly)
    {
        if (OperatingSystem.IsWindows() ||
            (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) != 0)
        {
            return executable;
        }

        string dotnetHost =
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            Environment.ProcessPath ??
            throw new InvalidOperationException(
                "The dotnet host path is unavailable.");
        Assert.True(File.Exists(dotnetHost), dotnetHost);
        string wrapperDirectory = Path.Combine(
            Path.GetTempPath(),
            "aethersdr-web-tests",
            $"watchdog-wrapper-{Environment.ProcessId}");
        Directory.CreateDirectory(wrapperDirectory);
        File.SetUnixFileMode(
            wrapperDirectory,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        string wrapper = Path.Combine(
            wrapperDirectory,
            "AetherSDR.TxWatchdog");
        File.WriteAllText(
            wrapper,
            $"#!/bin/sh{Environment.NewLine}" +
            $"exec {ShellQuote(dotnetHost)} {ShellQuote(hostAssembly)} \"$@\"" +
            Environment.NewLine);
        File.SetUnixFileMode(
            wrapper,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        return wrapper;
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static async Task<StationTxIndependentWatchdogAggregate>
        WaitForWatchdogsAsync(
            StationTxIndependentWatchdogRegistry watchdogs,
            Func<StationTxIndependentWatchdogAggregate, bool> predicate)
    {
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            StationTxIndependentWatchdogAggregate snapshot =
                watchdogs.Snapshot;
            if (predicate(snapshot))
            {
                return snapshot;
            }
            await Task.Delay(25, timeout.Token);
        }
        throw new TimeoutException(
            "The independent watchdog registry did not reach the expected state.");
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } =
            "AetherSDR.Web.Tests";
        public IFileProvider WebRootFileProvider { get; set; } =
            new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private static ClaimsPrincipal CreateUser(
        string userId,
        params string[] roles) =>
        new(
            new ClaimsIdentity(
                [
                    new Claim("oid", userId),
                    new Claim(ClaimTypes.NameIdentifier, userId),
                    .. roles.Select(role => new Claim(ClaimTypes.Role, role))
                ],
                authenticationType: "test"));
}
