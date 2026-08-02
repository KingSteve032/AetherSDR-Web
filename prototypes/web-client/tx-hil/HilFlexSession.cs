using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AetherSDR.Web.Radio;
using Microsoft.Extensions.Logging;

namespace AetherSDR.TxHil;

internal sealed record HilOwnedRadioResources(
    string PanId,
    string WaterfallId,
    int SliceId,
    FlexRadioCommandRouter Router,
    IStationTxCommandTransport Transport);

internal sealed record HilTransmitSettings(
    int RfPower,
    bool DaxEnabled,
    string MicSelection,
    bool VoxEnabled);

internal sealed record HilRadioSnapshot(
    uint ClientHandle,
    string Model,
    string Serial,
    RadioTxOccupancySnapshot TxOccupancy,
    IReadOnlyList<RadioGuiClientDiagnostics> GuiClients,
    int? RfPower,
    HilTransmitSettings? TransmitSettings,
    HilCwxSnapshot Cwx,
    IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> Slices)
{
    public IReadOnlyList<RadioGuiClientDiagnostics> ExternalGuiClients =>
        GuiClients.Where(client => !client.IsThisSession).ToArray();
}

internal sealed class HilFlexSession : IAsyncDisposable, IHilCwxRadio
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex InfoField = new(
        @"(?:^|[,\s])(?<key>[A-Za-z0-9_]+)=(?<value>[^,\s]+)",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex TopLevelTransmit = new(
        @"^transmit(?:\s+(?<fields>(?:[A-Za-z0-9_]+=).*))?$",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex TopLevelCwx = new(
        @"^cwx(?:\s+(?<fields>(?:[A-Za-z0-9_]+=).*))?$",
        RegexOptions.Compiled |
        RegexOptions.CultureInvariant |
        RegexOptions.IgnoreCase);
    private static readonly Regex KeyValue = new(
        @"(?<key>[A-Za-z0-9_]+)=(?<value>""(?:\\.|[^""])*""|\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ILogger m_logger;
    private readonly FlexControlSession m_control;
    private readonly FlexGuiClientRoster m_roster = new();
    private readonly FlexInterlockTracker m_interlock = new();
    private readonly RadioTxOccupancyRegistry m_occupancy = new();
    private readonly HilCwxStatusTracker m_cwx = new();
    private readonly ConcurrentDictionary<
        int,
        IReadOnlyDictionary<string, string>> m_slices = new();
    private readonly object m_transmitGate = new();
    private readonly Dictionary<string, string> m_transmit =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource m_lifetime = new();
    private readonly string m_radioId;
    private readonly string m_reporterId =
        $"tx-hil-{Guid.NewGuid():N}";

    private Task? m_refreshTask;
    private uint m_clientHandle;
    private string m_model = string.Empty;
    private string m_serial = string.Empty;
    private bool m_guiRegistered;
    private int m_disposed;

    public HilFlexSession(
        string radioId,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radioId);
        m_radioId = radioId.Trim().ToUpperInvariant();
        m_logger = logger ?? throw new ArgumentNullException(nameof(logger));
        m_control = new FlexControlSession(logger);
    }

    public uint ClientHandle => m_clientHandle;
    public bool GuiRegistered => m_guiRegistered;
    public bool IsConnected =>
        m_clientHandle != 0 &&
        Volatile.Read(ref m_disposed) == 0 &&
        !m_control.Completion.IsCompleted;
    public RadioTxOccupancyRegistry OccupancyRegistry => m_occupancy;

    public async Task ConnectAsync(
        string host,
        int port,
        bool registerGui,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out IPAddress? address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new InvalidOperationException(
                "The HIL radio host must be an IPv4 address.");
        }

        m_control.StatusReceived += ObserveStatus;
        await m_control.ConnectAsync(address, port, cancellationToken);
        m_clientHandle = await m_control.WaitForHandleAsync(
            TimeSpan.FromSeconds(5),
            cancellationToken);

        if (registerGui)
        {
            await SendBestEffortAsync(
                "client program AetherSDR",
                cancellationToken);
            string guiClientId = Guid.NewGuid().ToString();
            await SendRequiredAsync(
                $"client gui {guiClientId}",
                cancellationToken);
            await SendBestEffortAsync(
                "client station AETHER-TX-HIL",
                cancellationToken);
            m_guiRegistered = true;
        }

        await SendRequiredAsync("sub radio all", cancellationToken);
        await SendRequiredAsync("sub client all", cancellationToken);
        await SendRequiredAsync("sub tx all", cancellationToken);
        await SendRequiredAsync("sub slice all", cancellationToken);
        await SendRequiredAsync("sub cwx all", cancellationToken);

        FlexCommandResponse info = await m_control.SendCommandAsync(
            "info",
            CommandTimeout,
            cancellationToken);
        if (!info.IsSuccess)
        {
            throw new InvalidOperationException(
                $"FLEX info failed with 0x{info.Code:x8}: {info.Body}");
        }
        IReadOnlyDictionary<string, string> identity = ParseInfo(info.Body);
        m_model = identity.TryGetValue("model", out string? model)
            ? model
            : string.Empty;
        m_serial = identity.TryGetValue("serial", out string? serial)
            ? serial
            : identity.TryGetValue(
                "chassis_serial",
                out string? chassisSerial)
                ? chassisSerial
                : string.Empty;

        m_refreshTask = RefreshOccupancyAsync(m_lifetime.Token);
        await WaitForAsync(
            snapshot =>
                snapshot.TxOccupancy.ObservedAt is not null &&
                (!registerGui || snapshot.GuiClients.Any(
                    client => client.ClientHandle == m_clientHandle)),
            TimeSpan.FromSeconds(5),
            cancellationToken);
    }

    public HilRadioSnapshot Snapshot()
    {
        IReadOnlyList<RadioGuiClientDiagnostics> clients =
            m_roster.Snapshot(m_clientHandle);
        RefreshOccupancy(clients);
        int? rfPower = null;
        HilTransmitSettings? transmitSettings = null;
        lock (m_transmitGate)
        {
            if (TryReadTransmitSettings(m_transmit, out HilTransmitSettings? parsed))
            {
                transmitSettings = parsed!;
                rfPower = parsed!.RfPower;
            }
            else if (m_transmit.TryGetValue("rfpower", out string? text) &&
                     int.TryParse(
                         text,
                         NumberStyles.Integer,
                         CultureInfo.InvariantCulture,
                         out int parsedPower))
            {
                rfPower = parsedPower;
            }
        }

        return new HilRadioSnapshot(
            m_clientHandle,
            m_model,
            m_serial,
            m_occupancy.GetSnapshot(m_radioId),
            clients,
            rfPower,
            transmitSettings,
            m_cwx.Snapshot(),
            m_slices.ToDictionary(
                entry => entry.Key,
                entry => entry.Value));
    }

    public async Task RequestLocalPttAsync(CancellationToken cancellationToken)
    {
        EnsureGui();
        await SendRequiredAsync(
            "client set local_ptt=1",
            cancellationToken);
        await WaitForAsync(
            snapshot =>
                snapshot.TxOccupancy.HasExclusiveLocalPttAuthority(
                    m_clientHandle),
            TimeSpan.FromSeconds(3),
            cancellationToken);
    }

    public async Task<HilTransmitSettings> ConfigureSilentTransmitAsync(
        CancellationToken cancellationToken)
    {
        HilTransmitSettings previous = Snapshot().TransmitSettings ??
            throw new InvalidOperationException(
                "The radio did not report RF power, DAX, microphone selection, and VOX state; silent HIL transmit cannot be restored safely.");
        HilTransmitSettings target = previous with
        {
            RfPower = HilOptions.FixedFirstPulseRfPower,
            DaxEnabled = true,
            MicSelection = "PC",
            VoxEnabled = false
        };
        await ApplyTransmitSettingsAsync(target, cancellationToken);
        return previous;
    }

    public async Task RestoreTransmitSettingsAsync(
        HilTransmitSettings previous,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        await ApplyTransmitSettingsAsync(previous, cancellationToken);
    }

    public async Task ForceRestoreTransmitSettingsAsync(
        HilTransmitSettings target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        await ApplyTransmitSettingsAsync(target, cancellationToken);

        // The first transmit snapshot after a client transition can carry the
        // prior 100 W baseline even while the settled station state is still
        // the child's 1 W safety value. Reassert RF power unconditionally;
        // redundant mic-selection commands are not accepted in every idle
        // cleanup state on this FLEX firmware.
        await SetRfPowerCoreAsync(target.RfPower, cancellationToken);
        await WaitForAsync(
            snapshot => snapshot.TransmitSettings == target,
            TimeSpan.FromSeconds(3),
            cancellationToken);
    }

    private async Task ApplyTransmitSettingsAsync(
        HilTransmitSettings target,
        CancellationToken cancellationToken)
    {
        HilTransmitSettings current = Snapshot().TransmitSettings ??
            throw new InvalidOperationException(
                "The radio did not provide a complete transmit-settings snapshot.");
        if (current.VoxEnabled != target.VoxEnabled)
        {
            await SendRequiredAsync(
                $"transmit set vox_enable={(target.VoxEnabled ? 1 : 0)}",
                cancellationToken);
        }
        if (!string.Equals(
                current.MicSelection,
                target.MicSelection,
                StringComparison.Ordinal))
        {
            await SendRequiredAsync(
                $"transmit set mic_selection={target.MicSelection}",
                cancellationToken);
        }
        if (current.DaxEnabled != target.DaxEnabled)
        {
            await SendRequiredAsync(
                $"transmit set dax={(target.DaxEnabled ? 1 : 0)}",
                cancellationToken);
        }
        if (current.RfPower != target.RfPower)
        {
            await SetRfPowerCoreAsync(target.RfPower, cancellationToken);
        }
        await WaitForAsync(
            snapshot => snapshot.TransmitSettings == target,
            TimeSpan.FromSeconds(3),
            cancellationToken);
    }

    private async Task SetRfPowerCoreAsync(
        int rfPower,
        CancellationToken cancellationToken)
    {
        if (rfPower is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(rfPower));
        }
        await SendRequiredAsync(
            $"transmit set rfpower={rfPower}",
            cancellationToken);
        await WaitForAsync(
            snapshot => snapshot.RfPower == rfPower,
            TimeSpan.FromSeconds(3),
            cancellationToken);
    }

    public async Task<HilOwnedRadioResources> CreateOwnedTxResourcesAsync(
        HilOptions options,
        CancellationToken cancellationToken)
    {
        EnsureGui();
        HilRadioSnapshot before = Snapshot();
        if (before.ExternalGuiClients.Count != 0)
        {
            throw new InvalidOperationException(
                "The pulse command requires every external GUI client, including SmartSDR and Maestro, to be disconnected.");
        }
        if (before.TxOccupancy.State != RadioTxOccupancyState.Idle)
        {
            throw new InvalidOperationException(
                "The radio interlock is not confirmed idle.");
        }

        string? panId = null;
        string? waterfallId = null;
        int? sliceId = null;
        try
        {
            FlexCommandResponse panCreate = await m_control.SendCommandAsync(
                "display panafall create x=100 y=100",
                CommandTimeout,
                cancellationToken);
            if (!panCreate.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"FLEX pan creation failed with 0x{panCreate.Code:x8}: {panCreate.Body}");
            }
            (panId, waterfallId) =
                FlexRadioRxService.ParsePanafallCreateIds(panCreate.Body);
            if (panId is null)
            {
                throw new InvalidOperationException(
                    $"FLEX returned an unrecognized pan identifier: '{panCreate.Body}'.");
            }
            if (waterfallId is null)
            {
                throw new InvalidOperationException(
                    $"FLEX returned no waterfall identifier: '{panCreate.Body}'.");
            }
            await SendRequiredAsync(
                $"display pan set {panId} center=" +
                FormattableString.Invariant(
                    $"{options.FrequencyHz / 1_000_000d:F6} bandwidth=0.050000 fps=1"),
                cancellationToken);

            FlexCommandResponse sliceCreate = await m_control.SendCommandAsync(
                FormattableString.Invariant(
                    $"slice create pan={panId} freq={options.FrequencyHz / 1_000_000d:F6}"),
                CommandTimeout,
                cancellationToken);
            if (!sliceCreate.IsSuccess ||
                !int.TryParse(
                    sliceCreate.Body.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int parsedSliceId) ||
                parsedSliceId < 0)
            {
                throw new InvalidOperationException(
                    $"FLEX slice creation failed with 0x{sliceCreate.Code:x8}: {sliceCreate.Body}");
            }
            sliceId = parsedSliceId;

            await SendRequiredAsync(
                $"slice set {sliceId.Value} mode={options.Mode}",
                cancellationToken);
            await SendRequiredAsync(
                $"slice set {sliceId.Value} txant={options.TxAntenna}",
                cancellationToken);
            await SendRequiredAsync(
                $"slice set {sliceId.Value} tx=1",
                cancellationToken);

            try
            {
                await WaitForAsync(
                    snapshot => SliceMatches(
                        snapshot,
                        sliceId.Value,
                        m_clientHandle,
                        panId,
                        options),
                    TimeSpan.FromSeconds(5),
                    cancellationToken);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Timed out verifying HIL slice {sliceId.Value}; " +
                    $"observed status: {DescribeSliceStatus(sliceId.Value)}",
                    exception);
            }

            FlexRadioCommandRouter router = new();
            router.Attach(
                m_control,
                m_clientHandle,
                panId,
                options.FrequencyHz);
            HilStationTxCommandTransport transport = new(
                router,
                () => m_clientHandle);
            return new HilOwnedRadioResources(
                panId,
                waterfallId,
                sliceId.Value,
                router,
                transport);
        }
        catch
        {
            using CancellationTokenSource cleanup =
                new(TimeSpan.FromSeconds(5));
            if (sliceId is not null)
            {
                await SendBestEffortAsync(
                    $"slice remove {sliceId.Value}",
                    cleanup.Token);
            }
            if (waterfallId is not null)
            {
                await SendBestEffortAsync(
                    $"display panafall remove {waterfallId}",
                    cleanup.Token);
            }
            if (panId is not null)
            {
                await SendBestEffortAsync(
                    $"display pan remove {panId}",
                    cleanup.Token);
            }
            throw;
        }
    }

    public async Task SetOwnedSliceModeAsync(
        HilOwnedRadioResources resources,
        string mode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!string.Equals(mode, "CW", StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode),
                "The first HIL identification stage permits only CW mode.");
        }
        await SendRequiredAsync(
            $"slice set {resources.SliceId} mode={mode}",
            cancellationToken);
        await WaitForAsync(
            snapshot => OwnedSliceMatchesMode(
                snapshot,
                resources,
                m_clientHandle,
                mode),
            TimeSpan.FromSeconds(3),
            cancellationToken);
    }

    HilCwxRadioSnapshot IHilCwxRadio.Snapshot()
    {
        HilRadioSnapshot snapshot = Snapshot();
        return new HilCwxRadioSnapshot(
            snapshot.TxOccupancy,
            snapshot.GuiClients,
            snapshot.Cwx);
    }

    public async Task<StationTxTransportResult> RequestEmergencyUnkeyAsync(
        CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return StationTxTransportResult.Unknown(
                "The independent HIL safety observer is disconnected.");
        }
        try
        {
            FlexCommandResponse response = await m_control.SendCommandAsync(
                "xmit 0",
                CommandTimeout,
                cancellationToken);
            return response.IsSuccess
                ? StationTxTransportResult.Ok
                : StationTxTransportResult.Rejected(
                    $"FLEX returned 0x{response.Code:x8}: {response.Body}".Trim());
        }
        catch (InvalidOperationException exception)
        {
            return StationTxTransportResult.Rejected(exception.Message);
        }
        catch (IOException exception)
        {
            return StationTxTransportResult.Unknown(exception.Message);
        }
        catch (TimeoutException exception)
        {
            return StationTxTransportResult.Unknown(exception.Message);
        }
    }

    public async Task<HilCwxCommandResult> SendCwxCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedHilCwxCommand(command))
        {
            throw new InvalidOperationException(
                $"The HIL CWX boundary rejected command '{command}'.");
        }
        FlexCommandResponse response = await m_control.SendCommandAsync(
            command,
            CommandTimeout,
            cancellationToken);
        return new HilCwxCommandResult(
            response.IsSuccess,
            response.Code,
            response.Body);
    }

    public async Task RemoveOwnedTxResourcesAsync(
        HilOwnedRadioResources resources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        resources.Router.Detach(m_control);
        await SendBestEffortAsync(
            $"slice remove {resources.SliceId}",
            cancellationToken);
        await SendBestEffortAsync(
            $"display panafall remove {resources.WaterfallId}",
            cancellationToken);
        await SendBestEffortAsync(
            $"display pan remove {resources.PanId}",
            cancellationToken);
    }

    public async Task WaitForAsync(
        Func<HilRadioSnapshot, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HilRadioSnapshot snapshot = Snapshot();
            if (predicate(snapshot))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        throw new TimeoutException(
            "Timed out waiting for a radio-authoritative HIL condition.");
    }

    private async Task RefreshOccupancyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RefreshOccupancy(m_roster.Snapshot(m_clientHandle));
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ObserveStatus(string line)
    {
        bool rosterChanged = m_roster.Observe(line);
        bool interlockChanged = false;
        if (FlexStatusParser.TryParseInterlockStatus(
                line,
                out IReadOnlyDictionary<string, string>? interlockFields))
        {
            interlockChanged = m_interlock.Observe(
                interlockFields,
                out _);
        }

        if (FlexStatusParser.TryParseSliceStatus(
                line,
                out int sliceId,
                out IReadOnlyDictionary<string, string>? sliceFields))
        {
            if (sliceFields.TryGetValue("in_use", out string? inUse) &&
                inUse == "0")
            {
                m_slices.TryRemove(sliceId, out _);
            }
            else
            {
                m_slices.AddOrUpdate(
                    sliceId,
                    _ => new Dictionary<string, string>(
                        sliceFields,
                        StringComparer.OrdinalIgnoreCase),
                    (_, current) => MergeFields(current, sliceFields));
            }
        }

        int separator = line.IndexOf('|');
        if (separator >= 0 && separator < line.Length - 1)
        {
            string status = line[(separator + 1)..].Trim();
            Match transmit = TopLevelTransmit.Match(status);
            if (transmit.Success)
            {
                IReadOnlyDictionary<string, string> fields =
                    ParseFields(transmit.Groups["fields"].Value);
                lock (m_transmitGate)
                {
                    foreach ((string key, string value) in fields)
                    {
                        m_transmit[key] = value;
                    }
                }
            }

            Match cwx = TopLevelCwx.Match(status);
            if (cwx.Success)
            {
                m_cwx.Observe(ParseFields(cwx.Groups["fields"].Value));
            }
        }

        if (rosterChanged || interlockChanged)
        {
            RefreshOccupancy(m_roster.Snapshot(m_clientHandle));
        }
    }

    private void RefreshOccupancy(
        IReadOnlyList<RadioGuiClientDiagnostics> clients)
    {
        FlexInterlockObservation? interlock = m_interlock.Current;
        if (interlock is null || m_clientHandle == 0)
        {
            return;
        }
        m_occupancy.ObserveInterlock(
            m_radioId,
            m_reporterId,
            m_clientHandle,
            interlock.State,
            interlock.TxClientHandle,
            interlock.Source,
            clients);
    }

    private static bool OwnedSliceMatchesMode(
        HilRadioSnapshot snapshot,
        HilOwnedRadioResources resources,
        uint clientHandle,
        string mode) =>
        snapshot.Slices.TryGetValue(
            resources.SliceId,
            out IReadOnlyDictionary<string, string>? fields) &&
        fields.TryGetValue("client_handle", out string? owner) &&
        FlexStatusParser.TryParseFlexUInt(owner, out uint parsedOwner) &&
        parsedOwner == clientHandle &&
        fields.TryGetValue("pan", out string? observedPan) &&
        SameFlexId(observedPan, resources.PanId) &&
        fields.TryGetValue("tx", out string? tx) &&
        tx == "1" &&
        fields.TryGetValue("mode", out string? observedMode) &&
        string.Equals(
            observedMode,
            mode,
            StringComparison.OrdinalIgnoreCase);

    private string DescribeSliceStatus(int sliceId)
    {
        if (!m_slices.TryGetValue(
                sliceId,
                out IReadOnlyDictionary<string, string>? fields))
        {
            return "<no slice status>";
        }
        return string.Join(
            ' ',
            fields
                .OrderBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
                .Select(field => $"{field.Key}={field.Value}"));
    }

    private static bool SliceMatches(
        HilRadioSnapshot snapshot,
        int sliceId,
        uint clientHandle,
        string panId,
        HilOptions options)
    {
        if (!snapshot.Slices.TryGetValue(
                sliceId,
                out IReadOnlyDictionary<string, string>? fields) ||
            !fields.TryGetValue("client_handle", out string? owner) ||
            !FlexStatusParser.TryParseFlexUInt(owner, out uint parsedOwner) ||
            parsedOwner != clientHandle ||
            !fields.TryGetValue("pan", out string? observedPan) ||
            !SameFlexId(observedPan, panId) ||
            !fields.TryGetValue("tx", out string? tx) || tx != "1" ||
            !fields.TryGetValue("mode", out string? mode) ||
            !string.Equals(mode, options.Mode, StringComparison.OrdinalIgnoreCase) ||
            !fields.TryGetValue("txant", out string? antenna) ||
            !string.Equals(
                antenna,
                options.TxAntenna,
                StringComparison.OrdinalIgnoreCase) ||
            !fields.TryGetValue("RF_frequency", out string? frequency) ||
            !double.TryParse(
                frequency,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double mhz))
        {
            return false;
        }
        long observedHz = (long)Math.Round(mhz * 1_000_000d);
        return Math.Abs(observedHz - options.FrequencyHz) <= 1;
    }

    private async Task SendRequiredAsync(
        string command,
        CancellationToken cancellationToken)
    {
        FlexCommandResponse response = await m_control.SendCommandAsync(
            command,
            CommandTimeout,
            cancellationToken);
        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(
                $"FLEX rejected '{command}' with 0x{response.Code:x8}: {response.Body}");
        }
    }

    private async Task SendBestEffortAsync(
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            FlexCommandResponse response = await m_control.SendCommandAsync(
                command,
                CommandTimeout,
                cancellationToken);
            if (!response.IsSuccess)
            {
                m_logger.LogWarning(
                    "FLEX best-effort command {Command} returned 0x{Code:x8}: {Body}",
                    command,
                    response.Code,
                    response.Body);
            }
        }
        catch (Exception exception)
        {
            m_logger.LogWarning(
                exception,
                "FLEX best-effort command {Command} failed",
                command);
        }
    }

    private void EnsureGui()
    {
        if (!m_guiRegistered || m_clientHandle == 0)
        {
            throw new InvalidOperationException(
                "This HIL operation requires a registered FLEX GUI client.");
        }
    }

    private static IReadOnlyDictionary<string, string> MergeFields(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> update)
    {
        Dictionary<string, string> merged = new(
            current,
            StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in update)
        {
            merged[key] = value;
        }
        return merged;
    }

    private static IReadOnlyDictionary<string, string> ParseInfo(string body)
    {
        Dictionary<string, string> fields =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in InfoField.Matches(body))
        {
            fields[match.Groups["key"].Value] =
                match.Groups["value"].Value.Trim('"', '\\');
        }
        return fields;
    }

    private static IReadOnlyDictionary<string, string> ParseFields(string text)
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

    internal static bool TryReadTransmitSettings(
        IReadOnlyDictionary<string, string> fields,
        out HilTransmitSettings? settings)
    {
        settings = null;
        if (!fields.TryGetValue("rfpower", out string? powerText) ||
            !int.TryParse(
                powerText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int rfPower) ||
            rfPower is < 0 or > 100 ||
            !fields.TryGetValue("dax", out string? daxText) ||
            !TryReadBoolean(daxText, out bool daxEnabled) ||
            !fields.TryGetValue("mic_selection", out string? micSelection) ||
            string.IsNullOrWhiteSpace(micSelection) ||
            micSelection.Length > 32 ||
            micSelection.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character)) ||
            !fields.TryGetValue("vox_enable", out string? voxText) ||
            !TryReadBoolean(voxText, out bool voxEnabled))
        {
            return false;
        }

        settings = new HilTransmitSettings(
            rfPower,
            daxEnabled,
            micSelection.ToUpperInvariant(),
            voxEnabled);
        return true;
    }

    private static bool TryReadBoolean(string value, out bool parsed)
    {
        if (value == "1" ||
            bool.TryParse(value, out bool boolValue) && boolValue)
        {
            parsed = true;
            return true;
        }
        if (value == "0" ||
            bool.TryParse(value, out bool falseValue) && !falseValue)
        {
            parsed = false;
            return true;
        }
        parsed = false;
        return false;
    }

    internal static bool IsAllowedHilCwxCommand(string command)
    {
        if (command is "cwx clear" or "xmit 0" or
            $"cwx send \"{HilCwxIdentifier.RequiredCallsign}\" 1")
        {
            return true;
        }
        string[] parts = command.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || parts[0] != "cwx")
        {
            return false;
        }
        return parts[1] == "wpm" &&
               int.TryParse(
                   parts[2],
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out int wpm) &&
               wpm is >= 5 and <= 100;
    }

    private static bool SameFlexId(string left, string right) =>
        FlexStatusParser.TryParseFlexUInt(left, out uint leftId) &&
        FlexStatusParser.TryParseFlexUInt(right, out uint rightId) &&
        leftId == rightId;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref m_disposed, 1) != 0)
        {
            return;
        }
        m_lifetime.Cancel();
        if (m_refreshTask is not null)
        {
            try
            {
                await m_refreshTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        m_occupancy.RemoveReporter(m_radioId, m_reporterId);
        m_control.StatusReceived -= ObserveStatus;
        await m_control.DisposeAsync();
        m_lifetime.Dispose();
    }
}

