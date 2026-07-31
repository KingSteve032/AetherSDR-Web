export function shortId(value) {
  const text = String(value || "").trim();
  return text ? text.slice(0, 8) : "—";
}

export function formatHexId(value) {
  const numeric = Number(value);
  return Number.isFinite(numeric) && numeric > 0
    ? `0x${(numeric >>> 0).toString(16).padStart(8, "0")}`
    : "—";
}

export function formatFrequency(value) {
  const numeric = Number(value);
  return Number.isFinite(numeric) && numeric > 0
    ? `${(numeric / 1_000_000).toFixed(6)} MHz`
    : "—";
}

export function formatCount(value) {
  const numeric = Number(value);
  return Number.isFinite(numeric) && numeric >= 0
    ? Math.round(numeric).toLocaleString("en-US")
    : "0";
}

export function formatAge(value, now = Date.now()) {
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return "never";
  }
  const seconds = Math.max(0, Math.round((now - timestamp) / 1000));
  if (seconds < 60) {
    return `${seconds}s ago`;
  }
  const minutes = Math.round(seconds / 60);
  return minutes < 60 ? `${minutes}m ago` : `${Math.round(minutes / 60)}h ago`;
}

export function formatTxLifecycle(lifecycle, now = Date.now()) {
  if (!lifecycle?.registered) {
    return {
      value: "NOT REGISTERED",
      detail: "No station TX lifecycle snapshot is available"
    };
  }

  const gate = String(lifecycle.gateState || "unknown").toUpperCase();
  const safety = String(lifecycle.safetyState || "unknown").toUpperCase();
  const transportState =
    lifecycle.commandTransportAvailable ||
    lifecycle.emergencyUnkeyTransportAvailable
      ? "TX transport present"
      : "TX transports absent";
  const authorityReason = String(
    lifecycle.authorityReason || "unknown");
  const authorityState = lifecycle.authorityFresh
    ? "FRESH"
    : authorityReason === "no-active-lease"
      ? "NO LEASE"
      : "REVOKED";
  const watchdogState = lifecycle.watchdogRunning
    ? `watchdog ${formatCount(lifecycle.watchdogEvaluationSequence)} ` +
      `(${formatAge(lifecycle.lastWatchdogEvaluatedAt, now)})`
    : "watchdog stopped";
  return {
    value: `${gate} · ${safety} · ${authorityState}`,
    detail:
      `browser ${formatCount(lifecycle.browserObservationSequence)}/` +
      `${lifecycle.browserFresh ? "fresh" : "stale"} ` +
      `(${formatAge(lifecycle.lastBrowserObservedAt, now)}) · ` +
      `engine ${formatCount(lifecycle.engineObservationSequence)}/` +
      `${lifecycle.engineFresh ? "fresh" : "stale"} ` +
      `(${formatAge(lifecycle.lastEngineObservedAt, now)}) · ` +
      `gateway ${formatCount(lifecycle.gatewayObservationSequence)}/` +
      `${lifecycle.gatewayFresh ? "fresh" : "stale"} ` +
      `(${formatAge(lifecycle.lastGatewayObservedAt, now)}) · ` +
      `lease ${formatCount(lifecycle.leaseObservationSequence)} ` +
      `(${formatAge(lifecycle.lastLeaseObservedAt, now)}) · ` +
      `${watchdogState} · authority ${authorityReason} · ` +
      `last ${lifecycle.lastObservation || "none"} · ${transportState}`
  };
}

export function formatTuneTiming(tune, now = Date.now()) {
  const state = String(tune?.state || "idle").toLowerCase();
  const slice = tune?.sliceId || "?";
  const radioSlice = Number.isInteger(tune?.radioSliceId)
    ? tune.radioSliceId
    : "?";
  const target = formatFrequency(tune?.targetFrequencyHz);
  const route = `${slice} -> ${radioSlice}`;

  if (state === "pending") {
    return {
      value: "PENDING",
      detail: `${route} at ${target}; requested ${formatAge(
        tune?.requestedAt,
        now)}`
    };
  }
  if (state === "confirmed") {
    const milliseconds = Number(tune?.radioRoundTripMilliseconds);
    const value = Number.isFinite(milliseconds)
      ? milliseconds < 1
        ? "<1 ms"
        : milliseconds < 1_000
          ? `${Math.round(milliseconds)} ms`
          : `${(milliseconds / 1_000).toFixed(2)} s`
      : "CONFIRMED";
    return {
      value,
      detail: `${route} at ${target}; radio echo ${formatAge(
        tune?.confirmedAt,
        now)}`
    };
  }
  if (state === "failed") {
    return {
      value: "FAILED",
      detail: tune?.error || `${route} at ${target}`
    };
  }
  return {
    value: "No tune yet",
    detail: "Waiting for a valid browser frequency request"
  };
}

export function formatBrowserReconnect(reconnect, now = Date.now()) {
  if (!reconnect || Number(reconnect.connectionAttempts) <= 0) {
    return {
      value: "Waiting for browser",
      detail: "No browser socket has been admitted"
    };
  }

  const attempts = formatCount(reconnect.connectionAttempts);
  const successful = formatCount(reconnect.successfulConnections);
  const reconnects = formatCount(reconnect.reconnects);
  const rejected = formatCount(reconnect.rejectedConnections);
  const lastRecovery = Number(reconnect.lastRecoveryMilliseconds);
  return {
    value: Number(reconnect.reconnects) > 0
      ? `${reconnects} recovered`
      : "Initial socket",
    detail:
      `${successful} of ${attempts} admitted; ${rejected} overlapping; ` +
      (Number.isFinite(lastRecovery) && lastRecovery >= 0
        ? `${formatMilliseconds(lastRecovery)} last recovery`
        : `connected ${formatAge(reconnect.lastConnectedAt, now)}`)
  };
}

