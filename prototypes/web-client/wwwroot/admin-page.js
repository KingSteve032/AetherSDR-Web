import {
  buildPolicyRequest,
  buildTransmitPolicyRequest,
  formatAuditAction,
  formatAuditResult,
  formatClientCapacity,
  formatEnrollmentPurpose,
  formatRadioOwnership,
  formatStationCredentialSource,
  normalizeAdminMode,
  normalizeRadioLabel,
  normalizeStationId,
  normalizeTransmitPolicyState,
  stationIdValid
} from "./admin-controls.js?v=m8e-radio-onboarding-1";
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
  rememberSessionDiagnosticExpansion,
  sessionDiagnosticExpanded,
  shortId
} from "./admin-diagnostics.js?v=watchdog-arming-1";

const elements = {
  userName: document.querySelector("#user-name"),
  refresh: document.querySelector("#admin-refresh"),
  notice: document.querySelector("#admin-notice"),
  list: document.querySelector("#admin-radio-list"),
  summaryRadios: document.querySelector("#summary-radios"),
  summaryOnline: document.querySelector("#summary-online"),
  summaryOperators: document.querySelector("#summary-operators"),
  summaryStations: document.querySelector("#summary-stations"),
  summaryRemoteSessions: document.querySelector(
    "#summary-remote-sessions"),
  summaryExternalClients: document.querySelector(
    "#summary-external-clients"),
  stationStatus: document.querySelector("#admin-station-status"),
  stationList: document.querySelector("#admin-station-list"),
  enrollmentForm: document.querySelector("#admin-enrollment-form"),
  enrollmentStationId: document.querySelector(
    "#admin-enrollment-station-id"),
  enrollmentCreate: document.querySelector("#admin-enrollment-create"),
  enrollmentResult: document.querySelector("#admin-enrollment-result"),
  credentialList: document.querySelector("#admin-credential-list"),
  releaseForm: document.querySelector("#admin-release-form"),
  releaseIdentity: document.querySelector("#admin-release-identity"),
  releaseInstalledIdentity: document.querySelector(
    "#admin-release-installed-identity"),
  releaseInstalledVersion: document.querySelector(
    "#admin-release-installed-version"),
  releaseSchemaVersion: document.querySelector(
    "#admin-release-schema-version"),
  releaseProtocolVersion: document.querySelector(
    "#admin-release-protocol-version"),
  releasePrepare: document.querySelector("#admin-release-prepare"),
  releaseResult: document.querySelector("#admin-release-result"),
  releaseActivate: document.querySelector("#admin-release-activate"),
  releaseRollback: document.querySelector("#admin-release-rollback"),
  auditCount: document.querySelector("#admin-audit-count"),
  auditList: document.querySelector("#admin-audit-list")
};

const state = {
  radios: [],
  stationAdministration: {
    enabled: false,
    brokerReachable: false,
    refreshedAt: null,
    error: null,
    stations: [],
    credentials: []
  },
  enrollmentCode: null,
  enrollmentBootstrap: null,
  stationBootstrap: null,
  releaseTransaction: null,
  auditEvents: [],
  refreshing: false,
  sessionDiagnosticExpansion: new Map()
};

initialize().catch(error => {
  showNotice(error.message || "Administration could not be loaded.", true);
});

async function initialize() {
  const account = await getJson("/api/account");
  elements.userName.textContent =
    account.user.name || account.user.email || "Administrator";
  elements.refresh.addEventListener("click", () => refreshInventory(true));
  elements.enrollmentForm.addEventListener(
    "submit",
    createEnrollmentCode);
  elements.releaseForm.addEventListener("submit", prepareReleaseUpdate);
  elements.releaseActivate.addEventListener("click", activateReleaseUpdate);
  elements.releaseRollback.addEventListener("click", rollbackReleaseUpdate);
  await refreshInventory();
  window.setInterval(refreshInventory, 5000);
}

async function refreshInventory(announce = false) {
  if (state.refreshing) {
    return;
  }
  state.refreshing = true;
  elements.refresh.disabled = true;
  try {
    const [
      result,
      stations,
      bootstrap,
      audit,
      releaseTransaction
    ] = await Promise.all([
      getJson("/api/admin/radios"),
      getJson("/api/admin/stations"),
      getJson("/api/admin/stations/bootstrap"),
      getJson("/api/admin/audit?limit=50"),
      getJson("/api/admin/releases/transaction")
    ]);
    state.radios = Array.isArray(result.radios) ? result.radios : [];
    state.stationAdministration = stations || state.stationAdministration;
    state.stationBootstrap = bootstrap || null;
    state.auditEvents = Array.isArray(audit.events) ? audit.events : [];
    state.releaseTransaction = releaseTransaction || null;
    renderCredentialSecurity();
    renderReleaseTransaction();
    renderStations();
    renderInventory();
    renderAudit();
    const operatorCount = state.radios.reduce(
      (sum, radio) => sum + (radio.operators?.length || 0),
      0);
    elements.summaryRadios.textContent = String(state.radios.length);
    elements.summaryOnline.textContent =
      String(state.radios.filter(radio => radio.online).length);
    elements.summaryOperators.textContent = String(operatorCount);
    const remoteStations = Array.isArray(
      state.stationAdministration.stations)
      ? state.stationAdministration.stations
      : [];
    const remoteSessionCount = remoteStations.reduce(
      (sum, station) =>
        sum + (station.receiveSessions?.length || 0),
      0);
    const externalClientCount = state.radios.reduce(
      (sum, radio) =>
        sum + (radio.connectedClients || [])
          .filter(client => !client.browserOwned).length,
      0);
    elements.summaryStations.textContent = String(remoteStations.length);
    elements.summaryRemoteSessions.textContent =
      String(remoteSessionCount);
    elements.summaryExternalClients.textContent =
      String(externalClientCount);
    showNotice(
      `Radio policies, station health, GUI clients, and active web sessions` +
      (announce ? " · refreshed just now" : ""));
  } catch (error) {
    showNotice(
      error.message || "Radio administration is temporarily unavailable.",
      true);
  } finally {
    state.refreshing = false;
    elements.refresh.disabled = false;
  }
}

async function createEnrollmentCode(event) {
  event.preventDefault();
  const stationId = normalizeStationId(elements.enrollmentStationId.value);
  if (!stationIdValid(stationId)) {
    elements.enrollmentStationId.setCustomValidity(
      "Use 1-64 letters, numbers, periods, underscores, colons, or hyphens.");
    elements.enrollmentStationId.reportValidity();
    return;
  }
  elements.enrollmentStationId.setCustomValidity("");
  await requestEnrollmentCode(stationId);
}

