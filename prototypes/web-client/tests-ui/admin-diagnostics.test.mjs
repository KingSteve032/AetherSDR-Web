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

const productionReadinessMissing = [
  "transmit-disabled",
  "browser-tx-lease-disabled",
  "command-submission-disabled",
  "command-signing-unavailable",
  "command-verification-unavailable",
  "command-boundary-disabled",
  "command-gate-transmit-disabled",
  "command-transport-unavailable",
  "set-transmit-unavailable",
  "emergency-unkey-transport-unavailable",
  "watchdog-unkey-transport-unavailable",
  "watchdog-arming-unavailable"
];
const productionReadiness = {
  registered: true,
  ready: false,
  reason: "transmit-disabled",
  missingPrerequisites: productionReadinessMissing
};
const productionReadinessText =
  `production readiness blocked reason transmit-disabled missing 12 ` +
  `[${productionReadinessMissing.join(",")}]`;
const productionActivationConfigurationMissing = [
  "local-flex-mode-required",
  "transmit-disabled",
  "browser-tx-lease-disabled",
  "command-trust-verification-disabled",
  "command-trust-key-unconfigured",
  "command-signing-disabled",
  "command-signing-key-unconfigured",
  "command-submission-disabled",
  "command-transport-disabled",
  "command-transport-allowlist-empty",
  "emergency-unkey-transport-disabled",
  "emergency-unkey-transport-allowlist-empty",
  "watchdog-supervision-disabled",
  "watchdog-unkey-transport-disabled",
  "watchdog-unkey-transport-allowlist-empty",
  "watchdog-arming-disabled"
];
const productionActivationConfiguration = {
  registered: true,
  activationRequested: false,
  configurationValid: true,
  reason: "activation-not-requested",
  missingPrerequisites: productionActivationConfigurationMissing
};
const productionActivationPlan = {
  registered: true,
  configurationInterlockAttached: true,
  activationRequested: false,
  configurationValid: true,
  planAvailable: false,
  planApplied: false,
  reason: "activation-not-requested",
  plan: {
    commandBoundaryEnabled: false,
    commandGateTransmitEnabled: false,
    browserTransactionIngressExecutionEnabled: false,
    browserKeyingCapabilityEnabled: false
  }
};
const productionActivation = {
  registered: true,
  configurationInterlockAttached: true,
  activationPlanAttached: true,
  readinessEvaluationAttached: true,
  activationRequested: false,
  configurationValid: true,
  activationPlanAvailable: false,
  activationPlanApplied: false,
  activationAvailable: false,
  reason: "activation-not-requested",
  configuration: productionActivationConfiguration,
  plan: productionActivationPlan,
  readiness: productionReadiness
};
const productionActivationText =
  "production activation composition config attached request absent " +
  "configuration valid plan attached unavailable unapplied switches " +
  "boundary off gate off ingress off capability off plan-reason " +
  "activation-not-requested evaluation attached activation unavailable " +
  "reason activation-not-requested static-missing 16 " +
  `[${productionActivationConfigurationMissing.join(",")}]`;
const productionCommandTransport = {
  registered: true,
  configuredEnabled: false,
  localFlexEligible: true,
  radioAllowed: false,
  commandChannelAttached: true,
  clientHandleAvailable: true,
  available: false,
  setTransmitAvailable: false,
  commandTimeoutMilliseconds: 2000,
  attemptCount: 0,
  forwardedCount: 0,
  keyAttemptCount: 0,
  unkeyAttemptCount: 0,
  acceptedCount: 0,
  rejectedCount: 0,
  unknownCount: 0,
  lastOperation: "none",
  lastOutcome: "none",
  lastReason: "transport-disabled"
};
const productionCommandTransportText =
  "production command transport config disabled eligible yes radio blocked " +
  "channel attached handle available available no set-transmit unavailable " +
  "attempts 0 forwarded 0 key 0 unkey 0 accepted 0 rejected 0 unknown 0 " +
  "last none/none reason transport-disabled";
