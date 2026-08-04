using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AetherSDR.Web.Releases;
using AetherSDR.Web.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Tests;

public sealed class VerifiedReleaseActivationMigrationRunnerTrustTests
{
    [Fact]
    public void PublicSurfacesExposeDiagnosticsOnly()
    {
        string[] registryMethods =
            typeof(ReleaseMigrationRunnerTrustRegistry)
                .GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        string[] selectorMethods =
            typeof(VerifiedReleaseActivationMigrationRunnerSelector)
                .GetMethods(
                    BindingFlags.Public |
                    BindingFlags.Instance |
                    BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        Assert.Equal(["get_Snapshot"], registryMethods);
        Assert.Equal(["get_Snapshot"], selectorMethods);
    }

    [Fact]
    public void ConfigurationRejectsUnknownProperties()
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            [$"{ReleaseMigrationRunnerTrustSettings.SectionName}:" +
                "SelectionEnabled"] = "false",
            [$"{ReleaseMigrationRunnerTrustSettings.SectionName}:Unexpected"] =
                "value"
        };
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            configuration
                .GetSection(ReleaseMigrationRunnerTrustSettings.SectionName)
                .Get<ReleaseMigrationRunnerTrustSettings>(options =>
                    options.ErrorOnUnknownConfiguration = true));
    }

    [Fact]
    public void DisabledEmptyTrustStartsFailClosed()
    {
        ReleaseMigrationRunnerTrustRegistry registry = CreateRegistry(
            new ReleaseMigrationRunnerTrustSettings());
        ReleaseMigrationRunnerTrustDiagnostics snapshot = registry.Snapshot;

        Assert.True(snapshot.Registered);
        Assert.False(snapshot.SelectionEnabled);
        Assert.False(snapshot.SelectionAvailable);
        Assert.Equal(0, snapshot.TrustedRunnerCount);
        Assert.Equal(0, snapshot.TrustedMigrationCount);
        Assert.True(snapshot.FeatureOwnedConfigurationRegistered);
        Assert.True(snapshot.BoundedRunnerListRegistered);
        Assert.True(snapshot.BoundedMigrationListRegistered);
        Assert.True(snapshot.CanonicalRunnerPathValidationRegistered);
        Assert.True(snapshot.SymbolicLinkRejectionRegistered);
        Assert.True(snapshot.RunnerSizeValidationRegistered);
        Assert.True(snapshot.RunnerPermissionValidationRegistered);
        Assert.True(snapshot.RunnerDigestPinningRegistered);
        Assert.True(snapshot.ExactMigrationMappingRegistered);
        Assert.True(snapshot.RunnerArtifactReadRegistered);
        AssertNoExecutionOrCallers(snapshot);
    }

    [Fact]
    public void EnabledTrustRequiresAtLeastOneRunner()
    {
        Assert.Throws<InvalidOperationException>(() => CreateRegistry(
            new ReleaseMigrationRunnerTrustSettings
            {
                SelectionEnabled = true,
                Runners = []
            }));
    }

    [Fact]
    public void ValidPrivatePinnedRunnerLoadsExactMappings()
    {
        using Fixture fixture = new();
        ReleaseMigrationRunnerTrustRegistry registry =
            CreateRegistry(fixture.TrustSettings());

        ReleaseMigrationRunnerTrustDiagnostics snapshot = registry.Snapshot;
        Assert.True(snapshot.SelectionEnabled);
        Assert.True(snapshot.SelectionAvailable);
        Assert.Equal(1, snapshot.TrustedRunnerCount);
        Assert.Equal(1, snapshot.TrustedMigrationCount);
        AssertNoExecutionOrCallers(snapshot);

        Assert.True(registry.TrySelect(
            "schema-1-to-2",
            1,
            2,
            out ReleaseMigrationTrustedRunner? runner,
            out ReleaseMigrationRunnerMapping? mapping));
        Assert.NotNull(runner);
        Assert.NotNull(mapping);
        Assert.Equal(
            ReleaseMigrationRunnerTrustRegistry.CurrentRunnerProtocolVersion,
            runner.RunnerProtocolVersion);
        Assert.Equal(fixture.RunnerPath, runner.RunnerPath);
        Assert.Equal(fixture.RunnerBytes.Length, runner.RunnerLength);
        Assert.Equal(32, runner.Sha256.Count);
        Assert.Equal("schema-1-to-2", mapping.MigrationIdentity);
        Assert.False(registry.TrySelect(
            "schema-2-to-3",
            2,
            3,
            out _,
            out _));
    }

    [Fact]
    public void DigestMismatchFailsStartupClosed()
    {
        using Fixture fixture = new();
        ReleaseMigrationRunnerTrustSettings settings = fixture.TrustSettings();
        settings.Runners[0].Sha256 = new string('0', 64);

        Assert.Throws<InvalidOperationException>(() => CreateRegistry(settings));
    }

    [Fact]
    public void WritableOrLinkedRunnerFailsStartupClosed()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using Fixture writableFixture = new();
        File.SetUnixFileMode(
            writableFixture.RunnerPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);
        Assert.Throws<InvalidOperationException>(() =>
            CreateRegistry(writableFixture.TrustSettings()));

        using Fixture linkedFixture = new();
        string linkPath = Path.Combine(linkedFixture.RunnerDirectory, "linked-runner");
        File.CreateSymbolicLink(linkPath, linkedFixture.RunnerPath);
        ReleaseMigrationRunnerTrustSettings linked = linkedFixture.TrustSettings();
        linked.Runners[0].RunnerPath = linkPath;
        Assert.Throws<InvalidOperationException>(() => CreateRegistry(linked));
    }

    [Fact]
    public void DuplicateSignedMigrationIdentityFailsStartupClosed()
    {
        using Fixture fixture = new();
        string secondPath = fixture.CreateRunner(
            "runner-two",
            "second reviewed runner artifact"u8.ToArray());
        ReleaseMigrationRunnerTrustSettings settings = fixture.TrustSettings();
        settings.Runners =
        [
            settings.Runners[0],
            new ReleaseMigrationRunnerTrustEntrySettings
            {
                RunnerIdentity = "runner-two",
                RunnerProtocolVersion =
                    ReleaseMigrationRunnerTrustRegistry.CurrentRunnerProtocolVersion,
                RunnerPath = secondPath,
                Sha256 = fixture.Sha256(secondPath),
                Migrations =
                [
                    new ReleaseMigrationRunnerTrustMappingSettings
                    {
                        MigrationIdentity = "schema-1-to-2",
                        FromConfigurationSchemaVersion = 1,
                        ToConfigurationSchemaVersion = 2
                    }
                ]
            }
        ];

        Assert.Throws<InvalidOperationException>(() => CreateRegistry(settings));
    }

    [Fact]
    public void RequiredMigrationSelectsOneExactRunnerWithoutExecution()
    {
        using Fixture fixture = new();
        ReleaseMigrationRunnerTrustRegistry registry =
            CreateRegistry(fixture.TrustSettings());
        VerifiedReleaseActivationMigrationRunnerSelector selector = new(registry);

        VerifiedReleaseActivationMigrationRunnerSelectionReport report =
            selector.Select(fixture.MigrationPlanReport);

        Assert.True(report.Succeeded);
        Assert.Equal(
            VerifiedReleaseActivationMigrationRunnerSelectionFailureCode.None,
            report.FailureCode);
        Assert.Equal(ReleaseMigrationKind.Required, report.MigrationKind);
        Assert.Equal(1, report.FromConfigurationSchemaVersion);
        Assert.Equal(2, report.ToConfigurationSchemaVersion);
        Assert.True(report.MigrationRequired);
        Assert.False(report.NoOpMigrationResolved);
        Assert.True(report.ExactMigrationPlanBound);
        Assert.True(report.RunnerTrustEnabled);
        Assert.True(report.MigrationRunnerRequired);
        Assert.True(report.MigrationRunnerSelected);
        Assert.True(report.RunnerArtifactValidatedAtStartup);
        Assert.Equal(
            ReleaseMigrationRunnerTrustRegistry.CurrentRunnerProtocolVersion,
            report.RunnerProtocolVersion);
        Assert.False(report.MigrationSourceReadPerformed);
        Assert.False(report.RunnerInvoked);
        Assert.False(report.MigrationExecutionPerformed);
        Assert.False(report.MigrationReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);

        VerifiedReleaseActivationMigrationRunnerSelection selection =
            Assert.IsType<VerifiedReleaseActivationMigrationRunnerSelection>(
                report.Selection);
        Assert.Same(fixture.MigrationPlanReport.Plan, selection.Plan);
        Assert.NotNull(selection.Runner);
        Assert.NotNull(selection.Mapping);
    }

    [Fact]
    public void NoMigrationResolvesWithoutRunnerTrust()
    {
        using Fixture fixture = new(
            ReleaseMigrationKind.None,
            migrationFrom: null,
            migrationTo: null,
            migrationIdentity: string.Empty,
            targetSchema: 1);
        ReleaseMigrationRunnerTrustRegistry registry = CreateRegistry(
            new ReleaseMigrationRunnerTrustSettings());
        VerifiedReleaseActivationMigrationRunnerSelector selector = new(registry);

        VerifiedReleaseActivationMigrationRunnerSelectionReport report =
            selector.Select(fixture.MigrationPlanReport);

        Assert.True(report.Succeeded);
        Assert.Equal(ReleaseMigrationKind.None, report.MigrationKind);
        Assert.False(report.MigrationRequired);
        Assert.True(report.NoOpMigrationResolved);
        Assert.True(report.ExactMigrationPlanBound);
        Assert.False(report.RunnerTrustEnabled);
        Assert.False(report.MigrationRunnerRequired);
        Assert.False(report.MigrationRunnerSelected);
        Assert.False(report.RunnerArtifactValidatedAtStartup);
        Assert.Null(report.RunnerProtocolVersion);
        Assert.True(report.MigrationReady);
        Assert.False(report.RunnerInvoked);
        Assert.False(report.MigrationExecutionPerformed);
    }

    [Fact]
    public void DisabledOrUnmatchedTrustFailsRequiredMigrationClosed()
    {
        using Fixture fixture = new();
        VerifiedReleaseActivationMigrationRunnerSelector disabled = new(
            CreateRegistry(new ReleaseMigrationRunnerTrustSettings()));
        VerifiedReleaseActivationMigrationRunnerSelectionReport disabledReport =
            disabled.Select(fixture.MigrationPlanReport);
        AssertFailure(
            disabledReport,
            VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                .RunnerTrustDisabled);

        ReleaseMigrationRunnerTrustSettings unmatchedSettings =
            fixture.TrustSettings();
        unmatchedSettings.Runners[0].Migrations[0].MigrationIdentity =
            "schema-2-to-3";
        unmatchedSettings.Runners[0].Migrations[0]
            .FromConfigurationSchemaVersion = 2;
        unmatchedSettings.Runners[0].Migrations[0]
            .ToConfigurationSchemaVersion = 3;
        VerifiedReleaseActivationMigrationRunnerSelector unmatched = new(
            CreateRegistry(unmatchedSettings));
        VerifiedReleaseActivationMigrationRunnerSelectionReport unmatchedReport =
            unmatched.Select(fixture.MigrationPlanReport);
        AssertFailure(
            unmatchedReport,
            VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                .TrustedRunnerNotFound);
    }

    [Fact]
    public void MissingOrTamperedMigrationPlanFailsClosed()
    {
        using Fixture fixture = new();
        VerifiedReleaseActivationMigrationRunnerSelector selector = new(
            CreateRegistry(fixture.TrustSettings()));

        VerifiedReleaseActivationMigrationRunnerSelectionReport missing =
            selector.Select(fixture.MigrationPlanReport with { Plan = null });
        VerifiedReleaseActivationMigrationRunnerSelectionReport tampered =
            selector.Select(
                fixture.MigrationPlanReport with
                {
                    ToConfigurationSchemaVersion = 3
                });
        VerifiedReleaseActivationMigrationRunnerSelectionReport failed =
            selector.Select(
                fixture.MigrationPlanReport with
                {
                    Succeeded = false,
                    FailureCode =
                        VerifiedReleaseActivationMigrationPlanFailureCode
                            .MigrationLayoutUnsafe
                });

        AssertFailure(
            missing,
            VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                .MigrationPlanUnavailable);
        AssertFailure(
            tampered,
            VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                .MigrationPlanMismatch);
        AssertFailure(
            failed,
            VerifiedReleaseActivationMigrationRunnerSelectionFailureCode
                .MigrationPlanNotEligible);
    }

    [Fact]
    public void IndependentlyComposedPlansRetainDistinctSelectionBindings()
    {
        using Fixture fixture = new();
        ReleaseMigrationRunnerTrustRegistry registry =
            CreateRegistry(fixture.TrustSettings());
        VerifiedReleaseActivationMigrationRunnerSelector selector = new(registry);
        VerifiedReleaseActivationMigrationPlanReport secondPlan =
            fixture.CreateMigrationPlanReport();

        VerifiedReleaseActivationMigrationRunnerSelectionReport first =
            selector.Select(fixture.MigrationPlanReport);
        VerifiedReleaseActivationMigrationRunnerSelectionReport second =
            selector.Select(secondPlan);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.NotSame(first.Selection, second.Selection);
        Assert.Same(fixture.MigrationPlanReport.Plan, first.Selection!.Plan);
        Assert.Same(secondPlan.Plan, second.Selection!.Plan);
        Assert.NotSame(first.Selection.Plan, second.Selection.Plan);
        Assert.Same(first.Selection.Runner, second.Selection.Runner);
    }

    [Fact]
    public void PublicDiagnosticsAndReportsRedactRunnerTrustMaterial()
    {
        using Fixture fixture = new();
        ReleaseMigrationRunnerTrustRegistry registry =
            CreateRegistry(fixture.TrustSettings());
        VerifiedReleaseActivationMigrationRunnerSelectionReport report =
            new VerifiedReleaseActivationMigrationRunnerSelector(registry)
                .Select(fixture.MigrationPlanReport);
        Assert.True(report.Succeeded);

        string diagnosticsJson = JsonSerializer.Serialize(registry.Snapshot);
        string reportJson = JsonSerializer.Serialize(report);

        foreach (string json in new[] { diagnosticsJson, reportJson })
        {
            Assert.DoesNotContain(fixture.Root, json, StringComparison.Ordinal);
            Assert.DoesNotContain(fixture.RunnerPath, json, StringComparison.Ordinal);
            Assert.DoesNotContain("runner-one", json, StringComparison.Ordinal);
            Assert.DoesNotContain("schema-1-to-2", json, StringComparison.Ordinal);
            Assert.DoesNotContain(
                fixture.RunnerSha256,
                json,
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("trustedRunnerCount", diagnosticsJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("migrationRunnerSelected", reportJson,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectorDiagnosticsSeparateSelectionFromExecutionAndCallers()
    {
        ReleaseMigrationRunnerTrustRegistry registry = CreateRegistry(
            new ReleaseMigrationRunnerTrustSettings());
        VerifiedReleaseActivationMigrationRunnerSelectionDiagnostics snapshot =
            new VerifiedReleaseActivationMigrationRunnerSelector(registry).Snapshot;

        Assert.True(snapshot.Registered);
        Assert.True(snapshot.MigrationPlanInputRegistered);
        Assert.True(snapshot.RunnerTrustInputRegistered);
        Assert.True(snapshot.ExactMigrationPlanBindingRegistered);
        Assert.True(snapshot.NoOpMigrationResolutionRegistered);
        Assert.True(snapshot.RequiredRunnerSelectionRegistered);
        Assert.True(snapshot.ExactMigrationIdentityBindingRegistered);
        Assert.True(snapshot.SchemaTransitionBindingRegistered);
        Assert.True(snapshot.RunnerProtocolBindingRegistered);
        Assert.True(snapshot.RunnerArtifactDigestBindingRegistered);
        Assert.False(snapshot.RunnerInvocationRegistered);
        Assert.False(snapshot.MigrationSourceReadRegistered);
        Assert.False(snapshot.FileWriteRegistered);
        Assert.False(snapshot.DirectoryMutationRegistered);
        Assert.False(snapshot.MigrationExecutionRegistered);
        Assert.False(snapshot.MigrationEvidenceRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
        Assert.False(snapshot.ActivationAuthorityRegistered);
        Assert.False(snapshot.OperationalCallerRegistered);
        Assert.False(snapshot.CliCallerRegistered);
        Assert.False(snapshot.AdminCallerRegistered);
        Assert.False(snapshot.BrowserCallerRegistered);
        Assert.False(snapshot.HttpCallerRegistered);
        Assert.False(snapshot.WebSocketCallerRegistered);
        Assert.False(snapshot.HostedServiceCallerRegistered);
        Assert.False(snapshot.TimerCallerRegistered);
        Assert.False(snapshot.AetherRemoteCallerRegistered);
        Assert.False(snapshot.ServiceControlCallerRegistered);
        Assert.False(snapshot.HealthProbeCallerRegistered);
        Assert.False(snapshot.RollbackCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    private static ReleaseMigrationRunnerTrustRegistry CreateRegistry(
        ReleaseMigrationRunnerTrustSettings settings) =>
        new(
            Options.Create(settings),
            NullLogger<ReleaseMigrationRunnerTrustRegistry>.Instance);

    private static void AssertNoExecutionOrCallers(
        ReleaseMigrationRunnerTrustDiagnostics snapshot)
    {
        Assert.False(snapshot.RunnerInvocationRegistered);
        Assert.False(snapshot.MigrationExecutionRegistered);
        Assert.False(snapshot.MigrationEvidenceRegistered);
        Assert.False(snapshot.CurrentPointerMutationRegistered);
        Assert.False(snapshot.ActivationAuthorityRegistered);
        Assert.False(snapshot.OperationalCallerRegistered);
        Assert.False(snapshot.CliCallerRegistered);
        Assert.False(snapshot.AdminCallerRegistered);
        Assert.False(snapshot.BrowserCallerRegistered);
        Assert.False(snapshot.HttpCallerRegistered);
        Assert.False(snapshot.WebSocketCallerRegistered);
        Assert.False(snapshot.HostedServiceCallerRegistered);
        Assert.False(snapshot.TimerCallerRegistered);
        Assert.False(snapshot.AetherRemoteCallerRegistered);
        Assert.False(snapshot.ServiceControlCallerRegistered);
        Assert.False(snapshot.HealthProbeCallerRegistered);
        Assert.False(snapshot.RollbackCallerRegistered);
        Assert.False(snapshot.RadioCallerRegistered);
        Assert.False(snapshot.WatchdogCallerRegistered);
        Assert.False(snapshot.CommandCallerRegistered);
        Assert.False(snapshot.LeaseCallerRegistered);
        Assert.False(snapshot.TxCallerRegistered);
    }

    private static void AssertFailure(
        VerifiedReleaseActivationMigrationRunnerSelectionReport report,
        VerifiedReleaseActivationMigrationRunnerSelectionFailureCode failureCode)
    {
        Assert.False(report.Succeeded);
        Assert.Equal(failureCode, report.FailureCode);
        Assert.Null(report.Selection);
        Assert.False(report.ExactMigrationPlanBound);
        Assert.False(report.MigrationRunnerSelected);
        Assert.False(report.RunnerArtifactValidatedAtStartup);
        Assert.False(report.MigrationSourceReadPerformed);
        Assert.False(report.RunnerInvoked);
        Assert.False(report.MigrationExecutionPerformed);
        Assert.False(report.MigrationReady);
        Assert.False(report.CurrentPointerChanged);
        Assert.False(report.ActivationAuthorized);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ReleaseMigrationKind m_migrationKind;
        private readonly int? m_migrationFrom;
        private readonly int? m_migrationTo;
        private readonly string m_migrationIdentity;
        private readonly int m_targetSchema;

        internal Fixture(
            ReleaseMigrationKind migrationKind = ReleaseMigrationKind.Required,
            int? migrationFrom = 1,
            int? migrationTo = 2,
            string migrationIdentity = "schema-1-to-2",
            int targetSchema = 2)
        {
            m_migrationKind = migrationKind;
            m_migrationFrom = migrationFrom;
            m_migrationTo = migrationTo;
            m_migrationIdentity = migrationIdentity;
            m_targetSchema = targetSchema;
            Root = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                $"migration-runner-trust-{Guid.NewGuid():N}"));
            RunnerDirectory = Path.Combine(Root, "runner-trust");
            Directory.CreateDirectory(RunnerDirectory);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    RunnerDirectory,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
            RunnerBytes = "reviewed migration runner protocol one"u8.ToArray();
            RunnerPath = CreateRunner("runner-one", RunnerBytes);
            RunnerSha256 = Sha256(RunnerPath);

            DeploymentRoot = Path.Combine(Root, "deployment");
            Paths = new InstallationPaths(
                Path.Combine(Root, "configuration"),
                Path.Combine(Root, "state"),
                Path.Combine(Root, "secrets"),
                Path.Combine(DeploymentRoot, "releases"),
                Path.Combine(Root, "backups"),
                Path.Combine(Root, "logs"));
            MigrationPlanReport = CreateMigrationPlanReport();
        }

        internal string Root { get; }
        internal string RunnerDirectory { get; }
        internal string RunnerPath { get; }
        internal byte[] RunnerBytes { get; }
        internal string RunnerSha256 { get; }
        internal string DeploymentRoot { get; }
        internal InstallationPaths Paths { get; }
        internal VerifiedReleaseActivationMigrationPlanReport MigrationPlanReport
        {
            get;
        }

        internal ReleaseMigrationRunnerTrustSettings TrustSettings() =>
            new()
            {
                SelectionEnabled = true,
                Runners =
                [
                    new ReleaseMigrationRunnerTrustEntrySettings
                    {
                        RunnerIdentity = "runner-one",
                        RunnerProtocolVersion =
                            ReleaseMigrationRunnerTrustRegistry
                                .CurrentRunnerProtocolVersion,
                        RunnerPath = RunnerPath,
                        Sha256 = RunnerSha256,
                        Migrations =
                        [
                            new ReleaseMigrationRunnerTrustMappingSettings
                            {
                                MigrationIdentity = "schema-1-to-2",
                                FromConfigurationSchemaVersion = 1,
                                ToConfigurationSchemaVersion = 2
                            }
                        ]
                    }
                ]
            };

        internal string CreateRunner(string name, byte[] bytes)
        {
            string path = Path.Combine(RunnerDirectory, name);
            File.WriteAllBytes(path, bytes);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute);
            }
            return path;
        }

        internal string Sha256(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                .ToLowerInvariant();

        internal VerifiedReleaseActivationMigrationPlanReport
            CreateMigrationPlanReport()
        {
            VerifiedReleaseActivationPlanCompositionResult activationPlan =
                CreateActivationPlanResult();
            VerifiedReleaseActivationConfigurationBackupPlanReport backupPlan =
                new VerifiedReleaseActivationConfigurationBackupPlanner(Paths)
                    .Compose(activationPlan);
            Assert.True(backupPlan.Succeeded);
            VerifiedReleaseActivationConfigurationBackup backup = new(
                backupPlan.Plan!,
                directoryCount: 6,
                fileCount: 3,
                backupBytes: 51,
                manifestSha256: Enumerable.Repeat((byte)0x3A, 32).ToArray(),
                completedAt:
                    new DateTimeOffset(2026, 8, 4, 10, 30, 0, TimeSpan.Zero));
            VerifiedReleaseActivationConfigurationBackupReport backupReport =
                VerifiedReleaseActivationConfigurationBackupReport.Success(backup);
            VerifiedReleaseActivationMigrationPlanReport migrationPlan =
                new VerifiedReleaseActivationMigrationPlanComposer().Compose(
                    activationPlan,
                    backupReport);
            Assert.True(migrationPlan.Succeeded);
            return migrationPlan;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private VerifiedReleaseActivationPlanCompositionResult
            CreateActivationPlanResult()
        {
            string releaseRoot = Paths.ReleaseDirectory;
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
                bundleDirectory: Path.Combine(Root, "bundle"),
                manifestLength: 37,
                manifestSha256: Enumerable.Repeat((byte)0x7A, 32).ToArray(),
                releaseRoot,
                DeploymentRoot,
                targetPath,
                packages,
                m_targetSchema,
                m_migrationKind,
                m_migrationFrom,
                m_migrationTo,
                m_migrationIdentity,
                restartGatewayWeb:
                    m_migrationKind == ReleaseMigrationKind.Required,
                restartBroker: false,
                restartAetherRemoteAgent: false,
                restartStationEngine: false,
                restartHost: false,
                txSupportCapable: false,
                releaseNotesTitle: "AetherSDR 8.2.0",
                releaseNotesSummary: "Runner trust selection test release.");
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
                    Path.GetFullPath(Path.Combine(
                        targetPath,
                        input.Relative.Replace(
                            '/',
                            Path.DirectorySeparatorChar))));
            }).ToArray();
        }
    }
}