async function requestEnrollmentCode(stationId) {
  elements.enrollmentCreate.disabled = true;
  try {
    state.enrollmentBootstrap = await getJson(
      `/api/admin/stations/bootstrap?stationId=${encodeURIComponent(stationId)}`);
    if (!state.enrollmentBootstrap?.ready ||
        !state.enrollmentBootstrap?.installCommand) {
      throw new Error(
        state.enrollmentBootstrap?.message ||
        "A signed AetherRemote installer is not ready on this gateway.");
    }
    state.enrollmentCode = await postJson(
      "/api/admin/stations/enrollment-codes",
      { stationId });
    elements.enrollmentStationId.value = stationId;
    renderEnrollmentResult();
    showNotice(
      `${stationId} ${formatEnrollmentPurpose(
        state.enrollmentCode.purpose)} code created.`);
    await refreshInventory();
  } catch (error) {
    showNotice(
      error.message || "The station enrollment code was not created.",
      true);
  } finally {
    elements.enrollmentCreate.disabled = false;
  }
}

function renderCredentialSecurity() {
  renderEnrollmentResult();
  elements.credentialList.replaceChildren();
  const credentials = Array.isArray(
    state.stationAdministration?.credentials)
    ? state.stationAdministration.credentials
    : [];
  if (credentials.length === 0) {
    elements.credentialList.append(
      createElement(
        "div",
        "empty-card",
        "No station credentials have been enrolled yet."));
    return;
  }

  for (const credential of credentials) {
    elements.credentialList.append(buildCredentialRow(credential));
  }
}

function renderEnrollmentResult() {
  elements.enrollmentResult.replaceChildren();
  const enrollment = state.enrollmentCode;
  elements.enrollmentResult.hidden = !enrollment;
  if (!enrollment) {
    return;
  }

  const heading = createElement("div", "admin-enrollment-result-heading");
  heading.append(
    createElement(
      "strong",
      "",
      `${enrollment.stationId} · ` +
      `${formatEnrollmentPurpose(enrollment.purpose)}`),
    createElement(
      "span",
      "status-pill degraded",
      `EXPIRES ${formatEnrollmentExpiry(enrollment.expiresAt)}`));
  const warning = createElement(
    "p",
    "",
    "Copy this code now. It is shown only in this browser and works once.");
  const codeRow = createElement("div", "admin-enrollment-copy-row");
  const code = createElement(
    "code",
    "admin-enrollment-code",
    enrollment.enrollmentCode);
  const copyCode = createElement(
    "button",
    "secondary-action",
    "Copy code");
  copyCode.type = "button";
  copyCode.addEventListener("click", () =>
    copyEnrollmentText(
      enrollment.enrollmentCode,
      copyCode,
      "Code copied"));
  codeRow.append(code, copyCode);

  const commandText = state.enrollmentBootstrap?.installCommand || "";
  const commandRow = createElement("div", "admin-enrollment-copy-row");
  const command = createElement(
    "code",
    "admin-enrollment-command",
    commandText);
  const copyCommand = createElement(
    "button",
    "secondary-action",
    "Copy command");
  copyCommand.type = "button";
  copyCommand.addEventListener("click", () =>
    copyEnrollmentText(
      commandText,
      copyCommand,
      "Command copied"));
  copyCommand.disabled = !commandText;
  commandRow.append(command, copyCommand);
  const installHelp = createElement(
    "p",
    "muted",
    "Run the signed bootstrap command first. The station prompts for this " +
    "one-time code locally; the code is intentionally not part of the command.");
  elements.enrollmentResult.append(
    heading,
    warning,
    codeRow,
    installHelp,
    commandRow);
}

async function copyEnrollmentText(value, button, successLabel) {
  const original = button.textContent;
  try {
    await navigator.clipboard.writeText(value);
    button.textContent = successLabel;
    window.setTimeout(() => {
      button.textContent = original;
    }, 1800);
  } catch {
    showNotice("Copy was blocked. Select the text and copy it manually.", true);
  }
}

function buildCredentialRow(credential) {
  const stateName = String(credential.state || "revoked").toLowerCase();
  const row = createElement(
    "article",
    `admin-credential-row${stateName === "enabled" ? "" : " offline"}`);
  const identity = createElement("div", "admin-credential-identity");
  identity.append(
    createElement("strong", "", credential.stationId || "Unknown station"),
    createElement(
      "small",
      "",
      `${formatStationCredentialSource(credential.source)} · ` +
      `updated ${formatAge(credential.updatedAt)}`));
  const status = createElement(
    "span",
    `status-pill${stateName === "enabled" ? "" : " offline"}`,
    stateName.toUpperCase());
  const actions = createElement("div", "admin-credential-actions");

  const newCode = createElement(
    "button",
    "secondary-action",
    stateName === "revoked" ? "New enrollment code" : "Rotate credential");
  newCode.type = "button";
  newCode.addEventListener("click", () =>
    requestEnrollmentCode(credential.stationId));
  actions.append(newCode);

  if (stateName === "disabled") {
    actions.append(
      buildCredentialAction(
        credential,
        "enable",
        "Enable",
        "secondary-action"));
  } else if (stateName === "enabled") {
    actions.append(
      buildCredentialAction(
        credential,
        "disable",
        "Disable",
        "secondary-action"));
  }
  if (stateName !== "revoked") {
    actions.append(
      buildCredentialAction(
        credential,
        "revoke",
        "Revoke",
        "danger-action"));
  }
  row.append(identity, status, actions);
  return row;
}

function buildCredentialAction(credential, action, label, className) {
  const button = createElement("button", className, label);
  button.type = "button";
  button.addEventListener("click", async () => {
    const message = action === "revoke"
      ? `Revoke ${credential.stationId}? Its station link and remote browser ` +
        "sessions will close. A new one-time code will be required."
      : action === "disable"
        ? `Disable ${credential.stationId}? Its station link and active ` +
          "remote browser sessions will close."
        : `Enable ${credential.stationId}? The station may reconnect.`;
    if (!window.confirm(message)) {
      return;
    }
    button.disabled = true;
    try {
      await postJson(
        `/api/admin/stations/${encodeURIComponent(credential.stationId)}` +
        `/${action}`);
      showNotice(`${credential.stationId} credential ${action}d.`);
      await refreshInventory();
    } catch (error) {
      showNotice(
        error.message || `The station could not be ${action}d.`,
        true);
    } finally {
      button.disabled = false;
    }
  });
  return button;
}

function formatEnrollmentExpiry(value) {
  const timestamp = new Date(value);
  return Number.isNaN(timestamp.getTime())
    ? "SOON"
    : timestamp.toLocaleTimeString([], {
      hour: "numeric",
      minute: "2-digit"
    });
}