export function formatBrowserAudio(audio, now = Date.now()) {
  if (!audio) {
    return {
      latencyValue: "Waiting for report",
      latencyDetail: "Open the radio page to measure browser playback",
      healthValue: "No browser data",
      healthDetail: "Underruns and latency trims have not been reported",
      deliveryValue: "No browser data",
      deliveryDetail: "Packet sequence and arrival timing are unavailable"
    };
  }

  const slice = audio.activeSliceId
    ? `Slice ${audio.activeSliceId}`
    : "No active slice";
  const deliveryPath = formatDeliveryPath(audio.deliveryPath);
  const reportAge = formatAge(audio.reportedAt, now);
  const lifecycleState = audio.playbackSuppressed
    ? audio.pageVisible === false
      ? "background paused"
      : "re-priming"
    : audio.pageVisible === false
      ? "background"
      : "foreground";
  const backgroundTransitions = formatCount(audio.backgroundTransitions);
  const foregroundRecoveries = formatCount(audio.foregroundRecoveries);
  const backgroundLabel =
    `${backgroundTransitions} background pause` +
    `${Number(audio.backgroundTransitions) === 1 ? "" : "s"}`;
  const recoveryLabel =
    `${foregroundRecoveries} foreground recover` +
    `${Number(audio.foregroundRecoveries) === 1 ? "y" : "ies"}`;
  if (!audio.enabled) {
    return {
      latencyValue: "PC audio off",
      latencyDetail: `${slice}; report ${reportAge}`,
      healthValue: "Idle",
      healthDetail:
        `${formatCount(audio.malformedPackets)} malformed packet` +
        `${Number(audio.malformedPackets) === 1 ? "" : "s"}`,
      deliveryValue:
        `${deliveryPath} · ${formatCount(audio.missingPackets)} missing`,
      deliveryDetail:
        `${formatMilliseconds(audio.maximumPacketGapMilliseconds)} max gap`
    };
  }

  const estimated = formatMilliseconds(audio.estimatedLatencyMilliseconds);
  const queued = formatMilliseconds(audio.queueMilliseconds);
  return {
    latencyValue: `${estimated} est.`,
    latencyDetail:
      `${slice}; ${queued} queue; ` +
      `${audio.started ? "playing" : "buffering"}; ` +
      `${lifecycleState}; report ${reportAge}`,
    healthValue:
      `${formatCount(audio.underruns)} underrun` +
      `${Number(audio.underruns) === 1 ? "" : "s"}`,
    healthDetail:
      `${recoveryLabel}; ${backgroundLabel}; ` +
      `${formatCount(audio.trimmedFrames)} latency-trimmed; ` +
      `${formatCount(audio.clearedFrames)} cleared; ` +
      `${formatCount(audio.malformedPackets)} malformed`,
    deliveryValue:
      `${deliveryPath} · ${formatCount(audio.missingPackets)} missing`,
    deliveryDetail:
      `${formatMilliseconds(audio.maximumPacketGapMilliseconds)} ` +
      "max browser arrival gap"
  };
}

export function formatBrowserNetwork(network, now = Date.now()) {
  if (!network) {
    return {
      value: "Waiting for report",
      detail: "Open the radio page to measure browser traffic"
    };
  }

  const profile = String(network.profile || "normal").toUpperCase();
  const adaptation = network.adaptation === "manual"
    ? "manual hold"
    : "adaptive";
  return {
    value: `${profile} · ${formatDataRate(network.bytesPerSecond)}`,
    detail:
      `${formatDataRate(network.audioBytesPerSecond)} audio · ` +
      `${formatDataRate(network.spectrumBytesPerSecond)} display · ` +
      `${formatMilliseconds(network.maximumGapMilliseconds)} max gap · ` +
      `${adaptation} · report ${formatAge(network.reportedAt, now)}`
  };
}

function formatDeliveryPath(value) {
  if (value === "worker") {
    return "WORKER";
  }
  if (value === "main-thread-fallback") {
    return "MAIN FALLBACK";
  }
  return "LEGACY MAIN";
}

function formatMilliseconds(value) {
  const milliseconds = Number(value);
  if (!Number.isFinite(milliseconds) || milliseconds < 0) {
    return "0 ms";
  }
  if (milliseconds < 1) {
    return "<1 ms";
  }
  return milliseconds < 1000
    ? `${Math.round(milliseconds)} ms`
    : `${(milliseconds / 1000).toFixed(2)} s`;
}

function formatDataRate(bytesPerSecond) {
  const bitsPerSecond = Math.max(0, Number(bytesPerSecond) || 0) * 8;
  if (bitsPerSecond >= 1_000_000) {
    return `${(bitsPerSecond / 1_000_000).toFixed(2)} Mb/s`;
  }
  if (bitsPerSecond >= 1000) {
    return `${Math.round(bitsPerSecond / 1000)} kb/s`;
  }
  return `${Math.round(bitsPerSecond)} b/s`;
}

export function sessionDiagnosticExpanded(session, expansionStates) {
  const sessionId = String(session?.sessionId || "");
  if (sessionId && expansionStates?.has(sessionId)) {
    return expansionStates.get(sessionId) === true;
  }
  return Boolean(session?.connectionError) ||
    session?.connectionState !== "connected";
}

export function rememberSessionDiagnosticExpansion(
  expansionStates,
  sessionId,
  expanded
) {
  const key = String(sessionId || "");
  if (!key) {
    return;
  }
  expansionStates.set(key, expanded === true);
}
