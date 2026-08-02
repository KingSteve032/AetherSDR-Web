import test from "node:test";
import assert from "node:assert/strict";
import {
  BrowserTxController,
  normalizeTxCapability,
  txControlAvailability,
  txHeartbeatMilliseconds,
  txPendingRequestLimit,
  txProtocolVersion
} from "../wwwroot/tx-controls.js";

const readyCapability = {
  protocolVersion: txProtocolVersion,
  leaseConfigured: true,
  authenticated: true,
  roleAuthorized: true,
  connectionCurrent: true,
  radioConnected: true,
  occupancyAllowsLease: true,
  leaseHeldByBrowser: true,
  leaseAvailable: false,
  intentValidationAvailable: true,
  keyingAvailable: false,
  microphoneAvailable: false,
  tuneAvailable: false,
  cwAvailable: false,
  state: "intent-validation-ready",
  message: "Validated only"
};

const liveCapability = {
  ...readyCapability,
  keyingAvailable: true,
  state: "keying-ready",
  message: "Protected MOX/PTT ready"
};

const activeCapability = {
  ...liveCapability,
  occupancyAllowsLease: false,
  state: "transmit-active",
  message: "Protected TX active"
};

const availableCapability = {
  ...readyCapability,
  leaseHeldByBrowser: false,
  leaseAvailable: true,
  intentValidationAvailable: false,
  state: "lease-available"
};

const lease = {
  leaseId: "0123456789abcdef0123456789abcdef",
  radioId: "RADIO-A",
  sessionId: "session-a",
  clientId: "connection-a",
  acquiredAt: "2026-07-31T16:00:00Z",
  renewedAt: "2026-07-31T16:00:00Z",
  expiresAt: "2026-07-31T16:00:10Z"
};

function acquire(controller, capability = readyCapability) {
  assert.equal(controller.requestAcquire(), true);
  assert.equal(controller.handleMessage({
    id: 1,
    protocolVersion: txProtocolVersion,
    sequence: 1,
    ok: true,
    lease,
    capability
  }), true);
}

test("TX capability defaults and unsupported versions fail closed", () => {
  const capability = normalizeTxCapability(null);
  assert.equal(capability.leaseConfigured, false);
  assert.equal(capability.intentValidationAvailable, false);
  assert.equal(capability.keyingAvailable, false);
  assert.equal(
    normalizeTxCapability({
      ...availableCapability,
      protocolVersion: txProtocolVersion + 1
    }).leaseConfigured,
    false);
  assert.deepEqual(txControlAvailability(capability), {
    showAuthorityPanel: false,
    canAcquireLease: false,
    canReleaseLease: false,
    canValidateIntent: false,
    enableMox: false,
    enablePtt: false,
    enableTune: false,
    enableMicrophone: false,
    enableCw: false
  });
});

test("live keying capability replaces the validation-only surface", () => {
  const availability = txControlAvailability(liveCapability);

  assert.equal(availability.canValidateIntent, false);
  assert.equal(availability.enableMox, true);
  assert.equal(availability.enablePtt, true);
  assert.equal(availability.enableTune, false);
  assert.equal(availability.enableMicrophone, false);
  assert.equal(availability.enableCw, false);
});