function renderAudit() {
  elements.auditList.replaceChildren();
  const count = state.auditEvents.length;
  elements.auditCount.textContent =
    `${count} action${count === 1 ? "" : "s"}`;
  if (count === 0) {
    elements.auditList.append(
      createElement(
        "div",
        "empty-card",
        "No administrative changes have been recorded yet."));
    return;
  }

  for (const auditEvent of state.auditEvents) {
    elements.auditList.append(buildAuditEvent(auditEvent));
  }
}

function buildAuditEvent(auditEvent) {
  const row = createElement("article", "admin-audit-event");
  const identity = createElement("div", "admin-audit-identity");
  const target = auditEvent.targetId
    ? ` · target ${auditEvent.targetId}`
    : "";
  identity.append(
    createElement("strong", "", formatAuditAction(auditEvent.action)),
    createElement(
      "small",
      "",
      `${auditEvent.actorDisplayName || auditEvent.actorId || "Unknown"} · ` +
      `${radioLabel(auditEvent.radioId)}${target}`));

  const outcome = createElement(
    "span",
    `status-pill${auditEvent.result === "succeeded" ? "" : " offline"}`,
    formatAuditResult(auditEvent.result));
  const summary = createElement(
    "p",
    "admin-audit-summary",
    auditEvent.summary || "No details were recorded.");
  const occurredAt = document.createElement("time");
  occurredAt.dateTime = auditEvent.occurredAt || "";
  occurredAt.textContent = formatAuditTimestamp(auditEvent.occurredAt);
  row.append(identity, outcome, summary, occurredAt);
  return row;
}

function radioLabel(radioId) {
  const radio = state.radios.find(candidate =>
    candidate.radioId === radioId);
  return radio?.label || radioId || "Unknown radio";
}

function formatAuditTimestamp(value) {
  const timestamp = new Date(value);
  return Number.isNaN(timestamp.getTime())
    ? "Unknown time"
    : timestamp.toLocaleString();
}

function renderStations() {
  elements.stationList.replaceChildren();
  const administration = state.stationAdministration || {};
  const stations = Array.isArray(administration.stations)
    ? administration.stations
    : [];

  if (!administration.enabled) {
    elements.stationStatus.textContent = "DISABLED";
    elements.stationList.append(
      createElement(
        "div",
        "empty-card",
        "Remote station support is not enabled on this gateway."));
    return;
  }

  const refreshed = administration.refreshedAt
    ? ` · ${formatAge(administration.refreshedAt)}`
    : "";
  elements.stationStatus.textContent = administration.brokerReachable
    ? `${administration.error ? "PARTIAL" : "BROKER LIVE"}${refreshed}`
    : `BROKER UNAVAILABLE${refreshed}`;

  if (stations.length === 0) {
    elements.stationList.append(
      createElement(
        "div",
        "empty-card",
        administration.error ||
        "No remote stations have checked in yet."));
    return;
  }

  for (const station of stations) {
    elements.stationList.append(buildStationCard(station));
  }
}

function buildStationCard(station) {
  const stationState = String(station.state || "offline").toLowerCase();
  const card = createElement(
    "article",
    `portal-admin-card admin-station-card${stationState === "offline"
      ? " offline"
      : ""}`);
  const heading = createElement("div", "admin-card-heading");
  const identity = document.createElement("div");
  identity.append(
    createElement("h2", "", station.stationId || "Unknown station"),
    createElement(
      "small",
      "",
      `Agent ${station.softwareVersion || "unknown"} · ` +
      `Engine ${station.stationEngineVersion || "unknown"} · ` +
      `release ${station.releaseIdentity || "legacy"} · ` +
      `instance ${shortId(station.instanceId)}`));
  heading.append(
    identity,
    createElement(
      "span",
      `status-pill${stationState === "online"
        ? ""
        : stationState === "degraded"
          ? " degraded"
          : " offline"}`,
      stationState.toUpperCase()));

  const metrics = createElement("div", "admin-station-metrics");
  metrics.append(
    diagnosticMetric(
      "LAST CHECK-IN",
      formatAge(station.lastSeen),
      `${formatCount(station.heartbeatSequence)} heartbeats`),
    diagnosticMetric(
      "CONNECTED",
      formatAge(station.connectedAt),
      `${formatCount(station.inventorySequence)} inventory updates`),
    diagnosticMetric(
      "RADIOS",
      String(station.radios?.length || 0),
      formatStationCapabilities(station.capabilities)),
    diagnosticMetric(
      "RECEIVE SESSIONS",
      String(station.receiveSessions?.length || 0),
      "One isolated GUI client per browser"),
    diagnosticMetric(
      "LINK RECOVERY",
      formatStationConnectionCount(station.connectionCount),
      formatStationRecoveryDetail(station, stationState)),
    diagnosticMetric(
      "SIGNED RELEASE",
      station.releaseIdentity || "Legacy / unknown",
      station.stationEngineVersion
        ? `Station engine ${station.stationEngineVersion}`
        : "No exact station release metadata advertised"));

  const radios = createElement("div", "admin-station-connections");
  radios.append(
    createElement(
      "div",
      "admin-operator-title",
      `STATION RADIOS · ${station.radios?.length || 0}`));
  if (!station.radios?.length) {
    radios.append(
      createElement("small", "", "The station has not advertised a radio."));
  } else {
    for (const radio of station.radios) {
      const row = createElement("div", "portal-operator-row");
      const radioIdentity = document.createElement("div");
      radioIdentity.append(
        createElement(
          "strong",
          "",
          radio.nickname || radio.model || radio.radioId),
        createElement(
          "small",
          "",
          `${radio.model} · ${radio.serial} · ` +
          formatClientCapacity(
            radio.availableClients,
            radio.licensedClients)));
      row.append(
        radioIdentity,
        createElement(
          "span",
          `status-pill${radio.status === "available"
            ? ""
            : " degraded"}`,
          String(radio.status || "unknown").toUpperCase()));
      radios.append(row);
    }
  }

  const sessions = createElement("div", "admin-station-connections");
  sessions.append(
    createElement(
      "div",
      "admin-operator-title",
      `REMOTE RECEIVE SESSIONS · ${station.receiveSessions?.length || 0}`));
  if (!station.receiveSessions?.length) {
    sessions.append(
      createElement(
        "small",
        "",
        "No browser receive tunnels are active through this station."));
  } else {
    for (const session of station.receiveSessions) {
      sessions.append(buildRemoteReceiveSessionRow(session));
    }
  }

  const updateActions = createElement("div", "admin-enrollment-copy-row");
  const targetRelease = state.stationBootstrap?.ready
    ? state.stationBootstrap.releaseIdentity
    : "";
  const canUpdate =
    stationState !== "offline" &&
    Array.isArray(station.capabilities) &&
    station.capabilities.includes("release-update-v1") &&
    Boolean(targetRelease);
  if (canUpdate && station.releaseIdentity !== targetRelease) {
    const update = createElement(
      "button",
      "secondary-action",
      `Update to ${targetRelease}`);
    update.type = "button";
    update.addEventListener("click", () =>
      requestStationReleaseUpdate(station.stationId, update));
    updateActions.append(update);
  } else if (canUpdate && station.releaseIdentity === targetRelease) {
    updateActions.append(
      createElement("span", "status-pill", "RELEASE CURRENT"));
  }

  card.append(heading, metrics, radios, sessions);
  if (updateActions.childNodes.length > 0) {
    card.append(updateActions);
  }
  return card;
}

