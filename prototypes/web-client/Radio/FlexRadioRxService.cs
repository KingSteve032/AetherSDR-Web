using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace AetherSDR.Web.Radio;

public sealed class FlexRadioRxService(
    RadioCoordinator coordinator,
    FlexRadioCommandRouter commandRouter,
    IRadioConnectionSelection selectionManager,
    IOptions<RadioSettings> settings,
    RadioTxOccupancyRegistry txOccupancyRegistry,
    ILogger<FlexRadioRxService> logger)
    : BackgroundService, IRadioTransportDiagnostics
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(4);
    private readonly ConcurrentDictionary<string, string> m_waterfallsByPan =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object m_sliceGate = new();
    private readonly Dictionary<int, FlexSliceState> m_ownedSlices = [];
    private readonly WebSliceIdAllocator m_webSliceIds = new();
    private readonly FlexGuiClientRoster m_guiClients = new();
    private readonly FlexInterlockTracker m_interlock = new();
    private uint m_webSequence;
    private uint m_audioSequence;
    private long m_clientHandle;
    private int m_udpPort;
    private long m_audioStreamId;
    private long m_connectionAttempts;
    private long m_udpDatagrams;
    private long m_spectrumFrames;
    private long m_audioFrames;
    private long m_connectedUnixMilliseconds;
    private long m_lastDatagramUnixMilliseconds;
    private long m_lastSpectrumFrameUnixMilliseconds;
    private long m_lastAudioFrameUnixMilliseconds;
    private long m_lastHeartbeatUnixMilliseconds;

    public RadioTransportDiagnostics GetDiagnostics() =>
        new(
            "FlexRx",
            unchecked((uint)Math.Max(
                0,
                Volatile.Read(ref m_clientHandle))),
            Math.Max(0, Volatile.Read(ref m_udpPort)),
            unchecked((uint)Math.Max(
                0,
                Volatile.Read(ref m_audioStreamId))),
            Volatile.Read(ref m_connectionAttempts),
            Volatile.Read(ref m_udpDatagrams),
            Volatile.Read(ref m_spectrumFrames),
            Volatile.Read(ref m_audioFrames),
            FromUnixMilliseconds(
                Volatile.Read(ref m_connectedUnixMilliseconds)),
            FromUnixMilliseconds(
                Volatile.Read(ref m_lastDatagramUnixMilliseconds)),
            FromUnixMilliseconds(
                Volatile.Read(ref m_lastSpectrumFrameUnixMilliseconds)),
            FromUnixMilliseconds(
                Volatile.Read(ref m_lastAudioFrameUnixMilliseconds)),
            FromUnixMilliseconds(
                Volatile.Read(ref m_lastHeartbeatUnixMilliseconds)),
            m_guiClients.Snapshot(
                unchecked((uint)Math.Max(
                    0,
                    Volatile.Read(ref m_clientHandle)))));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(
                settings.Value.Mode,
                "FlexRx",
                StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Flex receive bridge disabled for radio mode {RadioMode}",
                settings.Value.Mode);
            return;
        }

        ValidateSettings(settings.Value);
        logger.LogInformation(
            "Starting selectable receive-only Flex bridge; transmit is unavailable");

        while (!stoppingToken.IsCancellationRequested)
        {
            SelectedRadioEndpoint endpoint = selectionManager.Selected;
            using CancellationTokenSource sessionLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            coordinator.SetRadioConnection(
                false,
                connectionState: "connecting");
            try
            {
                Task sessionTask = RunSessionAsync(
                    settings.Value,
                    endpoint,
                    sessionLifetime.Token);
                Task selectionChanged = selectionManager.WaitForChangeAsync(
                    endpoint.Revision,
                    sessionLifetime.Token);
                Task completed = await Task.WhenAny(
                    sessionTask,
                    selectionChanged);
                if (ReferenceEquals(completed, selectionChanged))
                {
                    await selectionChanged;
                    sessionLifetime.Cancel();
                    try
                    {
                        await sessionTask;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    logger.LogInformation(
                        "Flex radio selection changed; reconnecting the receive session");
                    continue;
                }

                sessionLifetime.Cancel();
                await sessionTask;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (FlexGuiClientRejectedException exception)
            {
                logger.LogWarning(
                    "Radio rejected GUI registration with 0x{Code:x8}; " +
                    "the browser session will retry without replacing another client",
                    exception.Code);
                coordinator.SetRadioConnection(
                    false,
                    radioModel: "FLEX (GUI client slot unavailable)",
                    connectionState: "radio-busy",
                    connectionError:
                        "The radio did not accept another GUI client. " +
                        "This browser will retry when the radio frees a client slot.");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Receive-only Flex session ended; retrying");
                coordinator.SetRadioConnection(
                    false,
                    connectionState: "reconnecting",
                    connectionError:
                        "The radio connection stopped. The browser is retrying.");
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    private async Task RunSessionAsync(
        RadioSettings radio,
        SelectedRadioEndpoint endpoint,
        CancellationToken stoppingToken)
    {
        Interlocked.Increment(ref m_connectionAttempts);
        ResetCurrentTransportIds();
        IPAddress address = IPAddress.Parse(endpoint.Host);
        m_waterfallsByPan.Clear();
        lock (m_sliceGate)
        {
            m_ownedSlices.Clear();
            m_webSliceIds.Reset();
        }
        coordinator.SetLiveSlices([]);

        using UdpClient udp = new(AddressFamily.InterNetwork);
        udp.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        int udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        Volatile.Write(ref m_udpPort, udpPort);

        await using FlexControlSession control = new(logger);
        await control.ConnectAsync(address, endpoint.Port, stoppingToken);
        uint handle = await control.WaitForHandleAsync(
            TimeSpan.FromSeconds(5),
            stoppingToken);
        Interlocked.Exchange(ref m_clientHandle, handle);
        Interlocked.Exchange(
            ref m_connectedUnixMilliseconds,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        FlexRestoredPanTracker restoredPans = new(handle);
        control.StatusReceived += line =>
        {
            restoredPans.Observe(line);
            ObserveStatus(line, handle);
        };

        logger.LogInformation(
            "Flex TCP session established with handle 0x{Handle:x8}; UDP port {UdpPort}",
            handle,
            udpPort);

        string guiClientId = string.IsNullOrWhiteSpace(radio.GuiClientId)
            ? Guid.NewGuid().ToString()
            : radio.GuiClientId;
        bool lowBandwidth = selectionManager.LowBandwidth;
        IReadOnlyDictionary<string, int> normalDisplayRatesToRestore =
            selectionManager.NormalDisplayRatesToRestore;
        int displayXPixels =
            lowBandwidth ? Math.Min(512, radio.XPixels) : radio.XPixels;
        int displayFramesPerSecond =
            lowBandwidth
                ? Math.Min(8, radio.FramesPerSecond)
                : radio.FramesPerSecond;
        if (!lowBandwidth && normalDisplayRatesToRestore.Count > 0)
        {
            displayFramesPerSecond =
                normalDisplayRatesToRestore.Values.First();
        }

        // Older firmware validates this token against its known GUI clients.
        // Match AetherSDR's native registration string exactly.
        await SendBestEffortAsync(control, "client program AetherSDR", stoppingToken);
        if (lowBandwidth)
        {
            // FLEX firmware 4.2.18 requires low_bw_connect after program
            // identity and before GUI registration, matching FlexLib/AetherSDR.
            await SendBestEffortAsync(
                control,
                "client low_bw_connect",
                stoppingToken);
        }
        FlexCommandResponse guiRegistration = await control.SendCommandAsync(
            $"client gui {guiClientId}",
            CommandTimeout,
            stoppingToken);
        if (!guiRegistration.IsSuccess)
        {
            throw new FlexGuiClientRejectedException(
                guiRegistration.Code,
                guiRegistration.Body);
        }
        await SendBestEffortAsync(
            control,
            $"client station {SanitizeStationName(radio.StationName)}",
            stoppingToken);
        await SendBestEffortAsync(
            control,
            $"client set enforce_network_mtu=1 network_mtu={radio.NetworkMtu}",
            stoppingToken);
        await SendRequiredAsync(control, "sub radio all", stoppingToken);
        await SendRequiredAsync(control, "sub client all", stoppingToken);
        await SendRequiredAsync(control, "sub tx all", stoppingToken);
        await SendRequiredAsync(control, "sub pan all", stoppingToken);
        await SendRequiredAsync(control, "sub audio all", stoppingToken);

        FlexCommandResponse info =
            await control.SendCommandAsync("info", CommandTimeout, stoppingToken);
        (string model, string serial) = ParseRadioIdentity(info.Body);
        coordinator.SetRadioConnection(
            false,
            string.IsNullOrWhiteSpace(model) ? "FLEX (receiving)" : model,
            string.IsNullOrWhiteSpace(serial) ? "RX-ONLY" : serial);

        IPEndPoint registrationEndpoint = new(address, 4992);
        await udp.SendAsync(
            new byte[] { 0 },
            registrationEndpoint,
            stoppingToken);

        FlexCommandResponse udpPortResponse = await control.SendCommandAsync(
            $"client udpport {udpPort}",
            CommandTimeout,
            stoppingToken);
        logger.LogInformation(
            "Flex UDP registration response: port={UdpPort} code=0x{Code:x8} body={Body}",
            udpPort,
            udpPortResponse.Code,
            string.IsNullOrWhiteSpace(udpPortResponse.Body)
                ? "<empty>"
                : udpPortResponse.Body);
        if (!udpPortResponse.IsSuccess)
        {
            logger.LogInformation(
                "Flex client udpport returned 0x{Code:x8}; continuing with the firmware-compatible UDP prime",
                udpPortResponse.Code);
        }

        using CancellationTokenSource receiveLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        TaskCompletionSource firstFrame =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        AudioStreamRegistration audioStream = new();
        Task receiveTask = ReceiveUdpAsync(
            udp,
            radio,
            audioStream,
            firstFrame,
            receiveLifetime.Token);

        string? panId = null;
        string? waterfallId = null;
        string? remoteAudioId = null;
        try
        {
            FlexRestoredPanStatus[] restoredPanStatuses =
                await restoredPans.WaitForSnapshotAsync(
                    TimeSpan.FromMilliseconds(250),
                    stoppingToken);
            PanadapterSnapshot[] sessionPanadapters =
                CreateRestoredPanSnapshots(
                    restoredPanStatuses,
                    radio,
                    displayFramesPerSecond);
            if (sessionPanadapters.Length == 0)
            {
                FlexCommandResponse create = await control.SendCommandAsync(
                    "display panafall create x=100 y=100",
                    CommandTimeout,
                    stoppingToken);
                if (!create.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Flex panafall creation failed with 0x{create.Code:x8}: {create.Body}");
                }

                (panId, waterfallId) = ParsePanafallCreateIds(create.Body);
                if (panId is null)
                {
                    throw new InvalidOperationException(
                        $"Flex returned an unrecognized panafall id: '{create.Body}'.");
                }
                logger.LogInformation(
                    "Flex panafall created: pan={PanId} waterfall={WaterfallId} response={Response}",
                    panId,
                    waterfallId ?? "<pending-status>",
                    create.Body);
                if (!FlexStatusParser.TryParseFlexUInt(
                        panId,
                        out uint createdPanStreamId))
                {
                    throw new InvalidOperationException(
                        $"Flex returned an invalid panadapter id: '{panId}'.");
                }
                lock (m_sliceGate)
                {
                    // The radio can replay slices restored for an older pan
                    // during the initial subscription. A fresh pan starts a
                    // new slice generation.
                    m_ownedSlices.Clear();
                    m_webSliceIds.Reset();
                }
                coordinator.SetLiveSlices([]);

                await SendRequiredAsync(
                    control,
                    FormattableString.Invariant(
                        $"display pan set {panId} xpixels={displayXPixels} ypixels={radio.YPixels}"),
                    stoppingToken);
                await SendRequiredAsync(
                    control,
                    FormattableString.Invariant(
                        $"display pan set {panId} min_dbm={radio.MinDbm} max_dbm={radio.MaxDbm}"),
                    stoppingToken);
                await SendRequiredAsync(
                    control,
                    FormattableString.Invariant(
                        $"display pan set {panId} center={radio.CenterFrequencyHz / 1_000_000d:F6} bandwidth={radio.BandwidthHz / 1_000_000d:F6}"),
                    stoppingToken);
                await SendRequiredAsync(
                    control,
                    $"display pan set {panId} fps={displayFramesPerSecond}",
                    stoppingToken);
                if (lowBandwidth && waterfallId is not null)
                {
                    await SendBestEffortAsync(
                        control,
                        $"display panafall set {waterfallId} line_duration=125",
                        stoppingToken);
                }

                sessionPanadapters =
                [
                    new PanadapterSnapshot(
                        CenterFrequencyHz: radio.CenterFrequencyHz,
                        BandwidthHz: radio.BandwidthHz,
                        MinDbm: radio.MinDbm,
                        MaxDbm: radio.MaxDbm,
                        FramesPerSecond: displayFramesPerSecond,
                        Id: panId,
                        StreamId: createdPanStreamId,
                        WaterfallId: waterfallId ?? string.Empty)
                ];
            }
            else
            {
                PanadapterSnapshot primaryRestoredPan =
                    sessionPanadapters[0];
                panId = primaryRestoredPan.Id;
                waterfallId = primaryRestoredPan.WaterfallId;
                logger.LogInformation(
                    "Adopting {PanCount} radio-restored panadapter(s); primary pan={PanId} waterfall={WaterfallId}",
                    sessionPanadapters.Length,
                    panId,
                    string.IsNullOrWhiteSpace(waterfallId)
                        ? "<pending-status>"
                        : waterfallId);
                foreach (string activationCommand in
                         BuildRestoredPanActivationCommands(
                             sessionPanadapters,
                             displayXPixels,
                             radio.YPixels,
                             normalDisplayRatesToRestore))
                {
                    await SendRequiredAsync(
                        control,
                        activationCommand,
                        stoppingToken);
                }
                sessionPanadapters = ApplyRestoredDisplayRates(
                    sessionPanadapters,
                    normalDisplayRatesToRestore);
            }
            if (!lowBandwidth &&
                normalDisplayRatesToRestore.Count > 0)
            {
                selectionManager.MarkNormalDisplayRatesRestored();
            }

            PanadapterSnapshot primaryPan = sessionPanadapters[0];
            uint primaryPanStreamId = primaryPan.StreamId;
            commandRouter.Attach(
                control,
                primaryPan.Id,
                primaryPan.CenterFrequencyHz);
            foreach (PanadapterSnapshot additionalPan in
                     sessionPanadapters.Skip(1))
            {
                commandRouter.RegisterPan(
                    additionalPan.Id,
                    additionalPan.CenterFrequencyHz);
            }
            coordinator.ReplacePanadapters(sessionPanadapters);

            await SendRequiredAsync(control, "sub slice all", stoppingToken);
            await SendBestEffortAsync(control, "slice list", stoppingToken);
            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            // FLEX firmware 4.2.18 can restore GUIClientID-owned slices on a
            // previous pan. They are hidden by this session's pan filter but
            // remain audible in remote_audio_rx. Preserve the active restored
            // frequency, then remove those hidden copies before opening audio.
            long defaultSliceFrequencyHz =
                await RemoveHiddenRestoredSlicesAsync(
                    control,
                    primaryPanStreamId,
                    stoppingToken) ??
                radio.InitialSliceFrequencyHz;
            await TryRevealOwnedSliceAsync(
                control,
                panId,
                radio.BandwidthHz,
                stoppingToken);
            bool defaultSliceRequested = await TryCreateDefaultSliceAsync(
                control,
                panId,
                defaultSliceFrequencyHz,
                stoppingToken);
            if (!defaultSliceRequested)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(750),
                    stoppingToken);
                await TryCreateDefaultSliceAsync(
                    control,
                    panId,
                    defaultSliceFrequencyHz,
                    stoppingToken);
            }

            await SendBestEffortAsync(
                control,
                "radio set mute_local_audio_when_remote=0",
                stoppingToken);
            FlexCommandResponse createAudio =
                await control.SendCommandAsync(
                    "stream create type=remote_audio_rx compression=none",
                    CommandTimeout,
                    stoppingToken);
            if (createAudio.IsSuccess &&
                TryParseStreamId(createAudio.Body, out uint parsedAudioId))
            {
                remoteAudioId = $"0x{parsedAudioId:x8}";
                audioStream.StreamId = parsedAudioId;
                Interlocked.Exchange(ref m_audioStreamId, parsedAudioId);
                logger.LogInformation(
                    "Flex receive audio stream created: {StreamId}",
                    remoteAudioId);
            }
            else
            {
                logger.LogWarning(
                    "Flex receive audio is unavailable: code=0x{Code:x8} body={Body}",
                    createAudio.Code,
                    createAudio.Body);
            }

            for (int attempt = 0;
                 attempt < 8 && !firstFrame.Task.IsCompleted;
                 attempt++)
            {
                await udp.SendAsync(
                    new byte[] { 0 },
                    registrationEndpoint,
                    stoppingToken);
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }

            Task firstCompleted = await Task.WhenAny(
                firstFrame.Task,
                control.Completion,
                Task.Delay(TimeSpan.FromSeconds(10), stoppingToken));
            if (firstCompleted != firstFrame.Task)
            {
                throw new IOException(
                    "No Flex FFT packets arrived within 10 seconds. TCP is connected, but UDP may be blocked.");
            }

            // The initially restored radio-active slice may be promoted to
            // web Slice A while the session is settling. Once the browser is
            // told the radio is connected, A/B identities must never move:
            // an in-progress drag continues addressing the same radio slice.
            m_webSliceIds.Freeze();
            coordinator.SetRadioConnection(
                true,
                model,
                serial,
                stationClientHandle: handle);
            logger.LogInformation(
                "Live Flex FFT is flowing from {RadioHost} through panadapter {PanId}",
                endpoint.Host,
                panId);

            Task heartbeatTask = RunHeartbeatAsync(
                control,
                handle,
                stoppingToken);
            Task completed = await Task.WhenAny(
                receiveTask,
                heartbeatTask,
                control.Completion);
            if (!stoppingToken.IsCancellationRequested)
            {
                await completed;
                throw new IOException("The live Flex receive session stopped.");
            }
        }
        finally
        {
            PanadapterSnapshot[] sessionPanSnapshots =
                (coordinator.Snapshot.Panadapters ??
                    [coordinator.Snapshot.Panadapter]).ToArray();
            string[] sessionPanIds = commandRouter.PanIds;
            int[] sessionCreatedSliceIds = commandRouter.Detach(control);
            receiveLifetime.Cancel();
            try
            {
                await receiveTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }

            if (remoteAudioId is not null)
            {
                await TryRemoveAsync(
                    control,
                    "radio set mute_local_audio_when_remote=0",
                    CancellationToken.None);
                await TryRemoveAsync(
                    control,
                    $"stream remove {remoteAudioId}",
                    CancellationToken.None);
            }
            foreach (int sliceId in sessionCreatedSliceIds)
            {
                await TryRemoveAsync(
                    control,
                    $"slice remove {sliceId}",
                    CancellationToken.None);
            }
            foreach (string sessionPanId in sessionPanIds)
            {
                string? sessionWaterfallId =
                    sessionPanSnapshots.FirstOrDefault(
                        panSnapshot => string.Equals(
                            panSnapshot.Id,
                            sessionPanId,
                            StringComparison.OrdinalIgnoreCase))?.WaterfallId;
                sessionWaterfallId =
                    string.IsNullOrWhiteSpace(sessionWaterfallId)
                        ? string.Equals(
                            sessionPanId,
                            panId,
                            StringComparison.OrdinalIgnoreCase)
                            ? waterfallId
                            : null
                        : sessionWaterfallId;
                sessionWaterfallId ??=
                    m_waterfallsByPan.TryGetValue(
                        sessionPanId,
                        out string? mappedId)
                        ? mappedId
                        : null;
                await RemoveDisplayStreamsAsync(
                    control,
                    sessionPanId,
                    sessionWaterfallId);
            }
            lock (m_sliceGate)
            {
                m_ownedSlices.Clear();
                m_webSliceIds.Reset();
            }
            coordinator.SetLiveSlices([]);
            coordinator.SetRadioConnection(
                false,
                model,
                serial,
                stationClientHandle: handle);
            ResetCurrentTransportIds();
        }
    }

    private async Task ReceiveUdpAsync(
        UdpClient udp,
        RadioSettings radio,
        AudioStreamRegistration audioStream,
        TaskCompletionSource firstFrame,
        CancellationToken cancellationToken)
    {
        FlexVitaFftDecoder decoder =
            new(radio.MinDbm, radio.MaxDbm, radio.YPixels);
        FlexVitaAudioDecoder audioDecoder = new();
        bool loggedFirstFrame = false;
        bool loggedFirstAudio = false;
        int loggedDatagrams = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram =
                await udp.ReceiveAsync(cancellationToken);
            Interlocked.Increment(ref m_udpDatagrams);
            Interlocked.Exchange(
                ref m_lastDatagramUnixMilliseconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (loggedDatagrams < 6)
            {
                loggedDatagrams++;
                FlexVitaFftDecoder.TryReadPacketIdentity(
                    datagram.Buffer,
                    out uint loggedStreamId,
                    out ushort loggedPacketClass);
                if (loggedPacketClass ==
                        FlexVitaFftDecoder.FftPacketClassCode &&
                    datagram.Buffer.Length >= 40)
                {
                    ReadOnlySpan<byte> fft = datagram.Buffer.AsSpan(28, 12);
                    logger.LogDebug(
                        "Flex UDP packet {PacketNumber}: from={Remote} bytes={ByteCount} stream=0x{StreamId:x8} pcc=0x{PacketClass:x4} frame={FrameIndex} start={StartBin} count={BinCount} size={BinSize} total={TotalBins}",
                        loggedDatagrams,
                        datagram.RemoteEndPoint,
                        datagram.Buffer.Length,
                        loggedStreamId,
                        loggedPacketClass,
                        BinaryPrimitives.ReadUInt32BigEndian(fft[8..]),
                        BinaryPrimitives.ReadUInt16BigEndian(fft),
                        BinaryPrimitives.ReadUInt16BigEndian(fft[2..]),
                        BinaryPrimitives.ReadUInt16BigEndian(fft[4..]),
                        BinaryPrimitives.ReadUInt16BigEndian(fft[6..]));
                }
                else
                {
                    logger.LogDebug(
                        "Flex UDP packet {PacketNumber}: from={Remote} bytes={ByteCount} stream=0x{StreamId:x8} pcc=0x{PacketClass:x4}",
                        loggedDatagrams,
                        datagram.RemoteEndPoint,
                        datagram.Buffer.Length,
                        loggedStreamId,
                        loggedPacketClass);
                }
            }

            uint audioStreamId = audioStream.StreamId;
            if (audioStreamId != 0 &&
                audioDecoder.TryDecode(
                    datagram.Buffer,
                    audioStreamId,
                    out FlexAudioFrame? audioFrame))
            {
                if (!loggedFirstAudio)
                {
                    loggedFirstAudio = true;
                    logger.LogInformation(
                        "First Flex receive-audio frame: stream=0x{StreamId:x8} stereo_frames={FrameCount} sample_rate={SampleRate}",
                        audioFrame!.StreamId,
                        audioFrame.Samples.Length / 2,
                        FlexVitaAudioDecoder.SampleRate);
                }
                coordinator.BroadcastAudio(
                    AudioFrameCodec.Encode(
                        audioFrame!.Samples,
                        unchecked(++m_audioSequence),
                        FlexVitaAudioDecoder.SampleRate));
                Interlocked.Increment(ref m_audioFrames);
                Interlocked.Exchange(
                    ref m_lastAudioFrameUnixMilliseconds,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                continue;
            }

            if (!FlexVitaFftDecoder.TryReadPacketIdentity(
                    datagram.Buffer,
                    out uint fftStreamId,
                    out ushort fftPacketClass) ||
                fftPacketClass != FlexVitaFftDecoder.FftPacketClassCode ||
                !commandRouter.IsOwnedPan(fftStreamId))
            {
                continue;
            }

            RadioSnapshot snapshot = coordinator.Snapshot;
            PanadapterSnapshot? panSnapshot =
                (snapshot.Panadapters ?? [snapshot.Panadapter])
                    .FirstOrDefault(pan => pan.StreamId == fftStreamId);
            if (panSnapshot is null)
            {
                continue;
            }
            decoder.SetDbmRange(
                panSnapshot.MinDbm,
                panSnapshot.MaxDbm);
            if (!decoder.TryDecode(
                    datagram.Buffer,
                    out FlexFftFrame? frame))
            {
                continue;
            }

            FlexFftFrame decodedFrame = frame!;
            uint sequence = unchecked(++m_webSequence);
            if (!loggedFirstFrame)
            {
                loggedFirstFrame = true;
                logger.LogInformation(
                    "First Flex FFT frame: stream=0x{StreamId:x8} bins={BinCount} raw={RawMin}-{RawMax} scale_pixels={EffectiveYPixels} dbm={MinDbm:F1}..{MaxDbm:F1}",
                    decodedFrame.StreamId,
                    decodedFrame.Bins.Length,
                    decodedFrame.RawMin,
                    decodedFrame.RawMax,
                    decodedFrame.EffectiveYPixels,
                    decodedFrame.Bins.Min(),
                    decodedFrame.Bins.Max());
            }

            coordinator.BroadcastSpectrum(
                SpectrumFrameCodec.Encode(
                    decodedFrame.Bins,
                    sequence,
                    commandRouter.PanCenterHzFor(decodedFrame.StreamId) > 0
                        ? commandRouter.PanCenterHzFor(decodedFrame.StreamId)
                        : panSnapshot.CenterFrequencyHz,
                    panSnapshot.BandwidthHz,
                    decodedFrame.StreamId));
            Interlocked.Increment(ref m_spectrumFrames);
            Interlocked.Exchange(
                ref m_lastSpectrumFrameUnixMilliseconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            firstFrame.TrySetResult();
        }
    }

    private int OwnedSliceCount
    {
        get
        {
            lock (m_sliceGate)
            {
                return m_ownedSlices.Values.Count(
                    slice =>
                        slice.IsReady &&
                        commandRouter.IsOwnedPan(slice.PanId));
            }
        }
    }

    private async Task<long?> RemoveHiddenRestoredSlicesAsync(
        FlexControlSession control,
        uint primaryPanStreamId,
        CancellationToken cancellationToken)
    {
        int[] hiddenSliceIds;
        long? preferredFrequencyHz;
        lock (m_sliceGate)
        {
            hiddenSliceIds = SelectHiddenRestoredSliceIds(
                m_ownedSlices.Values,
                primaryPanStreamId);
            preferredFrequencyHz = SelectPreferredRestoredFrequencyHz(
                m_ownedSlices.Values,
                primaryPanStreamId);
        }

        foreach (int radioId in hiddenSliceIds)
        {
            FlexCommandResponse response = await control.SendCommandAsync(
                $"slice remove {radioId}",
                CommandTimeout,
                cancellationToken);
            if (!response.IsSuccess)
            {
                logger.LogWarning(
                    "Flex hidden restored slice {SliceId} could not be removed: 0x{Code:x8} {Body}",
                    radioId,
                    response.Code,
                    response.Body);
                continue;
            }

            lock (m_sliceGate)
            {
                m_ownedSlices.Remove(radioId);
                m_webSliceIds.Release(radioId);
            }
            logger.LogInformation(
                "Removed hidden restored RX slice {SliceId} from the web client's audio mix",
                radioId);
        }
        return preferredFrequencyHz;
    }

    internal static int[] SelectHiddenRestoredSliceIds(
        IEnumerable<FlexSliceState> slices,
        uint primaryPanStreamId) =>
        slices
            .Where(
                slice =>
                    slice.IsReady &&
                    slice.PanId != 0 &&
                    slice.PanId != primaryPanStreamId)
            .Select(slice => slice.RadioId)
            .Order()
            .ToArray();

    internal static long? SelectPreferredRestoredFrequencyHz(
        IEnumerable<FlexSliceState> slices,
        uint primaryPanStreamId) =>
        slices
            .Where(
                slice =>
                    slice.IsReady &&
                    slice.PanId != 0 &&
                    slice.PanId != primaryPanStreamId)
            .OrderByDescending(slice => slice.IsActive)
            .ThenBy(slice => slice.RadioId)
            .Select(slice => (long?)slice.FrequencyHz)
            .FirstOrDefault();

    private async Task TryRevealOwnedSliceAsync(
        FlexControlSession control,
        string panId,
        int bandwidthHz,
        CancellationToken cancellationToken)
    {
        long? targetFrequencyHz;
        FlexStatusParser.TryParseFlexUInt(
            panId,
            out uint targetPanStreamId);
        lock (m_sliceGate)
        {
            targetFrequencyHz = m_ownedSlices.Values
                .Where(
                    slice =>
                        slice.IsReady &&
                        slice.PanId == targetPanStreamId)
                .OrderByDescending(slice => slice.IsActive)
                .ThenBy(slice => slice.RadioId)
                .Select(slice => (long?)slice.FrequencyHz)
                .FirstOrDefault();
        }

        if (!targetFrequencyHz.HasValue)
        {
            return;
        }

        long currentCenterHz = commandRouter.PanCenterHz;
        long halfBandwidth = bandwidthHz / 2L;
        if (targetFrequencyHz.Value >= currentCenterHz - halfBandwidth &&
            targetFrequencyHz.Value <= currentCenterHz + halfBandwidth)
        {
            return;
        }

        FlexCommandResponse response = await control.SendCommandAsync(
            FormattableString.Invariant(
                $"display pan set {panId} center={targetFrequencyHz.Value / 1_000_000d:F6}"),
            CommandTimeout,
            cancellationToken);
        if (!response.IsSuccess)
        {
            logger.LogWarning(
                "Flex could not reveal the restored RX slice: 0x{Code:x8} {Body}",
                response.Code,
                response.Body);
            return;
        }

        commandRouter.ObservePanCenter(panId, targetFrequencyHz.Value);
        coordinator.SetPanadapter(
            panId,
            targetFrequencyHz.Value,
            bandwidthHz);
        logger.LogInformation(
            "Flex pan recentered to reveal restored RX slice at {FrequencyMHz:F6} MHz",
            targetFrequencyHz.Value / 1_000_000d);
    }

    private void ObserveStatus(string line, uint clientHandle)
    {
        if (FlexStatusParser.TryParseInterlockStatus(
                line,
                out IReadOnlyDictionary<string, string>? interlockFields))
        {
            ObserveInterlock(interlockFields, clientHandle);
            return;
        }

        if (m_guiClients.Observe(line))
        {
            return;
        }

        if (FlexStatusParser.TryParsePanStatus(
                line,
                out uint statusPanId,
                out IReadOnlyDictionary<string, string>? panFields) &&
            commandRouter.IsOwnedPan(statusPanId) &&
            IsOurOrUnresolvedOwner(panFields, clientHandle))
        {
            RadioSnapshot snapshot = coordinator.Snapshot;
            PanadapterSnapshot? currentPan =
                (snapshot.Panadapters ?? [snapshot.Panadapter])
                    .FirstOrDefault(pan => pan.StreamId == statusPanId);
            if (currentPan is null)
            {
                return;
            }
            long centerHz =
                commandRouter.PanCenterHzFor(statusPanId) > 0
                    ? commandRouter.PanCenterHzFor(statusPanId)
                    : currentPan.CenterFrequencyHz;
            int bandwidthHz = currentPan.BandwidthHz;
            int minDbm = currentPan.MinDbm;
            int maxDbm = currentPan.MaxDbm;
            int fftAverage = currentPan.FftAverage;
            int framesPerSecond = currentPan.FramesPerSecond;
            bool wnbEnabled = currentPan.WnbEnabled;
            int wnbLevel = currentPan.WnbLevel;
            bool changed = false;
            if (TryParseMhz(
                    panFields,
                    "center",
                    100_000,
                    60_000_000,
                    out long parsedCenterHz))
            {
                centerHz = parsedCenterHz;
                commandRouter.ObservePanCenter(currentPan.Id, centerHz);
                changed = true;
            }
            if (TryParseMhz(
                    panFields,
                    "bandwidth",
                    10_000,
                    14_000_000,
                    out long parsedBandwidthHz))
            {
                bandwidthHz = checked((int)parsedBandwidthHz);
                changed = true;
            }
            if (TryParseRoundedDouble(
                    panFields,
                    "min_dbm",
                    -200,
                    0,
                    out int parsedMinDbm))
            {
                minDbm = parsedMinDbm;
                changed = true;
            }
            if (TryParseRoundedDouble(
                    panFields,
                    "max_dbm",
                    -200,
                    0,
                    out int parsedMaxDbm))
            {
                maxDbm = parsedMaxDbm;
                changed = true;
            }
            if (TryParseInt(
                    panFields,
                    "average",
                    0,
                    100,
                    out int parsedAverage))
            {
                fftAverage = parsedAverage;
                changed = true;
            }
            if (TryParseInt(
                    panFields,
                    "fps",
                    1,
                    30,
                    out int parsedFramesPerSecond))
            {
                framesPerSecond = parsedFramesPerSecond;
                changed = true;
            }
            if (TryParseInt(
                    panFields,
                    "wnb",
                    0,
                    1,
                    out int parsedWnbEnabled))
            {
                wnbEnabled = parsedWnbEnabled == 1;
                changed = true;
            }
            if (TryParseInt(
                    panFields,
                    "wnb_level",
                    0,
                    100,
                    out int parsedWnbLevel))
            {
                wnbLevel = parsedWnbLevel;
                changed = true;
            }
            if (changed)
            {
                coordinator.SetPanadapter(
                    currentPan.Id,
                    centerHz,
                    bandwidthHz,
                    minDbm,
                    maxDbm,
                    fftAverage,
                    framesPerSecond,
                    wnbEnabled,
                    wnbLevel);
            }
        }

        Match match = Regex.Match(
            line,
            @"display panafall\s+(?<waterfall>0x[0-9a-fA-F]+).*?\bpanadapter=(?<pan>0x[0-9a-fA-F]+)",
            RegexOptions.CultureInvariant);
        if (match.Success)
        {
            string? panId = NormalizeFlexId(match.Groups["pan"].Value);
            string? waterfallId =
                NormalizeFlexId(match.Groups["waterfall"].Value);
            if (panId is not null && waterfallId is not null)
            {
                m_waterfallsByPan[panId] = waterfallId;
            }
        }

        if (!FlexStatusParser.TryParseSliceStatus(
                line,
                out int radioId,
                out IReadOnlyDictionary<string, string>? fields))
        {
            return;
        }

        SliceSnapshot[]? snapshots = null;
        lock (m_sliceGate)
        {
            bool isKnown = m_ownedSlices.TryGetValue(
                radioId,
                out FlexSliceState? state);
            uint owner = 0;
            bool hasOwner =
                fields.TryGetValue("client_handle", out string? ownerText) &&
                FlexStatusParser.TryParseFlexUInt(ownerText, out owner);
            bool isForeignOwner =
                hasOwner &&
                owner != 0 &&
                owner != clientHandle;
            bool claimsOurClient =
                hasOwner &&
                owner == clientHandle;

            // FLEX firmware can emit a transient client_handle=0 while a
            // slice is being rebound. AetherSDR treats zero as unresolved,
            // not foreign; dropping a known slice here made the web card
            // disappear after otherwise successful control commands.
            if (isForeignOwner)
            {
                if (isKnown)
                {
                    m_ownedSlices.Remove(radioId);
                    m_webSliceIds.Release(radioId);
                    snapshots = SnapshotOwnedSlices();
                }
            }
            else if (fields.TryGetValue("in_use", out string? inUseText) &&
                     inUseText == "0")
            {
                if (isKnown)
                {
                    m_ownedSlices.Remove(radioId);
                    m_webSliceIds.Release(radioId);
                    snapshots = SnapshotOwnedSlices();
                }
            }
            else if (isKnown || claimsOurClient)
            {
                state ??= new FlexSliceState(radioId);
                state.Apply(fields);
                m_ownedSlices[radioId] = state;
                snapshots = SnapshotOwnedSlices();
            }
        }

        if (snapshots is not null)
        {
            coordinator.SetLiveSlices(snapshots);
        }
    }

    private SliceSnapshot[] SnapshotOwnedSlices()
    {
        FlexSliceState[] slices = m_ownedSlices.Values
            .Where(
                slice =>
                    slice.IsReady &&
                    commandRouter.IsOwnedPan(slice.PanId))
            .OrderBy(slice => slice.RadioId)
            .ToArray();
        FlexSliceState? active = slices.FirstOrDefault(
            slice => slice.IsActive);
        if (active is not null)
        {
            m_webSliceIds.GetOrCreate(
                active.RadioId,
                makePrimary: true);
        }

        return slices
            .Select(
                slice => slice.ToSnapshot(
                    m_webSliceIds.GetOrCreate(slice.RadioId)))
            .ToArray();
    }

    private static bool IsOurOrUnresolvedOwner(
        IReadOnlyDictionary<string, string> fields,
        uint clientHandle)
    {
        if (!fields.TryGetValue("client_handle", out string? ownerText))
        {
            return true;
        }

        return FlexStatusParser.TryParseFlexUInt(ownerText, out uint owner) &&
               (owner == 0 || owner == clientHandle);
    }

    private static bool TryParseMhz(
        IReadOnlyDictionary<string, string> fields,
        string key,
        long minimumHz,
        long maximumHz,
        out long valueHz)
    {
        valueHz = 0;
        if (!fields.TryGetValue(key, out string? text) ||
            !double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double valueMhz) ||
            !double.IsFinite(valueMhz))
        {
            return false;
        }

        valueHz = (long)Math.Round(valueMhz * 1_000_000d);
        return valueHz >= minimumHz && valueHz <= maximumHz;
    }

    private static bool TryParseInt(
        IReadOnlyDictionary<string, string> fields,
        string key,
        int minimum,
        int maximum,
        out int value)
    {
        value = 0;
        return fields.TryGetValue(key, out string? text) &&
               int.TryParse(
                   text,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out value) &&
               value >= minimum &&
               value <= maximum;
    }

    private static bool TryParseRoundedDouble(
        IReadOnlyDictionary<string, string> fields,
        string key,
        int minimum,
        int maximum,
        out int value)
    {
        value = 0;
        if (!fields.TryGetValue(key, out string? text) ||
            !double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double parsed) ||
            !double.IsFinite(parsed))
        {
            return false;
        }

        double rounded = Math.Round(parsed);
        if (rounded < minimum || rounded > maximum)
        {
            return false;
        }

        value = checked((int)rounded);
        return true;
    }

    private async Task RunHeartbeatAsync(
        FlexControlSession control,
        uint clientHandle,
        CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            FlexCommandResponse response = await control.SendCommandAsync(
                "ping",
                CommandTimeout,
                cancellationToken);
            if (!response.IsSuccess)
            {
                throw new IOException(
                    $"Flex heartbeat failed with 0x{response.Code:x8}.");
            }
            Interlocked.Exchange(
                ref m_lastHeartbeatUnixMilliseconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            coordinator.ObserveEngineHeartbeat(clientHandle);
            RefreshTxOccupancy();
        }
    }

    private void ResetCurrentTransportIds()
    {
        txOccupancyRegistry.RemoveReporter(
            settings.Value.RadioId,
            settings.Value.SessionId);
        m_interlock.Clear();
        m_guiClients.Clear();
        Interlocked.Exchange(ref m_clientHandle, 0);
        Volatile.Write(ref m_udpPort, 0);
        Interlocked.Exchange(ref m_audioStreamId, 0);
        Interlocked.Exchange(ref m_connectedUnixMilliseconds, 0);
    }

    private void ObserveInterlock(
        IReadOnlyDictionary<string, string> fields,
        uint clientHandle)
    {
        if (!m_interlock.Observe(fields, out _))
        {
            return;
        }
        RefreshTxOccupancy(clientHandle);
    }

    private void RefreshTxOccupancy(uint? knownClientHandle = null)
    {
        FlexInterlockObservation? observation = m_interlock.Current;
        if (observation is null)
        {
            return;
        }

        uint clientHandle = knownClientHandle ?? unchecked((uint)Math.Max(
            0,
            Volatile.Read(ref m_clientHandle)));
        if (clientHandle == 0)
        {
            return;
        }
        txOccupancyRegistry.ObserveInterlock(
            settings.Value.RadioId,
            settings.Value.SessionId,
            clientHandle,
            observation.State,
            observation.TxClientHandle,
            observation.Source,
            m_guiClients.Snapshot(clientHandle));
    }

    private static DateTimeOffset? FromUnixMilliseconds(long value) =>
        value <= 0
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(value);

    private async Task<bool> TryCreateDefaultSliceAsync(
        FlexControlSession control,
        string panId,
        long frequencyHz,
        CancellationToken cancellationToken)
    {
        if (OwnedSliceCount > 0)
        {
            return false;
        }

        FlexCommandResponse createSlice =
            await control.SendCommandAsync(
                FormattableString.Invariant(
                    $"slice create pan={panId} freq={frequencyHz / 1_000_000d:F6} mode=USB"),
                CommandTimeout,
                cancellationToken);
        if (!createSlice.IsSuccess)
        {
            logger.LogWarning(
                "Flex default RX slice creation returned 0x{Code:x8}: {Body}",
                createSlice.Code,
                createSlice.Body);
            return false;
        }

        logger.LogInformation(
            "Flex default RX slice created: {SliceId}",
            createSlice.Body.Trim());
        if (int.TryParse(
                createSlice.Body.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int createdSliceId))
        {
            commandRouter.TrackCreatedSlice(createdSliceId);
        }
        return true;
    }

    private static async Task SendRequiredAsync(
        FlexControlSession control,
        string command,
        CancellationToken cancellationToken)
    {
        FlexCommandResponse response = await control.SendCommandAsync(
            command,
            CommandTimeout,
            cancellationToken);
        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Flex command '{command}' failed with 0x{response.Code:x8}: {response.Body}");
        }
    }

    private async Task SendBestEffortAsync(
        FlexControlSession control,
        string command,
        CancellationToken cancellationToken)
    {
        FlexCommandResponse response = await control.SendCommandAsync(
            command,
            CommandTimeout,
            cancellationToken);
        if (!response.IsSuccess)
        {
            logger.LogDebug(
                "Optional Flex command {Command} returned 0x{Code:x8}: {Body}",
                command,
                response.Code,
                response.Body);
        }
    }

    private async Task RemoveDisplayStreamsAsync(
        FlexControlSession control,
        string? panId,
        string? waterfallId)
    {
        using CancellationTokenSource cleanup =
            new(TimeSpan.FromSeconds(5));
        string[] commands = BuildDisplayRemovalCommands(
            panId,
            waterfallId);
        if (commands.Length == 0)
        {
            return;
        }

        try
        {
            // A panafall owns two radio resources. Queue both commands before
            // awaiting either response, matching RadioModel::removePanadapter
            // and FlexLib 4.2.18. Waiting for the pan response first lets some
            // firmware prune the paired waterfall before its remove arrives.
            FlexCommandResponse[] responses =
                await control.SendCommandsAsync(
                    commands,
                    TimeSpan.FromSeconds(2),
                    cleanup.Token);
            for (int index = 0; index < responses.Length; index++)
            {
                FlexCommandResponse response = responses[index];
                if (!response.IsSuccess)
                {
                    logger.LogWarning(
                        "Flex cleanup command {Command} returned 0x{Code:x8}",
                        commands[index],
                        response.Code);
                }
            }
        }
        catch (Exception exception)
            when (exception is IOException or
                  ObjectDisposedException or
                  OperationCanceledException or
                  TimeoutException)
        {
            logger.LogDebug(
                exception,
                "Flex display cleanup commands could not complete");
        }
    }

    internal static string[] BuildDisplayRemovalCommands(
        string? panId,
        string? waterfallId)
    {
        List<string> commands = [];
        if (!string.IsNullOrWhiteSpace(panId))
        {
            commands.Add($"display pan remove {panId}");
        }

        if (!string.IsNullOrWhiteSpace(waterfallId))
        {
            commands.Add($"display panafall remove {waterfallId}");
        }

        return commands.ToArray();
    }

    internal static string[] BuildRestoredPanActivationCommands(
        IReadOnlyList<PanadapterSnapshot> panadapters,
        int xPixels,
        int yPixels,
        IReadOnlyDictionary<string, int>? framesPerSecondToRestore = null)
    {
        // FLEX firmware 4.2.18 restores GUIClientID-owned display objects
        // without resuming their FFT UDP stream. Re-register only the client
        // surface dimensions; center, bandwidth, levels, FPS, and other live
        // state remain radio-authoritative. The one exception is an FPS value
        // captured immediately before this operator explicitly entered low
        // bandwidth mode: low_bw_connect changes the owned pan's live FPS, so
        // the matching explicit return to normal restores that observed value.
        return panadapters
            .Where(pan => !string.IsNullOrWhiteSpace(pan.Id))
            .Select(
                pan =>
                {
                    string command =
                        $"display pan set {pan.Id} " +
                        $"xpixels={xPixels} ypixels={yPixels}";
                    if (framesPerSecondToRestore?.TryGetValue(
                            pan.Id,
                            out int framesPerSecond) == true &&
                        framesPerSecond is >= 1 and <= 30)
                    {
                        command += $" fps={framesPerSecond}";
                    }
                    return command;
                })
            .ToArray();
    }

    internal static PanadapterSnapshot[] ApplyRestoredDisplayRates(
        IReadOnlyList<PanadapterSnapshot> panadapters,
        IReadOnlyDictionary<string, int>? framesPerSecondToRestore)
    {
        // The activation command can succeed before firmware 4.2.18 echoes
        // the new FPS. Keep the published snapshot aligned with the command
        // that the radio accepted instead of replacing that echo with the
        // stale low-bandwidth value captured during GUI-client restoration.
        return panadapters
            .Select(
                pan =>
                    framesPerSecondToRestore?.TryGetValue(
                        pan.Id,
                        out int framesPerSecond) == true &&
                    framesPerSecond is >= 1 and <= 30
                        ? pan with { FramesPerSecond = framesPerSecond }
                        : pan)
            .ToArray();
    }

    private PanadapterSnapshot[] CreateRestoredPanSnapshots(
        IReadOnlyList<FlexRestoredPanStatus> restoredPans,
        RadioSettings radio,
        int fallbackFramesPerSecond)
    {
        return restoredPans
            .Select(
                restored =>
                {
                    IReadOnlyDictionary<string, string> fields =
                        restored.Fields;
                    long centerFrequencyHz = radio.CenterFrequencyHz;
                    int bandwidthHz = radio.BandwidthHz;
                    int minDbm = radio.MinDbm;
                    int maxDbm = radio.MaxDbm;
                    int fftAverage = 35;
                    int framesPerSecond = fallbackFramesPerSecond;
                    bool wnbEnabled = false;
                    int wnbLevel = 50;
                    if (TryParseMhz(
                            fields,
                            "center",
                            100_000,
                            60_000_000,
                            out long parsedCenterHz))
                    {
                        centerFrequencyHz = parsedCenterHz;
                    }
                    if (TryParseMhz(
                            fields,
                            "bandwidth",
                            10_000,
                            14_000_000,
                            out long parsedBandwidthHz))
                    {
                        bandwidthHz = checked((int)parsedBandwidthHz);
                    }
                    if (TryParseRoundedDouble(
                            fields,
                            "min_dbm",
                            -200,
                            0,
                            out int parsedMinDbm))
                    {
                        minDbm = parsedMinDbm;
                    }
                    if (TryParseRoundedDouble(
                            fields,
                            "max_dbm",
                            -200,
                            0,
                            out int parsedMaxDbm))
                    {
                        maxDbm = parsedMaxDbm;
                    }
                    if (TryParseInt(
                            fields,
                            "average",
                            0,
                            100,
                            out int parsedAverage))
                    {
                        fftAverage = parsedAverage;
                    }
                    if (TryParseInt(
                            fields,
                            "fps",
                            1,
                            30,
                            out int parsedFramesPerSecond))
                    {
                        framesPerSecond = parsedFramesPerSecond;
                    }
                    if (TryParseInt(
                            fields,
                            "wnb",
                            0,
                            1,
                            out int parsedWnbEnabled))
                    {
                        wnbEnabled = parsedWnbEnabled == 1;
                    }
                    if (TryParseInt(
                            fields,
                            "wnb_level",
                            0,
                            100,
                            out int parsedWnbLevel))
                    {
                        wnbLevel = parsedWnbLevel;
                    }

                    string panId = $"0x{restored.StreamId:x8}";
                    m_waterfallsByPan.TryGetValue(
                        panId,
                        out string? restoredWaterfallId);
                    return new PanadapterSnapshot(
                        CenterFrequencyHz: centerFrequencyHz,
                        BandwidthHz: bandwidthHz,
                        MinDbm: minDbm,
                        MaxDbm: maxDbm,
                        FftAverage: fftAverage,
                        FramesPerSecond: framesPerSecond,
                        WnbEnabled: wnbEnabled,
                        WnbLevel: wnbLevel,
                        Id: panId,
                        StreamId: restored.StreamId,
                        WaterfallId: restoredWaterfallId ?? string.Empty);
                })
            .ToArray();
    }

    private async Task TryRemoveAsync(
        FlexControlSession control,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            FlexCommandResponse response = await control.SendCommandAsync(
                command,
                TimeSpan.FromSeconds(2),
                cancellationToken);
            if (!response.IsSuccess)
            {
                logger.LogWarning(
                    "Flex cleanup command {Command} returned 0x{Code:x8}",
                    command,
                    response.Code);
            }
        }
        catch (Exception exception)
            when (exception is IOException or
                  ObjectDisposedException or
                  OperationCanceledException or
                  TimeoutException)
        {
            logger.LogDebug(
                exception,
                "Flex cleanup command {Command} could not complete",
                command);
        }
    }

    internal static (string? PanId, string? WaterfallId)
        ParsePanafallCreateIds(string body)
    {
        Match panKey = Regex.Match(
            body,
            @"(?:^|\s)pan=(?<id>(?:0x)?[0-9a-fA-F]+)",
            RegexOptions.CultureInvariant);
        Match waterfallKey = Regex.Match(
            body,
            @"(?:^|\s)waterfall=(?<id>(?:0x)?[0-9a-fA-F]+)",
            RegexOptions.CultureInvariant);
        if (panKey.Success)
        {
            return (
                NormalizeFlexId(panKey.Groups["id"].Value),
                waterfallKey.Success
                    ? NormalizeFlexId(waterfallKey.Groups["id"].Value)
                    : null);
        }

        string[] parts = body.Trim().Split(
            ',',
            StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries);
        return (
            parts.Length > 0 ? NormalizeFlexId(parts[0]) : null,
            parts.Length > 1 ? NormalizeFlexId(parts[1]) : null);
    }

    private static string? NormalizeFlexId(string value)
    {
        string text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(
            text,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out uint id)
            ? $"0x{id:x8}"
            : null;
    }

    private static bool TryParseStreamId(string body, out uint streamId)
    {
        Match keyed = Regex.Match(
            body,
            @"(?:^|[\s,])(?:stream_id|stream)=(?<id>(?:0x)?[0-9a-fA-F]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string candidate = keyed.Success
            ? keyed.Groups["id"].Value
            : body.Trim().Split(
                [' ', ','],
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        return FlexStatusParser.TryParseFlexUInt(candidate, out streamId);
    }

    private static (string Model, string Serial) ParseRadioIdentity(string body)
    {
        string model = ParseInfoValue(body, "model");
        string serial = ParseInfoValue(body, "serial");
        return (model, serial);
    }

    private static string ParseInfoValue(string body, string key)
    {
        Match match = Regex.Match(
            body,
            $@"(?:^|[,\s]){Regex.Escape(key)}=(?<value>[^,\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success
            ? match.Groups["value"].Value.Trim('"', '\\')
            : string.Empty;
    }

    private static string SanitizeStationName(string stationName)
    {
        string sanitized = Regex.Replace(
            stationName.Trim(),
            @"[^A-Za-z0-9_.-]",
            "-",
            RegexOptions.CultureInvariant);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "AETHER-WEB-RX"
            : sanitized[..Math.Min(sanitized.Length, 32)];
    }

    private static void ValidateSettings(RadioSettings radio)
    {
        if (!IPAddress.TryParse(radio.Host, out IPAddress? address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException(
                "Radio:Host must be an IPv4 address.");
        }

        if (radio.TcpPort is < 1 or > 65_535 ||
            radio.CenterFrequencyHz is < 100_000 or > 60_000_000 ||
            radio.BandwidthHz is < 10_000 or > 14_000_000 ||
            radio.MinDbm >= radio.MaxDbm ||
            radio.XPixels is < 64 or > 8_192 ||
            radio.YPixels is < 2 or > 4_096 ||
            radio.FramesPerSecond is < 1 or > 30 ||
            radio.NetworkMtu is < 512 or > 1_500)
        {
            throw new InvalidOperationException(
                "The configured receive-only Flex display dimensions are invalid.");
        }
    }
}

internal sealed record FlexRestoredPanStatus(
    uint StreamId,
    IReadOnlyDictionary<string, string> Fields);

internal sealed class FlexRestoredPanTracker(uint clientHandle)
{
    private readonly object m_gate = new();
    private readonly Dictionary<
        uint,
        Dictionary<string, string>> m_pans = [];
    private readonly TaskCompletionSource<bool> m_firstPan =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Observe(string line)
    {
        if (!FlexStatusParser.TryParsePanStatus(
                line,
                out uint panId,
                out IReadOnlyDictionary<string, string>? fields) ||
            !fields.TryGetValue(
                "client_handle",
                out string? ownerText) ||
            !FlexStatusParser.TryParseFlexUInt(
                ownerText,
                out uint owner) ||
            owner != clientHandle)
        {
            return;
        }

        lock (m_gate)
        {
            if (fields.TryGetValue("in_use", out string? inUse) &&
                inUse == "0")
            {
                m_pans.Remove(panId);
                return;
            }

            if (!m_pans.TryGetValue(
                    panId,
                    out Dictionary<string, string>? accumulated))
            {
                accumulated = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                m_pans[panId] = accumulated;
            }
            foreach ((string key, string value) in fields)
            {
                accumulated[key] = value;
            }
            m_firstPan.TrySetResult(true);
        }
    }

    public FlexRestoredPanStatus[] Snapshot()
    {
        lock (m_gate)
        {
            return m_pans
                .OrderBy(pair => pair.Key)
                .Select(
                    pair => new FlexRestoredPanStatus(
                        pair.Key,
                        new Dictionary<string, string>(
                            pair.Value,
                            StringComparer.OrdinalIgnoreCase)))
                .ToArray();
        }
    }

    public async Task<FlexRestoredPanStatus[]> WaitForSnapshotAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (Snapshot().Length == 0)
        {
            Task delay = Task.Delay(timeout, cancellationToken);
            Task completed = await Task.WhenAny(m_firstPan.Task, delay);
            if (ReferenceEquals(completed, delay))
            {
                await delay;
            }
        }

        if (m_firstPan.Task.IsCompleted)
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(50),
                cancellationToken);
        }
        return Snapshot();
    }
}

internal sealed class AudioStreamRegistration
{
    private int m_streamId;

    public uint StreamId
    {
        get => unchecked((uint)Volatile.Read(ref m_streamId));
        set => Volatile.Write(ref m_streamId, unchecked((int)value));
    }
}

internal sealed class WebSliceIdAllocator
{
    private readonly Dictionary<int, string> m_ids = [];
    private bool m_frozen;

    public string GetOrCreate(int radioId, bool makePrimary = false)
    {
        if (!m_ids.TryGetValue(radioId, out string? existing))
        {
            HashSet<string> used = m_ids.Values.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            for (char letter = 'A'; letter <= 'Z'; letter++)
            {
                string candidate = letter.ToString();
                if (used.Add(candidate))
                {
                    existing = candidate;
                    m_ids[radioId] = candidate;
                    break;
                }
            }

            if (existing is null)
            {
                existing = $"S{radioId}";
                m_ids[radioId] = existing;
            }
        }

        if (makePrimary &&
            !m_frozen &&
            !string.Equals(existing, "A", StringComparison.Ordinal))
        {
            int currentPrimaryRadioId = m_ids
                .Where(pair => pair.Value == "A")
                .Select(pair => pair.Key)
                .DefaultIfEmpty(-1)
                .Single();
            if (currentPrimaryRadioId >= 0)
            {
                m_ids[currentPrimaryRadioId] = existing;
            }
            m_ids[radioId] = "A";
            existing = "A";
        }

        return existing;
    }

    public void Release(int radioId) => m_ids.Remove(radioId);

    public void Freeze() => m_frozen = true;

    public void Reset()
    {
        m_ids.Clear();
        m_frozen = false;
    }
}

internal sealed class FlexSliceState(int radioId)
{
    public int RadioId { get; } = radioId;
    public bool IsReady => FrequencyHz > 0;
    public uint PanId { get; private set; }
    public long FrequencyHz { get; private set; }
    public string Mode { get; private set; } = "USB";
    public int FilterLowHz { get; private set; } = 300;
    public int FilterHighHz { get; private set; } = 3_000;
    public int AfGain { get; private set; } = 50;
    public int Squelch { get; private set; }
    public bool SquelchEnabled { get; private set; }
    public int AudioPan { get; private set; } = 50;
    public string AgcMode { get; private set; } = "MED";
    public int AgcThreshold { get; private set; } = 65;
    public string RxAntenna { get; private set; } = "ANT1";
    public bool Nb { get; private set; }
    public bool Nr { get; private set; }
    public bool Anf { get; private set; }
    public bool Nrl { get; private set; }
    public bool Nrs { get; private set; }
    public bool Rnn { get; private set; }
    public bool Nrf { get; private set; }
    public bool Anfl { get; private set; }
    public bool Anft { get; private set; }
    public int NbLevel { get; private set; } = 50;
    public int NrLevel { get; private set; }
    public int AnfLevel { get; private set; }
    public int NrlLevel { get; private set; } = 50;
    public int NrsLevel { get; private set; } = 50;
    public int NrfLevel { get; private set; } = 50;
    public int AnflLevel { get; private set; } = 50;
    public int DaxChannel { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsTx { get; private set; }
    public bool IsMuted { get; private set; }

    public void Apply(IReadOnlyDictionary<string, string> fields)
    {
        if (fields.TryGetValue("RF_frequency", out string? frequencyText) &&
            double.TryParse(
                frequencyText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double frequencyMhz))
        {
            FrequencyHz = (long)Math.Round(frequencyMhz * 1_000_000d);
        }
        if (fields.TryGetValue("pan", out string? panText) &&
            FlexStatusParser.TryParseFlexUInt(panText, out uint panId))
        {
            PanId = panId;
        }

        if (fields.TryGetValue("mode", out string? mode) &&
            !string.IsNullOrWhiteSpace(mode))
        {
            Mode = mode.ToUpperInvariant();
        }

        if (TryReadInt(fields, "filter_lo", out int filterLow))
        {
            FilterLowHz = filterLow;
        }
        if (TryReadInt(fields, "filter_hi", out int filterHigh))
        {
            FilterHighHz = filterHigh;
        }
        if (TryReadInt(fields, "audio_level", out int afGain))
        {
            AfGain = Math.Clamp(afGain, 0, 100);
        }
        if (TryReadInt(fields, "squelch_level", out int squelch))
        {
            Squelch = Math.Clamp(squelch, 0, 100);
        }
        if (TryReadBool(fields, "squelch", out bool squelchEnabled))
        {
            SquelchEnabled = squelchEnabled;
        }
        if (TryReadInt(fields, "audio_pan", out int audioPan))
        {
            AudioPan = Math.Clamp(audioPan, 0, 100);
        }
        if (fields.TryGetValue("agc_mode", out string? agcMode) &&
            !string.IsNullOrWhiteSpace(agcMode))
        {
            AgcMode = agcMode.Trim().ToUpperInvariant();
        }
        if (TryReadInt(fields, "agc_threshold", out int agcThreshold))
        {
            AgcThreshold = Math.Clamp(agcThreshold, 0, 100);
        }
        if (fields.TryGetValue("rxant", out string? rxAntenna) &&
            !string.IsNullOrWhiteSpace(rxAntenna))
        {
            RxAntenna = rxAntenna.Trim().ToUpperInvariant();
        }
        if (TryReadBool(fields, "nb", out bool nb))
        {
            Nb = nb;
        }
        if (TryReadBool(fields, "nr", out bool nr))
        {
            Nr = nr;
        }
        if (TryReadBool(fields, "anf", out bool anf))
        {
            Anf = anf;
        }
        if (TryReadBool(fields, "nrl", out bool nrl))
        {
            Nrl = nrl;
        }
        if (TryReadBool(fields, "nrs", out bool nrs))
        {
            Nrs = nrs;
        }
        if (TryReadBool(fields, "rnn", out bool rnn))
        {
            Rnn = rnn;
        }
        if (TryReadBool(fields, "nrf", out bool nrf))
        {
            Nrf = nrf;
        }
        if (TryReadBool(fields, "anfl", out bool anfl))
        {
            Anfl = anfl;
        }
        if (TryReadBool(fields, "anft", out bool anft))
        {
            Anft = anft;
        }
        if (TryReadInt(fields, "nb_level", out int nbLevel))
        {
            NbLevel = Math.Clamp(nbLevel, 0, 100);
        }
        if (TryReadInt(fields, "nr_level", out int nrLevel))
        {
            NrLevel = Math.Clamp(nrLevel, 0, 100);
        }
        if (TryReadInt(fields, "anf_level", out int anfLevel))
        {
            AnfLevel = Math.Clamp(anfLevel, 0, 100);
        }
        if (TryReadInt(fields, "lms_nr_level", out int nrlLevel))
        {
            NrlLevel = Math.Clamp(nrlLevel, 0, 100);
        }
        if (TryReadInt(fields, "speex_nr_level", out int nrsLevel))
        {
            NrsLevel = Math.Clamp(nrsLevel, 0, 100);
        }
        if (TryReadInt(fields, "nrf_level", out int nrfLevel))
        {
            NrfLevel = Math.Clamp(nrfLevel, 0, 100);
        }
        if (TryReadInt(fields, "lms_anf_level", out int anflLevel))
        {
            AnflLevel = Math.Clamp(anflLevel, 0, 100);
        }
        if (TryReadInt(fields, "dax", out int daxChannel))
        {
            DaxChannel = Math.Clamp(daxChannel, 0, 8);
        }
        if (TryReadBool(fields, "active", out bool active))
        {
            IsActive = active;
        }
        if (TryReadBool(fields, "tx", out bool tx))
        {
            IsTx = tx;
        }
        if (TryReadBool(fields, "audio_mute", out bool muted))
        {
            IsMuted = muted;
        }
    }

    public SliceSnapshot ToSnapshot(string webId)
    {
        return new SliceSnapshot(
            webId,
            FrequencyHz,
            Mode,
            FilterLowHz,
            FilterHighHz,
            AfGain,
            Squelch,
            IsActive,
            IsTx,
            RadioId,
            IsMuted,
            SquelchEnabled,
            AudioPan,
            AgcMode,
            AgcThreshold,
            RxAntenna,
            Nb,
            Nr,
            Anf,
            Nrl,
            Nrs,
            Rnn,
            Nrf,
            Anfl,
            Anft,
            NbLevel,
            NrLevel,
            AnfLevel,
            NrlLevel,
            NrsLevel,
            NrfLevel,
            AnflLevel,
            DaxChannel,
            PanId);
    }

    private static bool TryReadInt(
        IReadOnlyDictionary<string, string> fields,
        string key,
        out int value)
    {
        value = 0;
        return fields.TryGetValue(key, out string? text) &&
               int.TryParse(
                   text,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryReadBool(
        IReadOnlyDictionary<string, string> fields,
        string key,
        out bool value)
    {
        value = false;
        if (!fields.TryGetValue(key, out string? text))
        {
            return false;
        }

        if (text == "1" ||
            bool.TryParse(text, out value) && value)
        {
            value = true;
            return true;
        }

        if (text == "0" ||
            bool.TryParse(text, out value) && !value)
        {
            value = false;
            return true;
        }

        return false;
    }
}

public static class FlexStatusParser
{
    private static readonly Regex SliceStatus = new(
        @"^slice\s+(?<id>\d+)(?:\s+(?<fields>.*))?$",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex PanStatus = new(
        @"^display\s+pan\s+(?<id>(?:0x)?[0-9a-fA-F]+)(?:\s+(?<fields>.*))?$",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex ClientStatus = new(
        @"^client\s+(?<id>(?:0x)?[0-9a-fA-F]+)" +
        @"(?:\s+(?<action>connected|disconnected))?" +
        @"(?:\s+(?<fields>.*))?$",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex InterlockStatus = new(
        @"^interlock(?:\s+(?<fields>(?:[A-Za-z0-9_]+=).*))?$",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex KeyValue = new(
        @"(?<key>[A-Za-z0-9_]+)=(?<value>""(?:\\.|[^""])*""|\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParseSliceStatus(
        string line,
        out int radioId,
        out IReadOnlyDictionary<string, string> fields)
    {
        radioId = -1;
        fields = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        int separator = line.IndexOf('|');
        if (separator < 0 || separator == line.Length - 1)
        {
            return false;
        }

        Match slice = SliceStatus.Match(line[(separator + 1)..].Trim());
        if (!slice.Success ||
            !int.TryParse(
                slice.Groups["id"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out radioId))
        {
            return false;
        }

        fields = ParseFields(slice.Groups["fields"].Value);
        return true;
    }

    public static bool TryParsePanStatus(
        string line,
        out uint panId,
        out IReadOnlyDictionary<string, string> fields)
    {
        panId = 0;
        fields = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        int separator = line.IndexOf('|');
        if (separator < 0 || separator == line.Length - 1)
        {
            return false;
        }

        Match pan = PanStatus.Match(line[(separator + 1)..].Trim());
        if (!pan.Success ||
            !TryParseFlexUInt(pan.Groups["id"].Value, out panId))
        {
            return false;
        }

        fields = ParseFields(pan.Groups["fields"].Value);
        return true;
    }

    public static bool TryParseInterlockStatus(
        string line,
        out IReadOnlyDictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        int separator = line.IndexOf('|');
        if (separator < 0 || separator == line.Length - 1)
        {
            return false;
        }

        Match interlock = InterlockStatus.Match(
            line[(separator + 1)..].Trim());
        if (!interlock.Success)
        {
            return false;
        }

        fields = ParseFields(interlock.Groups["fields"].Value);
        return fields.Count > 0;
    }

    public static bool TryParseClientStatus(
        string line,
        out uint clientHandle,
        out string action,
        out IReadOnlyDictionary<string, string> fields)
    {
        clientHandle = 0;
        action = string.Empty;
        fields = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        int separator = line.IndexOf('|');
        if (separator < 0 || separator == line.Length - 1)
        {
            return false;
        }

        Match client = ClientStatus.Match(
            line[(separator + 1)..].Trim());
        if (!client.Success ||
            !TryParseFlexUInt(
                client.Groups["id"].Value,
                out clientHandle) ||
            clientHandle == 0)
        {
            return false;
        }

        action = client.Groups["action"].Value.ToLowerInvariant();
        fields = ParseFields(client.Groups["fields"].Value);
        return true;
    }

    public static bool TryParseFlexUInt(string value, out uint parsed)
    {
        string text = value.Trim().Trim('"');
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        return uint.TryParse(
            text,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out parsed);
    }

    private static IReadOnlyDictionary<string, string> ParseFields(
        string text)
    {
        Dictionary<string, string> parsed =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (Match keyValue in KeyValue.Matches(text))
        {
            parsed[keyValue.Groups["key"].Value] =
                keyValue.Groups["value"].Value
                    .Trim('"')
                    .Replace("\\\"", "\"");
        }
        return parsed;
    }
}

public sealed record FlexAudioFrame(uint StreamId, short[] Samples);

public sealed class FlexVitaAudioDecoder
{
    public const int SampleRate = 24_000;
    public const ushort FloatStereoPacketClassCode = 0x03E3;
    public const ushort ReducedBandwidthPacketClassCode = 0x0123;
    private const int VitaHeaderSize = 28;

    public bool TryDecode(
        ReadOnlySpan<byte> datagram,
        uint expectedStreamId,
        out FlexAudioFrame? frame)
    {
        frame = null;
        if (!FlexVitaFftDecoder.TryReadPacketIdentity(
                datagram,
                out uint streamId,
                out ushort packetClassCode) ||
            streamId != expectedStreamId ||
            packetClassCode is not
                (FloatStereoPacketClassCode or
                 ReducedBandwidthPacketClassCode))
        {
            return false;
        }

        uint word0 = BinaryPrimitives.ReadUInt32BigEndian(datagram);
        bool hasTrailer = (word0 & 0x04000000u) != 0;
        int declaredBytes = checked((int)(word0 & 0xFFFFu) * 4);
        int packetBytes = declaredBytes == 0
            ? datagram.Length
            : declaredBytes;
        if (packetBytes > datagram.Length ||
            packetBytes < VitaHeaderSize + (hasTrailer ? sizeof(uint) : 0))
        {
            return false;
        }

        int payloadBytes =
            packetBytes - VitaHeaderSize - (hasTrailer ? sizeof(uint) : 0);
        ReadOnlySpan<byte> payload =
            datagram.Slice(VitaHeaderSize, payloadBytes);
        short[] samples;

        if (packetClassCode == FloatStereoPacketClassCode)
        {
            int floatCount = payload.Length / sizeof(float);
            floatCount -= floatCount % 2;
            if (floatCount < 2)
            {
                return false;
            }

            samples = new short[floatCount];
            for (int index = 0; index < floatCount; index++)
            {
                int bits = BinaryPrimitives.ReadInt32BigEndian(
                    payload[(index * sizeof(float))..]);
                float value = BitConverter.Int32BitsToSingle(bits);
                if (!float.IsFinite(value))
                {
                    value = 0;
                }
                samples[index] = (short)Math.Clamp(
                    (int)Math.Round(Math.Clamp(value, -1f, 1f) * 32767f),
                    short.MinValue,
                    short.MaxValue);
            }
        }
        else
        {
            int monoCount = payload.Length / sizeof(short);
            if (monoCount < 1)
            {
                return false;
            }

            samples = new short[monoCount * 2];
            for (int index = 0; index < monoCount; index++)
            {
                short value = BinaryPrimitives.ReadInt16BigEndian(
                    payload[(index * sizeof(short))..]);
                samples[index * 2] = value;
                samples[(index * 2) + 1] = value;
            }
        }

        frame = new FlexAudioFrame(streamId, samples);
        return true;
    }
}

public static class AudioFrameCodec
{
    public const int HeaderSize = 16;

    public static byte[] Encode(
        ReadOnlySpan<short> samples,
        uint sequence,
        int sampleRate)
    {
        if (samples.Length < 2 || samples.Length % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(samples),
                "Audio must contain interleaved stereo samples.");
        }
        if (sampleRate is < 8_000 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        int frameCount = samples.Length / 2;
        byte[] frame =
            new byte[HeaderSize + (samples.Length * sizeof(short))];
        frame[0] = (byte)'A';
        frame[1] = (byte)'E';
        frame[2] = (byte)'T';
        frame[3] = (byte)'A';
        frame[4] = 0;
        frame[5] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(
            frame.AsSpan(6, sizeof(ushort)),
            (ushort)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(8, sizeof(uint)),
            sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(
            frame.AsSpan(12, sizeof(uint)),
            (uint)frameCount);

        for (int index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                frame.AsSpan(
                    HeaderSize + (index * sizeof(short)),
                    sizeof(short)),
                samples[index]);
        }

        return frame;
    }
}

public sealed record FlexFftFrame(
    uint StreamId,
    uint FrameIndex,
    float[] Bins,
    ushort RawMin,
    ushort RawMax,
    int EffectiveYPixels);

public sealed class FlexVitaFftDecoder(
    int minDbm = -130,
    int maxDbm = -40,
    int yPixels = 700)
{
    public const ushort FftPacketClassCode = 0x8003;
    public const ushort WaterfallPacketClassCode = 0x8004;
    private const int VitaHeaderSize = 28;
    private const int FftSubheaderSize = 12;
    private readonly Dictionary<uint, AssemblyState> m_frames = [];
    private int m_minDbm = minDbm;
    private int m_maxDbm = maxDbm;

    public void SetDbmRange(int minimumDbm, int maximumDbm)
    {
        if (minimumDbm is < -200 or > 0 ||
            maximumDbm is < -200 or > 0 ||
            minimumDbm >= maximumDbm)
        {
            return;
        }

        Volatile.Write(ref m_minDbm, minimumDbm);
        Volatile.Write(ref m_maxDbm, maximumDbm);
    }

    public bool TryDecode(
        ReadOnlySpan<byte> datagram,
        out FlexFftFrame? frame)
    {
        frame = null;
        if (!TryReadPacketIdentity(
                datagram,
                out uint streamId,
                out ushort packetClassCode) ||
            packetClassCode != FftPacketClassCode ||
            datagram.Length < VitaHeaderSize + FftSubheaderSize)
        {
            return false;
        }

        uint word0 = BinaryPrimitives.ReadUInt32BigEndian(datagram);
        bool hasTrailer = (word0 & 0x04000000u) != 0;
        int declaredBytes = checked((int)(word0 & 0xFFFFu) * 4);
        if (declaredBytes > 0 && declaredBytes > datagram.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> subheader =
            datagram.Slice(VitaHeaderSize, FftSubheaderSize);
        ushort startBin =
            BinaryPrimitives.ReadUInt16BigEndian(subheader);
        ushort numBins =
            BinaryPrimitives.ReadUInt16BigEndian(subheader[2..]);
        ushort binSize =
            BinaryPrimitives.ReadUInt16BigEndian(subheader[4..]);
        ushort totalBins =
            BinaryPrimitives.ReadUInt16BigEndian(subheader[6..]);
        uint frameIndex =
            BinaryPrimitives.ReadUInt32BigEndian(subheader[8..]);

        if (numBins == 0 ||
            binSize != sizeof(ushort) ||
            totalBins is < 64 or > 8_192 ||
            startBin > totalBins ||
            numBins > totalBins - startBin)
        {
            return false;
        }

        int payloadOffset = VitaHeaderSize + FftSubheaderSize;
        int trailerBytes = hasTrailer ? sizeof(uint) : 0;
        int requiredBytes = checked(numBins * binSize);
        if (datagram.Length - payloadOffset - trailerBytes < requiredBytes)
        {
            return false;
        }

        if (!m_frames.TryGetValue(streamId, out AssemblyState? state) ||
            state.FrameIndex != frameIndex ||
            state.Bins.Length != totalBins)
        {
            if (m_frames.Count >= 8 && !m_frames.ContainsKey(streamId))
            {
                m_frames.Remove(m_frames.Keys.First());
            }

            state = new AssemblyState(frameIndex, totalBins);
            m_frames[streamId] = state;
        }

        if (state.Completed)
        {
            return false;
        }

        ReadOnlySpan<byte> binData = datagram[payloadOffset..];
        for (int index = 0; index < numBins; index++)
        {
            int destination = startBin + index;
            state.Bins[destination] =
                BinaryPrimitives.ReadUInt16BigEndian(
                    binData[(index * sizeof(ushort))..]);
            if (!state.Received[destination])
            {
                state.Received[destination] = true;
                state.ReceivedCount++;
            }
        }

        if (state.ReceivedCount != state.Bins.Length)
        {
            return false;
        }

        ushort rawMin = ushort.MaxValue;
        ushort rawMax = ushort.MinValue;
        int overRangeCount = 0;
        int effectiveYPixels = Math.Max(yPixels, 2);
        foreach (ushort rawBin in state.Bins)
        {
            rawMin = Math.Min(rawMin, rawBin);
            rawMax = Math.Max(rawMax, rawBin);
            if (rawBin >= effectiveYPixels)
            {
                overRangeCount++;
            }
        }

        // Match AetherSDR's guard for firmware/status races: if a substantial
        // portion of a frame exceeds the reported height, scale against the
        // frame's observed pixel space instead of flattening it at min dBm.
        if (overRangeCount > Math.Max(8, state.Bins.Length / 8))
        {
            effectiveYPixels = Math.Max(effectiveYPixels, rawMax + 1);
        }

        float[] dbmBins = new float[state.Bins.Length];
        int activeMinDbm = Volatile.Read(ref m_minDbm);
        int activeMaxDbm = Volatile.Read(ref m_maxDbm);
        float range = activeMaxDbm - activeMinDbm;
        float pixelLimit = effectiveYPixels;
        for (int index = 0; index < state.Bins.Length; index++)
        {
            float pixel = Math.Clamp(
                state.Bins[index],
                0,
                pixelLimit - 1);
            dbmBins[index] = Math.Clamp(
                activeMaxDbm - ((pixel / (pixelLimit - 1)) * range),
                activeMinDbm,
                activeMaxDbm);
        }

        state.Completed = true;
        frame = new FlexFftFrame(
            streamId,
            frameIndex,
            dbmBins,
            rawMin,
            rawMax,
            effectiveYPixels);
        return true;
    }

    public static bool TryReadPacketIdentity(
        ReadOnlySpan<byte> datagram,
        out uint streamId,
        out ushort packetClassCode)
    {
        streamId = 0;
        packetClassCode = 0;
        if (datagram.Length < VitaHeaderSize)
        {
            return false;
        }

        streamId = BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]);
        packetClassCode = (ushort)(
            BinaryPrimitives.ReadUInt32BigEndian(datagram[12..]) &
            0xFFFFu);
        return true;
    }

    private sealed class AssemblyState(uint frameIndex, int binCount)
    {
        public uint FrameIndex { get; } = frameIndex;
        public ushort[] Bins { get; } = new ushort[binCount];
        public bool[] Received { get; } = new bool[binCount];
        public int ReceivedCount { get; set; }
        public bool Completed { get; set; }
    }
}

internal sealed record FlexCommandResponse(uint Code, string Body)
{
    public bool IsSuccess => Code == 0;
}

internal sealed class FlexGuiClientRejectedException(
    uint code,
    string responseBody)
    : InvalidOperationException(
        $"Flex GUI registration failed with 0x{code:x8}: {responseBody}")
{
    public uint Code { get; } = code;
}

internal sealed class FlexControlSession(ILogger logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<
        uint,
        TaskCompletionSource<FlexCommandResponse>> m_pending = new();
    private readonly SemaphoreSlim m_writeGate = new(1, 1);
    private readonly CancellationTokenSource m_lifetime = new();
    private readonly TaskCompletionSource<uint> m_handle =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource m_completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpClient? m_client;
    private StreamReader? m_reader;
    private StreamWriter? m_writer;
    private Task? m_readTask;
    private uint m_sequence;

    public event Action<string>? StatusReceived;
    public Task Completion => m_completion.Task;

    public async Task ConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        m_client = new TcpClient(AddressFamily.InterNetwork)
        {
            NoDelay = true
        };
        await m_client.ConnectAsync(address, port, cancellationToken);
        NetworkStream stream = m_client.GetStream();
        m_reader = new StreamReader(
            stream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        m_writer = new StreamWriter(
            stream,
            Encoding.ASCII,
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        m_readTask = ReadLoopAsync(m_lifetime.Token);
    }

    public async Task<uint> WaitForHandleAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        await m_handle.Task.WaitAsync(timeout, cancellationToken);

    public async Task<FlexCommandResponse> SendCommandAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        FlexCommandResponse[] responses = await SendCommandsAsync(
            [command],
            timeout,
            cancellationToken);
        return responses[0];
    }

    public async Task<FlexCommandResponse[]> SendCommandsAsync(
        IReadOnlyList<string> commands,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (commands.Count == 0)
        {
            return [];
        }

        StreamWriter writer = m_writer ??
            throw new InvalidOperationException(
                "The Flex control session is not connected.");
        List<PendingCommand> pending = new(commands.Count);

        try
        {
            await m_writeGate.WaitAsync(cancellationToken);
            try
            {
                foreach (string command in commands)
                {
                    uint sequence = unchecked(++m_sequence);
                    if (sequence == 0)
                    {
                        sequence = unchecked(++m_sequence);
                    }

                    TaskCompletionSource<FlexCommandResponse> response =
                        new(TaskCreationOptions.RunContinuationsAsynchronously);
                    PendingCommand queued =
                        new(sequence, command, response);
                    pending.Add(queued);
                    if (!m_pending.TryAdd(sequence, response))
                    {
                        throw new InvalidOperationException(
                            "Could not allocate a Flex command sequence.");
                    }
                }

                foreach (PendingCommand queued in pending)
                {
                    await writer.WriteLineAsync(
                        $"C{queued.Sequence}|{queued.Command}".AsMemory(),
                        cancellationToken);
                }
                await writer.FlushAsync(cancellationToken);
            }
            finally
            {
                m_writeGate.Release();
            }

            Task<FlexCommandResponse>[] responseTasks = pending
                .Select(queued =>
                    queued.Response.Task.WaitAsync(timeout, cancellationToken))
                .ToArray();
            return await Task.WhenAll(responseTasks);
        }
        finally
        {
            foreach (PendingCommand queued in pending)
            {
                m_pending.TryRemove(queued.Sequence, out _);
            }
        }
    }

    private sealed record PendingCommand(
        uint Sequence,
        string Command,
        TaskCompletionSource<FlexCommandResponse> Response);

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await m_reader!.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    throw new IOException("The Flex TCP connection closed.");
                }

                if (line.StartsWith('H') &&
                    uint.TryParse(
                        line.AsSpan(1),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out uint handle))
                {
                    m_handle.TrySetResult(handle);
                    continue;
                }

                if (line.StartsWith('S'))
                {
                    StatusReceived?.Invoke(line);
                    continue;
                }

                if (!line.StartsWith('R'))
                {
                    continue;
                }

                int firstSeparator = line.IndexOf('|');
                int secondSeparator = firstSeparator < 0
                    ? -1
                    : line.IndexOf('|', firstSeparator + 1);
                if (firstSeparator <= 1 || secondSeparator < 0 ||
                    !uint.TryParse(
                        line.AsSpan(1, firstSeparator - 1),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out uint sequence) ||
                    !uint.TryParse(
                        line.AsSpan(
                            firstSeparator + 1,
                            secondSeparator - firstSeparator - 1),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out uint code))
                {
                    continue;
                }

                if (m_pending.TryRemove(
                        sequence,
                        out TaskCompletionSource<FlexCommandResponse>? pending))
                {
                    pending.TrySetResult(
                        new FlexCommandResponse(
                            code,
                            line[(secondSeparator + 1)..]));
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            logger.LogDebug(exception, "Flex TCP reader stopped");
        }
        finally
        {
            Exception completionException =
                failure ?? new IOException("The Flex TCP session ended.");
            foreach (TaskCompletionSource<FlexCommandResponse> pending
                     in m_pending.Values)
            {
                pending.TrySetException(completionException);
            }

            m_handle.TrySetException(completionException);
            m_completion.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        m_lifetime.Cancel();
        m_client?.Close();
        if (m_readTask is not null)
        {
            try
            {
                await m_readTask;
            }
            catch (Exception)
            {
            }
        }

        m_reader?.Dispose();
        m_writer?.Dispose();
        m_client?.Dispose();
        m_writeGate.Dispose();
        m_lifetime.Dispose();
    }
}
