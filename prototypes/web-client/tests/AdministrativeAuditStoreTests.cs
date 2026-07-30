using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherSDR.Web.Tests;

public sealed class AdministrativeAuditStoreTests
{
    [Fact]
    public void AdministrativeActionSurvivesAStoreRestart()
    {
        using TestDirectory directory = new();
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 28, 14, 30, 0, TimeSpan.Zero));
        AdministrativeAuditStore first = CreateStore(directory, time);

        AdministrativeAuditEvent recorded = first.Record(
            "administrator-id",
            "Station Administrator",
            AdministrativeAuditActions.UpdateRadioPolicy,
            "flex:1234",
            "operator-id",
            AdministrativeAuditResults.Succeeded,
            "Access changed to exclusive; reservation set.");

        AdministrativeAuditStore reloaded = CreateStore(directory, time);
        AdministrativeAuditEvent auditEvent =
            Assert.Single(reloaded.GetRecent());

        Assert.Equal(recorded.EventId, auditEvent.EventId);
        Assert.Equal(time.GetUtcNow(), auditEvent.OccurredAt);
        Assert.Equal("administrator-id", auditEvent.ActorId);
        Assert.Equal("Station Administrator", auditEvent.ActorDisplayName);
        Assert.Equal("flex:1234", auditEvent.RadioId);
        Assert.Equal("operator-id", auditEvent.TargetId);
        Assert.Equal(AdministrativeAuditResults.Succeeded, auditEvent.Result);
    }

    [Fact]
    public void RecentHistoryIsNewestFirstAndCapped()
    {
        using TestDirectory directory = new();
        ManualTimeProvider time = new(DateTimeOffset.UnixEpoch);
        AdministrativeAuditStore store = CreateStore(
            directory,
            time,
            maximumEntries: 3);

        for (int index = 1; index <= 4; index++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            store.Record(
                "administrator",
                "Administrator",
                AdministrativeAuditActions.ForceDisconnectOperator,
                $"radio-{index}",
                $"operator-{index}",
                AdministrativeAuditResults.Succeeded,
                $"Released operator {index}.");
        }

        AdministrativeAuditEvent[] recent = store.GetRecent(2).ToArray();
        AdministrativeAuditStore reloaded = CreateStore(
            directory,
            time,
            maximumEntries: 3);

        Assert.Equal(
            ["radio-4", "radio-3"],
            recent.Select(auditEvent => auditEvent.RadioId));
        Assert.Equal(
            ["radio-4", "radio-3", "radio-2"],
            reloaded.GetRecent().Select(auditEvent => auditEvent.RadioId));
    }

    [Fact]
    public void FailedActionsAreDurableAndUnsafeTextIsSanitized()
    {
        using TestDirectory directory = new();
        ManualTimeProvider time = new(DateTimeOffset.UnixEpoch);
        AdministrativeAuditStore store = CreateStore(directory, time);

        store.Record(
            "administrator\r\nspoofed",
            "Administrator",
            AdministrativeAuditActions.UpdateRadioPolicy,
            "unknown-radio",
            null,
            AdministrativeAuditResults.Failed,
            "Radio was not found.\r\nInjected line");

        AdministrativeAuditEvent auditEvent =
            Assert.Single(CreateStore(directory, time).GetRecent());
        Assert.DoesNotContain('\r', auditEvent.ActorId);
        Assert.DoesNotContain('\n', auditEvent.ActorId);
        Assert.DoesNotContain('\r', auditEvent.Summary);
        Assert.DoesNotContain('\n', auditEvent.Summary);
        Assert.Equal(AdministrativeAuditResults.Failed, auditEvent.Result);
    }

    [Fact]
    public void InvalidResultDoesNotCreateAnAuditFile()
    {
        using TestDirectory directory = new();
        AdministrativeAuditStore store = CreateStore(
            directory,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.Throws<ArgumentException>(() => store.Record(
            "administrator",
            "Administrator",
            AdministrativeAuditActions.UpdateRadioPolicy,
            "flex:1234",
            null,
            "pending",
            "Not final."));
        Assert.False(File.Exists(directory.AuditPath));
    }

    [Fact]
    public void PersistenceFailureRollsBackTheInMemoryEvent()
    {
        using TestDirectory directory = new();
        Directory.CreateDirectory(directory.RootPath);
        string blockedDirectory = Path.Combine(
            directory.RootPath,
            "not-a-directory");
        File.WriteAllText(blockedDirectory, "occupied");
        AdministrativeAuditStore store = new(
            Path.Combine(blockedDirectory, "audit.json"),
            NullLogger<AdministrativeAuditStore>.Instance,
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.ThrowsAny<IOException>(() => store.Record(
            "administrator",
            "Administrator",
            AdministrativeAuditActions.UpdateRadioPolicy,
            "flex:1234",
            null,
            AdministrativeAuditResults.Succeeded,
            "Access changed to shared."));
        Assert.Empty(store.GetRecent());
    }

    private static AdministrativeAuditStore CreateStore(
        TestDirectory directory,
        TimeProvider timeProvider,
        int maximumEntries = 2_000) =>
        new(
            directory.AuditPath,
            NullLogger<AdministrativeAuditStore>.Instance,
            timeProvider,
            maximumEntries);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow)
        : TimeProvider
    {
        private DateTimeOffset m_utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => m_utcNow;

        public void Advance(TimeSpan duration)
        {
            m_utcNow = m_utcNow.Add(duration);
        }
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "aethersdr-web-tests",
                Guid.NewGuid().ToString("N"));
            AuditPath = Path.Combine(RootPath, "audit.json");
        }

        public string RootPath { get; }
        public string AuditPath { get; }

        public void Dispose()
        {
            string resolvedRoot = Path.GetFullPath(RootPath);
            string resolvedTestRoot = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "aethersdr-web-tests"));
            if (resolvedRoot.StartsWith(
                    resolvedTestRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }
}