async function requestStationReleaseUpdate(stationId, button) {
  button.disabled = true;
  try {
    const result = await postJson(
      `/api/admin/stations/${encodeURIComponent(stationId)}/release-update`,
      {});
    showNotice(
      result?.outcome === "already-current"
        ? `${stationId} is already on the gateway release.`
        : `${stationId} signed release update completed.`);
    await refreshInventory(true);
  } catch (error) {
    showNotice(
      error.message || "The signed station release update failed.",
      true);
  } finally {
    button.disabled = false;
  }
}

function buildRemoteReceiveSessionRow(session) {
  const row = createElement("div", "portal-operator-row");
  const identity = document.createElement("div");
  identity.append(
    createElement(
      "strong",
      "",
      `${session.radioModel || "Remote radio"} · ` +
      `${formatRemoteClientHandle(session.clientHandle)}`),
    createElement(
      "small",
      "",
      `${session.radioId} · opened ${formatAge(session.openedAt)} · ` +
      `session ${shortId(session.sessionId)}`));
  row.append(
    identity,
    createElement("span", "status-pill", "WEB TUNNEL"));
  return row;
}

function formatStationCapabilities(capabilities) {
  const values = Array.isArray(capabilities) ? capabilities : [];
  return values.length > 0
    ? values.map(capability =>
        capability === "receive-projection-v1"
          ? "Receive projection"
          : capability === "release-service-control-v1"
            ? "Release service control"
            : capability === "release-update-v1"
              ? "Signed release updates"
              : capability).join(", ")
    : "No receive capability advertised";
}

function formatStationConnectionCount(value) {
  const count = Number.isInteger(Number(value)) && Number(value) > 0
    ? Number(value)
    : 1;
  return count === 1 ? "Initial link" : `${formatCount(count - 1)} reconnects`;
}

function formatStationRecoveryDetail(station, stationState) {
  const reason = formatStationDisconnectReason(station.lastDisconnectReason);
  if (stationState === "offline" && station.lastDisconnectedAt) {
    return `Disconnected ${formatAge(station.lastDisconnectedAt)} · ${reason}`;
  }
  const recoveryMilliseconds = Number(station.lastRecoveryMilliseconds);
  if (station.lastRecoveredAt &&
      Number.isFinite(recoveryMilliseconds) &&
      recoveryMilliseconds >= 0) {
    return `Recovered ${formatAge(station.lastRecoveredAt)} · ` +
      `${formatStationDuration(recoveryMilliseconds)} outage · ${reason}`;
  }
  return "No reconnect recorded for this broker process";
}

function formatStationDisconnectReason(value) {
  const labels = {
    connection_closed: "station link closed",
    replaced: "new station connection replaced the old link",
    heartbeat_timeout: "heartbeat timeout",
    broker_disconnect: "broker disconnected the station"
  };
  return labels[String(value || "")] || "link ended";
}

function formatStationDuration(milliseconds) {
  if (milliseconds < 1000) {
    return `${Math.round(milliseconds)} ms`;
  }
  if (milliseconds < 60_000) {
    const seconds = milliseconds / 1000;
    return `${seconds < 10 ? seconds.toFixed(1) : Math.round(seconds)} s`;
  }
  const minutes = milliseconds / 60_000;
  return `${minutes < 10 ? minutes.toFixed(1) : Math.round(minutes)} min`;
}

function formatRemoteClientHandle(value) {
  const text = String(value || "").trim();
  return /^[0-9a-f]{1,8}$/i.test(text)
    ? `0x${text.toLowerCase().padStart(8, "0")}`
    : "—";
}

function renderInventory() {
  elements.list.replaceChildren();
  if (state.radios.length === 0) {
    elements.list.append(
      createElement(
        "div",
        "empty-card",
        "No radios are currently in the server inventory."));
    return;
  }
  state.radios.forEach((radio, index) => {
    elements.list.append(buildRadioCard(radio, index));
  });
}

function buildRadioCard(radio, index) {
  const card = createElement("article", "portal-admin-card");
  const health = normalizeRadioHealth(radio);
  const heading = createElement("div", "admin-card-heading");
  const identity = document.createElement("div");
  identity.append(
    createElement("h2", "", radio.label),
    createElement(
      "small",
      "",
      `${radio.serial || "No serial"} · ${formatAdminRadioLocation(radio)}`));
  heading.append(
    identity,
    createElement(
      "span",
      `status-pill${radioHealthClass(health.state)}`,
      health.state.toUpperCase()));

  const healthDetail = createElement(
    "p",
    "admin-capacity",
    formatRadioHealthDetail(health));
  const capacity = createElement(
    "p",
    "admin-capacity",
    `${formatClientCapacity(
      radio.availableClients,
      radio.licensedClients)} · ` +
    `${radio.multiFlexEnabled ? "Multi-Flex enabled" : "Multi-Flex disabled"}`);
  const capacityHistory = buildCapacityHistory(radio);

  const onboarding = buildOnboardingForm(radio);
  const policy = buildPolicyForm(radio, index);
  const connectedClients = buildConnectedClients(radio);
  const operators = createElement("div", "admin-operator-list");
  operators.append(
    createElement(
      "div",
      "admin-operator-title",
      `ACTIVE WEB OPERATORS · ${radio.operators?.length || 0}`));
  if (!radio.operators?.length) {
    operators.append(
      createElement(
        "small",
        "",
        "No web sessions are holding this radio."));
  } else {
    for (const operator of radio.operators) {
      operators.append(buildOperatorRow(radio, operator));
    }
  }

  const sessions = buildSessionDiagnostics(radio);
  card.append(
    heading,
    healthDetail,
    capacity,
    capacityHistory,
    onboarding,
    policy,
    connectedClients,
    operators,
    sessions);
  return card;
}

