using System.Text.Json;
using AetherSDR.Web.Radio;

namespace AetherSDR.Web.Tests;

public sealed class TxLeaseManagerTests
{
    [Fact]
    public void OnePhysicalRadioHasOneHolderAcrossBrowserSessions()
    {
        TxLeaseManager manager = new();

        Assert.True(manager.TryAcquire(
            "remote:odu-campus:flex:6400",
            "session-a",
            "client-a",
            "user-a",
            "Operator A",
            TimeSpan.FromSeconds(10),
            out TxLease? first,
            out string? firstError));
        Assert.Null(firstError);
        Assert.NotNull(first);
        Assert.Equal("REMOTE:ODU-CAMPUS:FLEX:6400", first.RadioId);
        Assert.Equal(32, first.LeaseId.Length);

        Assert.False(manager.TryAcquire(
            "REMOTE:ODU-CAMPUS:FLEX:6400",
            "session-b",
            "client-b",
            "user-b",
            "Operator B",
            TimeSpan.FromSeconds(10),
            out TxLease? conflict,
            out string? conflictError));
        Assert.Equal(first.LeaseId, conflict?.LeaseId);
        Assert.Contains("Operator A", conflictError, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentPhysicalRadiosHaveIndependentLeases()
    {
        TxLeaseManager manager = new();

        Assert.True(Acquire(manager, "radio-a", "session-a", "client-a", out _));
        Assert.True(Acquire(manager, "radio-b", "session-b", "client-b", out _));

        Assert.Equal(2, manager.GetSnapshot().Count);
    }

    [Fact]
    public void RadioPolicyRevocationReleasesOnlyTheExactRadio()
    {
        TxLeaseManager manager = new();
        List<TxLeaseChange> changes = [];
        manager.Changed += changes.Add;
        Assert.True(Acquire(
            manager,
            "radio-a",
            "session-a",
            "client-a",
            out _));
        Assert.True(Acquire(
            manager,
            "radio-b",
            "session-b",
            "client-b",
            out TxLease? other));

        Assert.Equal(
            1,
            manager.ReleaseRadio(
                "radio-a",
                "radio-transmit-policy-disabled"));

        Assert.Null(manager.GetCurrent("radio-a"));
        Assert.Equal(other?.LeaseId, manager.GetCurrent("radio-b")?.LeaseId);
        TxLeaseChange released = Assert.Single(
            changes,
            change =>
                !change.Active &&
                string.Equals(
                    change.Lease.RadioId,
                    "RADIO-A",
                    StringComparison.Ordinal));
        Assert.Equal(
            "radio-transmit-policy-disabled",
            released.Reason);
    }

    [Fact]
    public void RenewalRequiresOpaqueLeaseIdAndExactOwner()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 29, 23, 0, 0, TimeSpan.Zero));
        TxLeaseManager manager = new(time);
        Assert.True(Acquire(
            manager,
            "radio-a",
            "session-a",
            "client-a",
            out TxLease? original));
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.False(manager.TryRenew(
            "radio-a",
            "wrong-lease-id",
            "session-a",
            "client-a",
            TimeSpan.FromSeconds(10),
            out _,
            out _));
        Assert.False(manager.TryRenew(
            "radio-a",
            original!.LeaseId,
            "session-b",
            "client-b",
            TimeSpan.FromSeconds(10),
            out _,
            out _));
        Assert.True(manager.TryRenew(
            "radio-a",
            original.LeaseId,
            "session-a",
            "client-a",
            TimeSpan.FromSeconds(10),
            out TxLease? renewed,
            out string? error));

