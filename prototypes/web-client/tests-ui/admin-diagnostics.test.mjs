import test from "node:test";
import assert from "node:assert/strict";
import {
  formatAge,
  formatBrowserAudio,
  formatBrowserNetwork,
  formatBrowserReconnect,
  formatCount,
  formatFrequency,
  formatHexId,
  formatTxLifecycle,
  formatTuneTiming,
  formatUntil,
  rememberSessionDiagnosticExpansion,
  sessionDiagnosticExpanded,
  shortId
} from "../wwwroot/admin-diagnostics.js";

test("admin diagnostics format radio ownership identifiers", () => {
  assert.equal(formatHexId(0x452b3521), "0x452b3521");
  assert.equal(formatHexId(0), "—");
  assert.equal(shortId("1234567890abcdef"), "12345678");
  assert.equal(shortId(""), "—");
});

test("admin diagnostics format stream activity without local ambiguity", () => {
  const now = Date.parse("2026-07-27T14:00:00Z");
  assert.equal(formatFrequency(14_074_000), "14.074000 MHz");
  assert.equal(formatCount(12_345), "12,345");
  assert.equal(formatAge("2026-07-27T13:59:52Z", now), "8s ago");
  assert.equal(formatAge(null, now), "never");
  assert.equal(formatUntil("2026-07-27T14:00:08Z", now), "8s");
  assert.equal(formatUntil("2026-07-27T13:59:59Z", now), "expired");
});

test("admin diagnostics summarize fail-closed TX lifecycle freshness", () => {
  const now = Date.parse("2026-07-31T02:30:00Z");
  assert.deepEqual(formatTxLifecycle({
    registered: true,
    gateState: "Disabled",
    safetyState: "Disarmed",
    commandTransportAvailable: false,
    emergencyUnkeyTransportAvailable: false,
    stationCommandProtocolVersion: 1,
    stationCommandBoundaryRegistered: true,
    stationCommandBoundaryEnabled: false,
    stationCommandSignatureVerificationAvailable: false,
    stationCommandAdapterRegistered: false,
    stationCommandArmingAvailable: false,
    stationCommandSetTransmitAvailable: false,
    stationCommandAuditCount: 0,
    stationCommandAdapterComposition: {
      registered: true,
      executorAttached: false,
      executorRegistered: false,
      authoritySnapshotAvailable: false,
      commandAdapterRegistered: false,
      armingAvailable: false,
      setTransmitAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOutcome: "none",
      reason: "executor-unattached"
    },
    stationCommandSessionComposition: {
      registered: true,
      coordinatorAttached: true,
      boundaryAttached: true,
      authoritySnapshotAvailable: false,
      submissionAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOutcome: "none",
      reason: "submission-disabled"
    },
    browserObservationSequence: 4,
    lastBrowserObservedAt: "2026-07-31T02:29:59Z",
    engineObservationSequence: 7,
    lastEngineObservedAt: "2026-07-31T02:29:57Z",
    gatewayObservationSequence: 9,
    lastGatewayObservedAt: "2026-07-31T02:29:58Z",
    leaseObservationSequence: 2,
    lastLeaseObservedAt: "2026-07-31T02:29:50Z",
    watchdogRunning: true,
    watchdogEvaluationSequence: 3,
    lastWatchdogEvaluatedAt: "2026-07-31T02:29:59Z",
    browserFresh: true,
    engineFresh: true,
    gatewayFresh: true,
    authorityFresh: false,
    authorityReason: "no-active-lease",
    independentWatchdog: {
      supervisionEnabled: true,
      processRunning: true,
      processId: 4242,
      hostInstanceId: "watchdog-1234567890",
      state: "Disarmed",
      ipcConnected: true,
      lastSequence: 0,
      restartCount: 1
    },
    lastObservation: "gateway-heartbeat"
  }, now), {
    value: "DISABLED · DISARMED · NO LEASE",
    detail:
      "browser 4/fresh (1s ago) · engine 7/fresh (3s ago) · " +
      "gateway 9/fresh (2s ago) · lease 2 (10s ago) · " +
      "watchdog 3 (1s ago) · independent disarmed pid 4242 " +
      "host watchdog seq 0 restarts 1 · command boundary v1 disabled " +
      "signature absent adapter absent arming absent set-transmit absent " +
      "audit 0 · adapter composition executor absent registered no authority " +
      "absent adapter absent arming absent set-transmit absent attempts 0 " +
      "forwarded 0 last none reason executor-unattached · command composition " +
      "coordinator attached boundary attached " +
      "authority absent submission unavailable attempts 0 forwarded 0 " +
      "last none reason submission-disabled · authority no-active-lease · " +
      "last gateway-heartbeat · TX transports absent"
  });
  assert.deepEqual(formatTxLifecycle(null, now), {
    value: "NOT REGISTERED",
    detail: "No station TX lifecycle snapshot is available"
  });
});

