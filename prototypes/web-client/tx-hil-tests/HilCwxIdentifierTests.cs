using AetherSDR.TxHil;
using AetherSDR.Web.Radio;
using Xunit;

namespace AetherSDR.TxHil.Tests;

public sealed class HilCwxIdentifierTests
{
    private const uint ClientHandle = 0x10203040;
    private const uint ExternalHandle = 0x55667788;
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SendReplyUsesInsertionStartAndCharacterCount()
    {
        Assert.True(HilCwxIdentifier.TryParseSendReply(
            "49,1",
            7,
            out int start,
            out int end));

        Assert.Equal(49, start);
        Assert.Equal(55, end);
        Assert.False(HilCwxIdentifier.TryParseSendReply(
            "not-an-index,1",
            7,
            out _,
            out _));
    }

    [Fact]
    public void StatusTrackerBuildsCompleteSnapshotFromPartialUpdates()
    {
        ManualTimeProvider time = new(Now);
        HilCwxStatusTracker tracker = new(time);

        Assert.True(tracker.Observe(Fields(("sent", "4"))));
        Assert.True(tracker.Observe(Fields(("wpm", "20"))));
        Assert.True(tracker.Observe(Fields(("break_in_delay", "50"))));
        Assert.True(tracker.Observe(Fields(("qsk_enabled", "0"))));

        HilCwxSnapshot snapshot = tracker.Snapshot();
        Assert.Equal(4, snapshot.SentIndex);
        Assert.Equal(20, snapshot.Wpm);
        Assert.Equal(50, snapshot.BreakInDelayMilliseconds);
        Assert.False(snapshot.QskEnabled);
        Assert.True(snapshot.IsCompleteAndFresh(time.GetUtcNow()));

        time.Advance(HilCwxStatusTracker.ObservationLifetime);
        Assert.False(tracker.Snapshot().IsCompleteAndFresh(time.GetUtcNow()));
    }

    [Fact]
    public void NewSentStatusCannotRefreshStaleCwxConfiguration()
    {
        ManualTimeProvider time = new(Now);
        HilCwxStatusTracker tracker = new(time);
        tracker.Observe(Fields(
            ("sent", "0"),
            ("wpm", "20"),
            ("break_in_delay", "50"),
            ("qsk_enabled", "0")));
        time.Advance(TimeSpan.FromSeconds(7));
        tracker.Observe(Fields(("sent", "1")));
        time.Advance(TimeSpan.FromSeconds(2));

        HilCwxSnapshot snapshot = tracker.Snapshot();
        Assert.Equal(1, snapshot.SentIndex);
        Assert.False(snapshot.IsCompleteAndFresh(time.GetUtcNow()));
    }

    [Fact]
    public void CommandBoundaryHasNoKeyCommand()
    {
        Assert.True(HilFlexSession.IsAllowedHilCwxCommand("cwx clear"));
        Assert.True(HilFlexSession.IsAllowedHilCwxCommand(
            "cwx send \"KC4CAW\" 1"));
        Assert.True(HilFlexSession.IsAllowedHilCwxCommand("xmit 0"));
        Assert.False(HilFlexSession.IsAllowedHilCwxCommand("xmit 1"));
        Assert.False(HilFlexSession.IsAllowedHilCwxCommand(
            "cwx qsk_enabled 0"));
        Assert.False(HilFlexSession.IsAllowedHilCwxCommand(
            "cwx delay 50"));
        Assert.False(HilFlexSession.IsAllowedHilCwxCommand(
            "cwx send \"OTHER\" 1"));
        Assert.False(HilFlexSession.IsAllowedHilCwxCommand(
            "transmit set rfpower=100"));
    }