function buildCapacityHistory(radio) {
  const history = Array.isArray(radio.capacityHistory)
    ? radio.capacityHistory
      .map(sample => ({
        observedAt: sample?.observedAt || null,
        online: Boolean(sample?.online),
        availableClients: Number(sample?.availableClients),
        licensedClients: Number(sample?.licensedClients),
        status: String(sample?.status || "Unknown")
      }))
      .filter(sample => sample.observedAt &&
        !Number.isNaN(Date.parse(sample.observedAt)))
    : [];
  const section = createElement("details", "admin-operator-list");
  section.append(
    createElement(
      "summary",
      "admin-operator-title",
      `CLIENT CAPACITY HISTORY · ${history.length}`));

  if (history.length === 0) {
    section.append(
      createElement(
        "small",
        "",
        "Capacity samples will appear after the server sampler runs."));
    return section;
  }

  for (const sample of history.slice(-8).reverse()) {
    const row = createElement("div", "admin-operator-row");
    const identity = document.createElement("div");
    identity.append(
      createElement(
        "strong",
        "",
        formatClientCapacity(
          sample.availableClients,
          sample.licensedClients)),
      createElement(
        "small",
        "",
        `${sample.online ? "Online" : "Offline"} · ${sample.status} · ` +
        formatAge(sample.observedAt)));
    row.append(identity);
    section.append(row);
  }
  return section;
}

function normalizeRadioHealth(radio) {
  const raw = radio.health || {};
  const supported = new Set([
    "healthy",
    "busy",
    "degraded",
    "reconnecting",
    "offline"
  ]);
  const requested = String(raw.state || "").toLowerCase();
  const stateName = supported.has(requested)
    ? requested
    : radio.online
      ? "healthy"
      : "offline";
  return {
    state: stateName,
    summary: String(raw.summary ||
      (stateName === "offline"
        ? "The radio path is not currently reachable."
        : "Radio health telemetry is available after the next refresh.")),
    sessionCount: Number(raw.sessionCount || 0),
    oldestSessionAt: raw.oldestSessionAt || null,
    lastActivityAt: raw.lastActivityAt || null,
    lastStreamAt: raw.lastStreamAt || null,
    queueDepth: Number(raw.queueDepth || 0),
    queueCapacity: Number(raw.queueCapacity || 0),
    droppedMessages: Number(raw.droppedMessages || 0)
  };
}

function radioHealthClass(stateName) {
  if (stateName === "offline") {
    return " offline";
  }
  if (stateName === "healthy") {
    return "";
  }
  return " degraded";
}

function formatRadioHealthDetail(health) {
  const facts = [health.summary];
  if (health.sessionCount > 0 && health.oldestSessionAt) {
    facts.push(
      `${health.sessionCount} session${health.sessionCount === 1 ? "" : "s"}; ` +
      `oldest ${formatAge(health.oldestSessionAt)}`);
  }
  if (health.lastStreamAt) {
    facts.push(`last stream ${formatAge(health.lastStreamAt)}`);
  }
  if (health.queueCapacity > 0) {
    facts.push(`queue ${health.queueDepth} / ${health.queueCapacity}`);
  }
  if (health.droppedMessages > 0) {
    facts.push(`${formatCount(health.droppedMessages)} dropped`);
  }
  return facts.join(" · ");
}

function formatAdminRadioLocation(radio) {
  const host = String(radio.host || "Unknown source");
  const port = Number(radio.port);
  return Number.isInteger(port) && port > 0
    ? `${host}:${port}`
    : host;
}

function buildConnectedClients(radio) {
  const section = createElement("div", "admin-operator-list");
  const clients = Array.isArray(radio.connectedClients)
    ? radio.connectedClients
    : [];
  section.append(
    createElement(
      "div",
      "admin-operator-title",
      `FLEX GUI CONNECTIONS · ${clients.length} · ` +
      `${clients.filter(client => !client.browserOwned).length} EXTERNAL`));
  if (clients.length === 0) {
    section.append(
      createElement(
        "small",
        "",
        "The radio reports connection details while an AetherSDR web GUI is " +
        "active; the capacity above remains radio-reported."));
    return section;
  }

  for (const client of clients) {
    const row = createElement("div", "portal-operator-row");
    const identity = document.createElement("div");
    const station =
      client.station ||
      client.program ||
      formatHexId(client.clientHandle);
    const source = client.source ? ` · ${client.source}` : "";
    const operator = client.browserOwned && client.operatorName
      ? ` · ${client.operatorName}`
      : "";
    const localPtt = client.localPtt ? " · Local PTT owner" : "";
    const clientId = client.clientId
      ? ` · client ${shortId(client.clientId)}`
      : "";
    identity.append(
      createElement("strong", "", station),
      createElement(
        "small",
        "",
        `${client.program || "Unknown"} · ` +
        `${formatHexId(client.clientHandle)}` +
        `${source}${clientId}${operator}${localPtt}`));
    row.append(
      identity,
      createElement(
        "span",
        "status-pill",
        client.browserOwned ? "WEB" : "EXTERNAL"));
    section.append(row);
  }
  return section;
}

function buildSessionDiagnostics(radio) {
  const section = createElement("div", "admin-session-list");
  const sessions = Array.isArray(radio.sessions) ? radio.sessions : [];
  section.append(
    createElement(
      "div",
      "admin-operator-title",
      `LIVE GUI CLIENT DIAGNOSTICS · ${sessions.length}`));
  if (sessions.length === 0) {
    section.append(
      createElement(
        "small",
        "",
        "No browser-owned FLEX GUI clients are active."));
    return section;
  }

  for (const session of sessions) {
    section.append(buildSessionDiagnostic(session));
  }
  return section;
}

