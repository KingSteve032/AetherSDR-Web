import test from "node:test";
import assert from "node:assert/strict";
import {
  BrowserTxController,
  normalizeTxCapability,
  txControlAvailability,
  txPendingRequestLimit,
  txProtocolVersion
} from "../wwwroot/tx-controls.js";

const readyCapability = {
  protocolVersion: 1,
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

test("TX capability defaults and unsupported versions fail closed", () => {
  const capability = normalizeTxCapability(null);
  assert.equal(capability.leaseConfigured, false);
  assert.equal(capability.intentValidationAvailable, false);
  assert.equal(capability.keyingAvailable, false);
  assert.equal(
    normalizeTxCapability({
      ...availableCapability,
      protocolVersion: 2
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
  controller.requestAcquire();

  assert.equal(controller.handleMessage({
    id: 1,
    protocolVersion: 1,
    sequence: 1,
    ok: true,
    lease,
    capability: readyCapability
  }), true);
  assert.equal(controller.snapshot().lease.leaseId, lease.leaseId);
  assert.equal(scheduled.length, 1);
  assert.equal(scheduled[0].delay, 7_000);

  scheduled[0].callback();
  assert.deepEqual(sent[1], {
    id: 2,
    protocolVersion: 1,
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
  controller.requestAcquire();
  controller.handleMessage({
    id: 1,
    protocolVersion: 1,
    sequence: 1,
    ok: true,
    lease,
    capability: readyCapability
  });

  assert.equal(controller.requestIntent("mox.set", { enabled: true }), true);
  assert.deepEqual(sent[1], {
    id: 2,
    protocolVersion: 1,
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
  const controller = new BrowserTxController({
    send: () => {},
    nextRequestId: (() => {
      let value = 0;
      return () => ++value;
    })(),
    schedule: () => 1,
    cancel: () => {},
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  controller.requestAcquire();
  controller.handleMessage({
    id: 1,
    protocolVersion: 1,
    sequence: 1,
    ok: true,
    lease,
    capability: readyCapability
  });
  controller.requestIntent("tune.set", { enabled: true });
  controller.handleMessage({
    id: 2,
    protocolVersion: 1,
    sequence: 2,
    ok: false,
    validated: true,
    outcome: "transport-unavailable",
    error: "Production command transport is unavailable.",
    action: "tune.set",
    intentId: "intent-a",
    capability: readyCapability
  });

  const snapshot = controller.snapshot();
  assert.equal(snapshot.lastResult.validated, true);
  assert.equal(snapshot.lastResult.outcome, "transport-unavailable");
  assert.deepEqual(txControlAvailability(snapshot.capability), {
    showAuthorityPanel: true,
    canAcquireLease: false,
    canReleaseLease: true,
    canValidateIntent: true,
    enableMox: false,
    enablePtt: false,
    enableTune: false,
    enableMicrophone: false,
    enableCw: false
  });
});

test("disconnect discards the opaque lease and resets sequence", () => {
  const controller = new BrowserTxController({
    send: () => {},
    nextRequestId: () => 1,
    schedule: () => 10,
    cancel: () => {},
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  controller.requestAcquire();
  controller.handleMessage({
    id: 1,
    protocolVersion: 1,
    sequence: 1,
    ok: true,
    lease,
    capability: readyCapability
  });

  controller.resetForDisconnect();
  const snapshot = controller.snapshot();
  assert.equal(snapshot.lease, null);
  assert.equal(snapshot.sequence, 0);
  assert.equal(snapshot.capability.leaseConfigured, false);
  assert.equal(snapshot.lastResult.outcome, "disconnected");
});

test("deliberate release cancels renewal and discards the secret before response", () => {
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
  controller.requestAcquire();
  controller.handleMessage({
    id: 1,
    protocolVersion: 1,
    sequence: 1,
    ok: true,
    lease,
    capability: readyCapability
  });

  assert.equal(controller.requestRelease(), true);
  assert.equal(controller.snapshot().lease, null);
  assert.equal(
    controller.snapshot().capability.intentValidationAvailable,
    false);
  assert.deepEqual(cancelled, [77]);
  assert.deepEqual(sent[1], {
    id: 2,
    protocolVersion: 1,
    sequence: 2,
    cmd: "tx.release",
    leaseId: lease.leaseId
  });
});

test("lease release event clears the local secret without trusting redacted status", () => {
  const controller = new BrowserTxController({
    send: () => {},
    nextRequestId: () => 1,
    schedule: () => 10,
    cancel: () => {},
    now: () => Date.parse("2026-07-31T16:00:00Z"),
    createIntentId: () => "intent-a"
  });
  controller.applyWelcome(availableCapability);
  controller.requestAcquire();
  controller.handleMessage({
    id: 1,
    protocolVersion: 1,
    sequence: 1,
    ok: true,
    lease,
    capability: readyCapability
  });

  assert.equal(controller.handleMessage({
    event: "tx.lease.released",
    protocolVersion: 1,
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
  controller.requestAcquire();
  controller.handleMessage({
    id: 1,
    protocolVersion: 1,
    sequence: 1,
    ok: true,
    lease,
    capability: readyCapability
  });

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
  controller.requestAcquire();
  controller.handleMessage({
    id: 1,
    protocolVersion: 1,
    sequence: 1,
    ok: true,
    lease,
    capability: readyCapability
  });
  controller.requestIntent("mox.set", { enabled: true });

  assert.equal(controller.handleMessage({
    event: "tx.lease.changed",
    protocolVersion: 2,
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
    protocolVersion: 1,
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
    send: () => {},
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
    protocolVersion: 1,
    sequence: 99,
    ok: true,
    lease,
    capability: readyCapability
  }), true);
  assert.equal(controller.snapshot().lease, null);
});
