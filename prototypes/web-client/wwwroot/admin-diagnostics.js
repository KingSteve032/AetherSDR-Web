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

export function formatUntil(value, now = Date.now()) {
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return "unknown";
  }
  const seconds = Math.ceil((timestamp - now) / 1000);
  if (seconds <= 0) {
    return "expired";
  }
  if (seconds < 60) {
    return `${seconds}s`;
  }
  const minutes = Math.round(seconds / 60);
  return minutes < 60 ? `${minutes}m` : `${Math.round(minutes / 60)}h`;
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
  const leaseDetails = [];
  const leaseHolder = String(lifecycle.leaseDisplayName || "").trim();
  if (leaseHolder) {
    leaseDetails.push(
      `${lifecycle.leaseActive ? "holder" : "last holder"} ${leaseHolder}`);
  }
  if (lifecycle.leaseActive && lifecycle.leaseExpiresAt) {
    leaseDetails.push(
      `expires ${formatUntil(lifecycle.leaseExpiresAt, now)}`);
  }
  const leaseReason = String(
    lifecycle.lastLeaseChangeReason || "").trim();
  if (leaseReason) {
    leaseDetails.push(`reason ${leaseReason}`);
  }
  const leaseState =
    `lease ${formatCount(lifecycle.leaseObservationSequence)} ` +
    `(${formatAge(lifecycle.lastLeaseObservedAt, now)})` +
    (leaseDetails.length ? ` ${leaseDetails.join(" ")}` : "");
  const intentCount = Number(
    lifecycle.browserTxIntentObservationSequence) || 0;
  const intentState = intentCount > 0
    ? `intent ${formatCount(intentCount)} req ` +
      `${formatCount(lifecycle.lastBrowserTxIntentRequestSequence)} ` +
      `${String(lifecycle.lastBrowserTxIntentAction || "unknown")}/` +
      `${String(lifecycle.lastBrowserTxIntentOutcome || "unknown")} ` +
      `(${formatAge(lifecycle.lastBrowserTxIntentAt, now)}) ` +
      `${String(lifecycle.lastBrowserTxIntentReason || "").slice(0, 160)}`.trim()
    : "";
  const stationCommandState = lifecycle.stationCommandBoundaryRegistered
    ? `command boundary v${formatCount(
        lifecycle.stationCommandProtocolVersion)} ` +
      `${lifecycle.stationCommandBoundaryEnabled ? "enabled" : "disabled"} ` +
      `signature ${lifecycle.stationCommandSignatureVerificationAvailable
        ? "available"
        : "absent"} ` +
      `adapter ${lifecycle.stationCommandAdapterRegistered
        ? "registered"
        : "absent"} ` +
      `arming ${lifecycle.stationCommandArmingAvailable
        ? "available"
        : "absent"} ` +
      `set-transmit ${lifecycle.stationCommandSetTransmitAvailable
        ? "available"
        : "absent"} ` +
      `audit ${formatCount(lifecycle.stationCommandAuditCount)}`
    : "command boundary not registered";
  const adapterComposition = lifecycle.stationCommandAdapterComposition;
  const adapterCompositionState = adapterComposition?.registered
    ? `adapter composition executor ${adapterComposition.executorAttached
        ? "attached"
        : "absent"} ` +
      `registered ${adapterComposition.executorRegistered ? "yes" : "no"} ` +
      `authority ${adapterComposition.authoritySnapshotAvailable
        ? "available"
        : "absent"} ` +
      `adapter ${adapterComposition.commandAdapterRegistered
        ? "registered"
        : "absent"} ` +
      `arming ${adapterComposition.armingAvailable
        ? "available"
        : "absent"} ` +
      `set-transmit ${adapterComposition.setTransmitAvailable
        ? "available"
        : "absent"} ` +
      `attempts ${formatCount(adapterComposition.attemptCount)} ` +
      `forwarded ${formatCount(adapterComposition.forwardedCount)} ` +
      `last ${String(adapterComposition.lastOutcome || "none")} ` +
      `reason ${String(adapterComposition.reason || "unknown")}`
    : "adapter composition not registered";
  const safetyArmComposition =
    lifecycle.stationCommandSafetyArmComposition;
  const safetyArmCompositionState = safetyArmComposition?.registered
    ? `safety arm composition authority ${safetyArmComposition.armAuthorityAttached
        ? "attached"
        : "absent"} ` +
      `registered ${safetyArmComposition.armAuthorityRegistered ? "yes" : "no"} ` +
      `session authority ${safetyArmComposition.sessionAuthoritySnapshotAvailable
        ? "available"
        : "absent"} ` +
      `arm ${safetyArmComposition.armAvailable ? "available" : "unavailable"} ` +
      `heartbeat ${safetyArmComposition.heartbeatAvailable
        ? "available"
        : "unavailable"} ` +
      `abort ${safetyArmComposition.abortAvailable
        ? "available"
        : "unavailable"} ` +
      `attempts ${formatCount(safetyArmComposition.attemptCount)} ` +
      `forwarded ${formatCount(safetyArmComposition.forwardedCount)} ` +
      `last ${String(safetyArmComposition.lastOperation || "none")}/` +
      `${String(safetyArmComposition.lastOutcome || "none")} ` +
      `reason ${String(safetyArmComposition.reason || "unknown")}`
    : "safety arm composition not registered";
  const commandComposition = lifecycle.stationCommandSessionComposition;
  const commandCompositionState = commandComposition?.registered
    ? `command composition coordinator ${commandComposition.coordinatorAttached
        ? "attached"
        : "absent"} ` +
      `boundary ${commandComposition.boundaryAttached
        ? "attached"
        : "absent"} ` +
      `authority ${commandComposition.authoritySnapshotAvailable
        ? "available"
        : "absent"} ` +
      `submission ${commandComposition.submissionAvailable
        ? "available"
        : "unavailable"} ` +
      `attempts ${formatCount(commandComposition.attemptCount)} ` +
      `forwarded ${formatCount(commandComposition.forwardedCount)} ` +
      `last ${String(commandComposition.lastOutcome || "none")} ` +
      `reason ${String(commandComposition.reason || "unknown")}`
    : "command composition not registered";
  const independent = lifecycle.independentWatchdog;
  const independentState = !independent?.supervisionEnabled
    ? "independent not supervised"
    : independent.processRunning && independent.ipcConnected
      ? `independent ${String(independent.state || "disarmed").toLowerCase()} ` +
        `pid ${Number.isInteger(independent.processId) ? independent.processId : "?"} ` +
        `host ${shortId(independent.hostInstanceId)} ` +
        `seq ${formatCount(independent.lastSequence)} ` +
        `restarts ${formatCount(independent.restartCount)}`
      : `independent degraded (${independent.reason || "unavailable"})`;
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
      `${leaseState} · ` +
      `${intentState ? `${intentState} · ` : ""}` +
      `${watchdogState} · ${independentState} · ` +
      `${stationCommandState} · ${adapterCompositionState} · ` +
      `${safetyArmCompositionState} · ${commandCompositionState} · ` +
      `authority ${authorityReason} · ` +
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
