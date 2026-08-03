export const txProtocolVersion = 2;
export const txLeaseSeconds = 10;
export const txHeartbeatMilliseconds = 2_000;
export const txPendingRequestLimit = 16;
export const txIntentActions = Object.freeze([
  "mox.set",
  "ptt.set",
  "tune.set",
  "microphone.set",
  "cw.send"
]);

const emptyCapability = Object.freeze({
  protocolVersion: txProtocolVersion,
  leaseConfigured: false,
  authenticated: false,
  roleAuthorized: false,
  connectionCurrent: false,
  radioConnected: false,
  occupancyAllowsLease: false,
  leaseHeldByBrowser: false,
  leaseAvailable: false,
  intentValidationAvailable: false,
  keyingAvailable: false,
  microphoneAvailable: false,
  tuneAvailable: false,
  cwAvailable: false,
  state: "unavailable",
  message: "Browser TX authority is unavailable."
});

export function normalizeTxCapability(value) {
  const capability = value && typeof value === "object" ? value : {};
  const version = Number(capability.protocolVersion);
  if (version !== txProtocolVersion) {
    return {
      ...emptyCapability,
      state: "unsupported-protocol",
      message: "The browser TX protocol version is unsupported."
    };
  }
  return {
    protocolVersion: txProtocolVersion,
    leaseConfigured: capability.leaseConfigured === true,
    authenticated: capability.authenticated === true,
    roleAuthorized: capability.roleAuthorized === true,
    connectionCurrent: capability.connectionCurrent === true,
    radioConnected: capability.radioConnected === true,
    occupancyAllowsLease: capability.occupancyAllowsLease === true,
    leaseHeldByBrowser: capability.leaseHeldByBrowser === true,
    leaseAvailable: capability.leaseAvailable === true,
    intentValidationAvailable:
      capability.intentValidationAvailable === true,
    keyingAvailable: capability.keyingAvailable === true,
    microphoneAvailable: capability.microphoneAvailable === true,
    tuneAvailable: capability.tuneAvailable === true,
    cwAvailable: capability.cwAvailable === true,
    state: boundedText(capability.state, 128) || "unavailable",
    message:
      boundedText(capability.message, 512) ||
      "Browser TX authority is unavailable."
  };
}

export function txControlAvailability(capabilityValue) {
  const capability = normalizeTxCapability(capabilityValue);
  return {
    showAuthorityPanel: capability.leaseConfigured,
    canAcquireLease: capability.leaseAvailable,
    canReleaseLease: capability.leaseHeldByBrowser,
    canValidateIntent:
      capability.leaseHeldByBrowser &&
      capability.intentValidationAvailable &&
      !capability.keyingAvailable,
    enableMox: capability.keyingAvailable,
    enablePtt: capability.keyingAvailable,
    enableTune: capability.tuneAvailable,
    enableMicrophone: capability.microphoneAvailable,
    enableCw: capability.cwAvailable
  };
}

export class BrowserTxController {
  constructor({
    send,
    nextRequestId,
    onChange = () => {},
    schedule = (callback, delay) => globalThis.setTimeout(callback, delay),
    cancel = timer => globalThis.clearTimeout(timer),
    now = () => Date.now(),
    createIntentId = defaultIntentId
  }) {
    if (typeof send !== "function" || typeof nextRequestId !== "function") {
      throw new TypeError("TX controller send and request ID functions are required.");
    }
    this.send = send;
    this.nextRequestId = nextRequestId;
    this.onChange = onChange;
    this.schedule = schedule;
    this.cancel = cancel;
    this.now = now;
    this.createIntentId = createIntentId;
    this.capability = { ...emptyCapability };
    this.lease = null;
    this.sequence = 0;
    this.pending = new Map();
    this.renewTimer = null;
    this.heartbeatTimer = null;
    this.transmitting = false;
    this.lastResult = null;
  }

  snapshot() {
    return {
      capability: { ...this.capability },
      lease: this.lease ? { ...this.lease } : null,
      sequence: this.sequence,
      pendingCount: this.pending.size,
      transmitting: this.transmitting,
      lastResult: this.lastResult ? { ...this.lastResult } : null
    };
  }

