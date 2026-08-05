using System.Reflection;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseActivationOperatorApprovalTests
{
    [Fact]
    public void PublicSurfaceExposesDiagnosticsButNoApprovalCaller()
    {
        string[] methods =
            typeof(VerifiedReleaseActivationOperatorApprovalAuthority)
                .GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(["get_Snapshot"], methods);
    }

    [Fact]
    public void DisabledDefaultsExposeNoOperationalAuthority()
    {
        Fixture fixture = new(enabled: false);

        VerifiedReleaseActivationOperatorApprovalDiagnostics snapshot =
            fixture.Authority.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.False(snapshot.AuthorityEnabled);
        Assert.Equal(300, snapshot.MaximumApprovalAgeSeconds);
        Assert.True(snapshot.ExactPlanBindingRegistered);
        Assert.True(snapshot.AuthenticationEvidenceRequired);
        Assert.True(snapshot.AdministratorAuthorizationRequired);
        Assert.True(snapshot.ReauthenticationRequired);
        Assert.True(snapshot.BoundedApprovalLifetimeRegistered);
        Assert.True(snapshot.SingleActiveApprovalRegistered);
        Assert.True(snapshot.RevocationRegistered);
        Assert.False(snapshot.ActiveApproval);
        Assert.False(snapshot.ApprovalAvailable);
        Assert.False(snapshot.FileWriteRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
        Assert.False(snapshot.ActivationExecutionRegistered);
        Assert.False(snapshot.ActivationAuthorityRegistered);
        Assert.False(snapshot.TxLeaseMutationRegistered);
        Assert.False(snapshot.RadioCommandRegistered);
        Assert.False(snapshot.WatchdogMutationRegistered);
        Assert.False(snapshot.BackupExecutionRegistered);
        Assert.False(snapshot.MigrationExecutionRegistered);
        Assert.False(snapshot.ServiceControlRegistered);
        Assert.False(snapshot.HealthProbeCallerRegistered);
        Assert.False(snapshot.RollbackExecutionRegistered);
        Assert.False(snapshot.CliCallerRegistered);
        Assert.False(snapshot.AdminCallerRegistered);
        Assert.False(snapshot.BrowserCallerRegistered);
        Assert.False(snapshot.HttpCallerRegistered);
        Assert.False(snapshot.WebSocketCallerRegistered);
        Assert.False(snapshot.HostedServiceCallerRegistered);
        Assert.False(snapshot.TimerCallerRegistered);
        Assert.False(snapshot.AetherRemoteCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public void DisabledAuthorityRejectsWithoutRetainingApproval()
    {
        Fixture fixture = new(enabled: false);

        VerifiedReleaseActivationOperatorApprovalReport report =
            fixture.Approve();

        AssertFailure(
            report,
            VerifiedReleaseActivationOperatorApprovalFailureCode
                .AuthorityDisabled);
        Assert.False(fixture.Authority.Observe(fixture.Plan).OperatorApproved);
        Assert.Equal(1, fixture.Authority.Snapshot.AttemptCount);
        Assert.Equal(0, fixture.Authority.Snapshot.AcceptedCount);
        Assert.Equal(1, fixture.Authority.Snapshot.RejectedCount);
    }

    [Fact]
    public void FreshAdministratorReauthenticationCreatesExactExpiringApproval()
    {
        Fixture fixture = new(enabled: true);

        VerifiedReleaseActivationOperatorApprovalReport report =
            fixture.Approve();

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationOperatorApprovalFailureCode.None,
            report.FailureCode);
        Assert.True(report.AuthorityEnabled);
        Assert.True(report.ExactPlanBound);
        Assert.True(report.AuthenticationCurrent);
        Assert.True(report.AdministratorAuthorized);
        Assert.True(report.ReauthenticationCurrent);
        Assert.True(report.ApprovalFresh);
        Assert.True(report.ApprovalStored);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
        VerifiedReleaseActivationOperatorApproval approval =
            Assert.IsType<VerifiedReleaseActivationOperatorApproval>(
                report.Approval);
        Assert.Same(fixture.Plan, approval.Plan);
        Assert.Equal(fixture.Now, approval.ApprovedAt);
        Assert.Equal(fixture.Now.AddMinutes(5), approval.ExpiresAt);

        VerifiedReleaseActivationOperatorApprovalObservation observation =
            fixture.Authority.Observe(fixture.Plan);
        Assert.True(observation.OperatorApproved);
        Assert.Equal(fixture.Now, observation.ApprovedAt);
        Assert.Equal(fixture.Now.AddMinutes(5), observation.ExpiresAt);
        Assert.False(observation.Revoked);
        Assert.True(fixture.Authority.Snapshot.ActiveApproval);
        Assert.True(fixture.Authority.Snapshot.ApprovalAvailable);
    }

    [Fact]
    public void PublicReportRedactsApprovalAndSubjectIdentities()
    {
        Fixture fixture = new(enabled: true);

        string json = JsonSerializer.Serialize(fixture.Approve());

        Assert.DoesNotContain(
            "0123456789abcdef0123456789abcdef",
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "admin-user-secret",
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SubjectBinding",
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ApprovalIdentity",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnauthenticatedEvidenceFailsClosed()
    {
        Fixture fixture = new(enabled: true);

        VerifiedReleaseActivationOperatorApprovalReport report =
            fixture.Approve(authenticated: false);

        AssertFailure(
            report,
            VerifiedReleaseActivationOperatorApprovalFailureCode
                .AuthenticationNotCurrent);
        Assert.True(report.ExactPlanBound);
        Assert.False(report.AuthenticationCurrent);
        Assert.False(report.AdministratorAuthorized);
    }

    [Fact]
    public void NonAdministratorAuthenticationFailsClosed()
    {
        Fixture fixture = new(enabled: true);

        VerifiedReleaseActivationOperatorApprovalReport report =
            fixture.Approve(administratorAuthorized: false);

        AssertFailure(
            report,
            VerifiedReleaseActivationOperatorApprovalFailureCode
                .AdministratorAuthorizationMissing);
        Assert.True(report.ExactPlanBound);
        Assert.True(report.AuthenticationCurrent);
        Assert.False(report.AdministratorAuthorized);
    }

    [Fact]
    public void StaleReauthenticationFailsClosed()
    {
        Fixture fixture = new(enabled: true);

        VerifiedReleaseActivationOperatorApprovalReport report =
            fixture.Approve(
                reauthenticatedAt: fixture.Now.AddMinutes(-5));

        AssertFailure(
            report,
            VerifiedReleaseActivationOperatorApprovalFailureCode
                .ReauthenticationRequired);
        Assert.True(report.ExactPlanBound);
        Assert.True(report.AuthenticationCurrent);
        Assert.True(report.AdministratorAuthorized);
        Assert.False(report.ReauthenticationCurrent);
    }

    [Fact]
    public void MalformedAuthenticationEvidenceFailsClosed()
    {
        Fixture fixture = new(enabled: true);

        VerifiedReleaseActivationOperatorApprovalReport report =
            fixture.Approve(subjectBinding: " admin-user-secret ");

        AssertFailure(
            report,
            VerifiedReleaseActivationOperatorApprovalFailureCode
                .AuthenticationEvidenceInvalid);
    }

    [Fact]
    public void SuccessfulSummaryWithoutRetainedPlanFailsClosed()
    {
        Fixture fixture = new(enabled: true);
        VerifiedReleaseActivationPlanCompositionResult missing =
            fixture.PlanResult with { Plan = null };

        VerifiedReleaseActivationOperatorApprovalReport report =
            fixture.Authority.Approve(
                missing,
                fixture.Authentication());

        AssertFailure(
            report,
            VerifiedReleaseActivationOperatorApprovalFailureCode
                .ActivationPlanUnavailable);
    }

    [Fact]
    public void ActivationPlanSummaryMismatchFailsClosed()
    {
        Fixture fixture = new(enabled: true);
        VerifiedReleaseActivationPlanCompositionResult mismatched =
            fixture.PlanResult with { TargetVersion = "8.2.1" };

        VerifiedReleaseActivationOperatorApprovalReport report =
            fixture.Authority.Approve(
                mismatched,
                fixture.Authentication());

        AssertFailure(
            report,
            VerifiedReleaseActivationOperatorApprovalFailureCode
                .ActivationPlanMismatch);
    }

    [Fact]
    public void EquivalentDistinctPlanCannotReuseApproval()
    {
        Fixture first = new(enabled: true);
        Fixture second = new(enabled: true);
        Assert.True(first.Approve().Succeeded);

        VerifiedReleaseActivationOperatorApprovalObservation observation =
            first.Authority.Observe(second.Plan);

        Assert.False(observation.OperatorApproved);
        Assert.Null(observation.ApprovedAt);
        Assert.Null(observation.ExpiresAt);
    }

    [Fact]
    public void OnlyOneApprovalMayBeActive()
    {
        Fixture fixture = new(enabled: true);
        Assert.True(fixture.Approve().Succeeded);

        VerifiedReleaseActivationOperatorApprovalReport duplicate =
            fixture.Approve();

        AssertFailure(
            duplicate,
            VerifiedReleaseActivationOperatorApprovalFailureCode
                .ApprovalAlreadyActive);
        Assert.Equal(2, fixture.Authority.Snapshot.AttemptCount);
        Assert.Equal(1, fixture.Authority.Snapshot.AcceptedCount);
        Assert.Equal(1, fixture.Authority.Snapshot.RejectedCount);
    }

    [Fact]
    public void ExpiredApprovalIsUnavailableAndMayBeReplaced()
    {
        Fixture fixture = new(enabled: true);
        Assert.True(fixture.Approve().Succeeded);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.False(fixture.Authority.Observe(fixture.Plan).OperatorApproved);
        Assert.False(fixture.Authority.Snapshot.ActiveApproval);
        Assert.False(fixture.Authority.Snapshot.ApprovalAvailable);

        VerifiedReleaseActivationOperatorApprovalReport replacement =
            fixture.Approve(
                authenticatedAt: fixture.Time.GetUtcNow(),
                reauthenticatedAt: fixture.Time.GetUtcNow());
        Assert.True(replacement.Succeeded);
        Assert.NotSame(
            replacement.Approval,
            fixture.InitialApproval);
    }

    [Fact]
    public void ExactApprovalCanBeRevokedOnce()
    {
        Fixture fixture = new(enabled: true);
        VerifiedReleaseActivationOperatorApproval approval =
            Assert.IsType<VerifiedReleaseActivationOperatorApproval>(
                fixture.Approve().Approval);

        Assert.True(fixture.Authority.Revoke(approval));
        Assert.False(fixture.Authority.Revoke(approval));
        VerifiedReleaseActivationOperatorApprovalObservation observation =
            fixture.Authority.Observe(fixture.Plan);
        Assert.False(observation.OperatorApproved);
        Assert.True(observation.Revoked);
        Assert.Equal(1, fixture.Authority.Snapshot.RevokedCount);
        Assert.False(fixture.Authority.Snapshot.ActivationAuthorityRegistered);
    }

    [Fact]
    public void InvalidApprovalIdentitySourceFailsClosed()
    {
        Fixture fixture = new(
            enabled: true,
            approvalIdentityFactory: () => "not-canonical");

        VerifiedReleaseActivationOperatorApprovalReport report =
            fixture.Approve();

        AssertFailure(
            report,
            VerifiedReleaseActivationOperatorApprovalFailureCode
                .ApprovalIdentityInvalid);
        Assert.False(fixture.Authority.Observe(fixture.Plan).OperatorApproved);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(601)]
    public void ApprovalAgeOutsideBoundedRangeIsRejected(int seconds)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new VerifiedReleaseActivationOperatorApprovalAuthority(
                new ReleaseActivationOperatorApprovalSettings
                {
                    AuthorityEnabled = true,
                    MaximumApprovalAgeSeconds = seconds
                },
                TimeProvider.System));
    }

    private static void AssertFailure(
        VerifiedReleaseActivationOperatorApprovalReport report,
        VerifiedReleaseActivationOperatorApprovalFailureCode failureCode)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.False(report.ApprovalFresh);
        Assert.False(report.ApprovalStored);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
        Assert.Null(report.Approval);
    }

    private sealed class Fixture
    {
        internal Fixture(
            bool enabled,
            Func<string>? approvalIdentityFactory = null)
        {
            Now = new DateTimeOffset(2026, 8, 5, 2, 0, 0, TimeSpan.Zero);
            Time = new ManualTimeProvider(Now);
            PlanResult = CreatePlanResult();
            Plan = Assert.IsType<VerifiedReleaseActivationPlan>(PlanResult.Plan);
            Authority = new VerifiedReleaseActivationOperatorApprovalAuthority(
                new ReleaseActivationOperatorApprovalSettings
                {
                    AuthorityEnabled = enabled,
                    MaximumApprovalAgeSeconds = 300
                },
                Time,
                approvalIdentityFactory ??
                    (() => "0123456789abcdef0123456789abcdef"));
        }

        internal DateTimeOffset Now { get; }
        internal ManualTimeProvider Time { get; }
        internal VerifiedReleaseActivationPlanCompositionResult PlanResult { get; }
        internal VerifiedReleaseActivationPlan Plan { get; }
        internal VerifiedReleaseActivationOperatorApprovalAuthority Authority { get; }
        internal VerifiedReleaseActivationOperatorApproval? InitialApproval { get; private set; }

        internal VerifiedReleaseActivationOperatorApprovalReport Approve(
            string subjectBinding = "admin-user-secret",
            bool authenticated = true,
            bool administratorAuthorized = true,
            DateTimeOffset? authenticatedAt = null,
            DateTimeOffset? reauthenticatedAt = null)
        {
            VerifiedReleaseActivationOperatorApprovalReport report =
                Authority.Approve(
                    PlanResult,
                    Authentication(
                        subjectBinding,
                        authenticated,
                        administratorAuthorized,
                        authenticatedAt,
                        reauthenticatedAt));
            InitialApproval ??= report.Approval;
            return report;
        }

        internal VerifiedReleaseActivationOperatorAuthenticationEvidence
            Authentication(
                string subjectBinding = "admin-user-secret",
                bool authenticated = true,
                bool administratorAuthorized = true,
                DateTimeOffset? authenticatedAt = null,
                DateTimeOffset? reauthenticatedAt = null) =>
            new(
                subjectBinding,
                authenticated,
                administratorAuthorized,
                authenticatedAt ?? Time.GetUtcNow().AddMinutes(-10),
                reauthenticatedAt ?? Time.GetUtcNow());

        private static VerifiedReleaseActivationPlanCompositionResult
            CreatePlanResult()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-operator-approval-{Guid.NewGuid():N}");
            string deploymentRoot = Path.Combine(root, "deployment");
            string releaseRoot = Path.Combine(deploymentRoot, "releases");
            string targetPath = Path.Combine(releaseRoot, "aethersdr-8.2.0");
            VerifiedReleaseInstallationPackagePlan[] packages =
                CreatePackages(targetPath);
            VerifiedReleaseInstallationPlan installPlan = new(
                setupRevision: 7,
                installedReleaseIdentity: "aethersdr-8.1.0",
                targetReleaseIdentity: "aethersdr-8.2.0",
                targetVersion: "8.2.0",
                ReleaseManifestArchitecture.LinuxX64,
                InstallationUpdateChannel.Stable,
                pinnedReleaseIdentity: string.Empty,
                installTransmitSupport: false,
                bundleDirectory: Path.Combine(root, "bundle"),
                manifestLength: 37,
                manifestSha256: Enumerable.Repeat((byte)0x7A, 32).ToArray(),
                releaseRoot,
                deploymentRoot,
                targetPath,
                packages,
                targetConfigurationSchemaVersion: 2,
                ReleaseMigrationKind.Required,
                migrationFromConfigurationSchemaVersion: 1,
                migrationToConfigurationSchemaVersion: 2,
                migrationIdentity: "schema-1-to-2",
                restartGatewayWeb: true,
                restartBroker: true,
                restartAetherRemoteAgent: true,
                restartStationEngine: true,
                restartHost: false,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary: "Operator approval test release.");
            long bytes = 37 + packages.Sum(package => package.Length);
            VerifiedReleasePublicationReport publication =
                VerifiedReleasePublicationReport.Success(
                    new VerifiedPublishedRelease(installPlan, targetPath, bytes));
            VerifiedReleaseActivationPlanCompositionResult result =
                new VerifiedReleaseActivationPlanComposer().Compose(publication);
            Assert.True(result.Succeeded);
            return result;
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
                    Sha256 = new string((char)('A' + index), 64)
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
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        internal void Advance(TimeSpan duration) => m_now += duration;
    }
}