function buildSessionDiagnostic(session) {
  const details = document.createElement("details");
  details.className = "admin-session-diagnostic";
  details.dataset.sessionId = session.sessionId;
  details.open = sessionDiagnosticExpanded(
    session,
    state.sessionDiagnosticExpansion);
  details.addEventListener("toggle", () => {
    rememberSessionDiagnosticExpansion(
      state.sessionDiagnosticExpansion,
      session.sessionId,
      details.open);
  });

  const summary = document.createElement("summary");
  const identity = document.createElement("span");
  identity.append(
    createElement(
      "strong",
      "",
      `${session.displayName || session.userId} · ${shortId(session.sessionId)}`),
    createElement(
      "small",
      "",
      `${session.transport?.transport || "Unknown transport"} · ` +
      `${session.lowBandwidth ? "VPN low bandwidth" : "Normal bandwidth"}`));
  summary.append(
    identity,
    createElement(
      "span",
      `status-pill${session.connected ? "" : " offline"}`,
      String(session.connectionState || "unknown").toUpperCase()));

  const panIds = (session.panadapters || [])
    .map(pan => formatHexId(pan.streamId))
    .join(", ") || "—";
  const waterfallIds = (session.panadapters || [])
    .map(pan => pan.waterfallId)
    .filter(Boolean)
    .join(", ") || "—";
  const slices = (session.slices || [])
    .map(slice =>
      `${slice.id}→${slice.radioId} ${formatFrequency(slice.frequencyHz)}`)
    .join(" · ") || "None";
  const webClients = Array.isArray(session.webClients)
    ? session.webClients
    : [];
  const queueDepth = webClients.reduce(
    (sum, client) => sum + Number(client.queueDepth || 0),
    0);
  const queueCapacity = webClients.reduce(
    (sum, client) => sum + Number(client.queueCapacity || 0),
    0);
  const droppedMessages = webClients.reduce(
    (sum, client) => sum + Number(client.droppedMessages || 0),
    0);
  const transport = session.transport || {};
  const txOccupancy = formatTxOccupancy(session.txOccupancy);
  const pttAuthority = formatPttAuthority(session.txOccupancy);
  const txLifecycle = formatTxLifecycle(session.txLifecycle);
  const tuneTiming = formatTuneTiming(session.tune);
  const reconnect = formatBrowserReconnect(session.reconnect);
  const browserAudio = latestBrowserAudio(webClients);
  const audioHealth = formatBrowserAudio(browserAudio);
  const browserNetwork = latestBrowserNetwork(webClients);
  const networkHealth = formatBrowserNetwork(browserNetwork);
  const metrics = createElement("div", "admin-diagnostic-grid");
  metrics.append(
    diagnosticMetric(
      "FLEX HANDLE",
      formatHexId(transport.clientHandle),
      `GUI ${shortId(session.guiClientId)}`),
    diagnosticMetric(
      "UDP / AUDIO",
      `${transport.udpPort || "—"} · ${formatHexId(transport.audioStreamId)}`,
      `${formatCount(transport.udpDatagrams)} datagrams`),
    diagnosticMetric(
      "PAN / WATERFALL",
      panIds,
      waterfallIds),
    diagnosticMetric(
      "SLICES",
      String((session.slices || []).length),
      slices),
    diagnosticMetric(
      "SPECTRUM",
      formatCount(transport.spectrumFrames),
      `waterfall + S-meter source; last ` +
      `${formatAge(transport.lastSpectrumFrameAt)}`),
    diagnosticMetric(
      "AUDIO",
      formatCount(transport.audioFrames),
      `last ${formatAge(transport.lastAudioFrameAt)}`),
    diagnosticMetric(
      "BROWSER AUDIO",
      audioHealth.latencyValue,
      audioHealth.latencyDetail),
    diagnosticMetric(
      "AUDIO HEALTH",
      audioHealth.healthValue,
      audioHealth.healthDetail),
    diagnosticMetric(
      "AUDIO DELIVERY",
      audioHealth.deliveryValue,
      audioHealth.deliveryDetail),
    diagnosticMetric(
      "BROWSER TRAFFIC",
      networkHealth.value,
      networkHealth.detail),
    diagnosticMetric(
      "TX OCCUPANCY",
      txOccupancy.value,
      txOccupancy.detail),
    diagnosticMetric(
      "PTT AUTHORITY",
      pttAuthority.value,
      pttAuthority.detail),
    diagnosticMetric(
      "TX LIFECYCLE",
      txLifecycle.value,
      txLifecycle.detail),
    diagnosticMetric(
      "TUNE ECHO",
      tuneTiming.value,
      tuneTiming.detail),
    diagnosticMetric(
      "BROWSER QUEUE",
      queueCapacity > 0 ? `${queueDepth} / ${queueCapacity}` : "No socket",
      `${formatCount(droppedMessages)} dropped`),
    diagnosticMetric(
      "BROWSER RECONNECT",
      reconnect.value,
      reconnect.detail),
    diagnosticMetric(
      "SESSION",
      `v${session.snapshotVersion}`,
      `${formatCount(transport.connectionAttempts)} connection attempt` +
      `${Number(transport.connectionAttempts) === 1 ? "" : "s"}`));

  details.append(summary, metrics);
  if (session.connectionError) {
    details.append(
      createElement(
        "p",
        "admin-diagnostic-error",
        session.connectionError));
  }
  return details;
}

function formatTxOccupancy(occupancy) {
  const state = String(occupancy?.stateName || "unknown").toLowerCase();
  const labels = {
    idle: "Idle",
    external: "External TX",
    "aether-owned": "AetherSDR TX",
    ambiguous: "Ambiguous",
    unknown: "Unknown"
  };
  const occupants = Array.isArray(occupancy?.occupants)
    ? occupancy.occupants
    : [];
  const detail = occupants.length > 0
    ? occupants.map(occupant => {
        const owner = occupant.aetherOwned ? "AetherSDR" : "External";
        const station = occupant.station || occupant.program || "FLEX client";
        const handle = formatHexId(occupant.clientHandle);
        return `${owner}: ${station} ${handle}`;
      }).join(" · ")
    : state === "idle"
      ? `Observed ${formatAge(occupancy?.observedAt)}`
      : "No fresh radio-authoritative interlock observation";
  return {
    value: labels[state] || "Unknown",
    detail
  };
}

function formatPttAuthority(occupancy) {
  const owners = Array.isArray(occupancy?.localPttOwners)
    ? occupancy.localPttOwners
    : [];
  if (owners.length === 0) {
    return {
      value: "Unassigned",
      detail: "No fresh FLEX Local PTT owner"
    };
  }
  if (owners.length !== 1) {
    return {
      value: "Ambiguous",
      detail: owners.map(owner => {
        const station = owner.station || owner.program || "FLEX client";
        return `${station} ${formatHexId(owner.clientHandle)}`;
      }).join(" · ")
    };
  }

  const owner = owners[0];
  const station = owner.station || owner.program || "FLEX client";
  return {
    value: owner.aetherOwned ? "AetherSDR" : "External",
    detail: `${station} ${formatHexId(owner.clientHandle)}`
  };
}

function latestBrowserAudio(webClients) {
  return webClients
    .map(client => client.audio)
    .filter(Boolean)
    .sort((left, right) =>
      Date.parse(right.reportedAt) - Date.parse(left.reportedAt))[0] ?? null;
}

function latestBrowserNetwork(webClients) {
  return webClients
    .map(client => client.network)
    .filter(Boolean)
    .sort((left, right) =>
      Date.parse(right.reportedAt) - Date.parse(left.reportedAt))[0] ?? null;
}

function diagnosticMetric(label, value, detail) {
  const metric = createElement("div", "admin-diagnostic-metric");
  metric.append(
    createElement("span", "", label),
    createElement("strong", "", value),
    createElement("small", "", detail));
  return metric;
}