  applyWelcome(capability) {
    this.#cancelRenewal();
    this.#cancelHeartbeat();
    this.pending.clear();
    this.sequence = 0;
    this.lease = null;
    this.transmitting = false;
    this.capability = normalizeTxCapability(capability);
    this.lastResult = {
      outcome: "connected",
      error: null,
      validated: false
    };
    this.#notify();
  }

  applyCapability(capability) {
    const next = normalizeTxCapability(capability);
    if (!next.leaseHeldByBrowser) {
      this.#clearLease();
    } else if (!next.keyingAvailable) {
      this.#cancelHeartbeat();
      this.transmitting = false;
    }
    this.capability = next;
    this.#notify();
  }

  resetForDisconnect() {
    this.#cancelRenewal();
    this.#cancelHeartbeat();
    this.pending.clear();
    this.sequence = 0;
    this.lease = null;
    this.transmitting = false;
    this.capability = { ...emptyCapability };
    this.lastResult = {
      outcome: "disconnected",
      error: "The browser connection ended; any TX lease secret was discarded.",
      validated: false
    };
    this.#notify();
  }

  requestAcquire(seconds = txLeaseSeconds) {
    if (!this.capability.leaseAvailable || !validLeaseSeconds(seconds)) {
      return false;
    }
    return this.#sendRequest("acquire", {
      cmd: "tx.acquire",
      seconds
    });
  }

  requestRenew(seconds = txLeaseSeconds) {
    if (!this.lease ||
        !this.capability.leaseHeldByBrowser ||
        !validLeaseSeconds(seconds)) {
      return false;
    }
    return this.#sendRequest("renew", {
      cmd: "tx.renew",
      seconds,
      leaseId: this.lease.leaseId
    });
  }

  requestRelease() {
    if (!this.lease || !this.capability.leaseHeldByBrowser) {
      return false;
    }
    const leaseId = this.lease.leaseId;
    this.transmitting = false;
    this.#cancelHeartbeat();
    this.#clearLease();
    this.capability = {
      ...this.capability,
      leaseHeldByBrowser: false,
      intentValidationAvailable: false
    };
    return this.#sendRequest("release", {
      cmd: "tx.release",
      leaseId
    });
  }

  requestIntent(action, values) {
    if (!this.lease ||
        !this.capability.intentValidationAvailable ||
        !txIntentActions.includes(action) ||
        !validIntentValues(action, values)) {
      return false;
    }
    const intentId = boundedText(this.createIntentId(), 64);
    if (!intentId) {
      return false;
    }
    return this.#sendRequest("intent", {
      cmd: "tx.intent",
      leaseId: this.lease.leaseId,
      intentId,
      action,
      values
    }, intentId, {
      action,
      enabled: typeof values.enabled === "boolean" ? values.enabled : null
    });
  }

  handleMessage(message) {
    if (!message || typeof message !== "object") {
      return false;
    }

    if (message.event === "tx.lease.changed" ||
        message.event === "tx.lease.released") {
      if (message.protocolVersion !== txProtocolVersion) {
        this.#clearLease();
        this.pending.clear();
        this.capability = normalizeTxCapability({
          protocolVersion: message.protocolVersion
        });
        this.lastResult = {
          outcome: "unsupported-protocol",
          error: "The server TX lease event used an unsupported protocol version.",
          validated: false
        };
        this.#notify();
        return true;
      }
      this.capability = normalizeTxCapability(message.capability);
      if (message.event === "tx.lease.released" ||
          !this.capability.leaseHeldByBrowser) {
        this.transmitting = false;
        this.#cancelHeartbeat();
        this.#clearLease();
      }
      this.lastResult = {
        outcome: message.event,
        error: boundedText(message.reason, 256) || null,
        validated: false
      };
      this.#notify();
      return true;
    }

    if (message.protocolVersion !== txProtocolVersion ||
        typeof message.id !== "number" ||
        !Number.isSafeInteger(message.id) ||
        message.id <= 0 ||
        typeof message.sequence !== "number" ||
        !Number.isSafeInteger(message.sequence) ||
        message.sequence <= 0) {
      return false;
    }

    const requestId = message.id;
    const pending = this.pending.get(requestId);
    if (!pending || message.sequence !== pending.sequence) {
      return true;
    }
    this.pending.delete(requestId);
    this.capability = normalizeTxCapability(message.capability);

    if ((pending.kind === "acquire" || pending.kind === "renew") &&
        message.ok === true &&
        this.capability.leaseConfigured &&
        this.capability.leaseHeldByBrowser &&
        validLease(message.lease)) {
      this.lease = normalizeLease(message.lease);
      this.#scheduleRenewal();
    } else if (pending.kind === "release" && message.ok === true) {
      this.transmitting = false;
      this.#cancelHeartbeat();
      this.#clearLease();
    } else if (pending.kind === "intent" && message.ok === true) {
      if (pending.enabled === true) {
        this.transmitting = true;
        this.#scheduleHeartbeat();
      } else if (pending.enabled === false) {
        this.transmitting = false;
        this.#cancelHeartbeat();
      }
    } else if (pending.kind === "heartbeat" && message.ok === true) {
      this.#scheduleHeartbeat();
    } else if (pending.kind === "heartbeat" &&
        message.ok !== true &&
        this.transmitting) {
      this.transmitting = false;
      this.#cancelHeartbeat();
      this.#clearLease();
      this.capability = {
        ...this.capability,
        leaseHeldByBrowser: false,
        leaseAvailable: false,
        intentValidationAvailable: false,
        keyingAvailable: false
      };
    } else if (message.ok !== true &&
        (pending.kind === "renew" ||
          (pending.kind === "intent" && message.validated !== true) ||
          shouldDiscardLease(message.outcome, message.error))) {
      this.#clearLease();
      this.capability = {
        ...this.capability,
        leaseHeldByBrowser: false,
        leaseAvailable: false,
        intentValidationAvailable: false
      };
    }

    this.lastResult = {
      kind: pending.kind,
      outcome: boundedText(message.outcome, 128) ||
        (message.ok === true ? "ok" : "rejected"),
      error: boundedText(message.error, 512) || null,
      validated: message.validated === true,
      action: boundedText(message.action, 64) || null,
      intentId: boundedText(message.intentId, 64) || pending.intentId || null
    };
    this.#notify();
    return true;
  }

  #sendRequest(kind, fields, intentId = null, metadata = {}) {
    if (this.pending.size >= txPendingRequestLimit) {
      this.lastResult = {
        kind,
        outcome: "pending-limit",
        error: "Too many TX requests are awaiting exact server responses.",
        validated: false
      };
      this.#notify();
      return false;
    }

    const id = Number(this.nextRequestId());
    if (!Number.isSafeInteger(id) || id <= 0) {
      return false;
    }
    const sequence = this.sequence + 1;
    this.sequence = sequence;
    const message = {
      id,
      protocolVersion: txProtocolVersion,
      sequence,
      ...fields
    };
    this.pending.set(id, { kind, sequence, intentId, ...metadata });
    try {
      if (this.send(message) === false) {
        throw new Error("The radio session is not connected.");
      }
    } catch (error) {
      this.pending.delete(id);
      this.lastResult = {
        kind,
        outcome: "send-failed",
        error: error?.message || "The TX request could not be sent.",
        validated: false
      };
      this.#notify();
      return false;
    }
    this.#notify();
    return true;
  }

  #scheduleHeartbeat() {
    this.#cancelHeartbeat();
    if (!this.transmitting ||
        !this.lease ||
        !this.capability.leaseHeldByBrowser ||
        !this.capability.keyingAvailable) {
      return;
    }
    this.heartbeatTimer = this.schedule(() => {
      this.heartbeatTimer = null;
      if (!this.transmitting || !this.lease) {
        return;
      }
      if (!this.#sendRequest("heartbeat", {
        cmd: "tx.heartbeat",
        leaseId: this.lease.leaseId
      })) {
        this.transmitting = false;
        this.#clearLease();
        this.#notify();
      }
    }, txHeartbeatMilliseconds);
  }

  #scheduleRenewal() {
    this.#cancelRenewal();
    if (!this.lease) {
      return;
    }
    const expiresAt = Date.parse(this.lease.expiresAt);
    if (!Number.isFinite(expiresAt)) {
      this.#clearLease();
      return;
    }
    const delay = Math.max(250, expiresAt - this.now() - 3_000);
    this.renewTimer = this.schedule(() => {
      this.renewTimer = null;
      if (!this.requestRenew(txLeaseSeconds)) {
        this.#expireLocalLease("renewal-unavailable");
        return;
      }
      const expiryDelay = Math.max(0, expiresAt - this.now());
      this.renewTimer = this.schedule(() => {
        this.renewTimer = null;
        if (this.lease &&
            Date.parse(this.lease.expiresAt) <= this.now()) {
          this.#expireLocalLease("renewal-timeout");
        }
      }, expiryDelay);
    }, delay);
  }

  #expireLocalLease(outcome) {
    this.#clearLease();
    this.capability = {
      ...this.capability,
      leaseHeldByBrowser: false,
      leaseAvailable: false,
      intentValidationAvailable: false,
      state: outcome,
      message: "The local TX lease expired without an exact successful renewal."
    };
    this.lastResult = {
      outcome,
      error: "The TX lease was discarded locally because renewal was not confirmed before expiry.",
      validated: false
    };
    this.#notify();
  }

  #clearLease() {
    this.#cancelRenewal();
    this.#cancelHeartbeat();
    this.transmitting = false;
    this.lease = null;
  }

  #cancelHeartbeat() {
    if (this.heartbeatTimer !== null) {
      this.cancel(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }
  }

  #cancelRenewal() {
    if (this.renewTimer !== null) {
      this.cancel(this.renewTimer);
      this.renewTimer = null;
    }
  }

  #notify() {
    this.onChange(this.snapshot());
  }
}

