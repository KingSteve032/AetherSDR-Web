using System.Diagnostics;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class InstallationInstallerUbuntuMutationExecutorTests
{
    [Fact]
    public async Task ExecutesExactOrderedActionsAfterReadOnlyPreparation()
    {
        FakePrimitives primitives = new();
        InstallationInstallerUbuntuMutationExecutor executor =
            new(primitives);
        InstallationInstallerUbuntuMutationRequest request = Request();

        InstallationInstallerHostMutationResult result =
            await executor.ExecuteAsync(request);

        Assert.Equal(
            InstallationInstallerHostMutationOutcome.Applied,
            result.Outcome);
        Assert.Equal("prepare", primitives.Events[0]);
        Assert.Equal(
            request.Actions.Select(action => $"execute:{action.Order}"),
            primitives.Events.Skip(1));
        Assert.Equal(request.Actions.Count + 1, primitives.Events.Count);
    }

    [Fact]
    public async Task PreparationRejectionPerformsNoMutationStep()
    {
        FakePrimitives primitives = new()
        {
            PrepareResult = InstallationInstallerUbuntuStepResult.Rejected(
                "root-required",
                "The fixed installer transaction requires root.")
        };
        InstallationInstallerUbuntuMutationExecutor executor =
            new(primitives);

        InstallationInstallerHostMutationResult result =
            await executor.ExecuteAsync(Request());

        Assert.Equal(
            InstallationInstallerHostMutationOutcome.Rejected,
            result.Outcome);
        Assert.Equal(["prepare"], primitives.Events);
    }

    [Fact]
    public async Task RejectionAfterAppliedStepRequiresReconciliation()
    {
        FakePrimitives primitives = new();
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Applied());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Rejected(
                "unit-rejected",
                "The fixed service-unit step was rejected."));
        InstallationInstallerUbuntuMutationExecutor executor =
            new(primitives);

        InstallationInstallerHostMutationResult result =
            await executor.ExecuteAsync(Request());

        Assert.Equal(
            InstallationInstallerHostMutationOutcome.Unknown,
            result.Outcome);
        Assert.Equal("ubuntu-partial-mutation", result.Code);
        Assert.Equal(3, primitives.Events.Count);
    }

    [Fact]
    public async Task UnknownStepIsNeverRetried()
    {
        FakePrimitives primitives = new();
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Unknown(
                "publish-unknown",
                "The atomic release publish outcome is unknown."));
        InstallationInstallerUbuntuMutationExecutor executor =
            new(primitives);

        InstallationInstallerHostMutationResult result =
            await executor.ExecuteAsync(Request());

        Assert.Equal(
            InstallationInstallerHostMutationOutcome.Unknown,
            result.Outcome);
        Assert.Equal("publish-unknown", result.Code);
        Assert.Equal(2, primitives.Events.Count);
    }

    [Fact]
    public async Task HealthRejectionRollsBackOnlyTransactionAppliedActivation()
    {
        FakeRollbackPrimitives primitives = new();
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Converged());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Converged());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Applied());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Applied());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Rejected(
                "health-not-ready",
                "The exact health endpoint was not ready."));
        InstallationInstallerUbuntuMutationExecutor executor =
            new(primitives);

        InstallationInstallerHostMutationResult result =
            await executor.ExecuteAsync(ActivationRequest());

        Assert.Equal(
            InstallationInstallerHostMutationOutcome.Rejected,
            result.Outcome);
        Assert.Equal("ubuntu-transaction-rolled-back", result.Code);
        Assert.Equal([3, 4], primitives.RollbackCandidateOrders);
    }

    [Fact]
    public async Task RepairRollbackExcludesPreexistingConvergedService()
    {
        FakeRollbackPrimitives primitives = new();
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Converged());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Converged());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Applied());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Converged());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Rejected(
                "health-not-ready",
                "The exact health endpoint was not ready."));
        InstallationInstallerUbuntuMutationExecutor executor =
            new(primitives);

        InstallationInstallerHostMutationResult result =
            await executor.ExecuteAsync(ActivationRequest(repair: true));

        Assert.Equal(
            InstallationInstallerHostMutationOutcome.Rejected,
            result.Outcome);
        Assert.Equal([3], primitives.RollbackCandidateOrders);
        Assert.DoesNotContain(4, primitives.RollbackCandidateOrders);
    }

    [Fact]
    public async Task CancellationRollsBackUnknownCleanActivationBeforePropagating()
    {
        FakeRollbackPrimitives primitives = new()
        {
            CancelAtOrder = 4
        };
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Converged());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Converged());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Applied());
        InstallationInstallerUbuntuMutationExecutor executor =
            new(primitives);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(ActivationRequest()));

        Assert.Equal([3, 4], primitives.RollbackCandidateOrders);
    }

    [Fact]
    public async Task RollbackFailureRequiresReconciliation()
    {
        FakeRollbackPrimitives primitives = new()
        {
            RollbackResult = InstallationInstallerUbuntuStepResult.Unknown(
                "rollback-failed",
                "The rollback postcondition was not reached.")
        };
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Converged());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Converged());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Applied());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Applied());
        primitives.StepResults.Enqueue(
            InstallationInstallerUbuntuStepResult.Rejected(
                "health-not-ready",
                "The exact health endpoint was not ready."));
        InstallationInstallerUbuntuMutationExecutor executor =
            new(primitives);

        InstallationInstallerHostMutationResult result =
            await executor.ExecuteAsync(ActivationRequest());

        Assert.Equal(
            InstallationInstallerHostMutationOutcome.Unknown,
            result.Outcome);
        Assert.Equal(
            "ubuntu-rollback-reconciliation-required",
            result.Code);
    }

    [Theory]
    [InlineData(ReleaseMigrationKind.Required, false)]
    [InlineData(ReleaseMigrationKind.None, true)]
    public void InitialReleaseBoundaryRejectsMigrationAndHostRestart(
        ReleaseMigrationKind migrationKind,
        bool restartHost)
    {
        VerifiedReleaseInstallationPlan plan =
            InitialReleasePlan(migrationKind, restartHost);

        Assert.Throws<InvalidOperationException>(
            () => InstallationInstallerVerifiedReleaseBinding
                .RequireInitialExecutionBoundary(plan));
    }

    [Fact]
    public void InitialReleaseBoundaryAcceptsNoMigrationAndNoHostRestart()
    {
        InstallationInstallerVerifiedReleaseBinding
            .RequireInitialExecutionBoundary(
                InitialReleasePlan(
                    ReleaseMigrationKind.None,
                    restartHost: false));
    }

    [Fact]
    public async Task LocalRuntimeMutationDefaultsDisabled()
    {
        LocalInstallationInstallerUbuntuRuntime runtime = new();

        InstallationInstallerHostMutationResult result =
            await runtime.MutateAsync(Request());

        Assert.Equal(
            InstallationInstallerHostMutationOutcome.Rejected,
            result.Outcome);
        Assert.Equal("ubuntu-mutation-disabled", result.Code);
    }

    [Fact]
    public async Task LocalRuntimeRequiresRegisteredExecutorWhenEnabled()
    {
        LocalInstallationInstallerUbuntuRuntime runtime =
            new(
                executor: null,
                new InstallationInstallerUbuntuRuntimeSettings
                {
                    MutationEnabled = true
                });

        InstallationInstallerHostMutationResult result =
            await runtime.MutateAsync(Request());

        Assert.Equal(
            InstallationInstallerHostMutationOutcome.Rejected,
            result.Outcome);
        Assert.Equal("ubuntu-mutation-unregistered", result.Code);
    }

    [Fact]
    public void PrimitivePlanUsesOnlyFixedExecutablesAndExactArguments()
    {
        InstallationInstallerUbuntuMutationRequest request = Request();
        IReadOnlyList<InstallationInstallerUbuntuPrimitiveOperation> operations =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request);

        Assert.Equal("/usr/sbin/useradd", operations[0].Executable);
        Assert.Equal("aethersdr", operations[0].Arguments[^1]);
        Assert.Equal(string.Empty, operations[1].Executable);
        Assert.Empty(operations[1].Arguments);
        Assert.Equal(
            InstallationInstallerUbuntuPrimitiveKind.VerifyImmutableRelease,
            operations[1].Kind);
        Assert.Equal("/usr/bin/install", operations[2].Executable);
        Assert.Equal(
            Path.Combine(
                request.InstallerAssetRoot,
                "installer",
                "systemd",
                "aethersdr-web.service"),
            operations[2].Arguments[^2]);
        Assert.All(
            operations,
            operation =>
            {
                Assert.NotEqual("/bin/sh", operation.Executable);
                Assert.NotEqual("/bin/bash", operation.Executable);
            });
    }

    [Fact]
    public void InstallerEvidenceAndReleaseRootsAreRootOwned()
    {
        InstallationInstallerUbuntuMutationRequest request = Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.EnsureDirectory,
                "/var/lib/aethersdr-installer"),
            new(
                3,
                InstallationInstallerActionKind.EnsureDirectory,
                "/opt/aethersdr/releases"),
            new(
                4,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64")
        ]);

        IReadOnlyList<InstallationInstallerUbuntuPrimitiveOperation> operations =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request);

        Assert.Equal(
            ["-d", "-m", "0750", "-o", "root", "-g", "root", "--",
                "/var/lib/aethersdr-installer"],
            operations[1].Arguments);
        Assert.Equal(
            ["-d", "-m", "0755", "-o", "root", "-g", "root", "--",
                "/opt/aethersdr/releases"],
            operations[2].Arguments);
    }

    [Fact]
    public void MultiServiceStateRootAllowsTraversalWithoutDirectoryListing()
    {
        InstallationInstallerUbuntuMutationRequest request = Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.EnsureDirectory,
                "/var/lib/aethersdr"),
            new(
                3,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64")
        ]);

        InstallationInstallerUbuntuPrimitiveOperation stateRoot =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request)[1];

        Assert.Equal(
            [
                "-d",
                "-m",
                "0751",
                "-o",
                "aethersdr",
                "-g",
                "aethersdr",
                "--",
                "/var/lib/aethersdr"
            ],
            stateRoot.Arguments);
    }

    [Fact]
    public void IdentityAndDataProtectionDirectoriesAreOwnerOnlyAndRoleOwned()
    {
        InstallationInstallerUbuntuMutationRequest request = Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aetherremote"),
            new(
                3,
                InstallationInstallerActionKind.EnsureDirectory,
                "/var/lib/aethersdr/secrets/data-protection"),
            new(
                4,
                InstallationInstallerActionKind.EnsureDirectory,
                "/var/lib/aethersdr/identity"),
            new(
                5,
                InstallationInstallerActionKind.EnsureDirectory,
                "/var/lib/aethersdr/aetherremote/station-engine/data-protection"),
            new(
                6,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64")
        ]);

        IReadOnlyList<InstallationInstallerUbuntuPrimitiveOperation> operations =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request);

        Assert.Equal(
            ["-d", "-m", "0700", "-o", "aethersdr", "-g", "aethersdr", "--",
                "/var/lib/aethersdr/secrets/data-protection"],
            operations[2].Arguments);
        Assert.Equal(
            ["-d", "-m", "0700", "-o", "aethersdr", "-g", "aethersdr", "--",
                "/var/lib/aethersdr/identity"],
            operations[3].Arguments);
        Assert.Equal(
            ["-d", "-m", "0700", "-o", "aetherremote", "-g", "aetherremote", "--",
                "/var/lib/aethersdr/aetherremote/station-engine/data-protection"],
            operations[4].Arguments);
    }

    [Fact]
    public void SetupIdentityHandoffMapsToOneExactDirectPrimitive()
    {
        InstallationInstallerUbuntuMutationRequest request = Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.AdoptSetupIdentityState,
                "/var/lib/aethersdr"),
            new(
                3,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64")
        ]);

        InstallationInstallerUbuntuPrimitiveOperation handoff =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request)[1];

        Assert.Equal(
            InstallationInstallerUbuntuPrimitiveKind.AdoptSetupIdentityState,
            handoff.Kind);
        Assert.Equal("/usr/bin/chown", handoff.Executable);
        Assert.Equal(
            [
                "--recursive",
                "--no-dereference",
                "aethersdr:aethersdr",
                "--",
                "/var/lib/aethersdr/identity",
                "/var/lib/aethersdr/secrets/data-protection",
                "/var/lib/aethersdr/setup"
            ],
            handoff.Arguments);
    }

    [Fact]
    public void NoncanonicalSetupIdentityHandoffFailsBeforeExecution()
    {
        InstallationInstallerUbuntuMutationRequest request = Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.AdoptSetupIdentityState,
                "/tmp/operator-selected"),
            new(
                3,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64")
        ]);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => InstallationInstallerUbuntuPrimitivePlanner.Compose(
                    request));

        Assert.Contains(
            "noncanonical setup identity handoff",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityDatabaseMapsToOneExactTypedManagedPrimitive()
    {
        InstallationInstallerUbuntuMutationRequest request = Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64"),
            new(
                3,
                InstallationInstallerActionKind.InitializeIdentityDatabase,
                "/var/lib/aethersdr/identity/aethersdr-identity.db")
        ]);

        InstallationInstallerUbuntuPrimitiveOperation identity =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request)[2];

        Assert.Equal(
            InstallationInstallerUbuntuPrimitiveKind.InitializeIdentityDatabase,
            identity.Kind);
        Assert.Equal(
            "/var/lib/aethersdr/identity/aethersdr-identity.db",
            identity.Target);
        Assert.Empty(identity.Executable);
        Assert.Empty(identity.Arguments);
    }

    [Fact]
    public void NoncanonicalIdentityDatabaseFailsBeforePrimitiveExecution()
    {
        InstallationInstallerUbuntuMutationRequest request = Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64"),
            new(
                3,
                InstallationInstallerActionKind.InitializeIdentityDatabase,
                "/tmp/operator-selected.db")
        ]);

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => InstallationInstallerUbuntuPrimitivePlanner.Compose(
                    request));

        Assert.Contains(
            "noncanonical identity database",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LanCertificateTrustMapsToOneTypedManagedPrimitive()
    {
        InstallationInstallerUbuntuMutationRequest request = Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64"),
            new(
                3,
                InstallationInstallerActionKind.TrustInternalCertificate,
                "/usr/local/share/ca-certificates/aethersdr-caddy-local.crt")
        ]);

        InstallationInstallerUbuntuPrimitiveOperation trust =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request)[2];

        Assert.Equal(
            InstallationInstallerUbuntuPrimitiveKind.TrustInternalCertificate,
            trust.Kind);
        Assert.Empty(trust.Executable);
        Assert.Empty(trust.Arguments);
    }

    [Fact]
    public void NoncanonicalDirectoryFailsBeforePrimitiveExecution()
    {
        InstallationInstallerUbuntuMutationRequest request = Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.EnsureDirectory,
                "/tmp/operator-selected"),
            new(
                3,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64")
        ]);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => InstallationInstallerUbuntuPrimitivePlanner.Compose(request));

        Assert.Contains("noncanonical", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedUnitFailsBeforePrimitivePreparation()
    {
        FakePrimitives primitives = new();
        InstallationInstallerUbuntuMutationExecutor executor =
            new(primitives);
        InstallationInstallerUbuntuMutationRequest request = Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64"),
            new(
                3,
                InstallationInstallerActionKind.InstallSystemdUnit,
                "unreviewed.service")
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(request));

        Assert.Empty(primitives.Events);
    }

    [Fact]
    public async Task ExactPlanInspectionIncludesManagedReleaseState()
    {
        FakeManagedHandler managed = new()
        {
            Inspection = new(
                InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing,
                "ubuntu-release-missing",
                "The verified immutable release is not published.")
        };
        LocalInstallationInstallerUbuntuMutationPrimitives primitives = new(
            new FakeInspector(),
            (_, _) => Task.FromResult(
                new InstallationInstallerUbuntuDirectProcessResult(
                    0,
                    string.Empty,
                    string.Empty)),
            () => true,
            managed);

        InstallationInstallerHostInspectionResult result =
            await primitives.InspectPlanAsync(Request());

        Assert.Equal(
            InstallationInstallerHostInspectionOutcome.Drift,
            result.Outcome);
        Assert.Equal("ubuntu-release-missing", result.Code);
        Assert.Equal(1, managed.InspectionCount);
    }

    [Fact]
    public async Task ConcretePreflightRejectsMissingVerifiedReleaseBeforeProcess()
    {
        FakeInspector inspector = new();
        int processCount = 0;
        LocalInstallationInstallerUbuntuMutationPrimitives primitives = new(
            inspector,
            (_, _) =>
            {
                processCount++;
                return Task.FromResult(new InstallationInstallerUbuntuDirectProcessResult(
                    0,
                    string.Empty,
                    string.Empty));
            },
            () => true);

        InstallationInstallerUbuntuStepResult result =
            await primitives.PrepareAsync(Request());

        Assert.Equal(
            InstallationInstallerUbuntuStepOutcome.Rejected,
            result.Outcome);
        Assert.Equal("ubuntu-verified-release-unavailable", result.Code);
        Assert.Equal(0, inspector.InspectionCount);
        Assert.Equal(0, processCount);
    }

    [Fact]
    public async Task MissingPlannedUnitIsClassifiedAsInstallableState()
    {
        Queue<InstallationInstallerUbuntuDirectProcessResult> results = new(
        [
            new(4, string.Empty, "not-found"),
            new(4, string.Empty, "inactive")
        ]);
        LocalInstallationInstallerUbuntuPrimitiveInspector inspector = new(
            (_, _) => Task.FromResult(results.Dequeue()));
        InstallationInstallerUbuntuMutationRequest request =
            ActivationRequest();
        InstallationInstallerUbuntuPrimitiveOperation operation =
            InstallationInstallerUbuntuPrimitivePlanner.Compose(request)[3];

        InstallationInstallerUbuntuPrimitiveInspection inspection =
            await inspector.InspectAsync(request, operation);

        Assert.Equal(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing,
            inspection.Outcome);
        Assert.Empty(results);
    }

    [Fact]
    public async Task ConcretePrimitiveUsesOneDirectFixedProcessAndPostcondition()
    {
        FakeInspector inspector = new();
        inspector.Results.Enqueue(Inspection(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing));
        inspector.Results.Enqueue(Inspection(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged));
        ProcessStartInfo? captured = null;
        LocalInstallationInstallerUbuntuMutationPrimitives primitives = new(
            inspector,
            (start, _) =>
            {
                captured = start;
                return Task.FromResult(new InstallationInstallerUbuntuDirectProcessResult(
                    0,
                    string.Empty,
                    string.Empty));
            },
            () => true);
        InstallationInstallerUbuntuMutationRequest request = Request();

        InstallationInstallerUbuntuStepResult result =
            await primitives.ExecuteAsync(request, request.Actions[0]);

        Assert.Equal(InstallationInstallerUbuntuStepOutcome.Applied, result.Outcome);
        Assert.NotNull(captured);
        Assert.Equal("/usr/sbin/useradd", captured.FileName);
        Assert.False(captured.UseShellExecute);
        Assert.Empty(captured.Environment);
        Assert.Equal(
            [
                "--system",
                "--user-group",
                "--home-dir",
                "/nonexistent",
                "--shell",
                "/usr/sbin/nologin",
                "--",
                "aethersdr"
            ],
            captured.ArgumentList.Cast<string>());
        Assert.Equal(2, inspector.InspectionCount);
    }

    [Fact]
    public async Task ConcretePrimitiveDoesNotRunWhenAlreadyConverged()
    {
        FakeInspector inspector = new();
        inspector.Results.Enqueue(Inspection(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged));
        int processCount = 0;
        LocalInstallationInstallerUbuntuMutationPrimitives primitives = new(
            inspector,
            (_, _) =>
            {
                processCount++;
                return Task.FromResult(new InstallationInstallerUbuntuDirectProcessResult(
                    0,
                    string.Empty,
                    string.Empty));
            },
            () => true);
        InstallationInstallerUbuntuMutationRequest request = Request();

        InstallationInstallerUbuntuStepResult result =
            await primitives.ExecuteAsync(request, request.Actions[0]);

        Assert.Equal(
            InstallationInstallerUbuntuStepOutcome.Converged,
            result.Outcome);
        Assert.Equal(0, processCount);
    }

    [Fact]
    public async Task ConcretePrimitiveConvergesDirectoryMetadataOnApply()
    {
        FakeInspector inspector = new();
        inspector.Results.Enqueue(Inspection(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Drift));
        inspector.Results.Enqueue(Inspection(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged));
        int processCount = 0;
        LocalInstallationInstallerUbuntuMutationPrimitives primitives = new(
            inspector,
            (_, _) =>
            {
                processCount++;
                return Task.FromResult(new InstallationInstallerUbuntuDirectProcessResult(
                    0,
                    string.Empty,
                    string.Empty));
            },
            () => true);
        InstallationInstallerUbuntuMutationRequest request = DirectoryRequest(
            repair: false);

        InstallationInstallerUbuntuStepResult result =
            await primitives.ExecuteAsync(request, request.Actions[1]);

        Assert.Equal(
            InstallationInstallerUbuntuStepOutcome.Applied,
            result.Outcome);
        Assert.Equal(1, processCount);
        Assert.Equal(2, inspector.InspectionCount);
    }

    [Fact]
    public async Task ConcretePrimitiveStillRejectsUnitDriftOnApply()
    {
        FakeInspector inspector = new();
        inspector.Results.Enqueue(Inspection(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Drift));
        int processCount = 0;
        LocalInstallationInstallerUbuntuMutationPrimitives primitives = new(
            inspector,
            (_, _) =>
            {
                processCount++;
                return Task.FromResult(new InstallationInstallerUbuntuDirectProcessResult(
                    0,
                    string.Empty,
                    string.Empty));
            },
            () => true);
        InstallationInstallerUbuntuMutationRequest request = Request();

        InstallationInstallerUbuntuStepResult result =
            await primitives.ExecuteAsync(request, request.Actions[2]);

        Assert.Equal(
            InstallationInstallerUbuntuStepOutcome.Rejected,
            result.Outcome);
        Assert.Equal(0, processCount);
    }

    [Fact]
    public async Task ConcretePrimitiveAllowsExactRepairAndRequiresPostcondition()
    {
        FakeInspector inspector = new();
        inspector.Results.Enqueue(Inspection(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Drift));
        inspector.Results.Enqueue(Inspection(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged));
        int processCount = 0;
        LocalInstallationInstallerUbuntuMutationPrimitives primitives = new(
            inspector,
            (_, _) =>
            {
                processCount++;
                return Task.FromResult(new InstallationInstallerUbuntuDirectProcessResult(
                    0,
                    string.Empty,
                    string.Empty));
            },
            () => true);
        InstallationInstallerUbuntuMutationRequest request = DirectoryRequest(
            repair: true);

        InstallationInstallerUbuntuStepResult result =
            await primitives.ExecuteAsync(request, request.Actions[1]);

        Assert.Equal(InstallationInstallerUbuntuStepOutcome.Applied, result.Outcome);
        Assert.Equal(1, processCount);
        Assert.Equal(2, inspector.InspectionCount);
    }

    [Fact]
    public async Task SetupIdentityHandoffExecutesOneExactChownAndPostcondition()
    {
        FakeInspector inspector = new();
        inspector.Results.Enqueue(Inspection(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing));
        inspector.Results.Enqueue(Inspection(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged));
        List<ProcessStartInfo> starts = [];
        LocalInstallationInstallerUbuntuMutationPrimitives primitives = new(
            inspector,
            (start, _) =>
            {
                starts.Add(start);
                return Task.FromResult(
                    new InstallationInstallerUbuntuDirectProcessResult(
                        0,
                        string.Empty,
                        string.Empty));
            },
            () => true);
        InstallationInstallerUbuntuMutationRequest request = Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.AdoptSetupIdentityState,
                "/var/lib/aethersdr"),
            new(
                3,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64")
        ]);

        InstallationInstallerUbuntuStepResult result =
            await primitives.ExecuteAsync(request, request.Actions[1]);

        Assert.Equal(InstallationInstallerUbuntuStepOutcome.Applied, result.Outcome);
        ProcessStartInfo start = Assert.Single(starts);
        Assert.Equal("/usr/bin/chown", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Empty(start.Environment);
        Assert.Equal(
            [
                "--recursive",
                "--no-dereference",
                "aethersdr:aethersdr",
                "--",
                "/var/lib/aethersdr/identity",
                "/var/lib/aethersdr/secrets/data-protection",
                "/var/lib/aethersdr/setup"
            ],
            start.ArgumentList.Cast<string>());
        Assert.Equal(2, inspector.InspectionCount);
    }

    [Fact]
    public async Task ConcretePrimitiveTreatsNonzeroAsUnknownAndNeverRetries()
    {
        FakeInspector inspector = new();
        inspector.Results.Enqueue(Inspection(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing));
        int processCount = 0;
        LocalInstallationInstallerUbuntuMutationPrimitives primitives = new(
            inspector,
            (_, _) =>
            {
                processCount++;
                return Task.FromResult(new InstallationInstallerUbuntuDirectProcessResult(
                    9,
                    string.Empty,
                    "rejected"));
            },
            () => true);
        InstallationInstallerUbuntuMutationRequest request = Request();

        InstallationInstallerUbuntuStepResult result =
            await primitives.ExecuteAsync(request, request.Actions[0]);

        Assert.Equal(
            InstallationInstallerUbuntuStepOutcome.Unknown,
            result.Outcome);
        Assert.Equal("ubuntu-process-rejected-unknown", result.Code);
        Assert.Equal(1, processCount);
        Assert.Equal(1, inspector.InspectionCount);
    }

    [Fact]
    public async Task ConcreteRollbackUsesFixedDisableNowAndExactPostcondition()
    {
        Queue<InstallationInstallerUbuntuDirectProcessResult> results = new(
        [
            new(0, string.Empty, string.Empty),
            new(0, string.Empty, string.Empty),
            new(0, string.Empty, string.Empty),
            new(1, string.Empty, string.Empty),
            new(3, string.Empty, string.Empty)
        ]);
        List<ProcessStartInfo> starts = [];
        LocalInstallationInstallerUbuntuMutationPrimitives primitives = new(
            new FakeInspector(),
            (start, _) =>
            {
                starts.Add(start);
                return Task.FromResult(results.Dequeue());
            },
            () => true);
        InstallationInstallerUbuntuMutationRequest request =
            ActivationRequest();

        InstallationInstallerUbuntuStepResult result =
            await primitives.RollbackAsync(
                request,
                [request.Actions[3]]);

        Assert.Equal(
            InstallationInstallerUbuntuStepOutcome.Applied,
            result.Outcome);
        Assert.Empty(results);
        Assert.Equal(5, starts.Count);
        Assert.All(
            starts,
            start =>
            {
                Assert.Equal("/usr/bin/systemctl", start.FileName);
                Assert.False(start.UseShellExecute);
                Assert.Empty(start.Environment);
            });
        Assert.Equal(
            ["is-enabled", "--quiet", "--", "aethersdr-web.service"],
            starts[0].ArgumentList.Cast<string>());
        Assert.Equal(
            ["is-active", "--quiet", "--", "aethersdr-web.service"],
            starts[1].ArgumentList.Cast<string>());
        Assert.Equal(
            ["disable", "--now", "--", "aethersdr-web.service"],
            starts[2].ArgumentList.Cast<string>());
    }

    [Fact]
    public async Task SetupIdentityHandoffRequiresRootOwnedTreeAdoption()
    {
        using TemporaryDirectory temporary = new();
        string identity = Path.Combine(temporary.Path, "identity");
        string protection = Path.Combine(temporary.Path, "data-protection");
        string setup = Path.Combine(temporary.Path, "setup");
        Directory.CreateDirectory(identity);
        Directory.CreateDirectory(protection);
        Directory.CreateDirectory(setup);
        await File.WriteAllTextAsync(
            Path.Combine(identity, "aethersdr-identity.db"),
            "identity");
        await File.WriteAllTextAsync(
            Path.Combine(protection, "key.xml"),
            "key");
        await File.WriteAllTextAsync(
            Path.Combine(setup, "installation.json"),
            "setup");
        List<ProcessStartInfo> starts = [];
        LocalInstallationInstallerUbuntuPrimitiveInspector rootOwned = new(
            (start, _) =>
            {
                starts.Add(start);
                return Task.FromResult(new
                    InstallationInstallerUbuntuDirectProcessResult(
                        0,
                        "root:root\n",
                        string.Empty));
            });

        InstallationInstallerUbuntuPrimitiveInspection missing =
            await rootOwned.InspectAsync(
                Request(),
                SetupIdentityOperation(identity, protection, setup));

        Assert.Equal(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing,
            missing.Outcome);
        Assert.Equal(6, starts.Count);
        Assert.All(starts, start =>
        {
            Assert.Equal("/usr/bin/stat", start.FileName);
            Assert.Equal(
                "--format=%U:%G",
                start.ArgumentList[0]);
            Assert.Equal("--", start.ArgumentList[1]);
        });

        LocalInstallationInstallerUbuntuPrimitiveInspector adopted = new(
            (_, _) => Task.FromResult(new
                InstallationInstallerUbuntuDirectProcessResult(
                    0,
                    "aethersdr:aethersdr\n",
                    string.Empty)));

        InstallationInstallerUbuntuPrimitiveInspection converged =
            await adopted.InspectAsync(
                Request(),
                SetupIdentityOperation(identity, protection, setup));

        Assert.Equal(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged,
            converged.Outcome);
    }

    [Fact]
    public async Task SetupIdentityHandoffRejectsSymbolicLinksWithoutChown()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using TemporaryDirectory temporary = new();
        string identity = Path.Combine(temporary.Path, "identity");
        string protection = Path.Combine(temporary.Path, "data-protection");
        string setup = Path.Combine(temporary.Path, "setup");
        Directory.CreateDirectory(identity);
        Directory.CreateDirectory(protection);
        Directory.CreateDirectory(setup);
        File.CreateSymbolicLink(
            Path.Combine(identity, "unexpected-link"),
            "/etc/passwd");
        LocalInstallationInstallerUbuntuPrimitiveInspector inspector = new(
            (_, _) => throw new InvalidOperationException(
                "Unsafe ownership inventory must not invoke stat."));

        InstallationInstallerUbuntuPrimitiveInspection rejected =
            await inspector.InspectAsync(
                Request(),
                SetupIdentityOperation(identity, protection, setup));

        Assert.Equal(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Rejected,
            rejected.Outcome);
        Assert.Equal(
            "ubuntu-setup-identity-handoff-unsafe",
            rejected.Code);
    }

    [Fact]
    public async Task IdentityInspectionIsMissingBeforeReleasePublication()
    {
        using TemporaryDirectory temporary = new();
        LocalInstallationInstallerUbuntuManagedPrimitiveHandler handler = new(
            (_, _) => throw new InvalidOperationException(
                "A missing release must not start the identity command."));

        InstallationInstallerUbuntuPrimitiveInspection inspection =
            await handler.InspectAsync(
                Request(targetReleasePath:
                    Path.Combine(temporary.Path, "missing-release")),
                IdentityOperation());

        Assert.Equal(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Missing,
            inspection.Outcome);
    }

    [Fact]
    public async Task IdentityInitializationUsesFixedServiceIdentityAndExactPlan()
    {
        using TemporaryDirectory temporary = new();
        string release = CreateExecutableGatewayRelease(temporary.Path);
        string missingPlanId = new('b', 64);
        Queue<InstallationInstallerUbuntuDirectProcessResult> results = new(
        [
            ProcessResult(IdentityReport(
                "incomplete",
                "identity-schema-not-initialized",
                existingSchemaVersion: null,
                mutationRequired: true,
                mutationAttempted: false,
                databaseCreated: false,
                new string('a', 64))),
            ProcessResult(IdentityReport(
                "planned",
                "identity-schema-initialization-required",
                existingSchemaVersion: null,
                mutationRequired: true,
                mutationAttempted: false,
                databaseCreated: false,
                missingPlanId)),
            ProcessResult(IdentityReport(
                "applied",
                "identity-schema-initialized",
                existingSchemaVersion: 1,
                mutationRequired: false,
                mutationAttempted: true,
                databaseCreated: true,
                new string('c', 64))),
            ProcessResult(IdentityReport(
                "converged",
                "identity-schema-converged",
                existingSchemaVersion: 1,
                mutationRequired: false,
                mutationAttempted: false,
                databaseCreated: false,
                new string('d', 64)))
        ]);
        List<ProcessStartInfo> starts = [];
        LocalInstallationInstallerUbuntuManagedPrimitiveHandler handler = new(
            (start, _) =>
            {
                starts.Add(start);
                return Task.FromResult(results.Dequeue());
            });

        InstallationInstallerUbuntuStepResult result = await handler.ExecuteAsync(
            Request(targetReleasePath: release),
            IdentityOperation());

        Assert.Equal(
            InstallationInstallerUbuntuStepOutcome.Applied,
            result.Outcome);
        Assert.Equal("ubuntu-identity-schema-initialized", result.Code);
        Assert.Empty(results);
        Assert.Equal(4, starts.Count);
        Assert.All(
            starts,
            start =>
            {
                Assert.Equal("/usr/sbin/runuser", start.FileName);
                Assert.Equal(
                    Path.Combine(release, "gateway-web"),
                    start.WorkingDirectory);
                Assert.False(start.UseShellExecute);
                Assert.Empty(start.Environment);
                Assert.Equal(
                    ["--user", "aethersdr", "--",
                        Path.Combine(
                            release,
                            "gateway-web",
                            "AetherSDR.Web")],
                    start.ArgumentList.Cast<string>().Take(4));
            });
        Assert.Equal(
            "--identity-database-validate",
            starts[0].ArgumentList[4]);
        Assert.Equal(
            "--identity-database-plan",
            starts[1].ArgumentList[4]);
        Assert.Equal(
            [
                "--identity-database-apply",
                "--confirm-identity-database-plan",
                missingPlanId
            ],
            starts[2].ArgumentList.Cast<string>().Skip(4));
        Assert.Equal(
            "--identity-database-validate",
            starts[3].ArgumentList[4]);
    }

    [Fact]
    public async Task IdentityInitializationIsIdempotentWhenAlreadyConverged()
    {
        using TemporaryDirectory temporary = new();
        string release = CreateExecutableGatewayRelease(temporary.Path);
        List<ProcessStartInfo> starts = [];
        LocalInstallationInstallerUbuntuManagedPrimitiveHandler handler = new(
            (start, _) =>
            {
                starts.Add(start);
                return Task.FromResult(ProcessResult(IdentityReport(
                    "converged",
                    "identity-schema-converged",
                    existingSchemaVersion: 1,
                    mutationRequired: false,
                    mutationAttempted: false,
                    databaseCreated: false,
                    new string('a', 64))));
            });

        InstallationInstallerUbuntuStepResult result = await handler.ExecuteAsync(
            Request(targetReleasePath: release, repair: true),
            IdentityOperation());

        Assert.Equal(
            InstallationInstallerUbuntuStepOutcome.Converged,
            result.Outcome);
        ProcessStartInfo start = Assert.Single(starts);
        Assert.Equal(
            "--identity-database-validate",
            start.ArgumentList[4]);
    }

    [Fact]
    public async Task IdentityApplyFailureRequiresReconciliation()
    {
        using TemporaryDirectory temporary = new();
        string release = CreateExecutableGatewayRelease(temporary.Path);
        Queue<InstallationInstallerUbuntuDirectProcessResult> results = new(
        [
            ProcessResult(IdentityReport(
                "incomplete",
                "identity-schema-not-initialized",
                existingSchemaVersion: null,
                mutationRequired: true,
                mutationAttempted: false,
                databaseCreated: false,
                new string('a', 64))),
            ProcessResult(IdentityReport(
                "planned",
                "identity-schema-initialization-required",
                existingSchemaVersion: null,
                mutationRequired: true,
                mutationAttempted: false,
                databaseCreated: false,
                new string('b', 64))),
            new(
                2,
                "{\"outcome\":\"rejected\"}",
                string.Empty)
        ]);
        LocalInstallationInstallerUbuntuManagedPrimitiveHandler handler = new(
            (_, _) => Task.FromResult(results.Dequeue()));

        InstallationInstallerUbuntuStepResult result = await handler.ExecuteAsync(
            Request(targetReleasePath: release),
            IdentityOperation());

        Assert.Equal(
            InstallationInstallerUbuntuStepOutcome.Unknown,
            result.Outcome);
        Assert.Equal("ubuntu-identity-apply-unknown", result.Code);
        Assert.Empty(results);
    }

    [Fact]
    public async Task PointerRollbackPreservesMismatchedSymlink()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "target");
        string other = Path.Combine(temporary.Path, "other");
        string current = Path.Combine(temporary.Path, "current");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(other);
        Directory.CreateSymbolicLink(current, other);
        LocalInstallationInstallerUbuntuManagedPrimitiveHandler handler = new(
            (_, _) => throw new InvalidOperationException(
                "Pointer rollback must not run a process."),
            () => new HttpClientHandler(),
            current);

        InstallationInstallerUbuntuStepResult result =
            await handler.RollbackInitialReleaseAsync(
                Request(targetReleasePath: target));

        Assert.Equal(
            InstallationInstallerUbuntuStepOutcome.Unknown,
            result.Outcome);
        Assert.Equal(
            "ubuntu-current-pointer-rollback-preserved",
            result.Code);
        Assert.Equal(
            other,
            Path.GetFullPath(
                Path.Combine(
                    temporary.Path,
                    new DirectoryInfo(current).LinkTarget!)));
    }

    [Fact]
    public async Task PointerRollbackRemovesOnlyExactTransactionSymlink()
    {
        using TemporaryDirectory temporary = new();
        string target = Path.Combine(temporary.Path, "target");
        string current = Path.Combine(temporary.Path, "current");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(current, target);
        LocalInstallationInstallerUbuntuManagedPrimitiveHandler handler = new(
            (_, _) => throw new InvalidOperationException(
                "Pointer rollback must not run a process."),
            () => new HttpClientHandler(),
            current);

        InstallationInstallerUbuntuStepResult result =
            await handler.RollbackInitialReleaseAsync(
                Request(targetReleasePath: target));

        Assert.Equal(
            InstallationInstallerUbuntuStepOutcome.Applied,
            result.Outcome);
        Assert.False(Directory.Exists(current));
        Assert.Null(new DirectoryInfo(current).LinkTarget);
    }

    private static InstallationInstallerUbuntuPrimitiveOperation
        SetupIdentityOperation(
            string identity,
            string protection,
            string setup) =>
        new(
            2,
            InstallationInstallerUbuntuPrimitiveKind.AdoptSetupIdentityState,
            Path.GetDirectoryName(identity)!,
            "/usr/bin/chown",
            [
                "--recursive",
                "--no-dereference",
                "aethersdr:aethersdr",
                "--",
                identity,
                protection,
                setup
            ]);

    private static InstallationInstallerUbuntuPrimitiveOperation
        IdentityOperation() =>
        new(
            3,
            InstallationInstallerUbuntuPrimitiveKind.InitializeIdentityDatabase,
            "/var/lib/aethersdr/identity/aethersdr-identity.db",
            Executable: string.Empty,
            Arguments: []);

    private static InstallationInstallerUbuntuDirectProcessResult ProcessResult(
        string output) =>
        new(0, output, string.Empty);

    private static string IdentityReport(
        string outcome,
        string code,
        int? existingSchemaVersion,
        bool mutationRequired,
        bool mutationAttempted,
        bool databaseCreated,
        string planId) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            outcome,
            code,
            targetSchemaVersion = 1,
            existingSchemaVersion,
            databasePath =
                "/var/lib/aethersdr/identity/aethersdr-identity.db",
            planId,
            mutationRequired,
            mutationAttempted,
            databaseCreated,
            backupRequired = false,
            rollbackAttempted = false,
            rollbackSucceeded = false
        });

    private static string CreateExecutableGatewayRelease(string root)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "The Ubuntu primitive test requires Linux.");
        }
        string release = Path.Combine(root, "release");
        string gateway = Path.Combine(release, "gateway-web");
        string executable = Path.Combine(gateway, "AetherSDR.Web");
        Directory.CreateDirectory(gateway);
        File.WriteAllText(executable, "#!/bin/false\n");
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);
        return release;
    }

    private static InstallationInstallerUbuntuPrimitiveInspection Inspection(
        InstallationInstallerUbuntuPrimitiveInspectionOutcome outcome) =>
        new(outcome, "inspection-code", "The inspection result is bounded.");

    private static VerifiedReleaseInstallationPlan InitialReleasePlan(
        ReleaseMigrationKind migrationKind,
        bool restartHost) =>
        new(
            setupRevision: 7,
            installedReleaseIdentity: string.Empty,
            targetReleaseIdentity: "2026.8.0",
            targetVersion: "2026.8.0",
            ReleaseManifestArchitecture.LinuxX64,
            InstallationUpdateChannel.Stable,
            pinnedReleaseIdentity: string.Empty,
            installTransmitSupport: false,
            bundleDirectory: "/srv/aethersdr-bundle",
            manifestLength: 1,
            manifestSha256: new byte[32],
            releaseRootPath: "/opt/aethersdr/releases",
            deploymentRootPath: "/opt/aethersdr",
            targetReleasePath: "/opt/aethersdr/releases/2026.8.0",
            packages: [],
            targetConfigurationSchemaVersion: 1,
            migrationKind,
            migrationFromConfigurationSchemaVersion:
                migrationKind == ReleaseMigrationKind.Required ? 1 : null,
            migrationToConfigurationSchemaVersion:
                migrationKind == ReleaseMigrationKind.Required ? 2 : null,
            migrationIdentity:
                migrationKind == ReleaseMigrationKind.Required
                    ? "schema-1-to-2"
                    : string.Empty,
            restartGatewayWeb: false,
            restartBroker: false,
            restartAetherRemoteAgent: false,
            restartStationEngine: false,
            restartHost,
            txSupportCapable: false,
            releaseNotesTitle: "Test release",
            releaseNotesSummary: "Initial release boundary test.");

    private static InstallationInstallerUbuntuMutationRequest ActivationRequest(
        bool repair = false) =>
        Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64"),
            new(
                3,
                InstallationInstallerActionKind.ActivateInitialRelease,
                "2026.8.0"),
            new(
                4,
                InstallationInstallerActionKind.ActivateSystemdUnit,
                "aethersdr-web.service"),
            new(
                5,
                InstallationInstallerActionKind.VerifyHealth,
                "https://radio.example/healthz")
        ],
        repair);

    private static InstallationInstallerUbuntuMutationRequest DirectoryRequest(
        bool repair) =>
        Request(
        [
            new(
                1,
                InstallationInstallerActionKind.EnsureServiceUser,
                "aethersdr"),
            new(
                2,
                InstallationInstallerActionKind.EnsureDirectory,
                "/var/lib/aethersdr"),
            new(
                3,
                InstallationInstallerActionKind.InstallVerifiedRelease,
                "2026.8.0/linux-x64")
        ],
        repair);

    private static InstallationInstallerUbuntuMutationRequest Request(
        IReadOnlyList<InstallationInstallerPlanAction>? actions = null,
        bool repair = false,
        string? targetReleasePath = null) =>
        new(
            new string('a', 64),
            setupRevision: 7,
            releaseIdentity: "2026.8.0",
            InstallationInstallerArchitecture.LinuxX64,
            immutableStagingPath: "/var/lib/aethersdr/.release-staging/exact",
            targetReleasePath:
                targetReleasePath ??
                "/var/lib/aethersdr/releases/2026.8.0",
            repair,
            actions: actions ??
            [
                new(
                    1,
                    InstallationInstallerActionKind.EnsureServiceUser,
                    "aethersdr"),
                new(
                    2,
                    InstallationInstallerActionKind.InstallVerifiedRelease,
                    "2026.8.0/linux-x64"),
                new(
                    3,
                    InstallationInstallerActionKind.InstallSystemdUnit,
                    "aethersdr-web.service")
            ]);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aethersdr-installer-rollback-{Guid.NewGuid():N}");
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

    private sealed class FakeInspector :
        IInstallationInstallerUbuntuPrimitiveInspector
    {
        internal Queue<InstallationInstallerUbuntuPrimitiveInspection> Results
        {
            get;
        } = new();

        internal int InspectionCount { get; private set; }

        public Task<InstallationInstallerUbuntuPrimitiveInspection> InspectAsync(
            InstallationInstallerUbuntuMutationRequest request,
            InstallationInstallerUbuntuPrimitiveOperation operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectionCount++;
            return Task.FromResult(
                Results.Count > 0
                    ? Results.Dequeue()
                    : Inspection(
                        InstallationInstallerUbuntuPrimitiveInspectionOutcome
                            .Converged));
        }
    }

    private class FakePrimitives :
        IInstallationInstallerUbuntuMutationPrimitives
    {
        internal InstallationInstallerUbuntuStepResult PrepareResult
        {
            get;
            init;
        } = InstallationInstallerUbuntuStepResult.Converged();

        internal int? CancelAtOrder { get; init; }

        internal Queue<InstallationInstallerUbuntuStepResult> StepResults
        {
            get;
        } = new();

        internal List<string> Events { get; } = [];

        public Task<InstallationInstallerUbuntuStepResult> PrepareAsync(
            InstallationInstallerUbuntuMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("prepare");
            return Task.FromResult(PrepareResult);
        }

        public Task<InstallationInstallerUbuntuStepResult> ExecuteAsync(
            InstallationInstallerUbuntuMutationRequest request,
            InstallationInstallerPlanAction action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"execute:{action.Order}");
            if (CancelAtOrder == action.Order)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            return Task.FromResult(
                StepResults.Count > 0
                    ? StepResults.Dequeue()
                    : InstallationInstallerUbuntuStepResult.Converged());
        }
    }

    private sealed class FakeManagedHandler :
        IInstallationInstallerUbuntuManagedPrimitiveHandler
    {
        internal InstallationInstallerUbuntuPrimitiveInspection Inspection
        {
            get;
            init;
        } = new(
            InstallationInstallerUbuntuPrimitiveInspectionOutcome.Converged,
            "managed-converged",
            "The fake managed primitive is converged.");

        internal int InspectionCount { get; private set; }

        public bool Supports(InstallationInstallerUbuntuPrimitiveKind kind) =>
            kind == InstallationInstallerUbuntuPrimitiveKind.VerifyImmutableRelease;

        public Task<InstallationInstallerUbuntuPrimitiveInspection> InspectAsync(
            InstallationInstallerUbuntuMutationRequest request,
            InstallationInstallerUbuntuPrimitiveOperation operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectionCount++;
            return Task.FromResult(Inspection);
        }

        public Task<InstallationInstallerUbuntuStepResult> ExecuteAsync(
            InstallationInstallerUbuntuMutationRequest request,
            InstallationInstallerUbuntuPrimitiveOperation operation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The fake managed mutation path is not used.");

        public Task<InstallationInstallerUbuntuStepResult>
            RollbackInitialReleaseAsync(
                InstallationInstallerUbuntuMutationRequest request,
                CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "The fake managed rollback path is not used.");
    }

    private sealed class FakeRollbackPrimitives :
        FakePrimitives,
        IInstallationInstallerUbuntuMutationRollback
    {
        internal InstallationInstallerUbuntuStepResult RollbackResult
        {
            get;
            init;
        } = InstallationInstallerUbuntuStepResult.Applied(
            "rollback-applied",
            "The fake activation rollback completed.");

        internal List<int> RollbackCandidateOrders { get; } = [];

        public Task<InstallationInstallerUbuntuStepResult> RollbackAsync(
            InstallationInstallerUbuntuMutationRequest request,
            IReadOnlyList<InstallationInstallerPlanAction> rollbackCandidates,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RollbackCandidateOrders.AddRange(
                rollbackCandidates.Select(action => action.Order));
            Events.Add("rollback");
            return Task.FromResult(RollbackResult);
        }
    }
}
