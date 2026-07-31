namespace AetherSDR.Web.Radio;

public sealed record RadioGuiClientDiagnostics(
    uint ClientHandle,
    string ClientId,
    string Program,
    string Station,
    string Source,
    bool LocalPtt,
    bool IsThisSession);

public sealed record RadioTransportDiagnostics(
    string Transport,
    uint ClientHandle,
    int UdpPort,
    uint AudioStreamId,
    long ConnectionAttempts,
    long UdpDatagrams,
    long SpectrumFrames,
    long AudioFrames,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? LastDatagramAt,
    DateTimeOffset? LastSpectrumFrameAt,
    DateTimeOffset? LastAudioFrameAt,
    DateTimeOffset? LastHeartbeatAt,
    IReadOnlyList<RadioGuiClientDiagnostics> GuiClients);

public interface IRadioTransportDiagnostics
{
    RadioTransportDiagnostics GetDiagnostics();
}

public sealed record RadioClientQueueDiagnostics(
    string ClientId,
    DateTimeOffset ConnectedAt,
    int QueueDepth,
    int QueueCapacity,
    long EnqueuedMessages,
    long DroppedMessages,
    DateTimeOffset? LastEnqueuedAt,
    DateTimeOffset? LastDequeuedAt,
    RadioBrowserAudioDiagnostics? Audio,
    RadioBrowserNetworkDiagnostics? Network);

public sealed record RadioBrowserNetworkDiagnostics(
    string Profile,
    string Adaptation,
    bool PageVisible,
    double SampleMilliseconds,
    long ReceivedBytes,
    long ReceivedMessages,
    double BytesPerSecond,
    double BitsPerSecond,
    double AudioBytesPerSecond,
    double SpectrumBytesPerSecond,
    double TextBytesPerSecond,
    double MessagesPerSecond,
    double MaximumGapMilliseconds,
    long AudioPackets,
    long SpectrumFrames,
    long TextMessages,
    long MissingAudioPackets,
    DateTimeOffset ReportedAt);

public sealed record RadioBrowserReconnectDiagnostics(
    long ConnectionAttempts,
    long SuccessfulConnections,
    long Reconnects,
    long RejectedConnections,
    DateTimeOffset? LastConnectedAt,
    DateTimeOffset? LastDisconnectedAt,
    double? LastRecoveryMilliseconds);

public sealed record RadioBrowserAudioDiagnostics(
    bool Enabled,
    string ContextState,
    string DeliveryPath,
    bool PageVisible,
    bool PlaybackSuppressed,
    bool RecoveryPending,
    long BackgroundTransitions,
    long ForegroundRecoveries,
    bool SliceAvailable,
    string ActiveSliceId,
    int SourceSampleRate,
    int OutputSampleRate,
    long ReceivedPackets,
    long ReceivedFrames,
    long MalformedPackets,
    long MissingPackets,
    double MaximumPacketGapMilliseconds,
    long PlayedFrames,
    int QueueFrames,
    double QueueMilliseconds,
    bool Started,
    long Underruns,
    long TrimmedFrames,
    long ClearedFrames,
    double BaseLatencyMilliseconds,
    double OutputLatencyMilliseconds,
    double EstimatedLatencyMilliseconds,
    double? WorkletReportAgeMilliseconds,
    DateTimeOffset ReportedAt);

public sealed record RadioTuneTimingDiagnostics(
    string State,
    string SliceId,
    int RadioSliceId,
    long TargetFrequencyHz,
    DateTimeOffset? RequestedAt,
    DateTimeOffset? ConfirmedAt,
    double? RadioRoundTripMilliseconds,
    string? Error);

public sealed record RadioSessionDiagnostics(
    string SessionId,
    string GuiClientId,
    string UserId,
    string DisplayName,
    string RadioId,
    string Host,
    int Port,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivity,
    int BrowserConnections,
    RadioBrowserReconnectDiagnostics Reconnect,
    bool LowBandwidth,
    long SnapshotVersion,
    bool Connected,
    string ConnectionState,
    string? ConnectionError,
    string RadioModel,
    string Serial,
    RadioTransportDiagnostics Transport,
    IReadOnlyList<RadioClientQueueDiagnostics> WebClients,
    IReadOnlyList<PanadapterSnapshot> Panadapters,
    IReadOnlyList<SliceSnapshot> Slices,
    RadioTxOccupancySnapshot TxOccupancy,
    RadioTuneTimingDiagnostics Tune,
    StationTxLifecycleDiagnostics? TxLifecycle = null);