const productionEmergencyUnkeyTransport = {
  registered: true,
  configuredEnabled: false,
  localFlexEligible: true,
  radioAllowed: false,
  commandChannelAttached: true,
  clientHandleAvailable: true,
  available: false,
  unkeyAvailable: false,
  commandTimeoutMilliseconds: 2000,
  attemptCount: 0,
  forwardedCount: 0,
  acceptedCount: 0,
  rejectedCount: 0,
  unknownCount: 0,
  lastOutcome: "none",
  lastReason: "transport-disabled"
};
const productionEmergencyUnkeyTransportText =
  "emergency unkey transport config disabled eligible yes radio blocked " +
  "channel attached handle available available no unkey unavailable " +
  "attempts 0 forwarded 0 accepted 0 rejected 0 unknown 0 " +
  "last none reason transport-disabled";

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
    stationCommandAdapterRegistered: true,
    stationCommandArmingAvailable: false,
    stationCommandSetTransmitAvailable: false,
    stationCommandAuditCount: 0,
    stationCommandAdapterComposition: {
      registered: true,
      executorAttached: true,
      executorRegistered: true,
      authoritySnapshotAvailable: false,
      commandAdapterRegistered: true,
      armingAvailable: false,
      setTransmitAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOutcome: "none",
      reason: "executor-arming-unavailable"
    },
    stationCommandSafetyArmAuthority: {
      registered: true,
      boundaryEnabled: false,
      signatureVerificationAvailable: false,
      commandAdapterRegistered: true,
      adapterExecutorAttached: true,
      adapterExecutorRegistered: true,
      gateExecutorRegistered: true,
      gateTransmitEnabled: false,
      commandTransportAvailable: false,
      gateSetTransmitAvailable: false,
      sessionAuthoritySnapshotAvailable: false,
      gateState: "Disabled",
      safetyState: "Disarmed",
      armAvailable: false,
      heartbeatAvailable: false,
      abortAvailable: false,
      attemptCount: 0,
      acceptedCount: 0,
      rejectedCount: 0,
      lastOperation: "none",
      lastOutcome: "none",
      reason: "connection-unavailable"
    },
    stationCommandSafetyArmComposition: {
      registered: true,
      armAuthorityAttached: true,
      armAuthorityRegistered: true,
      sessionAuthoritySnapshotAvailable: false,
      armAvailable: false,
      heartbeatAvailable: false,
      abortAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOperation: "none",
      lastOutcome: "none",
      reason: "connection-unavailable"
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
    stationCommandTransactionComposition: {
      registered: true,
      safetyArmCompositionAttached: true,
      commandSessionCompositionAttached: true,
      authoritySnapshotAvailable: false,
      keyAvailable: false,
      heartbeatAvailable: false,
      unkeyAvailable: false,
      abortAvailable: false,
      active: false,
      reconciliationRequired: false,
      state: "idle",
      attemptCount: 0,
      armForwardedCount: 0,
      commandForwardedCount: 0,
      heartbeatForwardedCount: 0,
      cleanupForwardedCount: 0,
      acceptedCount: 0,
      rejectedCount: 0,
      unknownCount: 0,
      lastOperation: "none",
      lastOutcome: "none",
      reason: "submission-disabled"
    },
    browserTxTransactionIngress: {
      registered: true,
      executionEnabled: false,
      transactionBoundaryAttached: true,
      keyAvailable: false,
      unkeyAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      acceptedCount: 0,
      rejectedCount: 0,
      unknownCount: 0,
      lastOutcome: "none",
      lastReason: "execution-disabled"
    },
    productionCommandTransport,
    productionEmergencyUnkeyTransport,
    productionReadiness,
    productionActivation,
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
      restartCount: 1,
      radioCommandTransportAvailable: false,
      armingAvailable: false,
      armed: false,
      heartbeatDeadlineAt: null,
      unkeyAttemptCount: 0,
      unkeyAcceptedCount: 0,
      unkeyRejectedCount: 0,
      unkeyUnknownCount: 0,
      lastUnkeyOutcome: "none",
      lastUnkeyReason: "none"
    },
    lastObservation: "gateway-heartbeat"
  }, now), {
    value: "DISABLED · DISARMED · NO LEASE",
    detail:
      "browser 4/fresh (1s ago) · engine 7/fresh (3s ago) · " +
      "gateway 9/fresh (2s ago) · lease 2 (10s ago) · " +
      "watchdog 3 (1s ago) · independent disarmed pid 4242 " +
      "host watchdog seq 0 restarts 1 unkey-transport disabled arming unavailable " +
      "armed no deadline none unkey-attempts 0 accepted 0 rejected 0 unknown 0 " +
      "last none/none · " +
      "command boundary v1 disabled " +
      "signature absent adapter registered arming absent set-transmit absent " +
      "audit 0 · adapter composition executor attached registered yes authority " +
      "absent adapter registered arming absent set-transmit absent attempts 0 " +
      "forwarded 0 last none reason executor-arming-unavailable · safety arm authority " +
      "boundary disabled signature absent adapter registered executor attached/registered " +
      "gate registered transmit disabled transport absent set-transmit absent session authority " +
      "absent gate-state Disabled safety-state Disarmed arm unavailable heartbeat unavailable " +
      "abort unavailable attempts 0 accepted 0 rejected 0 last none/none reason " +
      "connection-unavailable · safety arm composition authority attached registered yes " +
      "session authority absent arm unavailable heartbeat unavailable abort unavailable attempts 0 " +
      "forwarded 0 last none/none reason connection-unavailable · command composition " +
      "coordinator attached boundary attached " +
      "authority absent submission unavailable attempts 0 forwarded 0 " +
      "last none reason submission-disabled · transaction lifecycle boundary safety attached " +
      "command attached authority absent key unavailable heartbeat unavailable unkey unavailable " +
      "abort unavailable active no reconcile no state idle attempts 0 arm 0 command 0 " +
      "heartbeat-forwarded 0 cleanup 0 accepted 0 rejected 0 unknown 0 last none/none " +
      "reason submission-disabled · browser transaction ingress execution disabled boundary " +
      "attached key unavailable unkey unavailable attempts 0 forwarded 0 accepted 0 rejected 0 " +
      "unknown 0 last none reason execution-disabled · " +
      `${productionCommandTransportText} · ${productionReadinessText} · ` +
      `${productionActivationText} · authority no-active-lease · ` +
      `last gateway-heartbeat · ` +
      productionEmergencyUnkeyTransportText
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
    stationCommandAdapterRegistered: true,
    stationCommandArmingAvailable: false,
    stationCommandSetTransmitAvailable: false,
    stationCommandAuditCount: 0,
    stationCommandAdapterComposition: {
      registered: true,
      executorAttached: true,
      executorRegistered: true,
      authoritySnapshotAvailable: false,
      commandAdapterRegistered: true,
      armingAvailable: false,
      setTransmitAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOutcome: "none",
      reason: "executor-arming-unavailable"
    },
    stationCommandSafetyArmAuthority: {
      registered: true,
      boundaryEnabled: false,
      signatureVerificationAvailable: true,
      commandAdapterRegistered: true,
      adapterExecutorAttached: true,
      adapterExecutorRegistered: true,
      gateExecutorRegistered: true,
      gateTransmitEnabled: false,
      commandTransportAvailable: false,
      gateSetTransmitAvailable: false,
      sessionAuthoritySnapshotAvailable: false,
      gateState: "Disabled",
      safetyState: "Disarmed",
      armAvailable: false,
      heartbeatAvailable: false,
      abortAvailable: false,
      attemptCount: 0,
      acceptedCount: 0,
      rejectedCount: 0,
      lastOperation: "none",
      lastOutcome: "none",
      reason: "connection-unavailable"
    },
    stationCommandSafetyArmComposition: {
      registered: true,
      armAuthorityAttached: true,
      armAuthorityRegistered: true,
      sessionAuthoritySnapshotAvailable: false,
      armAvailable: false,
      heartbeatAvailable: false,
      abortAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOperation: "none",
      lastOutcome: "none",
      reason: "connection-unavailable"
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
    stationCommandTransactionComposition: {
      registered: true,
      safetyArmCompositionAttached: true,
      commandSessionCompositionAttached: true,
      authoritySnapshotAvailable: false,
      keyAvailable: false,
      heartbeatAvailable: false,
      unkeyAvailable: false,
      abortAvailable: false,
      active: false,
      reconciliationRequired: false,
      state: "idle",
      attemptCount: 0,
      armForwardedCount: 0,
      commandForwardedCount: 0,
      heartbeatForwardedCount: 0,
      cleanupForwardedCount: 0,
      acceptedCount: 0,
      rejectedCount: 0,
      unknownCount: 0,
      lastOperation: "none",
      lastOutcome: "none",
      reason: "submission-disabled"
    },
    browserTxTransactionIngress: {
      registered: true,
      executionEnabled: false,
      transactionBoundaryAttached: true,
      keyAvailable: false,
      unkeyAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      acceptedCount: 0,
      rejectedCount: 0,
      unknownCount: 0,
      lastOutcome: "none",
      lastReason: "execution-disabled"
    },
    productionCommandTransport,
    productionReadiness,
    productionActivation,
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
    /command boundary v1 disabled signature available adapter registered arming absent set-transmit absent audit 0/);
  assert.match(
    result.detail,
    /adapter composition executor attached registered yes authority absent adapter registered arming absent set-transmit absent attempts 0 forwarded 0 last none reason executor-arming-unavailable/);
  assert.match(
    result.detail,
    /safety arm authority boundary disabled signature available adapter registered executor attached\/registered gate registered transmit disabled transport absent set-transmit absent session authority absent gate-state Disabled safety-state Disarmed arm unavailable heartbeat unavailable abort unavailable attempts 0 accepted 0 rejected 0 last none\/none reason connection-unavailable/);
  assert.match(
    result.detail,
    /safety arm composition authority attached registered yes session authority absent arm unavailable heartbeat unavailable abort unavailable attempts 0 forwarded 0 last none\/none reason connection-unavailable/);
  assert.match(
    result.detail,
    /command composition coordinator attached boundary attached authority absent submission unavailable attempts 0 forwarded 0 last none reason submission-disabled/);
  assert.match(
    result.detail,
    /transaction lifecycle boundary safety attached command attached authority absent key unavailable heartbeat unavailable unkey unavailable abort unavailable active no reconcile no state idle attempts 0 arm 0 command 0 heartbeat-forwarded 0 cleanup 0 accepted 0 rejected 0 unknown 0 last none\/none reason submission-disabled/);
  assert.match(
    result.detail,
    /browser transaction ingress execution disabled boundary attached key unavailable unkey unavailable attempts 0 forwarded 0 accepted 0 rejected 0 unknown 0 last none reason execution-disabled/);
  assert.ok(result.detail.includes(productionReadinessText));
  assert.match(
    result.detail,
    /production command transport config disabled eligible yes radio blocked channel attached handle available available no set-transmit unavailable attempts 0 forwarded 0 key 0 unkey 0 accepted 0 rejected 0 unknown 0 last none\/none reason transport-disabled/);
  assert.match(result.detail, /emergency unkey absent/);
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
      executorAttached: true,
      executorRegistered: true,
      authoritySnapshotAvailable: true,
      commandAdapterRegistered: true,
      armingAvailable: false,
      setTransmitAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOutcome: "none",
      reason: "executor-arming-unavailable"
    },
    stationCommandSafetyArmAuthority: {
      registered: true,
      boundaryEnabled: false,
      signatureVerificationAvailable: false,
      commandAdapterRegistered: true,
      adapterExecutorAttached: true,
      adapterExecutorRegistered: true,
      gateExecutorRegistered: true,
      gateTransmitEnabled: false,
      commandTransportAvailable: false,
      gateSetTransmitAvailable: false,
      sessionAuthoritySnapshotAvailable: true,
      gateState: "Disabled",
      safetyState: "Disarmed",
      armAvailable: false,
      heartbeatAvailable: false,
      abortAvailable: false,
      attemptCount: 0,
      acceptedCount: 0,
      rejectedCount: 0,
      lastOperation: "none",
      lastOutcome: "none",
      reason: "occupancy-stale"
    },
    stationCommandSafetyArmComposition: {
      registered: true,
      armAuthorityAttached: true,
      armAuthorityRegistered: true,
      sessionAuthoritySnapshotAvailable: true,
      armAvailable: false,
      heartbeatAvailable: false,
      abortAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      lastOperation: "none",
      lastOutcome: "none",
      reason: "occupancy-stale"
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
    stationCommandTransactionComposition: {
      registered: true,
      safetyArmCompositionAttached: true,
      commandSessionCompositionAttached: true,
      authoritySnapshotAvailable: true,
      keyAvailable: false,
      heartbeatAvailable: false,
      unkeyAvailable: false,
      abortAvailable: false,
      active: false,
      reconciliationRequired: false,
      state: "idle",
      attemptCount: 0,
      armForwardedCount: 0,
      commandForwardedCount: 0,
      heartbeatForwardedCount: 0,
      cleanupForwardedCount: 0,
      acceptedCount: 0,
      rejectedCount: 0,
      unknownCount: 0,
      lastOperation: "none",
      lastOutcome: "none",
      reason: "submission-disabled"
    },
    browserTxTransactionIngress: {
      registered: true,
      executionEnabled: false,
      transactionBoundaryAttached: true,
      keyAvailable: false,
      unkeyAvailable: false,
      attemptCount: 0,
      forwardedCount: 0,
      acceptedCount: 0,
      rejectedCount: 0,
      unknownCount: 0,
      lastOutcome: "none",
      lastReason: "execution-disabled"
    },
    productionCommandTransport,
    productionReadiness,
    productionActivation,
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
    /adapter composition executor attached registered yes authority available adapter registered arming absent set-transmit absent attempts 0 forwarded 0 last none reason executor-arming-unavailable/);
  assert.match(
    result.detail,
    /safety arm authority boundary disabled signature absent adapter registered executor attached\/registered gate registered transmit disabled transport absent set-transmit absent session authority available gate-state Disabled safety-state Disarmed arm unavailable heartbeat unavailable abort unavailable attempts 0 accepted 0 rejected 0 last none\/none reason occupancy-stale/);
  assert.match(
    result.detail,
    /safety arm composition authority attached registered yes session authority available arm unavailable heartbeat unavailable abort unavailable attempts 0 forwarded 0 last none\/none reason occupancy-stale/);
  assert.match(
    result.detail,
    /command composition coordinator attached boundary attached authority available submission unavailable attempts 0 forwarded 0 last none reason submission-disabled/);
  assert.match(
    result.detail,
    /transaction lifecycle boundary safety attached command attached authority available key unavailable heartbeat unavailable unkey unavailable abort unavailable active no reconcile no state idle attempts 0 arm 0 command 0 heartbeat-forwarded 0 cleanup 0 accepted 0 rejected 0 unknown 0 last none\/none reason submission-disabled/);
  assert.match(
    result.detail,
    /browser transaction ingress execution disabled boundary attached key unavailable unkey unavailable attempts 0 forwarded 0 accepted 0 rejected 0 unknown 0 last none reason execution-disabled/);
  assert.ok(result.detail.includes(productionReadinessText));
  assert.match(
    result.detail,
    /production command transport config disabled eligible yes radio blocked channel attached handle available available no set-transmit unavailable attempts 0 forwarded 0 key 0 unkey 0 accepted 0 rejected 0 unknown 0 last none\/none reason transport-disabled/);
  assert.match(result.detail, /emergency unkey absent$/);
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