test("lease acquire uses an exact versioned monotonic envelope", () => {
  const sent = [];
  let requestId = 40;
  const controller = new BrowserTxController({
    send: message => sent.push(message),
    nextRequestId: () => ++requestId,
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);

  assert.equal(controller.requestAcquire(10), true);
  assert.deepEqual(sent, [{
    id: 41,
    protocolVersion: txProtocolVersion,
    sequence: 1,
    cmd: "tx.acquire",
    seconds: 10
  }]);
  assert.equal(controller.requestAcquire(16), false);
});

test("acquire response stores only the exact holder secret and schedules renewal", () => {
  const sent = [];
  const scheduled = [];
  let requestId = 0;
  const controller = new BrowserTxController({
    send: message => sent.push(message),
    nextRequestId: () => ++requestId,
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    schedule: (callback, delay) => {
      scheduled.push({ callback, delay });
      return scheduled.length;
    },
    cancel: () => {},
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  acquire(controller);

  assert.equal(controller.snapshot().lease.leaseId, lease.leaseId);
  assert.equal(scheduled.length, 1);
  assert.equal(scheduled[0].delay, 7_000);

  scheduled[0].callback();
  assert.deepEqual(sent[1], {
    id: 2,
    protocolVersion: txProtocolVersion,
    sequence: 2,
    cmd: "tx.renew",
    seconds: 10,
    leaseId: lease.leaseId
  });
});

test("deliberate dry-run intent requires exact lease and validation readiness", () => {
  const sent = [];
  let requestId = 0;
  const controller = new BrowserTxController({
    send: message => sent.push(message),
    nextRequestId: () => ++requestId,
    schedule: () => 1,
    cancel: () => {},
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => "intent-validated-a"
  });
  controller.applyWelcome(availableCapability);
  acquire(controller);

  assert.equal(controller.requestIntent("mox.set", { enabled: true }), true);
  assert.deepEqual(sent[1], {
    id: 2,
    protocolVersion: txProtocolVersion,
    sequence: 2,
    cmd: "tx.intent",
    leaseId: lease.leaseId,
    intentId: "intent-validated-a",
    action: "mox.set",
    values: { enabled: true }
  });
  assert.equal(controller.requestIntent("cw.send", { text: "" }), false);
  assert.equal(controller.requestIntent("unknown", { enabled: true }), false);
});

test("validated transport-unavailable result never grants a real control", () => {
  let requestId = 0;
  const controller = new BrowserTxController({
    send: () => true,
    nextRequestId: () => ++requestId,
    schedule: () => 1,
    cancel: () => {},
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  acquire(controller);
  controller.requestIntent("tune.set", { enabled: true });
  controller.handleMessage({
    id: 2,
    protocolVersion: txProtocolVersion,
    sequence: 2,
    ok: false,
    validated: true,
    outcome: "transport-unavailable",
    error: "Production activation is unavailable.",
    action: "tune.set",
    intentId: "intent-a",
    capability: readyCapability
  });

  const snapshot = controller.snapshot();
  assert.equal(snapshot.lastResult.validated, true);
  assert.equal(snapshot.lastResult.outcome, "transport-unavailable");
  assert.equal(snapshot.transmitting, false);
  assert.equal(txControlAvailability(snapshot.capability).enableMox, false);
});

test("accepted key starts a purpose-bound heartbeat and accepted unkey stops it", () => {
  const sent = [];
  const timers = new Map();
  const cancelled = [];
  let requestId = 0;
  let timerId = 0;
  const controller = new BrowserTxController({
    send: message => sent.push(message),
    nextRequestId: () => ++requestId,
    schedule: (callback, delay) => {
      const id = ++timerId;
      timers.set(id, { callback, delay });
      return id;
    },
    cancel: id => {
      cancelled.push(id);
      timers.delete(id);
    },
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => `intent-${requestId + 1}`
  });
  controller.applyWelcome(availableCapability);
  acquire(controller, liveCapability);

  assert.equal(controller.requestIntent("mox.set", { enabled: true }), true);
  assert.equal(controller.handleMessage({
    id: 2,
    protocolVersion: txProtocolVersion,
    sequence: 2,
    ok: true,
    validated: true,
    outcome: "key-confirmed",
    action: "mox.set",
    intentId: "intent-2",
    capability: activeCapability
  }), true);
  assert.equal(controller.snapshot().transmitting, true);
  const heartbeat = [...timers.entries()]
    .find(([, value]) => value.delay === txHeartbeatMilliseconds);
  assert.ok(heartbeat);

  timers.delete(heartbeat[0]);
  heartbeat[1].callback();
  assert.equal(sent.at(-1).cmd, "tx.heartbeat");
  const heartbeatRequest = sent.at(-1);
  assert.equal(controller.handleMessage({
    id: heartbeatRequest.id,
    protocolVersion: txProtocolVersion,
    sequence: heartbeatRequest.sequence,
    ok: true,
    outcome: "heartbeat-accepted",
    capability: activeCapability
  }), true);
  assert.ok([...timers.values()].some(
    value => value.delay === txHeartbeatMilliseconds));

  assert.equal(controller.requestIntent("mox.set", { enabled: false }), true);
  const unkeyRequest = sent.at(-1);
  assert.equal(controller.handleMessage({
    id: unkeyRequest.id,
    protocolVersion: txProtocolVersion,
    sequence: unkeyRequest.sequence,
    ok: true,
    validated: true,
    outcome: "unkey-confirmed",
    action: "mox.set",
    intentId: unkeyRequest.intentId,
    capability: liveCapability
  }), true);
  assert.equal(controller.snapshot().transmitting, false);
  assert.ok(cancelled.length > 0);
});

test("stale heartbeat failure after confirmed unkey does not discard the idle lease", () => {
  const sent = [];
  const scheduled = [];
  let requestId = 0;
  const controller = new BrowserTxController({
    send: message => sent.push(message),
    nextRequestId: () => ++requestId,
    schedule: (callback, delay) => {
      scheduled.push({ callback, delay });
      return scheduled.length;
    },
    cancel: () => {},
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => `intent-${requestId + 1}`
  });
  controller.applyWelcome(availableCapability);
  acquire(controller, liveCapability);
  controller.requestIntent("mox.set", { enabled: true });
  controller.handleMessage({
    id: 2,
    protocolVersion: txProtocolVersion,
    sequence: 2,
    ok: true,
    validated: true,
    outcome: "key_confirmed",
    action: "mox.set",
    intentId: "intent-2",
    capability: activeCapability
  });
  const heartbeat = scheduled.find(
    item => item.delay === txHeartbeatMilliseconds);
  heartbeat.callback();
  const heartbeatRequest = sent.at(-1);

  controller.requestIntent("mox.set", { enabled: false });
  const unkeyRequest = sent.at(-1);
  controller.handleMessage({
    id: unkeyRequest.id,
    protocolVersion: txProtocolVersion,
    sequence: unkeyRequest.sequence,
    ok: true,
    validated: true,
    outcome: "unkey_confirmed",
    action: "mox.set",
    intentId: unkeyRequest.intentId,
    capability: liveCapability
  });
  controller.handleMessage({
    id: heartbeatRequest.id,
    protocolVersion: txProtocolVersion,
    sequence: heartbeatRequest.sequence,
    ok: false,
    outcome: "stale-heartbeat",
    error: "Heartbeat completed after unkey.",
    capability: liveCapability
  });

  const snapshot = controller.snapshot();
  assert.equal(snapshot.transmitting, false);
  assert.equal(snapshot.lease.leaseId, lease.leaseId);
  assert.equal(snapshot.capability.keyingAvailable, true);
});

test("heartbeat failure while transmitting discards local authority", () => {
  const sent = [];
  const scheduled = [];
  let requestId = 0;
  const controller = new BrowserTxController({
    send: message => sent.push(message),
    nextRequestId: () => ++requestId,
    schedule: (callback, delay) => {
      scheduled.push({ callback, delay });
      return scheduled.length;
    },
    cancel: () => {},
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => "intent-key"
  });
  controller.applyWelcome(availableCapability);
  acquire(controller, liveCapability);
  controller.requestIntent("ptt.set", { enabled: true });
  controller.handleMessage({
    id: 2,
    protocolVersion: txProtocolVersion,
    sequence: 2,
    ok: true,
    validated: true,
    outcome: "key-confirmed",
    action: "ptt.set",
    intentId: "intent-key",
    capability: activeCapability
  });
  const heartbeat = scheduled.find(
    item => item.delay === txHeartbeatMilliseconds);
  heartbeat.callback();
  const request = sent.at(-1);
  controller.handleMessage({
    id: request.id,
    protocolVersion: txProtocolVersion,
    sequence: request.sequence,
    ok: false,
    outcome: "heartbeat-authority-lost",
    error: "Authority was lost.",
    capability: readyCapability
  });

  const snapshot = controller.snapshot();
  assert.equal(snapshot.transmitting, false);
  assert.equal(snapshot.lease, null);
  assert.equal(snapshot.capability.keyingAvailable, false);
});

test("disconnect discards the opaque lease, heartbeat, and sequence", () => {
  let requestId = 0;
  const controller = new BrowserTxController({
    send: () => true,
    nextRequestId: () => ++requestId,
    schedule: () => 10,
    cancel: () => {},
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  acquire(controller);

  controller.resetForDisconnect();
  const snapshot = controller.snapshot();
  assert.equal(snapshot.lease, null);
  assert.equal(snapshot.transmitting, false);
  assert.equal(snapshot.sequence, 0);
  assert.equal(snapshot.capability.leaseConfigured, false);
  assert.equal(snapshot.lastResult.outcome, "disconnected");
});

test("deliberate release cancels timers and discards the secret before response", () => {
  const sent = [];
  const cancelled = [];
  let requestId = 0;
  const controller = new BrowserTxController({
    send: message => sent.push(message),
    nextRequestId: () => ++requestId,
    schedule: () => 77,
    cancel: timer => cancelled.push(timer),
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  acquire(controller);

  assert.equal(controller.requestRelease(), true);
  assert.equal(controller.snapshot().lease, null);
  assert.equal(controller.snapshot().capability.intentValidationAvailable, false);
  assert.deepEqual(cancelled, [77]);
  assert.deepEqual(sent[1], {
    id: 2,
    protocolVersion: txProtocolVersion,
    sequence: 2,
    cmd: "tx.release",
    leaseId: lease.leaseId
  });
});

test("lease release event clears the local secret without trusting redacted status", () => {
  let requestId = 0;
  const controller = new BrowserTxController({
    send: () => true,
    nextRequestId: () => ++requestId,
    schedule: () => 10,
    cancel: () => {},
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  acquire(controller);

  assert.equal(controller.handleMessage({
    event: "tx.lease.released",
    protocolVersion: txProtocolVersion,
    reason: "expired",
    lease: { radioId: "RADIO-A", displayName: "Operator A" },
    capability: availableCapability
  }), true);
  assert.equal(controller.snapshot().lease, null);
  assert.equal(controller.snapshot().lastResult.outcome, "tx.lease.released");
});

test("disconnected send fails closed without retaining a pending request", () => {
  let requestId = 0;
  const controller = new BrowserTxController({
    send: () => false,
    nextRequestId: () => ++requestId,
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);

  assert.equal(controller.requestAcquire(), false);
  const snapshot = controller.snapshot();
  assert.equal(snapshot.lease, null);
  assert.equal(snapshot.lastResult.outcome, "send-failed");
  assert.match(snapshot.lastResult.error, /not connected/i);
});

test("pending TX requests are bounded when exact responses never arrive", () => {
  let requestId = 0;
  const controller = new BrowserTxController({
    send: () => true,
    nextRequestId: () => ++requestId,
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);

  for (let index = 0; index < txPendingRequestLimit; index += 1) {
    assert.equal(controller.requestAcquire(), true);
  }
  assert.equal(controller.requestAcquire(), false);
  assert.equal(controller.snapshot().pendingCount, txPendingRequestLimit);
  assert.equal(controller.snapshot().lastResult.outcome, "pending-limit");
});

test("unconfirmed renewal discards the local lease at server expiry", () => {
  const sent = [];
  const timers = new Map();
  let nextTimer = 0;
  let requestId = 0;
  let now = Date.parse("2026-07-31T16:00:00Z");
  const controller = new BrowserTxController({
    send: message => sent.push(message),
    nextRequestId: () => ++requestId,
    now: () => now,
    schedule: (callback, delay) => {
      const id = ++nextTimer;
      timers.set(id, { callback, delay });
      return id;
    },
    cancel: timer => timers.delete(timer),
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  acquire(controller);

  const renewal = timers.get(1);
  assert.equal(renewal.delay, 7_000);
  now = Date.parse("2026-07-31T16:00:07Z");
  timers.delete(1);
  renewal.callback();
  assert.equal(sent[1].cmd, "tx.renew");

  const expiry = timers.get(2);
  assert.equal(expiry.delay, 3_000);
  now = Date.parse("2026-07-31T16:00:10Z");
  timers.delete(2);
  expiry.callback();

  const snapshot = controller.snapshot();
  assert.equal(snapshot.lease, null);
  assert.equal(snapshot.capability.intentValidationAvailable, false);
  assert.equal(snapshot.lastResult.outcome, "renewal-timeout");
});

test("unsupported lease events discard secrets and pending authority", () => {
  let requestId = 0;
  const controller = new BrowserTxController({
    send: () => true,
    nextRequestId: () => ++requestId,
    schedule: () => 1,
    cancel: () => {},
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  acquire(controller);
  controller.requestIntent("mox.set", { enabled: true });

  assert.equal(controller.handleMessage({
    event: "tx.lease.changed",
    protocolVersion: txProtocolVersion + 1,
    capability: readyCapability
  }), true);
  const snapshot = controller.snapshot();
  assert.equal(snapshot.lease, null);
  assert.equal(snapshot.pendingCount, 0);
  assert.equal(snapshot.capability.leaseConfigured, false);
  assert.equal(snapshot.lastResult.outcome, "unsupported-protocol");
});

test("string-coerced response identifiers cannot mutate the lease", () => {
  let requestId = 0;
  const controller = new BrowserTxController({
    send: () => true,
    nextRequestId: () => ++requestId,
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  controller.requestAcquire();

  assert.equal(controller.handleMessage({
    id: "1",
    protocolVersion: txProtocolVersion,
    sequence: "1",
    ok: true,
    lease,
    capability: readyCapability
  }), false);
  assert.equal(controller.snapshot().lease, null);
});

test("mismatched and stale responses cannot mutate the lease", () => {
  let requestId = 0;
  const controller = new BrowserTxController({
    send: () => true,
    nextRequestId: () => ++requestId,
    schedule: () => 1,
    cancel: () => {},
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  controller.requestAcquire();

  assert.equal(controller.handleMessage({
    id: 1,
    protocolVersion: txProtocolVersion,
    sequence: 99,
    ok: true,
    lease,
    capability: readyCapability
  }), true);
  assert.equal(controller.snapshot().lease, null);
});
