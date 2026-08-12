using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationInstallerCoordinatorTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GatewayPlanIsExactDeterministicAndOrdered()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        InstallationInstallerSelection selection = new(
            InstallationInstallerArchitecture.LinuxX64,
            InstallationReverseProxyMode.ManagedCaddy,
            "2026.8.0");
        FakeHost host = new();
        using InstallationInstallerCoordinator coordinator =
            new(store, host);

        InstallationInstallerPlanReport first =
            await coordinator.PlanAsync(selection);
        InstallationInstallerPlanReport second =
            await coordinator.PlanAsync(selection);

        Assert.Equal(first.PlanId, second.PlanId);
        Assert.Equal(64, first.PlanId.Length);
        Assert.All(first.PlanId, character =>
            Assert.True(
                char.IsAsciiHexDigit(character) &&
                !char.IsAsciiLetterUpper(character)));
        Assert.Equal(
            ["aethersdr", "aetherremote"],
            first.ServiceUsers);
        Assert.Equal(20, first.Directories.Count);
        Assert.Contains(
            first.Directories,
            directory => directory.EndsWith(
                "/secrets/data-protection",
                StringComparison.Ordinal));
        Assert.Contains(
            first.Directories,
            directory => directory.EndsWith(
                "/identity",
                StringComparison.Ordinal));
        Assert.Contains(
            first.Directories,
            directory => directory.EndsWith(
                "/station-engine/data-protection",
                StringComparison.Ordinal));
        Assert.Contains(
            first.Directories,
            directory => directory.EndsWith(
                "/aetherremote/broker",
                StringComparison.Ordinal));
        Assert.Contains(
            first.Directories,
            directory => directory.EndsWith(
                "/aetherremote/station-engine",
                StringComparison.Ordinal));
        Assert.Equal(
            [
                "aethersdr-web.service",
                "aethersdr-release-updater.service",
                "aetherremote-broker.service",
                "aetherremote-station-engine.service"
            ],
            first.Services);
        Assert.Equal(
            InstallationInstallerActionKind.EnsureServiceUser,
            first.Actions[0].Kind);
        Assert.Equal(41, first.Actions.Count);
        InstallationInstallerPlanAction release = Assert.Single(
            first.Actions,
            action => action.Kind ==
                InstallationInstallerActionKind.InstallVerifiedRelease);
        Assert.Equal("2026.8.0/linux-x64", release.Target);
        InstallationInstallerPlanAction adoption = Assert.Single(
            first.Actions,
            action => action.Kind ==
                InstallationInstallerActionKind.AdoptSetupIdentityState);
        Assert.Equal(Path.Combine(temporary.Path, "state"), adoption.Target);
        InstallationInstallerPlanAction identity = Assert.Single(
            first.Actions,
            action => action.Kind ==
                InstallationInstallerActionKind.InitializeIdentityDatabase);
        Assert.EndsWith(
            Path.Combine("identity", "aethersdr-identity.db"),
            identity.Target,
            StringComparison.Ordinal);
        Assert.Equal(release.Order + 1, identity.Order);
        Assert.Contains(
            first.Actions,
            action =>
                action.Kind ==
                    InstallationInstallerActionKind.ConfigureReverseProxy &&
                action.Target ==
                    InstallationReverseProxyMode.ManagedCaddy.ToString());
        Assert.Contains(
            first.Actions,
            action =>
                action.Kind == InstallationInstallerActionKind.VerifyHealth &&
                action.Target == "https://radio.example.org/healthz");
        Assert.Equal(
            Enumerable.Range(1, first.Actions.Count),
            first.Actions.Select(action => action.Order));
        Assert.False(first.InstallTransmitSupport);
        Assert.Equal(0, host.TotalCalls);
    }

    [Fact]
    public async Task ExternalAuthenticationIsExactAndSecretFreeInPlan()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        InstallationInstallerSelection selection = new(
            InstallationInstallerArchitecture.LinuxX64,
            InstallationReverseProxyMode.ManagedCaddy,
            "2026.8.0")
        {
            Authentication = new(
                InstallationInstallerAuthenticationMode
                    .CombinedOpenIdConnect,
                "primary",
                "https://issuer.example/",
                "client-id")
        };

        InstallationInstallerPlanReport plan =
            await new InstallationInstallerCoordinator(store, new FakeHost())
                .PlanAsync(selection);

        Assert.Equal(selection.Authentication, plan.Authentication);
        Assert.Equal(42, plan.Actions.Count);
        Assert.Contains(
            plan.Actions,
            action =>
                action.Kind ==
                    InstallationInstallerActionKind
                        .InstallAuthenticationClientSecret &&
                action.Target ==
                    "/var/lib/aethersdr/secrets/auth-client-secret");
        Assert.Contains(
            plan.Actions,
            action =>
                action.Kind ==
                    InstallationInstallerActionKind
                        .ConfigureGatewayEnvironment &&
                action.Target == "/etc/aethersdr/environment");
        Assert.DoesNotContain(
            "secret-value",
            System.Text.Json.JsonSerializer.Serialize(plan),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LanInternalCertificateTrustIsOrderedAfterCaddyActivation()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        InstallationInstallerPlanReport plan =
            await new InstallationInstallerCoordinator(store, new FakeHost())
                .PlanAsync(new(
                    InstallationInstallerArchitecture.LinuxX64,
                    InstallationReverseProxyMode.LanInternalCertificate,
                    "2026.8.0"));

        InstallationInstallerPlanAction trust = Assert.Single(
            plan.Actions,
            action => action.Kind ==
                InstallationInstallerActionKind.TrustInternalCertificate);
        InstallationInstallerPlanAction caddy = Assert.Single(
            plan.Actions,
            action =>
                action.Kind ==
                    InstallationInstallerActionKind.ActivateSystemdUnit &&
                action.Target == "caddy.service");
        InstallationInstallerPlanAction health = Assert.Single(
            plan.Actions,
            action =>
                action.Kind == InstallationInstallerActionKind.VerifyHealth);

        Assert.Equal(
            "/usr/local/share/ca-certificates/aethersdr-caddy-local.crt",
            trust.Target);
        Assert.True(caddy.Order < trust.Order);
        Assert.True(trust.Order < health.Order);
    }

    [Fact]
    public async Task LinuxSystemPlanSeparatesRootOwnedInstallerEvidence()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        InstallationSetupState configured = await store.LoadAsync();
        InstallationSetupState linux = configured with
        {
            Paths = InstallationPaths.Resolve(
                temporary.Path,
                InstallationPathLayout.LinuxSystem)
        };

        InstallationInstallerPlanReport plan =
            InstallationInstallerPlanComposer.Compose(
                linux,
                DefaultSelection());

        Assert.Equal(7, plan.SchemaVersion);
        Assert.Contains(
            "/var/lib/aethersdr/identity",
            plan.Directories);
        Assert.Contains(
            "/var/lib/aethersdr-installer",
            plan.Directories);
        Assert.Contains(
            plan.Actions,
            action =>
                action.Kind ==
                    InstallationInstallerActionKind.InitializeIdentityDatabase &&
                action.Target ==
                    "/var/lib/aethersdr/identity/aethersdr-identity.db");
        Assert.Contains(
            "/var/lib/aethersdr-installer/releases",
            plan.Directories);
        Assert.DoesNotContain(
            "/var/lib/aethersdr/installer",
            plan.Directories);
    }

    [Fact]
    public async Task PlanIdentityChangesWithExactInstallerSelection()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        using InstallationInstallerCoordinator coordinator =
            new(store, new FakeHost());

        InstallationInstallerPlanReport x64 =
            await coordinator.PlanAsync(new(
                InstallationInstallerArchitecture.LinuxX64,
                InstallationReverseProxyMode.ExistingNginx,
                "2026.8.0"));
        InstallationInstallerPlanReport arm64 =
            await coordinator.PlanAsync(new(
                InstallationInstallerArchitecture.LinuxArm64,
                InstallationReverseProxyMode.ExistingNginx,
                "2026.8.0"));
        InstallationInstallerPlanReport caddy =
            await coordinator.PlanAsync(new(
                InstallationInstallerArchitecture.LinuxX64,
                InstallationReverseProxyMode.ManagedCaddy,
                "2026.8.0"));

        Assert.NotEqual(x64.PlanId, arm64.PlanId);
        Assert.NotEqual(x64.PlanId, caddy.PlanId);
    }

    [Fact]
    public async Task ProxySelectionMatchesInstalledTopology()
    {
        using TemporaryDirectory gatewayDirectory = new();
        InstallationSetupStore gateway = await CreateConfiguredStoreAsync(
            gatewayDirectory.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        using InstallationInstallerCoordinator gatewayCoordinator =
            new(gateway, new FakeHost());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gatewayCoordinator.PlanAsync(new(
                InstallationInstallerArchitecture.LinuxX64,
                InstallationReverseProxyMode.None,
                "2026.8.0")));

        using TemporaryDirectory nodeDirectory = new();
        InstallationSetupStore node = await CreateConfiguredStoreAsync(
            nodeDirectory.Path,
            InstallationTopologyKind.RemoteStationNode,
            installTransmitSupport: false);
        using InstallationInstallerCoordinator nodeCoordinator =
            new(node, new FakeHost());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => nodeCoordinator.PlanAsync(new(
                InstallationInstallerArchitecture.LinuxArm64,
                InstallationReverseProxyMode.ManagedCaddy,
                "2026.8.0")));

        InstallationInstallerPlanReport nodePlan =
            await nodeCoordinator.PlanAsync(new(
                InstallationInstallerArchitecture.LinuxArm64,
                InstallationReverseProxyMode.None,
                "2026.8.0")
            {
                Authentication =
                    new(InstallationInstallerAuthenticationMode.None)
            });
        Assert.Equal(["aetherremote"], nodePlan.ServiceUsers);
        Assert.Equal(
            [
                "aetherremote-station-engine.service",
                "aetherremote-agent.service"
            ],
            nodePlan.Services);
        Assert.DoesNotContain(
            nodePlan.Actions,
            action =>
                action.Kind ==
                InstallationInstallerActionKind.ConfigureReverseProxy);
        Assert.DoesNotContain(
            nodePlan.Actions,
            action =>
                action.Kind == InstallationInstallerActionKind.VerifyHealth);
        Assert.DoesNotContain(
            nodePlan.Actions,
            action =>
                action.Kind ==
                    InstallationInstallerActionKind.InitializeIdentityDatabase);
    }

    [Fact]
    public async Task ApplyAndRepairDefaultToDisabledWithoutCallingHost()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        FakeHost host = new();
        using InstallationInstallerCoordinator coordinator =
            new(store, host);
        InstallationInstallerPlanReport plan =
            await coordinator.PlanAsync(DefaultSelection());

        InstallationInstallerOperationResult apply =
            await coordinator.ApplyAsync(plan);
        InstallationInstallerOperationResult repair =
            await coordinator.RepairAsync(plan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.Disabled,
            apply.Outcome);
        Assert.Equal(
            InstallationInstallerOperationOutcome.Disabled,
            repair.Outcome);
        Assert.False(apply.MutationAttempted);
        Assert.False(repair.MutationAttempted);
        Assert.Equal(0, host.TotalCalls);
    }

    [Fact]
    public async Task ApplyRunsOnceAndRequiresPostApplyConvergence()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        FakeHost host = new();
        host.Inspections.Enqueue(
            InstallationInstallerHostInspectionResult.Converged());
        using InstallationInstallerCoordinator coordinator =
            EnabledCoordinator(store, host);
        InstallationInstallerPlanReport plan =
            await coordinator.PlanAsync(DefaultSelection());

        InstallationInstallerOperationResult result =
            await coordinator.ApplyAsync(plan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.Applied,
            result.Outcome);
        Assert.True(result.MutationAttempted);
        Assert.Equal(1, result.InspectionCount);
        Assert.Equal(1, host.ApplyCalls);
        Assert.Equal(1, host.InspectCalls);
        Assert.Equal(plan.PlanId, host.LastPlanId);
    }

    [Fact]
    public async Task ApplyUnknownOutcomeRequiresReconciliationWithoutRetry()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        FakeHost host = new()
        {
            ApplyResult = InstallationInstallerHostMutationResult.Unknown(
                "transport-unknown",
                "The host transaction outcome is unknown.")
        };
        using InstallationInstallerCoordinator coordinator =
            EnabledCoordinator(store, host);
        InstallationInstallerPlanReport plan =
            await coordinator.PlanAsync(DefaultSelection());

        InstallationInstallerOperationResult result =
            await coordinator.ApplyAsync(plan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.ReconciliationRequired,
            result.Outcome);
        Assert.Equal(1, host.ApplyCalls);
        Assert.Equal(0, host.InspectCalls);
    }

    [Fact]
    public async Task PostApplyDriftRequiresReconciliation()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        FakeHost host = new();
        host.Inspections.Enqueue(
            InstallationInstallerHostInspectionResult.Drift(
                "service-drift",
                "A service does not match the exact plan."));
        using InstallationInstallerCoordinator coordinator =
            EnabledCoordinator(store, host);
        InstallationInstallerPlanReport plan =
            await coordinator.PlanAsync(DefaultSelection());

        InstallationInstallerOperationResult result =
            await coordinator.ApplyAsync(plan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.ReconciliationRequired,
            result.Outcome);
        Assert.Equal("service-drift", result.Code);
        Assert.Equal(1, host.ApplyCalls);
        Assert.Equal(1, host.InspectCalls);
    }

    [Fact]
    public async Task RepairIsIdempotentAndMutatesOnlyKnownDrift()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        FakeHost convergedHost = new();
        convergedHost.Inspections.Enqueue(
            InstallationInstallerHostInspectionResult.Converged());
        using InstallationInstallerCoordinator convergedCoordinator =
            EnabledCoordinator(store, convergedHost);
        InstallationInstallerPlanReport convergedPlan =
            await convergedCoordinator.PlanAsync(DefaultSelection());

        InstallationInstallerOperationResult noOp =
            await convergedCoordinator.RepairAsync(convergedPlan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.Converged,
            noOp.Outcome);
        Assert.False(noOp.MutationAttempted);
        Assert.Equal(0, convergedHost.RepairCalls);

        FakeHost driftHost = new();
        driftHost.Inspections.Enqueue(
            InstallationInstallerHostInspectionResult.Drift(
                "directory-mode-drift",
                "A directory permission differs from the exact plan."));
        driftHost.Inspections.Enqueue(
            InstallationInstallerHostInspectionResult.Converged());
        using InstallationInstallerCoordinator driftCoordinator =
            EnabledCoordinator(store, driftHost);
        InstallationInstallerPlanReport driftPlan =
            await driftCoordinator.PlanAsync(DefaultSelection());

        InstallationInstallerOperationResult repaired =
            await driftCoordinator.RepairAsync(driftPlan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.Repaired,
            repaired.Outcome);
        Assert.True(repaired.MutationAttempted);
        Assert.Equal(2, repaired.InspectionCount);
        Assert.Equal(1, driftHost.RepairCalls);
    }

    [Fact]
    public async Task RepairRefusesToMutateUnknownInspectionState()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        FakeHost host = new();
        host.Inspections.Enqueue(
            InstallationInstallerHostInspectionResult.Unknown(
                "inspection-unknown",
                "The host inspection did not complete."));
        using InstallationInstallerCoordinator coordinator =
            EnabledCoordinator(store, host);
        InstallationInstallerPlanReport plan =
            await coordinator.PlanAsync(DefaultSelection());

        InstallationInstallerOperationResult result =
            await coordinator.RepairAsync(plan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.ReconciliationRequired,
            result.Outcome);
        Assert.False(result.MutationAttempted);
        Assert.Equal(0, host.RepairCalls);
    }

    [Fact]
    public async Task ExactPlanRejectsTamperingAndStaleSetupRevision()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        FakeHost host = new();
        using InstallationInstallerCoordinator coordinator =
            EnabledCoordinator(store, host);
        InstallationInstallerPlanReport plan =
            await coordinator.PlanAsync(DefaultSelection());

        InstallationInstallerPlanReport tampered =
            plan with { PlanId = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.ValidateAsync(tampered));

        _ = await store.UpdateAsync(
            plan.SetupRevision,
            state => state);
        InstallationSetupConcurrencyException exception =
            await Assert.ThrowsAsync<InstallationSetupConcurrencyException>(
                () => coordinator.ApplyAsync(plan));

        Assert.Equal(plan.SetupRevision, exception.ExpectedRevision);
        Assert.Equal(plan.SetupRevision + 1, exception.ActualRevision);
        Assert.Equal(0, host.TotalCalls);
    }

    [Fact]
    public void HostResultTextIsBoundedPlainText()
    {
        Assert.Throws<InvalidOperationException>(
            () => InstallationInstallerHostMutationResult.Rejected(
                "bad code",
                "Rejected."));
        Assert.Throws<InvalidOperationException>(
            () => InstallationInstallerHostInspectionResult.Unknown(
                "unknown",
                "Line one\nline two."));
        Assert.Throws<InvalidOperationException>(
            () => InstallationInstallerHostMutationResult.Unknown(
                "unknown",
                new string('x', 513)));
    }

    [Fact]
    public async Task UbuntuParticipantInspectsOnlyExactPlannedResources()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        CapturingUbuntuRuntime runtime = new();
        InstallationInstallerUbuntuHostTransaction host = new(runtime);
        using InstallationInstallerCoordinator coordinator =
            new(store, host);
        InstallationInstallerPlanReport plan =
            await coordinator.PlanAsync(DefaultSelection());
        runtime.Inspection =
            InstallationInstallerHostInspectionResult.Converged();

        InstallationInstallerOperationResult result =
            await coordinator.ValidateAsync(plan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.Converged,
            result.Outcome);
        Assert.Equal(plan.ServiceUsers, runtime.ServiceUsers);
        Assert.Equal(plan.Directories, runtime.Directories);
        Assert.Equal(plan.Services, runtime.Services);
        Assert.Equal(
            plan.Actions,
            Assert.IsType<InstallationInstallerUbuntuMutationRequest>(
                runtime.InspectionRequest).Actions);
    }

    [Fact]
    public async Task UbuntuParticipantReportsBoundedKnownDrift()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        CapturingUbuntuRuntime runtime = new();
        InstallationInstallerUbuntuHostTransaction host = new(runtime);
        using InstallationInstallerCoordinator coordinator =
            new(store, host);
        InstallationInstallerPlanReport plan =
            await coordinator.PlanAsync(DefaultSelection());
        runtime.Inspection =
            InstallationInstallerHostInspectionResult.Drift(
                "directory-drift",
                "One or more required directories do not match the exact installer plan.");

        InstallationInstallerOperationResult result =
            await coordinator.ValidateAsync(plan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.DriftDetected,
            result.Outcome);
        Assert.Equal("directory-drift", result.Code);
    }

    [Fact]
    public async Task UbuntuParticipantFailsClosedWhenInspectionThrows()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        CapturingUbuntuRuntime runtime = new()
        {
            Exception = new IOException("unbounded host detail")
        };
        using InstallationInstallerCoordinator coordinator =
            new(
                store,
                new InstallationInstallerUbuntuHostTransaction(runtime));
        InstallationInstallerPlanReport plan =
            await coordinator.PlanAsync(DefaultSelection());

        InstallationInstallerOperationResult result =
            await coordinator.ValidateAsync(plan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.ReconciliationRequired,
            result.Outcome);
        Assert.Equal("ubuntu-inspection-failed", result.Code);
        Assert.DoesNotContain("unbounded", result.Summary);
    }

    [Fact]
    public async Task UbuntuParticipantRejectsMutationUntilVerifiedReleaseIsBound()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        CapturingUbuntuRuntime runtime = new();
        using InstallationInstallerCoordinator coordinator =
            new(
                store,
                new InstallationInstallerUbuntuHostTransaction(runtime),
                new InstallationInstallerExecutionSettings
                {
                    Enabled = true
                });
        InstallationInstallerPlanReport plan =
            await coordinator.PlanAsync(DefaultSelection());

        InstallationInstallerOperationResult result =
            await coordinator.ApplyAsync(plan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.Rejected,
            result.Outcome);
        Assert.Equal("verified-release-unbound", result.Code);
        Assert.True(result.MutationAttempted);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task VerifiedStagedReleaseBindsOneExactMutationRequest()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        InstallationInstallerPlanReport plan;
        using (InstallationInstallerCoordinator planner =
            new(store, new FakeHost()))
        {
            plan = await planner.PlanAsync(DefaultSelection());
        }
        CapturingUbuntuRuntime runtime = new()
        {
            Inspection =
                InstallationInstallerHostInspectionResult.Converged()
        };
        InstallationInstallerVerifiedReleaseBinding binding =
            InstallationInstallerVerifiedReleaseBinding.Create(
                VerifiedStagingFor(plan, temporary.Path));
        using InstallationInstallerCoordinator coordinator =
            new(
                store,
                new InstallationInstallerUbuntuHostTransaction(
                    runtime,
                    binding),
                new InstallationInstallerExecutionSettings
                {
                    Enabled = true
                });

        InstallationInstallerOperationResult result =
            await coordinator.ApplyAsync(plan);

        Assert.Equal(
            InstallationInstallerOperationOutcome.Applied,
            result.Outcome);
        InstallationInstallerUbuntuMutationRequest request =
            Assert.IsType<InstallationInstallerUbuntuMutationRequest>(
                runtime.MutationRequest);
        Assert.Equal(plan.PlanId, request.PlanId);
        Assert.Equal(plan.SetupRevision, request.SetupRevision);
        Assert.Equal(plan.ReleaseIdentity, request.ReleaseIdentity);
        Assert.False(request.Repair);
        Assert.Equal(2, runtime.Calls);
    }

    [Fact]
    public async Task VerifiedStagedReleaseRejectsChangedSummaryAndPlanMismatch()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        using InstallationInstallerCoordinator planner =
            new(store, new FakeHost());
        InstallationInstallerPlanReport plan =
            await planner.PlanAsync(DefaultSelection());
        VerifiedReleaseStagingReport staging =
            VerifiedStagingFor(plan, temporary.Path);

        Assert.Throws<InvalidOperationException>(
            () => InstallationInstallerVerifiedReleaseBinding.Create(
                staging with { StagedBytes = staging.StagedBytes + 1 }));

        InstallationInstallerVerifiedReleaseBinding mismatched =
            InstallationInstallerVerifiedReleaseBinding.Create(
                VerifiedStagingFor(
                    plan with
                    {
                        ReleaseIdentity = "2026.8.1"
                    },
                    temporary.Path));
        InstallationInstallerUbuntuHostTransaction host =
            new(new CapturingUbuntuRuntime(), mismatched);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.ApplyAsync(plan));
    }

    [Fact]
    public void InstallerConsoleParserRequiresExactMutationConfirmation()
    {
        Assert.Throws<InvalidOperationException>(
            () => InstallationInstallerConsoleCommandParser.Parse(
            [
                InstallationInstallerConsoleCommandParser.ApplySwitch,
                InstallationInstallerConsoleCommandParser.ArchitectureSwitch,
                "linux-x64",
                InstallationInstallerConsoleCommandParser.ReverseProxySwitch,
                "managed-caddy",
                InstallationInstallerConsoleCommandParser.ReleaseSwitch,
                "2026.8.0",
                InstallationInstallerConsoleCommandParser.AuthenticationSwitch,
                "local"
            ]));

        InstallationInstallerConsoleCommandLine parsed =
            InstallationInstallerConsoleCommandParser.Parse(
            [
                InstallationInstallerConsoleCommandParser.ApplySwitch,
                InstallationInstallerConsoleCommandParser.ArchitectureSwitch,
                "linux-x64",
                InstallationInstallerConsoleCommandParser.ReverseProxySwitch,
                "managed-caddy",
                InstallationInstallerConsoleCommandParser.ReleaseSwitch,
                "2026.8.0",
                InstallationInstallerConsoleCommandParser.ConfirmPlanSwitch,
                new string('a', 64),
                InstallationInstallerConsoleCommandParser.BundleSwitch,
                "/srv/aethersdr-bundle",
                InstallationInstallerConsoleCommandParser.ConfigurationSchemaSwitch,
                "1",
                InstallationInstallerConsoleCommandParser.ProtocolVersionSwitch,
                "2",
                InstallationInstallerConsoleCommandParser.AuthenticationSwitch,
                "local",
                "--urls",
                "http://127.0.0.1:5080"
            ]);

        Assert.Equal(
            InstallationInstallerConsoleCommandKind.Apply,
            parsed.Command);
        Assert.Equal(new string('a', 64), parsed.ConfirmedPlanId);
        Assert.Equal(
            ["--urls", "http://127.0.0.1:5080"],
            parsed.ApplicationArguments);
    }

    [Fact]
    public void InstallerConsoleSeparatesExternalPlanFromMutationSecret()
    {
        string[] common =
        [
            InstallationInstallerConsoleCommandParser.ArchitectureSwitch,
            "linux-x64",
            InstallationInstallerConsoleCommandParser.ReverseProxySwitch,
            "managed-caddy",
            InstallationInstallerConsoleCommandParser.ReleaseSwitch,
            "2026.8.0",
            InstallationInstallerConsoleCommandParser.AuthenticationSwitch,
            "combined-oidc",
            InstallationInstallerConsoleCommandParser
                .AuthenticationProviderIdSwitch,
            "primary",
            InstallationInstallerConsoleCommandParser
                .AuthenticationAuthoritySwitch,
            "https://issuer.example",
            InstallationInstallerConsoleCommandParser
                .AuthenticationClientIdSwitch,
            "client"
        ];

        InstallationInstallerConsoleCommandLine planned =
            InstallationInstallerConsoleCommandParser.Parse(
                [InstallationInstallerConsoleCommandParser.PlanSwitch, .. common]);
        Assert.True(planned.Authentication?.UsesExternalProvider);
        Assert.Empty(planned.AuthenticationClientSecretSourceFile);

        Assert.Throws<InvalidOperationException>(
            () => InstallationInstallerConsoleCommandParser.Parse(
            [
                InstallationInstallerConsoleCommandParser.ApplySwitch,
                .. common,
                InstallationInstallerConsoleCommandParser.ConfirmPlanSwitch,
                new string('a', 64),
                InstallationInstallerConsoleCommandParser.BundleSwitch,
                "/srv/bundle",
                InstallationInstallerConsoleCommandParser
                    .ConfigurationSchemaSwitch,
                "1",
                InstallationInstallerConsoleCommandParser
                    .ProtocolVersionSwitch,
                "2"
            ]));

        InstallationInstallerConsoleCommandLine applied =
            InstallationInstallerConsoleCommandParser.Parse(
            [
                InstallationInstallerConsoleCommandParser.ApplySwitch,
                .. common,
                InstallationInstallerConsoleCommandParser.ConfirmPlanSwitch,
                new string('a', 64),
                InstallationInstallerConsoleCommandParser.BundleSwitch,
                "/srv/bundle",
                InstallationInstallerConsoleCommandParser
                    .ConfigurationSchemaSwitch,
                "1",
                InstallationInstallerConsoleCommandParser
                    .ProtocolVersionSwitch,
                "2",
                InstallationInstallerConsoleCommandParser
                    .AuthenticationClientSecretFileSwitch,
                "/srv/private/client-secret"
            ]);
        Assert.Equal(
            "/srv/private/client-secret",
            applied.AuthenticationClientSecretSourceFile);
    }

    [Fact]
    public async Task InstallerConsoleRejectsWrongPlanBeforeHostMutation()
    {
        using TemporaryDirectory temporary = new();
        InstallationSetupStore store = await CreateConfiguredStoreAsync(
            temporary.Path,
            InstallationTopologyKind.PersonalSingleStation,
            installTransmitSupport: false);
        FakeHost host = new();
        using InstallationInstallerCoordinator coordinator =
            EnabledCoordinator(store, host);
        InstallationInstallerConsole console = new(coordinator);
        StringWriter output = new();
        InstallationInstallerConsoleCommandLine command = new(
            InstallationInstallerConsoleCommandKind.Apply,
            InstallationInstallerArchitecture.LinuxX64,
            InstallationReverseProxyMode.ManagedCaddy,
            InstallationFirewallMode.GuidanceOnly,
            "2026.8.0",
            new string('a', 64),
            BundleDirectory: "/srv/aethersdr-bundle",
            ConfigurationSchemaVersion: 1,
            ProtocolVersion: 2,
            Authentication:
                InstallationInstallerAuthenticationSelection.Local,
            AuthenticationClientSecretSourceFile: string.Empty,
            ApplicationArguments: []);

        int exitCode = await console.ExecuteAsync(command, output);

        Assert.Equal(2, exitCode);
        Assert.Contains(
            "plan-confirmation-mismatch",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(0, host.TotalCalls);
    }

    private static VerifiedReleaseStagingReport VerifiedStagingFor(
        InstallationInstallerPlanReport plan,
        string root)
    {
        VerifiedReleaseInstallationPlan release = new(
            plan.SetupRevision,
            installedReleaseIdentity: string.Empty,
            plan.ReleaseIdentity,
            targetVersion: plan.ReleaseIdentity,
            ReleaseManifestArchitecture.LinuxX64,
            InstallationUpdateChannel.Stable,
            pinnedReleaseIdentity: string.Empty,
            plan.InstallTransmitSupport,
            bundleDirectory: Path.Combine(root, "bundle"),
            manifestLength: 1,
            manifestSha256: new byte[32],
            releaseRootPath: Path.Combine(root, "releases"),
            deploymentRootPath: root,
            targetReleasePath: Path.Combine(
                root,
                "releases",
                plan.ReleaseIdentity),
            packages: [],
            targetConfigurationSchemaVersion: 1,
            ReleaseMigrationKind.None,
            migrationFromConfigurationSchemaVersion: null,
            migrationToConfigurationSchemaVersion: null,
            migrationIdentity: string.Empty,
            restartGatewayWeb: false,
            restartBroker: false,
            restartAetherRemoteAgent: false,
            restartStationEngine: false,
            restartHost: false,
            txSupportCapable: plan.InstallTransmitSupport,
            releaseNotesTitle: "Test release",
            releaseNotesSummary: "Exact installer binding test.");
        VerifiedStagedRelease staged = new(
            release,
            Path.Combine(root, ".release-staging", plan.PlanId),
            stagedBytes: 1);
        return VerifiedReleaseStagingReport.Success(staged);
    }

    private static InstallationInstallerSelection DefaultSelection() =>
        new(
            InstallationInstallerArchitecture.LinuxX64,
            InstallationReverseProxyMode.ManagedCaddy,
            "2026.8.0");

    private static InstallationInstallerCoordinator EnabledCoordinator(
        InstallationSetupStore store,
        FakeHost host) =>
        new(
            store,
            host,
            new InstallationInstallerExecutionSettings
            {
                Enabled = true
            });

    private static async Task<InstallationSetupStore> CreateConfiguredStoreAsync(
        string root,
        InstallationTopologyKind topology,
        bool installTransmitSupport)
    {
        FixedTimeProvider time = new(Start);
        InstallationPaths paths = new(
            Path.Combine(root, "config"),
            Path.Combine(root, "state"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "releases"),
            Path.Combine(root, "backups"),
            Path.Combine(root, "logs"));
        InstallationSetupStore store =
            new(paths.SetupStatePath, time);
        InstallationSetupState initial =
            await store.LoadOrCreateAsync();
        InstallationBootstrapTokenService tokens = new(store, time);
        InstallationBootstrapTokenIssue issue =
            await tokens.IssueAsync(initial.Revision);
        InstallationSetupState claimed =
            await tokens.ClaimAsync(issue.State.Revision, issue.Token);
        InstallationSetupWorkflow workflow = new(store);
        InstallationSetupState topologyState =
            await workflow.ConfigureTopologyAsync(
                claimed.Revision,
                topology);
        InstallationSetupState publicUrl =
            await workflow.ConfigurePublicUrlAsync(
                topologyState.Revision,
                "https://radio.example.org");
        InstallationSetupState pathState =
            await workflow.ConfigurePathsAsync(
                publicUrl.Revision,
                paths);
        InstallationSetupState channel =
            await workflow.ConfigureUpdateChannelAsync(
                pathState.Revision,
                InstallationUpdateChannel.Stable);
        InstallationSetupState backup =
            await workflow.ConfirmBackupLocationAsync(channel.Revision);
        _ = await workflow.ConfigureTransmitSupportAsync(
            backup.Revision,
            installTransmitSupport);
        return store;
    }

    private sealed class FakeHost : IInstallationInstallerHostTransaction
    {
        internal Queue<InstallationInstallerHostInspectionResult> Inspections
        {
            get;
        } = new();

        internal InstallationInstallerHostMutationResult ApplyResult
        {
            get;
            init;
        } = InstallationInstallerHostMutationResult.Applied();

        internal InstallationInstallerHostMutationResult RepairResult
        {
            get;
            init;
        } = InstallationInstallerHostMutationResult.Applied(
            "repaired",
            "The exact installer plan was repaired.");

        internal int InspectCalls { get; private set; }

        internal int ApplyCalls { get; private set; }

        internal int RepairCalls { get; private set; }

        internal int TotalCalls => InspectCalls + ApplyCalls + RepairCalls;

        internal string LastPlanId { get; private set; } = string.Empty;

        public Task<InstallationInstallerHostInspectionResult> InspectAsync(
            InstallationInstallerPlanReport plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCalls++;
            LastPlanId = plan.PlanId;
            return Task.FromResult(
                Inspections.Count > 0
                    ? Inspections.Dequeue()
                    : InstallationInstallerHostInspectionResult.Converged());
        }

        public Task<InstallationInstallerHostMutationResult> ApplyAsync(
            InstallationInstallerPlanReport plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            LastPlanId = plan.PlanId;
            return Task.FromResult(ApplyResult);
        }

        public Task<InstallationInstallerHostMutationResult> RepairAsync(
            InstallationInstallerPlanReport plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RepairCalls++;
            LastPlanId = plan.PlanId;
            return Task.FromResult(RepairResult);
        }
    }

    private sealed class CapturingUbuntuRuntime :
        IInstallationInstallerUbuntuRuntime
    {
        internal InstallationInstallerHostInspectionResult Inspection
        {
            get;
            set;
        } = InstallationInstallerHostInspectionResult.Converged();

        internal InstallationInstallerUbuntuMutationRequest? InspectionRequest
        {
            get;
            private set;
        }

        internal Exception? Exception { get; init; }

        internal int Calls { get; private set; }

        internal InstallationInstallerUbuntuMutationRequest? MutationRequest
        {
            get;
            private set;
        }

        internal IReadOnlyList<string> ServiceUsers { get; private set; } = [];

        internal IReadOnlyList<string> Directories { get; private set; } = [];

        internal IReadOnlyList<string> Services { get; private set; } = [];

        public Task<InstallationInstallerHostInspectionResult> InspectAsync(
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            InspectionRequest = request;
            ServiceUsers = request.Actions
                .Where(action =>
                    action.Kind ==
                    InstallationInstallerActionKind.EnsureServiceUser)
                .Select(action => action.Target)
                .ToArray();
            Directories = request.Actions
                .Where(action =>
                    action.Kind ==
                    InstallationInstallerActionKind.EnsureDirectory)
                .Select(action => action.Target)
                .ToArray();
            Services = request.Actions
                .Where(action =>
                    action.Kind ==
                    InstallationInstallerActionKind.InstallSystemdUnit)
                .Select(action => action.Target)
                .ToArray();
            if (Exception is not null)
            {
                throw Exception;
            }
            return Task.FromResult(Inspection);
        }

        public Task<InstallationInstallerHostMutationResult> MutateAsync(
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            MutationRequest = request;
            return Task.FromResult(
                InstallationInstallerHostMutationResult.Applied());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-installer-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