    [Fact]
    public async Task SuccessfulIdentificationRequiresOwnedTxDrainAndIdle()
    {
        FakeCwxRadio radio = new(
        [
            IdleSnapshot(CwxWithoutSent(wpm: 18, delay: 300, qsk: true)),
            IdleSnapshot(CwxWithoutSent(wpm: 20, delay: 300, qsk: true)),
            OwnedSnapshot(Cwx(sent: 9, wpm: 20, delay: 300, qsk: true)),
            OwnedSnapshot(Cwx(sent: 15, wpm: 20, delay: 300, qsk: true)),
            IdleSnapshot(Cwx(sent: 15, wpm: 20, delay: 300, qsk: true)),
            IdleSnapshot(Cwx(sent: 15, wpm: 18, delay: 300, qsk: true))
        ]);
        HilCwxIdentifier identifier = FastIdentifier();

        HilCwxIdentificationResult result = await identifier.IdentifyAsync(
            radio,
            CancellationToken.None);

        Assert.Equal("KC4CAW", result.Callsign);
        Assert.Equal(10, result.StartIndex);
        Assert.Equal(15, result.EndIndex);
        Assert.True(result.SawExactOwnedTransmit);
        Assert.Equal(
        [
            "cwx clear",
            "cwx wpm 20",
            "cwx send \"KC4CAW\" 1",
            "cwx clear",
            "cwx wpm 18"
        ],
            radio.Commands);
        Assert.DoesNotContain("xmit 0", radio.Commands);
        Assert.DoesNotContain("xmit 1", radio.Commands);
    }

    [Fact]
    public async Task ExternalLocalPttOwnerBlocksBeforeAnyCommand()
    {
        FakeCwxRadio radio = new(
        [
            IdleSnapshot(
                Cwx(sent: 0, wpm: 20, delay: 50, qsk: false),
                localPttOwner: ExternalHandle)
        ]);
        HilCwxIdentifier identifier = FastIdentifier();

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                identifier.IdentifyAsync(radio, CancellationToken.None));