function validLeaseSeconds(seconds) {
  return Number.isInteger(Number(seconds)) &&
    Number(seconds) >= 1 && Number(seconds) <= 15;
}

function validLease(value) {
  return value &&
    typeof value === "object" &&
    /^[0-9a-f]{32}$/.test(String(value.leaseId || "")) &&
    Number.isFinite(Date.parse(value.expiresAt));
}

function normalizeLease(value) {
  return {
    leaseId: String(value.leaseId),
    radioId: boundedText(value.radioId, 128),
    sessionId: boundedText(value.sessionId, 128),
    clientId: boundedText(value.clientId, 128),
    acquiredAt: value.acquiredAt,
    renewedAt: value.renewedAt,
    expiresAt: value.expiresAt
  };
}

function validIntentValues(action, values) {
  if (!values || typeof values !== "object" || Array.isArray(values)) {
    return false;
  }
  const keys = Object.keys(values);
  if (action === "cw.send") {
    const text = String(values.text || "").trim();
    return keys.length === 1 && keys[0] === "text" &&
      text.length > 0 && text.length <= 32 &&
      /^[\x20-\x21\x23-\x5b\x5d-\x7e]+$/.test(text);
  }
  return keys.length === 1 && keys[0] === "enabled" &&
    typeof values.enabled === "boolean";
}

function shouldDiscardLease(outcome, error) {
  const value = `${outcome || ""} ${error || ""}`.toLowerCase();
  return value.includes("lease-invalid") ||
    value.includes("authentication") ||
    value.includes("connection-replaced") ||
    value.includes("missing, expired") ||
    value.includes("current tx lease");
}

function boundedText(value, maximumLength) {
  const text = typeof value === "string" ? value.trim() : "";
  return text.length > 0 && text.length <= maximumLength &&
    !/[\u0000-\u001f\u007f]/.test(text)
      ? text
      : "";
}

function defaultIntentId() {
  if (globalThis.crypto?.randomUUID) {
    return globalThis.crypto.randomUUID();
  }
  if (typeof globalThis.crypto?.getRandomValues !== "function") {
    return "";
  }
  const bytes = new Uint8Array(16);
  globalThis.crypto.getRandomValues(bytes);
  return [...bytes]
    .map(value => value.toString(16).padStart(2, "0"))
    .join("");
}