        Assert.Null(error);
        Assert.Equal(original.LeaseId, renewed?.LeaseId);
        Assert.Equal(time.GetUtcNow(), renewed?.RenewedAt);
        Assert.Equal(time.GetUtcNow().AddSeconds(10), renewed?.ExpiresAt);
    }

    [Fact]
    public void ReleaseRequiresLeaseIdAndExactOwner()
    {
        TxLeaseManager manager = new();
        Assert.True(Acquire(
            manager,
            "radio-a",
            "session-a",
            "client-a",
            out TxLease? lease));

        Assert.False(manager.TryRelease(
            "radio-a",
            "wrong",
            "session-a",
            "client-a",
            "operator-request",
            out _));
        Assert.False(manager.TryRelease(
            "radio-a",
            lease!.LeaseId,
            "session-b",
            "client-b",
            "operator-request",
            out _));
        Assert.True(manager.TryRelease(
            "radio-a",
            lease.LeaseId,
            "session-a",
            "client-a",
            "operator-request",
            out TxLease? released));

        Assert.Equal(lease.LeaseId, released?.LeaseId);
        Assert.Null(manager.GetCurrent("radio-a"));
    }

    [Fact]
    public void DisconnectAndSessionDisposalReleaseAuthority()
    {
        TxLeaseManager manager = new();
        Assert.True(Acquire(
            manager,
            "radio-a",
            "session-a",
            "client-a",
            out _));

        Assert.False(manager.TryReleaseOwner(
            "radio-a",
            "session-a",
            "client-b",
            "client-disconnected",
            out _));
        Assert.True(manager.TryReleaseOwner(
            "radio-a",
            "session-a",
            "client-a",
            "client-disconnected",
            out _));

        Assert.True(Acquire(
            manager,
            "radio-a",
            "session-a",
            "client-c",
            out _));
        Assert.Equal(1, manager.ReleaseSession(
            "session-a",
            "session-disposed"));
        Assert.Empty(manager.GetSnapshot());
    }

    [Fact]
    public void ExpiryPublishesForceUnkeyReasonAndAllowsNextHolder()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 29, 23, 0, 0, TimeSpan.Zero));
        TxLeaseManager manager = new(time);
        List<TxLeaseChange> changes = [];
        manager.Changed += changes.Add;
        Assert.True(Acquire(
            manager,
            "radio-a",
            "session-a",
            "client-a",
            out _));

        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(1, manager.SweepExpired());

        TxLeaseChange expired = Assert.Single(
            changes,
            change => !change.Active);
        Assert.Equal("expired", expired.Reason);
        Assert.Null(manager.GetCurrent("radio-a"));
        Assert.True(Acquire(
            manager,
            "radio-a",
            "session-b",
            "client-b",
            out _));
    }

    [Fact]
    public void FailedRenewAfterExpiryStillPublishesExpiry()
    {
        ManualTimeProvider time = new(
            new DateTimeOffset(2026, 7, 29, 23, 0, 0, TimeSpan.Zero));
        TxLeaseManager manager = new(time);
        List<TxLeaseChange> changes = [];
        manager.Changed += changes.Add;
        Assert.True(Acquire(
            manager,
            "radio-a",
            "session-a",
            "client-a",
            out TxLease? lease));
        time.Advance(TimeSpan.FromSeconds(11));

        Assert.False(manager.TryRenew(
            "radio-a",
            lease!.LeaseId,
            "session-a",
            "client-a",
            TimeSpan.FromSeconds(10),
            out _,
            out _));

        TxLeaseChange expired = Assert.Single(
            changes,
            change => !change.Active);
        Assert.Equal("expired", expired.Reason);
    }

    [Fact]
    public void PublicLeaseStatusDoesNotExposeHolderSecrets()
    {
        TxLeaseManager manager = new();
        Assert.True(manager.TryAcquire(
            "radio-a",
            "session-secret",
            "client-secret",
            "user-secret",
            "Visible Operator",
            TimeSpan.FromSeconds(10),
            out TxLease? lease,
            out _));

        string json = JsonSerializer.Serialize(lease!.ToStatus());

        Assert.DoesNotContain(lease.LeaseId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(lease.SessionId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(lease.ClientId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(lease.UserId, json, StringComparison.Ordinal);
        Assert.Contains(lease.DisplayName, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16)]
    public void InvalidLeaseDurationIsRejected(int seconds)
    {
        TxLeaseManager manager = new();

        Assert.False(manager.TryAcquire(
            "radio-a",
            "session-a",
            "client-a",
            "user-a",
            "Operator A",
            TimeSpan.FromSeconds(seconds),
            out TxLease? lease,
            out string? error));
        Assert.Null(lease);
        Assert.NotNull(error);
    }

    private static bool Acquire(
        TxLeaseManager manager,
        string radioId,
        string sessionId,
        string clientId,
        out TxLease? lease) =>
        manager.TryAcquire(
            radioId,
            sessionId,
            clientId,
            $"user-{clientId}",
            $"Operator {clientId}",
            TimeSpan.FromSeconds(10),
            out lease,
            out _);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan duration)
        {
            m_now = m_now.Add(duration);
        }
    }
}