function buildOnboardingForm(radio) {
  const section = createElement("div", "admin-operator-list");
  section.append(
    createElement(
      "div",
      "admin-operator-title",
      "RADIO ONBOARDING · RECEIVE-ONLY BY DEFAULT"),
    createElement("small", "", formatRadioOwnership(radio)));

  const identityForm = createElement("div", "admin-policy-form");
  const labelField = document.createElement("label");
  labelField.append(createElement("span", "", "STABLE LABEL"));
  const label = document.createElement("input");
  label.type = "text";
  label.maxLength = 64;
  label.required = true;
  label.value = radio.onboarding?.label || radio.label || "";
  label.setAttribute("aria-label", "Stable label for " + radio.label);
  labelField.append(label);
  const saveLabel = createElement(
    "button",
    "secondary-action",
    "Save label");
  saveLabel.type = "button";
  saveLabel.addEventListener("click", async () => {
    const normalized = normalizeRadioLabel(label.value);
    if (!normalized) {
      showNotice("A stable radio label is required.", true);
      return;
    }
    saveLabel.disabled = true;
    try {
      await postJson(
        "/api/admin/radios/" + encodeURIComponent(radio.radioId) +
        "/identity",
        { label: normalized });
      showNotice(
        normalized + " identity saved; transmit remains receive-only.");
      await refreshInventory();
    } catch (error) {
      showNotice(error.message || "The radio identity was not saved.", true);
    } finally {
      saveLabel.disabled = false;
    }
  });
  identityForm.append(labelField, saveLabel);

  const transmitForm = createElement("div", "admin-policy-form");
  const stateField = document.createElement("label");
  stateField.append(createElement("span", "", "TRANSMIT POLICY"));
  const transmitState = document.createElement("select");
  transmitState.setAttribute(
    "aria-label",
    "Transmit policy for " + radio.label);
  transmitState.append(
    createOption("receive-only", "Receive only"),
    createOption("tx-eligible", "TX eligible"),
    createOption("temporarily-disabled", "Temporarily disabled"),
    createOption("prerequisites-failed", "Prerequisites failed"));
  transmitState.options[3].disabled = true;
  transmitState.value = normalizeTransmitPolicyState(
    radio.onboarding?.transmitPolicyState);
  stateField.append(transmitState);

  const saveTransmit = createElement(
    "button",
    "danger-action",
    "Apply with reauthentication");
  saveTransmit.type = "button";
  saveTransmit.disabled = !radio.onboarding?.onboarded;
  saveTransmit.addEventListener("click", async () => {
    const stateName = normalizeTransmitPolicyState(transmitState.value);
    const confirmed = window.confirm(
      "Change " + radio.label + " transmit policy to " + stateName + "? " +
      "Fresh administrator reauthentication is required. Enabling only " +
      "records eligibility after exact-radio validation and does not key TX.");
    if (!confirmed) {
      return;
    }
    saveTransmit.disabled = true;
    try {
      await postJson(
        "/api/admin/radios/" + encodeURIComponent(radio.radioId) +
        "/transmit-policy",
        buildTransmitPolicyRequest(stateName));
      showNotice(
        radio.label + " transmit policy changed to " + stateName + ".");
      await refreshInventory();
    } catch (error) {
      showNotice(
        error.message ||
        "The transmit policy transition was rejected.",
        true);
      await refreshInventory();
    } finally {
      saveTransmit.disabled = false;
    }
  });
  transmitForm.append(stateField, saveTransmit);

  const preflight = radio.onboarding?.transmitPreflight;
  const detail = preflight
    ? "Last exact-radio preflight: " + preflight.reason + " · " +
      (preflight.ready ? "ready" : "not ready") + " · " +
      formatAge(preflight.evaluatedAt)
    : radio.onboarding?.onboarded
      ? "No TX eligibility preflight is retained for this receive-only policy."
      : "Save a stable label before changing transmit policy.";
  section.append(
    identityForm,
    transmitForm,
    createElement("small", "", detail));
  return section;
}

function buildPolicyForm(radio, index) {
  const form = createElement("div", "admin-policy-form");

  const modeLabel = document.createElement("label");
  modeLabel.append(createElement("span", "", "ACCESS"));
  const mode = document.createElement("select");
  mode.setAttribute("aria-label", `Access mode for ${radio.label}`);
  mode.append(
    createOption("shared", "Shared"),
    createOption("exclusive", "Exclusive"));
  mode.value = normalizeAdminMode(radio.policy?.mode);
  modeLabel.append(mode);

  const reservationLabel = document.createElement("label");
  reservationLabel.append(
    createElement("span", "", "RESERVED ENTRA ACCOUNT ID"));
  const reservation = document.createElement("input");
  reservation.type = "text";
  reservation.maxLength = 256;
  reservation.placeholder = "Blank allows any authorized account";
  reservation.value = radio.policy?.reservedUserId || "";
  reservation.setAttribute(
    "aria-label",
    `Reserved account for ${radio.label}`);
  const suggestions = document.createElement("datalist");
  suggestions.id = `admin-users-${index}`;
  reservation.setAttribute("list", suggestions.id);
  for (const operator of radio.operators || []) {
    const option = document.createElement("option");
    option.value = operator.userId;
    option.label = operator.displayName;
    suggestions.append(option);
  }
  reservationLabel.append(reservation, suggestions);

  const save = createElement("button", "secondary-action", "Save policy");
  save.type = "button";
  save.addEventListener("click", async () => {
    save.disabled = true;
    try {
      await postJson(
        `/api/admin/radios/${encodeURIComponent(radio.radioId)}/policy`,
        buildPolicyRequest(mode.value, reservation.value));
      showNotice(`${radio.label} policy saved.`);
      await refreshInventory();
    } catch (error) {
      showNotice(error.message || "The radio policy was not saved.", true);
    } finally {
      save.disabled = false;
    }
  });

  form.append(modeLabel, reservationLabel, save);
  return form;
}

function buildOperatorRow(radio, operator) {
  const row = createElement("div", "portal-operator-row");
  const identity = document.createElement("div");
  identity.append(
    createElement("strong", "", operator.displayName),
    createElement(
      "small",
      "",
      `${operator.browserConnections} browser connection` +
      `${operator.browserConnections === 1 ? "" : "s"} · ` +
      `${operator.radioSessions} radio session` +
      `${operator.radioSessions === 1 ? "" : "s"}`));

  const disconnect = createElement(
    "button",
    "danger-action",
    "Disconnect");
  disconnect.type = "button";
  disconnect.addEventListener("click", async () => {
    const confirmed = window.confirm(
      `Disconnect ${operator.displayName} from ${radio.label}? ` +
      "Their slices, audio, and radio stream will be released.");
    if (!confirmed) {
      return;
    }
    disconnect.disabled = true;
    try {
      await postJson(
        `/api/admin/radios/${encodeURIComponent(radio.radioId)}` +
        `/operators/${encodeURIComponent(operator.userId)}/disconnect`);
      showNotice(`${operator.displayName} disconnected from ${radio.label}.`);
      await refreshInventory();
    } catch (error) {
      showNotice(
        error.message || "The operator could not be disconnected.",
        true);
    } finally {
      disconnect.disabled = false;
    }
  });

  row.append(identity, disconnect);
  return row;
}