test("admin diagnostics keep ready signature verification separate from commands", () => {
  const result = formatTxLifecycle({
    registered: true,
    gateState: "Disabled",
    safetyState: "Disarmed",
    commandTransportAvailable: false,
    emergencyUnkeyTransportAvailable: false,
    stationCommandProtocolVersion: 1,
    stationCommandBoundaryRegistered: true,
    stationCommandBoundaryEnabled: false,
    stationCommandSignatureVerificationAvailable: true,
    stationCommandAdapterRegistered: false,
    stationCommandArmingAvailable: false,
    stationCommandSetTransmitAvailable: false,
    stationCommandAuditCount: 0,
    stationCommandAdapterComposition: {
      registered: true,
      executorAttached: false,
      executorRegistered: false,
      authoritySnapshotAvailable: false,
      commandAdapterRegistered: false,
      armingAvailable: false,
      setTransmitAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOutcome: "none",
      reason: "executor-unattached"
    },
    stationCommandSessionComposition: {
      registered: true,
      coordinatorAttached: true,
      boundaryAttached: true,
      authoritySnapshotAvailable: false,
      submissionAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOutcome: "none",
      reason: "submission-disabled"
    },
    authorityFresh: false,
    authorityReason: "no-active-lease",
    independentWatchdog: {
      supervisionEnabled: false
    },
    lastObservation: "registered-disabled"
  });

  assert.equal(result.value, "DISABLED · DISARMED · NO LEASE");
  assert.match(
    result.detail,
    /command boundary v1 disabled signature available adapter absent arming absent set-transmit absent audit 0/);
  assert.match(
    result.detail,
    /adapter composition executor absent registered no authority absent adapter absent arming absent set-transmit absent attempts 0 forwarded 0 last none reason executor-unattached/);
  assert.match(
    result.detail,
    /command composition coordinator attached boundary attached authority absent submission unavailable attempts 0 forwarded 0 last none reason submission-disabled/);
  assert.match(result.detail, /TX transports absent/);
});

test("admin diagnostics surface lease holder expiry and browser TX intent outcome", () => {
  const now = Date.parse("2026-07-31T16:00:00Z");
  const result = formatTxLifecycle({
    registered: true,
    gateState: "Disabled",
    safetyState: "Disarmed",
    commandTransportAvailable: false,
    emergencyUnkeyTransportAvailable: false,
    stationCommandAdapterComposition: {
      registered: true,
      executorAttached: false,
      executorRegistered: false,
      authoritySnapshotAvailable: true,
      commandAdapterRegistered: false,
      armingAvailable: false,
      setTransmitAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOutcome: "none",
      reason: "executor-unattached"
    },
    stationCommandSessionComposition: {
      registered: true,
      coordinatorAttached: true,
      boundaryAttached: true,
      authoritySnapshotAvailable: true,
      submissionAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOutcome: "none",
      reason: "submission-disabled"
    },
    browserObservationSequence: 12,
    lastBrowserObservedAt: "2026-07-31T15:59:59Z",
    engineObservationSequence: 13,
    lastEngineObservedAt: "2026-07-31T15:59:59Z",
    gatewayObservationSequence: 14,
    lastGatewayObservedAt: "2026-07-31T15:59:59Z",
    leaseObservationSequence: 3,
    lastLeaseObservedAt: "2026-07-31T15:59:58Z",
    leaseActive: true,
    leaseDisplayName: "Operator A",
    leaseExpiresAt: "2026-07-31T16:00:10Z",
    lastLeaseChangeReason: "renewed",
    browserTxIntentObservationSequence: 2,
    lastBrowserTxIntentRequestSequence: 9,
    lastBrowserTxIntentAction: "mox.set",
    lastBrowserTxIntentOutcome: "transport-unavailable",
    lastBrowserTxIntentReason:
      "The deliberate TX intent was validated, but production radio command transport is unavailable.",
    lastBrowserTxIntentAt: "2026-07-31T15:59:59Z",
    watchdogRunning: true,
    watchdogEvaluationSequence: 20,
    lastWatchdogEvaluatedAt: "2026-07-31T15:59:59Z",
    browserFresh: true,
    engineFresh: true,
    gatewayFresh: true,
    authorityFresh: true,
    authorityReason: "fresh",
    independentWatchdog: {
      supervisionEnabled: true,
      processRunning: true,
      processId: 4242,
      hostInstanceId: "watchdog-1234567890",
      state: "Disarmed",
      ipcConnected: true,
      lastSequence: 2,
      restartCount: 0
    },
    lastObservation: "browser-tx-intent-transport-unavailable"
  }, now);

  assert.equal(result.value, "DISABLED · DISARMED · FRESH");
  assert.match(
    result.detail,
    /lease 3 \(2s ago\) holder Operator A expires 10s reason renewed/);
  assert.match(
    result.detail,
    /intent 2 req 9 mox\.set\/transport-unavailable \(1s ago\)/);
  assert.match(
    result.detail,
    /adapter composition executor absent registered no authority available adapter absent arming absent set-transmit absent attempts 0 forwarded 0 last none reason executor-unattached/);
  assert.match(
    result.detail,
    /command composition coordinator attached boundary attached authority available submission unavailable attempts 0 forwarded 0 last none reason submission-disabled/);
  assert.match(result.detail, /TX transports absent$/);
});

