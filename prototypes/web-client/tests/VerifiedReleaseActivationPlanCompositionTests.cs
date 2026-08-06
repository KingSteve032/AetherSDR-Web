using System.Reflection;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseActivationPlanCompositionTests
{
    [Fact]
    public void PublicSurfaceExposesOnlyDiagnosticsAndPureComposition()
    {
        string[] methods = typeof(VerifiedReleaseActivationPlanComposer)
            .GetMethods(
                BindingFlags.Public |
                BindingFlags.Instance |
                BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Compose", "Compose", "get_Snapshot"], methods);
    }

    [Fact]
    public void DiagnosticsExposePlanningWithoutExecutionAuthority()
    {
        VerifiedReleaseActivationPlanDiagnostics snapshot =
            new VerifiedReleaseActivationPlanComposer().Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.PublishedReleaseInputRegistered);
        Assert.True(snapshot.ActivationPathCompositionRegistered);
        Assert.True(snapshot.TxQuiescencePlanningRegistered);
        Assert.True(snapshot.BackupPlanningRegistered);
        Assert.True(snapshot.MigrationPlanningRegistered);
        Assert.True(snapshot.ServiceRestartPlanningRegistered);
        Assert.True(snapshot.HealthVerificationPlanningRegistered);
        Assert.True(snapshot.RollbackPlanningRegistered);
        Assert.False(snapshot.NetworkDownloadRegistered);
        Assert.False(snapshot.ArchiveExtractionRegistered);
        Assert.False(snapshot.FileWriteRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
        Assert.False(snapshot.ActivationExecutionRegistered);
        Assert.False(snapshot.BackupExecutionRegistered);
        Assert.False(snapshot.MigrationExecutionRegistered);
        Assert.False(snapshot.ServiceControlRegistered);
        Assert.False(snapshot.HealthProbeCallerRegistered);
        Assert.False(snapshot.CliCallerRegistered);
        Assert.False(snapshot.AdminCallerRegistered);
        Assert.False(snapshot.BrowserCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    [Fact]
    public void SuccessfulPublicationProducesCanonicalActivationPlan()
    {
        Fixture fixture = new();

        VerifiedReleaseActivationPlanCompositionResult result =
            fixture.Compose();

        Assert.True(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPlanFailureCode.None,
            result.FailureCode);
        Assert.Equal(7, result.SetupRevision);
        Assert.Equal("aethersdr-8.1.0", result.InstalledReleaseIdentity);
        Assert.Equal("aethersdr-8.2.0", result.TargetReleaseIdentity);
        Assert.Equal("8.2.0", result.TargetVersion);
        Assert.Equal(ReleaseManifestArchitecture.LinuxX64, result.Architecture);
        Assert.Equal(4, result.PackageCount);
        Assert.Equal(fixture.PublishedBytes, result.PublishedBytes);
        Assert.Equal(2, result.TargetConfigurationSchemaVersion);
        Assert.Equal(ReleaseMigrationKind.Required, result.MigrationKind);
        Assert.True(result.MigrationRequired);
        Assert.Equal(4, result.RestartServiceCount);
        Assert.True(result.HostRestartRequired);
        Assert.True(result.TxLeaseAdmissionClosureRequired);
        Assert.True(result.RadioAuthoritativeIdleRequired);
        Assert.True(result.WatchdogsDisarmedRequired);
        Assert.True(result.ConfigurationBackupRequired);
        Assert.True(result.AtomicCurrentPointerSwitchRequired);
        Assert.True(result.ServiceHealthVerificationRequired);
        Assert.True(result.AutomaticRollbackRequired);
        Assert.True(result.OperatorApprovalRequired);
        Assert.False(result.CurrentPointerMutationPerformed);
        Assert.False(result.ActivationPerformed);

        VerifiedReleaseActivationPlan plan =
            Assert.IsType<VerifiedReleaseActivationPlan>(result.Plan);
        Assert.Equal(fixture.ReleaseRoot, plan.ReleaseRootPath);
        Assert.Equal(fixture.DeploymentRoot, plan.DeploymentRootPath);
        Assert.Equal(
            Path.Combine(fixture.ReleaseRoot, "aethersdr-8.1.0"),
            plan.InstalledReleasePath);
        Assert.Equal(fixture.TargetPath, plan.TargetReleasePath);
        Assert.Equal(
            Path.Combine(fixture.DeploymentRoot, "current"),
            plan.CurrentPointerPath);
        Assert.Equal(
            Path.Combine("releases", "aethersdr-8.1.0"),
            plan.InstalledCurrentLinkTarget);
        Assert.Equal(
            Path.Combine("releases", "aethersdr-8.2.0"),
            plan.TargetCurrentLinkTarget);
        Assert.Equal(
            [
                ReleasePackageRole.GatewayWeb,
                ReleasePackageRole.Broker,
                ReleasePackageRole.AetherRemoteAgent,
                ReleasePackageRole.StationEngine
            ],
            plan.Packages.Select(package => package.Role));
    }

    [Fact]
    public void SignedMigrationRestartAndNotesMetadataSurviveComposition()
    {
        Fixture fixture = new();

        VerifiedReleaseActivationPlan plan = fixture.Compose().Plan!;

        Assert.Equal(ReleaseMigrationKind.Required, plan.MigrationKind);
        Assert.Equal(1, plan.MigrationFromConfigurationSchemaVersion);
        Assert.Equal(2, plan.MigrationToConfigurationSchemaVersion);
        Assert.Equal("schema-1-to-2", plan.MigrationIdentity);
        Assert.True(plan.RestartGatewayWeb);
        Assert.True(plan.RestartBroker);
        Assert.True(plan.RestartAetherRemoteAgent);
        Assert.True(plan.RestartStationEngine);
        Assert.True(plan.RestartHost);
        Assert.Equal("AetherSDR 8.2.0", plan.ReleaseNotesTitle);
        Assert.Equal("Verified activation planning metadata.", plan.ReleaseNotesSummary);
        Assert.True(plan.TxLeaseAdmissionClosureRequired);
        Assert.True(plan.RadioAuthoritativeIdleRequired);
        Assert.True(plan.WatchdogsDisarmedRequired);
        Assert.True(plan.ConfigurationBackupRequired);
        Assert.True(plan.ServiceHealthVerificationRequired);
        Assert.True(plan.AutomaticRollbackRequired);
    }

    [Fact]
    public void NoMigrationPublicationProducesNoMigrationStep()
    {
        Fixture fixture = new(
            migrationKind: ReleaseMigrationKind.None,
            migrationFrom: null,
            migrationTo: null,
            migrationIdentity: string.Empty,
            targetSchema: 1);

        VerifiedReleaseActivationPlanCompositionResult result = fixture.Compose();

        Assert.True(result.Succeeded);
        Assert.False(result.MigrationRequired);
        Assert.Equal(ReleaseMigrationKind.None, result.MigrationKind);
        Assert.False(result.Plan!.MigrationRequired);
    }

    [Fact]
    public void PublicResultIsPathPackageAndDigestRedacted()
    {
        Fixture fixture = new();

        string json = JsonSerializer.Serialize(fixture.Compose());

        Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.TargetPath, json, StringComparison.Ordinal);
        Assert.DoesNotContain("gateway.tar", json, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('A', 64), json, StringComparison.Ordinal);
        Assert.DoesNotContain("currentPointerPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publishedPath", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompositionPerformsNoFilesystemIo()
    {
        Fixture fixture = new();
        Assert.False(Directory.Exists(fixture.Root));

        VerifiedReleaseActivationPlanCompositionResult result = fixture.Compose();

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(fixture.Root));
        Assert.False(File.Exists(Path.Combine(fixture.DeploymentRoot, "current")));
    }

    [Fact]
    public void FailedPublicationCannotComposeActivationPlan()
    {
        Fixture fixture = new();
        VerifiedReleasePublicationReport publication = fixture.Publication with
        {
            Succeeded = false,
            FailureCode =
                VerifiedReleasePublicationFailureCode.AtomicPublishFailed
        };

        VerifiedReleaseActivationPlanCompositionResult result =
            new VerifiedReleaseActivationPlanComposer().Compose(publication);

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPlanFailureCode.PublicationNotEligible,
            result.FailureCode);
        Assert.Null(result.Plan);
    }

    [Fact]
    public void SuccessfulSummaryWithoutPublishedTokenFailsClosed()
    {
        Fixture fixture = new();
        VerifiedReleasePublicationReport publication = fixture.Publication with
        {
            PublishedRelease = null
        };

        VerifiedReleaseActivationPlanCompositionResult result =
            new VerifiedReleaseActivationPlanComposer().Compose(publication);

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPlanFailureCode.PublishedReleaseUnavailable,
            result.FailureCode);
    }

    [Theory]
    [InlineData("source-consumed")]
    [InlineData("target-published")]
    [InlineData("target-immutable")]
    [InlineData("current-changed")]
    [InlineData("activation-performed")]
    [InlineData("reconciliation")]
    [InlineData("zero-bytes")]
    [InlineData("wrong-package-count")]
    public void PublicationEvidenceMustBeExact(string mismatch)
    {
        Fixture fixture = new();
        VerifiedReleasePublicationReport publication = mismatch switch
        {
            "source-consumed" => fixture.Publication with
            {
                SourceStagingTreeConsumed = false
            },
            "target-published" => fixture.Publication with
            {
                TargetPublished = false
            },
            "target-immutable" => fixture.Publication with
            {
                TargetImmutable = false
            },
            "current-changed" => fixture.Publication with
            {
                CurrentPointerChanged = true
            },
            "activation-performed" => fixture.Publication with
            {
                ActivationPerformed = true
            },
            "reconciliation" => fixture.Publication with
            {
                ReconciliationRequired = true
            },
            "zero-bytes" => fixture.Publication with { PublishedBytes = 0 },
            "wrong-package-count" => fixture.Publication with { PackageCount = 3 },
            _ => throw new InvalidOperationException()
        };

        VerifiedReleaseActivationPlanCompositionResult result =
            new VerifiedReleaseActivationPlanComposer().Compose(publication);

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPlanFailureCode.PublicationNotEligible,
            result.FailureCode);
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("installed")]
    [InlineData("target")]
    [InlineData("packages")]
    [InlineData("bytes")]
    public void PublicationSummaryMustMatchInternalToken(string mismatch)
    {
        Fixture fixture = new();
        VerifiedReleasePublicationReport publication = mismatch switch
        {
            "setup" => fixture.Publication with { SetupRevision = 8 },
            "installed" => fixture.Publication with
            {
                InstalledReleaseIdentity = "aethersdr-8.0.0"
            },
            "target" => fixture.Publication with
            {
                TargetReleaseIdentity = "aethersdr-8.3.0"
            },
            "packages" => new Fixture(packageMismatch: "missing-package")
                .Publication with
            {
                PackageCount = 4
            },
            "bytes" => fixture.Publication with
            {
                PublishedBytes = fixture.PublishedBytes + 1
            },
            _ => throw new InvalidOperationException()
        };

        VerifiedReleaseActivationPlanCompositionResult result =
            new VerifiedReleaseActivationPlanComposer().Compose(publication);

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPlanFailureCode.PublicationPlanMismatch,
            result.FailureCode);
    }

    [Fact]
    public void PublishedPathMustMatchCanonicalTarget()
    {
        Fixture fixture = new();
        VerifiedReleasePublicationReport publication = fixture.Publication with
        {
            PublishedRelease = new VerifiedPublishedRelease(
                fixture.Plan,
                Path.Combine(fixture.ReleaseRoot, "aethersdr-8.3.0"),
                fixture.PublishedBytes)
        };

        VerifiedReleaseActivationPlanCompositionResult result =
            new VerifiedReleaseActivationPlanComposer().Compose(publication);

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPlanFailureCode.InvalidActivationPaths,
            result.FailureCode);
    }

    [Theory]
    [InlineData("installed-identity")]
    [InlineData("target-identity")]
    [InlineData("same-identity")]
    [InlineData("version")]
    [InlineData("architecture")]
    [InlineData("channel")]
    [InlineData("pinned")]
    [InlineData("tx-policy")]
    [InlineData("release-root")]
    [InlineData("deployment-root")]
    [InlineData("target-path")]
    public void InvalidSourcePlanFailsBeforeActivationPlanning(string mismatch)
    {
        Fixture fixture = mismatch switch
        {
            "installed-identity" => new Fixture(
                installedIdentity: " aethersdr-8.1.0"),
            "target-identity" => new Fixture(
                targetIdentity: "aethersdr-8.2.0 "),
            "same-identity" => new Fixture(targetIdentity: "aethersdr-8.1.0"),
            "version" => new Fixture(targetVersion: "8.2"),
            "architecture" => new Fixture(
                architecture: ReleaseManifestArchitecture.Unknown),
            "channel" => new Fixture(
                updateChannel: (InstallationUpdateChannel)99),
            "pinned" => new Fixture(
                updateChannel: InstallationUpdateChannel.Pinned,
                pinnedIdentity: "aethersdr-8.3.0"),
            "tx-policy" => new Fixture(
                installTransmitSupport: true,
                txSupportCapable: false),
            "release-root" => new Fixture(releaseRootOverride: "relative/releases"),
            "deployment-root" => new Fixture(
                deploymentRootOverride: "relative/deployment"),
            "target-path" => new Fixture(
                targetPathOverride: Path.GetTempPath()),
            _ => throw new InvalidOperationException()
        };

        VerifiedReleaseActivationPlanCompositionResult result = fixture.Compose();

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPlanFailureCode.InvalidActivationPaths,
            result.FailureCode);
    }

    [Theory]
    [InlineData("duplicate-role")]
    [InlineData("duplicate-identity")]
    [InlineData("unsafe-relative-path")]
    [InlineData("target-mismatch")]
    [InlineData("zero-length")]
    public void InvalidPackagePlanFailsClosed(string mismatch)
    {
        Fixture fixture = new(packageMismatch: mismatch);

        VerifiedReleaseActivationPlanCompositionResult result = fixture.Compose();

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPlanFailureCode.InvalidPackagePlan,
            result.FailureCode);
    }

    [Theory]
    [InlineData("schema-zero")]
    [InlineData("none-with-identity")]
    [InlineData("required-without-from")]
    [InlineData("required-without-to")]
    [InlineData("required-backwards")]
    [InlineData("required-without-identity")]
    public void InvalidMigrationPlanFailsClosed(string mismatch)
    {
        Fixture fixture = mismatch switch
        {
            "schema-zero" => new Fixture(targetSchema: 0),
            "none-with-identity" => new Fixture(
                migrationKind: ReleaseMigrationKind.None,
                migrationFrom: null,
                migrationTo: null,
                migrationIdentity: "unexpected",
                targetSchema: 1),
            "required-without-from" => new Fixture(migrationFrom: null),
            "required-without-to" => new Fixture(migrationTo: null),
            "required-backwards" => new Fixture(
                migrationFrom: 2,
                migrationTo: 1,
                targetSchema: 1),
            "required-without-identity" => new Fixture(
                migrationIdentity: string.Empty),
            _ => throw new InvalidOperationException()
        };

        VerifiedReleaseActivationPlanCompositionResult result = fixture.Compose();

        Assert.False(result.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationPlanFailureCode.InvalidMigrationPlan,
            result.FailureCode);
    }

    [Fact]
    public void StableAndBetaPlansRequireNoPinnedIdentity()
    {
        Fixture stable = new();
        Fixture beta = new(updateChannel: InstallationUpdateChannel.Beta);

        Assert.True(stable.Compose().Succeeded);
        Assert.True(beta.Compose().Succeeded);
        Assert.Equal(string.Empty, stable.Compose().Plan!.PinnedReleaseIdentity);
        Assert.Equal(string.Empty, beta.Compose().Plan!.PinnedReleaseIdentity);
    }

    [Fact]
    public void ExactPinnedPlanIsAccepted()
    {
        Fixture fixture = new(
            updateChannel: InstallationUpdateChannel.Pinned,
            pinnedIdentity: "aethersdr-8.2.0");

        VerifiedReleaseActivationPlanCompositionResult result = fixture.Compose();

        Assert.True(result.Succeeded);
        Assert.Equal(
            "aethersdr-8.2.0",
            result.Plan!.PinnedReleaseIdentity);
    }

    private sealed class Fixture
    {
        internal Fixture(
            string installedIdentity = "aethersdr-8.1.0",
            string targetIdentity = "aethersdr-8.2.0",
            string targetVersion = "8.2.0",
            ReleaseManifestArchitecture architecture =
                ReleaseManifestArchitecture.LinuxX64,
            InstallationUpdateChannel updateChannel =
                InstallationUpdateChannel.Stable,
            string pinnedIdentity = "",
            bool installTransmitSupport = false,
            bool txSupportCapable = false,
            string? releaseRootOverride = null,
            string? deploymentRootOverride = null,
            string? targetPathOverride = null,
            ReleaseMigrationKind migrationKind = ReleaseMigrationKind.Required,
            int? migrationFrom = 1,
            int? migrationTo = 2,
            string migrationIdentity = "schema-1-to-2",
            int targetSchema = 2,
            string packageMismatch = "")
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"aethersdr-activation-plan-{Guid.NewGuid():N}");
            DeploymentRoot = deploymentRootOverride ??
                Path.Combine(Root, "deployment");
            ReleaseRoot = releaseRootOverride ??
                Path.Combine(DeploymentRoot, "releases");
            TargetPath = targetPathOverride ??
                Path.Combine(ReleaseRoot, targetIdentity);

            VerifiedReleaseInstallationPackagePlan[] packages =
                CreatePackages(TargetPath, packageMismatch);
            long manifestLength = 37;
            Plan = new VerifiedReleaseInstallationPlan(
                setupRevision: 7,
                installedIdentity,
                targetIdentity,
                targetVersion,
                architecture,
                updateChannel,
                pinnedIdentity,
                installTransmitSupport,
                Path.Combine(Root, "bundle"),
                manifestLength,
                Enumerable.Repeat((byte)0x7A, 32).ToArray(),
                ReleaseRoot,
                DeploymentRoot,
                TargetPath,
                packages,
                targetSchema,
                migrationKind,
                migrationFrom,
                migrationTo,
                migrationIdentity,
                restartGatewayWeb: true,
                restartBroker: true,
                restartAetherRemoteAgent: true,
                restartStationEngine: true,
                restartHost: true,
                txSupportCapable,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary: "Verified activation planning metadata.");
            PublishedBytes = checked(
                manifestLength + packages.Sum(package => package.Length));
            Publication = VerifiedReleasePublicationReport.Success(
                new VerifiedPublishedRelease(
                    Plan,
                    TargetPath,
                    PublishedBytes));
        }

        internal string Root { get; }
        internal string DeploymentRoot { get; }
        internal string ReleaseRoot { get; }
        internal string TargetPath { get; }
        internal long PublishedBytes { get; }
        internal VerifiedReleaseInstallationPlan Plan { get; }
        internal VerifiedReleasePublicationReport Publication { get; }

        internal VerifiedReleaseActivationPlanCompositionResult Compose() =>
            new VerifiedReleaseActivationPlanComposer().Compose(Publication);

        private static VerifiedReleaseInstallationPackagePlan[] CreatePackages(
            string targetPath,
            string mismatch)
        {
            PackageInput[] inputs =
            [
                new("gateway", ReleasePackageRole.GatewayWeb, "packages/gateway.tar", 11, 'A'),
                new("broker", ReleasePackageRole.Broker, "packages/broker.tar", 12, 'B'),
                new("agent", ReleasePackageRole.AetherRemoteAgent, "packages/agent.tar", 13, 'C'),
                new("engine", ReleasePackageRole.StationEngine, "packages/engine.tar", 14, 'D')
            ];

            if (mismatch == "duplicate-role")
            {
                inputs[1] = inputs[1] with { Role = ReleasePackageRole.GatewayWeb };
            }
            if (mismatch == "duplicate-identity")
            {
                inputs[1] = inputs[1] with { Identity = "gateway" };
            }
            if (mismatch == "unsafe-relative-path")
            {
                inputs[1] = inputs[1] with { RelativePath = "../broker.tar" };
            }
            if (mismatch == "zero-length")
            {
                inputs[1] = inputs[1] with { Length = 0 };
            }

            IEnumerable<PackageInput> selected =
                mismatch == "missing-package"
                    ? inputs.Take(3)
                    : inputs;

            return selected.Select((input, index) =>
            {
                SignedReleasePackage package = new()
                {
                    PackageIdentity = input.Identity,
                    Role = input.Role,
                    FileName = input.RelativePath,
                    Length = input.Length,
                    Sha256 = new string(input.DigestCharacter, 64)
                };
                string packageTarget = Path.GetFullPath(
                    Path.Combine(
                        targetPath,
                        input.RelativePath.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
                if (mismatch == "target-mismatch" && index == 1)
                {
                    packageTarget = Path.Combine(targetPath, "wrong.tar");
                }
                return new VerifiedReleaseInstallationPackagePlan(
                    new VerifiedReleasePackageSnapshot(package),
                    packageTarget);
            }).ToArray();
        }

        private sealed record PackageInput(
            string Identity,
            ReleasePackageRole Role,
            string RelativePath,
            long Length,
            char DigestCharacter);
    }
}
