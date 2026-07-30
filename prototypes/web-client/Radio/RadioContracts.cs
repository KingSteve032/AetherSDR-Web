using System.Text.Json;

namespace AetherSDR.Web.Radio;

public sealed record SliceSnapshot(
    string Id,
    long FrequencyHz,
    string Mode,
    int FilterLowHz,
    int FilterHighHz,
    int AfGain,
    int Squelch,
    bool IsActive,
    bool IsTx,
    int RadioId = -1,
    bool IsMuted = false,
    bool SquelchEnabled = false,
    int AudioPan = 50,
    string AgcMode = "MED",
    int AgcThreshold = 65,
    string RxAntenna = "ANT1",
    bool Nb = false,
    bool Nr = false,
    bool Anf = false,
    bool Nrl = false,
    bool Nrs = false,
    bool Rnn = false,
    bool Nrf = false,
    bool Anfl = false,
    bool Anft = false,
    int NbLevel = 50,
    int NrLevel = 0,
    int AnfLevel = 0,
    int NrlLevel = 50,
    int NrsLevel = 50,
    int NrfLevel = 50,
    int AnflLevel = 50,
    int DaxChannel = 0,
    uint PanStreamId = 0);

public sealed record PanadapterSnapshot(
    long CenterFrequencyHz,
    int BandwidthHz,
    int MinDbm,
    int MaxDbm,
    int FftAverage = 35,
    int FramesPerSecond = 15,
    bool WnbEnabled = false,
    int WnbLevel = 50,
    string Id = "PAN-1",
    uint StreamId = 1,
    string WaterfallId = "");

public sealed record RadioSnapshot(
    long Version,
    string SessionId,
    string RadioModel,
    string Serial,
    bool Connected,
    bool CanTransmit,
    string ActiveSliceId,
    PanadapterSnapshot Panadapter,
    IReadOnlyList<SliceSnapshot> Slices,
    IReadOnlyList<PanadapterSnapshot>? Panadapters = null,
    string ConnectionState = "connecting",
    string? ConnectionError = null);

public sealed record PresenceSnapshot(
    string ClientId,
    string UserId,
    string DisplayName,
    IReadOnlyList<string> Roles,
    DateTimeOffset ConnectedAt);

public sealed record OperatorPresenceSnapshot(
    string UserId,
    string DisplayName,
    IReadOnlyList<string> Roles,
    DateTimeOffset ConnectedAt,
    int ConnectionCount);

public sealed record ControlIntent(
    string Action,
    string Selector,
    JsonElement Values);

public sealed record IntentResult(
    bool Ok,
    string? Error,
    long Version,
    string Model,
    string Selector,
    IReadOnlyDictionary<string, object?> Changes)
{
    public static IntentResult Failure(string error, long version) =>
        new(false, error, version, string.Empty, string.Empty,
            new Dictionary<string, object?>());
}

public sealed record TxLease(
    string LeaseId,
    string RadioId,
    string SessionId,
    string ClientId,
    string UserId,
    string DisplayName,
    DateTimeOffset AcquiredAt,
    DateTimeOffset RenewedAt,
    DateTimeOffset ExpiresAt)
{
    public TxLeaseStatus ToStatus() =>
        new(
            RadioId,
            DisplayName,
            AcquiredAt,
            RenewedAt,
            ExpiresAt);
}

public sealed record TxLeaseStatus(
    string RadioId,
    string DisplayName,
    DateTimeOffset AcquiredAt,
    DateTimeOffset RenewedAt,
    DateTimeOffset ExpiresAt);