internal sealed class HilEmergencyUnkeyTransport(HilFlexSession observer)
    : IStationTxEmergencyUnkeyTransport
{
    public bool IsConnected => observer.IsConnected;

    public Task<StationTxTransportResult> RequestUnkeyAsync(
        uint expectedProtectedClientHandle,
        CancellationToken cancellationToken) =>
        observer.RequestEmergencyUnkeyAsync(cancellationToken);
}

internal sealed class HilStationTxCommandTransport(
    FlexRadioCommandRouter router,
    Func<uint> clientHandleProvider)
    : IStationTxCommandTransport
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    public bool IsConnected => router.IsAttached;
    public uint ClientHandle => clientHandleProvider();

    public async Task<StationTxTransportResult> SetTransmitAsync(
        bool enabled,
        uint expectedClientHandle,
        CancellationToken cancellationToken)
    {
        try
        {
            FlexCommandResponse response = await router.SendForClientAsync(
                expectedClientHandle,
                enabled ? "xmit 1" : "xmit 0",
                CommandTimeout,
                cancellationToken);
            return response.IsSuccess
                ? StationTxTransportResult.Ok
                : StationTxTransportResult.Rejected(
                    $"FLEX returned 0x{response.Code:x8}: {response.Body}".Trim());
        }
        catch (InvalidOperationException exception)
        {
            return StationTxTransportResult.Rejected(exception.Message);
        }
        catch (IOException exception)
        {
            return StationTxTransportResult.Unknown(exception.Message);
        }
        catch (TimeoutException exception)
        {
            return StationTxTransportResult.Unknown(exception.Message);
        }
    }
}