test("admin diagnostics distinguish pending and radio-confirmed tunes", () => {
  const now = Date.parse("2026-07-27T14:00:01Z");
  assert.deepEqual(
    formatTuneTiming({
      state: "pending",
      sliceId: "B",
      radioSliceId: 4,
      targetFrequencyHz: 14_074_000,
      requestedAt: "2026-07-27T14:00:00Z"
    }, now),
    {
      value: "PENDING",
      detail: "B -> 4 at 14.074000 MHz; requested 1s ago"
    });
  assert.deepEqual(
    formatTuneTiming({
      state: "confirmed",
      sliceId: "B",
      radioSliceId: 4,
      targetFrequencyHz: 14_074_000,
      confirmedAt: "2026-07-27T14:00:00Z",
      radioRoundTripMilliseconds: 47.4
    }, now),
    {
      value: "47 ms",
      detail: "B -> 4 at 14.074000 MHz; radio echo 1s ago"
    });
});

test("admin diagnostics separate audio latency from underruns and trims", () => {
  const now = Date.parse("2026-07-27T14:00:01Z");
  assert.deepEqual(
    formatBrowserAudio({
      enabled: true,
      deliveryPath: "worker",
      pageVisible: true,
      playbackSuppressed: false,
      backgroundTransitions: 2,
      foregroundRecoveries: 2,
      activeSliceId: "A",
      estimatedLatencyMilliseconds: 36.8,
      queueMilliseconds: 19.5,
      started: true,
      underruns: 1,
      trimmedFrames: 64,
      clearedFrames: 480,
      malformedPackets: 0,
      missingPackets: 2,
      maximumPacketGapMilliseconds: 38.5,
      reportedAt: "2026-07-27T14:00:00Z"
    }, now),
    {
      latencyValue: "37 ms est.",
      latencyDetail:
        "Slice A; 20 ms queue; playing; foreground; report 1s ago",
      healthValue: "1 underrun",
      healthDetail:
        "2 foreground recoveries; 2 background pauses; " +
        "64 latency-trimmed; 480 cleared; 0 malformed",
      deliveryValue: "WORKER · 2 missing",
      deliveryDetail: "39 ms max browser arrival gap"
    });
});

test("admin diagnostics show when browser audio is intentionally background-paused", () => {
  const now = Date.parse("2026-07-27T14:00:01Z");
  const result = formatBrowserAudio({
    enabled: true,
    deliveryPath: "worker",
    pageVisible: false,
    playbackSuppressed: true,
    backgroundTransitions: 1,
    foregroundRecoveries: 0,
    activeSliceId: "B",
    estimatedLatencyMilliseconds: 0,
    queueMilliseconds: 0,
    started: false,
    underruns: 0,
    trimmedFrames: 0,
    clearedFrames: 1080,
    malformedPackets: 0,
    missingPackets: 0,
    maximumPacketGapMilliseconds: 20,
    reportedAt: "2026-07-27T14:00:00Z"
  }, now);

  assert.match(result.latencyDetail, /background paused/);
  assert.match(result.healthDetail, /1 background pause/);
});

test("admin diagnostics publish measured browser traffic by profile", () => {
  const now = Date.parse("2026-07-28T01:00:01Z");
  assert.deepEqual(formatBrowserNetwork({
    profile: "low",
    adaptation: "automatic",
    bytesPerSecond: 18_750,
    audioBytesPerSecond: 12_000,
    spectrumBytesPerSecond: 6_250,
    maximumGapMilliseconds: 44,
    reportedAt: "2026-07-28T01:00:00Z"
  }, now), {
    value: "LOW · 150 kb/s",
    detail:
      "96 kb/s audio · 50 kb/s display · 44 ms max gap · " +
      "adaptive · report 1s ago"
  });
});

test("admin diagnostics report admitted browser reconnects and recovery time", () => {
  assert.deepEqual(
    formatBrowserReconnect({
      connectionAttempts: 13,
      successfulConnections: 11,
      reconnects: 10,
      rejectedConnections: 2,
      lastConnectedAt: "2026-07-27T14:00:00Z",
      lastRecoveryMilliseconds: 842
    }),
    {
      value: "10 recovered",
      detail: "11 of 13 admitted; 2 overlapping; 842 ms last recovery"
    });
});

test("admin diagnostics retain each session expansion across refreshes", () => {
  const expansionStates = new Map();
  const connected = {
    sessionId: "connected-session",
    connectionState: "connected",
    connectionError: null
  };
  const failed = {
    sessionId: "failed-session",
    connectionState: "faulted",
    connectionError: "Radio unavailable"
  };

  assert.equal(
    sessionDiagnosticExpanded(connected, expansionStates),
    false);
  assert.equal(
    sessionDiagnosticExpanded(failed, expansionStates),
    true);

  rememberSessionDiagnosticExpansion(
    expansionStates,
    connected.sessionId,
    true);
  rememberSessionDiagnosticExpansion(
    expansionStates,
    failed.sessionId,
    false);

  assert.equal(
    sessionDiagnosticExpanded(connected, expansionStates),
    true);
  assert.equal(
    sessionDiagnosticExpanded(failed, expansionStates),
    false);
});