async function prepareReleaseUpdate(event) {
  event.preventDefault();
  elements.releasePrepare.disabled = true;
  try {
    const result = await postReleaseJson(
      "/api/admin/releases/prepare",
      {
        releaseIdentity: elements.releaseIdentity.value.trim(),
        installedReleaseIdentity:
          elements.releaseInstalledIdentity.value.trim(),
        installedVersion: elements.releaseInstalledVersion.value.trim(),
        configurationSchemaVersion:
          Number.parseInt(elements.releaseSchemaVersion.value, 10),
        protocolVersion:
          Number.parseInt(elements.releaseProtocolVersion.value, 10)
      });
    state.releaseTransaction = result;
    renderReleaseTransaction();
    showNotice(
      `${result.targetReleaseIdentity || "Release"} is prepared inactive; ` +
      "fresh approval is required before activation.");
    await refreshInventory();
  } catch (error) {
    showNotice(error.message || "The release was not prepared.", true);
  } finally {
    elements.releasePrepare.disabled = false;
  }
}

async function activateReleaseUpdate() {
  const transaction = state.releaseTransaction;
  if (!transaction?.transactionId ||
      transaction.phase !== "awaitingApproval") {
    return;
  }
  const confirmed = window.confirm(
    `Activate ${transaction.targetReleaseIdentity}? ` +
    "This closes TX-lease admission, requires all radios idle and watchdogs " +
    "Disarmed, restarts only signed services, verifies health, and rolls back " +
    "automatically on failure.");
  if (!confirmed) {
    return;
  }
  elements.releaseActivate.disabled = true;
  try {
    const result = await postReleaseJson(
      `/api/admin/releases/${encodeURIComponent(transaction.transactionId)}` +
      "/activate");
    state.releaseTransaction = result;
    renderReleaseTransaction();
    showNotice(result.message || "Release activation completed.");
    await refreshInventory();
  } catch (error) {
    showNotice(
      error.message ||
      "Release activation was rejected. Sign in again for fresh reauthentication.",
      true);
  } finally {
    renderReleaseTransaction();
  }
}

async function rollbackReleaseUpdate() {
  const transaction = state.releaseTransaction;
  if (!transaction?.transactionId || transaction.phase !== "completed") {
    return;
  }
  const typed = window.prompt(
    "Type the exact transaction ID to approve rollback:",
    "");
  if (typed !== transaction.transactionId) {
    showNotice("Rollback confirmation did not match the transaction ID.", true);
    return;
  }
  elements.releaseRollback.disabled = true;
  try {
    const result = await postReleaseJson(
      `/api/admin/releases/${encodeURIComponent(transaction.transactionId)}` +
      "/rollback");
    state.releaseTransaction = result;
    renderReleaseTransaction();
    showNotice(result.message || "Release rollback completed.");
    await refreshInventory();
  } catch (error) {
    showNotice(
      error.message ||
      "Release rollback was rejected. Sign in again for fresh reauthentication.",
      true);
  } finally {
    renderReleaseTransaction();
  }
}

function renderReleaseTransaction() {
  const transaction = state.releaseTransaction;
  elements.releaseResult.replaceChildren();
  if (!transaction?.transactionId) {
    elements.releaseResult.append(
      createElement(
        "p",
        "muted",
        transaction?.message || "No release update transaction is active."));
    elements.releaseActivate.disabled = true;
    elements.releaseRollback.disabled = true;
    return;
  }

  const heading = createElement("div", "admin-enrollment-result-heading");
  heading.append(
    createElement(
      "strong",
      "",
      `${transaction.targetReleaseIdentity || "Release"} · ` +
      `${transaction.phase || "unknown"}`),
    createElement(
      "span",
      transaction.reconciliationRequired
        ? "status-pill degraded"
        : "status-pill",
      transaction.succeeded ? "READY" : "ATTENTION"));
  const details = createElement(
    "p",
    "",
    transaction.message || "Transaction status is available.");
  const identity = createElement(
    "code",
    "admin-enrollment-command",
    transaction.transactionId);
  elements.releaseResult.append(heading, details, identity);
  elements.releaseActivate.disabled =
    transaction.phase !== "awaitingApproval";
  elements.releaseRollback.disabled =
    transaction.phase !== "completed" || !transaction.rollbackReady;
}

async function postReleaseJson(url, body) {
  const antiforgeryHeaders = await getAntiforgeryHeaders();
  const options = {
    method: "POST",
    credentials: "same-origin",
    headers: {
      Accept: "application/json",
      ...antiforgeryHeaders
    }
  };
  if (body !== undefined) {
    options.headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(body);
  }
  const response = await fetch(url, options);
  if (response.status === 401) {
    window.location.assign(
      `/auth/login?returnUrl=${encodeURIComponent("/admin")}`);
    throw new Error("Sign-in is required.");
  }
  const text = await response.text();
  let result = {};
  if (text) {
    try {
      result = JSON.parse(text);
    } catch {
      result = {};
    }
  }
  if (!response.ok) {
    throw new Error(
      result.error || result.message || `Request failed (${response.status}).`);
  }
  return result;
}

async function getJson(url) {
  const response = await fetch(url, {
    credentials: "same-origin",
    headers: { Accept: "application/json" }
  });
  return requireJson(response);
}

async function postJson(url, body) {
  const antiforgeryHeaders = await getAntiforgeryHeaders();
  const options = {
    method: "POST",
    credentials: "same-origin",
    headers: {
      Accept: "application/json",
      ...antiforgeryHeaders
    }
  };
  if (body !== undefined) {
    options.headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(body);
  }
  const response = await fetch(url, options);
  return requireJson(response);
}

async function getAntiforgeryHeaders() {
  const token = await getJson("/api/antiforgery");
  if (token.headerName !== "X-Aether-CSRF" || !token.requestToken) {
    throw new Error("The request security token is invalid.");
  }
  return { [token.headerName]: token.requestToken };
}

async function requireJson(response) {
  if (response.status === 401) {
    window.location.assign(
      `/auth/login?returnUrl=${encodeURIComponent("/admin")}`);
    throw new Error("Sign-in is required.");
  }
  if (response.status === 403) {
    window.location.assign("/access-denied");
    throw new Error("Administrator access is required.");
  }
  const text = await response.text();
  let result = {};
  if (text) {
    try {
      result = JSON.parse(text);
    } catch {
      result = {};
    }
  }
  if (!response.ok) {
    throw new Error(result.error || `Request failed (${response.status}).`);
  }
  return result;
}

function createOption(value, label) {
  const option = document.createElement("option");
  option.value = value;
  option.textContent = label;
  return option;
}

function showNotice(message, error = false) {
  elements.notice.textContent = message;
  elements.notice.classList.toggle("error", error);
}

function createElement(tagName, className, text = "") {
  const element = document.createElement(tagName);
  if (className) {
    element.className = className;
  }
  element.textContent = text;
  return element;
}