        Assert.Contains("another FLEX GUI client", error.Message);
        Assert.Empty(radio.Commands);
    }

    [Fact]
    public async Task ExternalOwnerAfterSendIsNeverGloballyUnkeyed()
    {
        FakeCwxRadio radio = new(
        [
            IdleSnapshot(Cwx(sent: 0, wpm: 18, delay: 300, qsk: true)),
            IdleSnapshot(Cwx(sent: 0, wpm: 20, delay: 300, qsk: true)),
            ExternalTxSnapshot(Cwx(sent: 0, wpm: 20, delay: 300, qsk: true)),
            IdleSnapshot(Cwx(sent: 0, wpm: 20, delay: 300, qsk: true)),
            IdleSnapshot(Cwx(sent: 0, wpm: 18, delay: 300, qsk: true))
        ]);
        HilCwxIdentifier identifier = FastIdentifier();

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                identifier.IdentifyAsync(radio, CancellationToken.None));

        Assert.Contains("lost unambiguous", error.Message);
        Assert.Contains("cwx clear", radio.Commands);
        Assert.DoesNotContain("xmit 0", radio.Commands);
        Assert.DoesNotContain("xmit 1", radio.Commands);
    }

    [Fact]
    public async Task UnknownSendOutcomeUnkeysOnlyWhenExactOwnershipAppears()
    {
        FakeCwxRadio radio = new(
        [
            IdleSnapshot(Cwx(sent: 0, wpm: 18, delay: 300, qsk: true)),
            IdleSnapshot(Cwx(sent: 0, wpm: 20, delay: 300, qsk: true)),
            OwnedSnapshot(Cwx(sent: 0, wpm: 20, delay: 300, qsk: true))
        ])
        {
            ThrowOnIdentificationSend = true,
            AfterEmergencyUnkeySnapshots =
            [
                IdleSnapshot(Cwx(sent: 0, wpm: 20, delay: 300, qsk: true)),
                IdleSnapshot(Cwx(sent: 0, wpm: 18, delay: 300, qsk: true))
            ]
        };
        HilCwxIdentifier identifier = FastIdentifier();

        await Assert.ThrowsAsync<IOException>(() =>
            identifier.IdentifyAsync(radio, CancellationToken.None));

        Assert.Contains("cwx clear", radio.Commands);
        Assert.Contains("xmit 0", radio.Commands);
        Assert.DoesNotContain("xmit 1", radio.Commands);
    }

    [Fact]
    public async Task StuckExactOwnerReceivesEmergencyUnkeyOnly()
    {
        FakeCwxRadio radio = new(
        [
            IdleSnapshot(Cwx(sent: 0, wpm: 18, delay: 300, qsk: true)),
            IdleSnapshot(Cwx(sent: 0, wpm: 20, delay: 300, qsk: true)),
            OwnedSnapshot(Cwx(sent: 0, wpm: 20, delay: 300, qsk: true))
        ]);
        radio.AfterEmergencyUnkeySnapshots =
        [
            IdleSnapshot(Cwx(sent: 0, wpm: 20, delay: 300, qsk: true)),
            IdleSnapshot(Cwx(sent: 0, wpm: 18, delay: 300, qsk: true))
        ];
        HilCwxIdentifier identifier = new(
            pollInterval: TimeSpan.FromMilliseconds(1),
            transmitTimeout: TimeSpan.FromMilliseconds(12),
            idleTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            identifier.IdentifyAsync(radio, CancellationToken.None));

        Assert.Contains("cwx clear", radio.Commands);
        Assert.Contains("xmit 0", radio.Commands);
        Assert.DoesNotContain("xmit 1", radio.Commands);
    }

    [Fact]
    public async Task IncompleteCwxStatusBlocksBeforeAnyCommand()
    {
        FakeCwxRadio radio = new(
        [
            IdleSnapshot(new HilCwxSnapshot(
                SentIndex: 0,
                Wpm: null,
                BreakInDelayMilliseconds: 50,
                QskEnabled: false,
                SentObservedAt: Now,
                SentFreshUntil: Now + TimeSpan.FromMinutes(1),
                ConfigurationObservedAt: null,
                ConfigurationFreshUntil: null))
        ]);
        HilCwxIdentifier identifier = FastIdentifier();

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                identifier.IdentifyAsync(radio, CancellationToken.None));

        Assert.Contains("fresh WPM", error.Message);
        Assert.Empty(radio.Commands);
    }

    private static HilCwxIdentifier FastIdentifier() =>
        new(
            pollInterval: TimeSpan.FromMilliseconds(1),
            transmitTimeout: TimeSpan.FromMilliseconds(100),
            idleTimeout: TimeSpan.FromMilliseconds(100));

    private static HilCwxSnapshot Cwx(
        int sent,
        int wpm,
        int delay,
        bool qsk) =>
        new(
            sent,
            wpm,
            delay,
            qsk,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));

    private static HilCwxSnapshot CwxWithoutSent(
        int wpm,
        int delay,
        bool qsk) =>
        new(
            SentIndex: null,
            Wpm: wpm,
            BreakInDelayMilliseconds: delay,
            QskEnabled: qsk,
            SentObservedAt: null,
            SentFreshUntil: null,
            ConfigurationObservedAt: DateTimeOffset.UtcNow,
            ConfigurationFreshUntil:
                DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1));

    private static HilCwxRadioSnapshot IdleSnapshot(
        HilCwxSnapshot cwx,
        uint localPttOwner = ClientHandle) =>
        new(
            Occupancy(
                RadioTxOccupancyState.Idle,
                occupants: [],
                localPttOwner),
            Clients(localPttOwner),
            cwx);

    private static HilCwxRadioSnapshot OwnedSnapshot(HilCwxSnapshot cwx) =>
        new(
            Occupancy(
                RadioTxOccupancyState.AetherOwned,
                occupants: [AetherOccupant()],
                ClientHandle),
            Clients(ClientHandle),
            cwx);

    private static HilCwxRadioSnapshot ExternalTxSnapshot(
        HilCwxSnapshot cwx) =>
        new(
            Occupancy(
                RadioTxOccupancyState.External,
                occupants: [ExternalOccupant()],
                ClientHandle),
            Clients(ClientHandle),
            cwx);

    private static RadioTxOccupancySnapshot Occupancy(
        RadioTxOccupancyState state,
        IReadOnlyList<RadioTxOccupant> occupants,
        uint localPttOwner) =>
        new(
            HilOptions.Psoc2RadioId,
            state,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1),
            occupants,
            localPttOwner == ClientHandle
                ? [AetherOccupant()]
                : [ExternalOccupant()]);

    private static IReadOnlyList<RadioGuiClientDiagnostics> Clients(
        uint localPttOwner) =>
        localPttOwner == ClientHandle
            ?
            [
                new RadioGuiClientDiagnostics(
                    ClientHandle,
                    "hil",
                    "AetherSDR",
                    "AETHER-TX-HIL",
                    "",
                    true,
                    true)
            ]
            :
            [
                new RadioGuiClientDiagnostics(
                    ClientHandle,
                    "hil",
                    "AetherSDR",
                    "AETHER-TX-HIL",
                    "",
                    false,
                    true),
                new RadioGuiClientDiagnostics(
                    ExternalHandle,
                    "external",
                    "SmartSDR-Win",
                    "STEVENS-SURFACE",
                    "",
                    true,
                    false)
            ];

    private static RadioTxOccupant AetherOccupant() =>
        new(
            ClientHandle,
            "AetherSDR",
            "AETHER-TX-HIL",
            "SW",
            true);

    private static RadioTxOccupant ExternalOccupant() =>
        new(
            ExternalHandle,
            "SmartSDR-Win",
            "STEVENS-SURFACE",
            "SW",
            false);

    private static IReadOnlyDictionary<string, string> Fields(
        params (string Key, string Value)[] values) =>
        values.ToDictionary(
            value => value.Key,
            value => value.Value,
            StringComparer.OrdinalIgnoreCase);

    private sealed class FakeCwxRadio(
        IReadOnlyList<HilCwxRadioSnapshot> snapshots) : IHilCwxRadio
    {
        private readonly Queue<HilCwxRadioSnapshot> m_snapshots =
            new(snapshots);
        private HilCwxRadioSnapshot m_current = snapshots.Count > 0
            ? snapshots[0]
            : throw new ArgumentException(
                "At least one snapshot is required.",
                nameof(snapshots));

        public uint ClientHandle => HilCwxIdentifierTests.ClientHandle;
        public List<string> Commands { get; } = [];
        public bool ThrowOnIdentificationSend { get; init; }
        public IReadOnlyList<HilCwxRadioSnapshot>? AfterEmergencyUnkeySnapshots
        {
            get;
            set;
        }

        public HilCwxRadioSnapshot Snapshot()
        {
            if (m_snapshots.Count > 0)
            {
                m_current = m_snapshots.Dequeue();
            }
            return m_current;
        }

        public Task<HilCwxCommandResult> SendCwxCommandAsync(
            string command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            if (ThrowOnIdentificationSend &&
                command == "cwx send \"KC4CAW\" 1")
            {
                throw new IOException("socket closed after send");
            }
            if (command == "xmit 0" &&
                AfterEmergencyUnkeySnapshots is not null)
            {
                m_snapshots.Clear();
                foreach (HilCwxRadioSnapshot snapshot in
                         AfterEmergencyUnkeySnapshots)
                {
                    m_snapshots.Enqueue(snapshot);
                }
            }
            return Task.FromResult(
                command == "cwx send \"KC4CAW\" 1"
                    ? HilCwxCommandResult.Ok("10,1")
                    : HilCwxCommandResult.Ok());
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset m_now = now;

        public override DateTimeOffset GetUtcNow() => m_now;

        public void Advance(TimeSpan duration) =>
            m_now = m_now.Add(duration);
    }
}
