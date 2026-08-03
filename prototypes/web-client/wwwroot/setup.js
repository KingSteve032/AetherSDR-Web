const endpoints = Object.freeze({
  claim: "/setup/api/claim",
  session: "/setup/api/session",
  preflight: "/setup/api/preflight",
  topology: "/setup/api/topology",
  publicUrl: "/setup/api/public-url",
  paths: "/setup/api/paths",
  updateChannel: "/setup/api/update-channel",
  backup: "/setup/api/backup",
  transmitSupport: "/setup/api/transmit-support",
  revoke: "/setup/api/revoke"
});

export const setupSteps = Object.freeze([
  "bootstrapClaim",
  "topology",
  "publicUrl",
  "paths",
  "updateChannel",
  "backup",
  "transmitSupport",
  "preflight"
]);

const stepRanks = Object.freeze({
  none: 0,
  bootstrapClaim: 1,
  topology: 2,
  publicUrl: 3,
  paths: 4,
  updateChannel: 5,
  backup: 6,
  transmitSupport: 7,
  preflight: 8,
  administrator: 9
});

const topologyHelp = Object.freeze({
  personalSingleStation: "Gateway, broker, and one local station engine run on this host.",
  localStationGateway: "Gateway and broker serve one or more local station engines.",
  remoteStationGateway: "Gateway and broker accept enrolled remote stations; no local station engine is planned.",
  hybridGateway: "Gateway and broker serve both local and enrolled remote stations."
});

export function nextSetupStep(lastCompletedStep) {
  switch (String(lastCompletedStep || "none")) {
    case "none": return "bootstrapClaim";
    case "bootstrapClaim": return "topology";
    case "topology": return "publicUrl";
    case "publicUrl": return "paths";
    case "paths": return "updateChannel";
    case "updateChannel": return "backup";
    case "backup": return "transmitSupport";
    default: return "preflight";
  }
}

export function cookieValue(cookieHeader, name) {
  const prefix = `${name}=`;
  for (const part of String(cookieHeader || "").split(";")) {
    const candidate = part.trim();
    if (candidate.startsWith(prefix)) {
      return decodeURIComponent(candidate.slice(prefix.length));
    }
  }
  return "";
}

export function buildMutationBody(kind, revision, values = {}) {
  const expectedRevision = Number(revision);
  switch (kind) {
    case "topology":
      return { expectedRevision, topology: values.topology };
    case "publicUrl":
      return { expectedRevision, canonicalPublicUrl: values.canonicalPublicUrl };
    case "paths":
      return {
        expectedRevision,
        configurationDirectory: values.configurationDirectory,
        stateDirectory: values.stateDirectory,
        secretDirectory: values.secretDirectory,
        releaseDirectory: values.releaseDirectory,
        backupDirectory: values.backupDirectory,
        logDirectory: values.logDirectory
      };
    case "updateChannel":
      return {
        expectedRevision,
        updateChannel: values.updateChannel,
        pinnedRelease: values.updateChannel === "pinned"
          ? values.pinnedRelease
          : null
      };
    case "backup":
      return { expectedRevision, confirmed: values.confirmed === true };
    case "transmitSupport":
      return {
        expectedRevision,
        installTransmitSupport: values.installTransmitSupport === true,
        acknowledgedInstallationDoesNotEnableTransmit:
          values.acknowledgedInstallationDoesNotEnableTransmit === true
      };
    case "revoke":
      return { expectedRevision };
    default:
      throw new Error("Unsupported setup mutation.");
  }
}

export function statusSummary(status) {
  if (!status) return "Setup state is unavailable.";
  if (status.setupComplete) return "Setup is complete.";
  if (status.lockMode === "bootstrapRequired") {
    return status.bootstrapTokenPresent
      ? "Waiting for the local bootstrap token."
      : "A local bootstrap token must be issued before setup can be claimed.";
  }
  return `Claimed · next step: ${humanize(nextSetupStep(status.lastCompletedStep))}`;
}

function humanize(value) {
  return String(value || "")
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/^./, character => character.toUpperCase());
}

function initialState(root) {
  const data = root.dataset;
  return {
    revision: Number(data.setupRevision),
    lockMode: data.setupLockMode,
    lastCompletedStep: data.setupLastStep,
    setupComplete: data.setupComplete === "true",
    bootstrapTokenPresent: data.bootstrapTokenPresent === "true",
    bootstrapTokenExpiresAt: data.bootstrapTokenExpiresAt || null,
    topology: data.topology || null,
    canonicalPublicUrlConfigured: data.canonicalUrlConfigured === "true",
    installationPathsConfigured: data.installationPathsConfigured === "true",
    updateChannel: data.updateChannel || "stable",
    installTransmitSupport: data.installTransmitSupport === "true",
    sessionReady: false,
    preflight: null,
    busy: false
  };
}

function statusFromResponse(response) {
  return response?.status || null;
}

function applyStatus(state, response) {
  const status = statusFromResponse(response);
  if (!status) return;
  state.revision = Number(status.revision);
  state.lockMode = status.lockMode;
  state.lastCompletedStep = status.lastCompletedStep;
  state.setupComplete = Boolean(status.setupComplete);
  state.bootstrapTokenPresent = Boolean(status.bootstrapTokenPresent);
  state.bootstrapTokenExpiresAt = status.bootstrapTokenExpiresAt || null;
  state.topology = status.topology || null;
  state.canonicalPublicUrlConfigured = Boolean(status.canonicalPublicUrlConfigured);
  state.installationPathsConfigured = Boolean(status.installationPathsConfigured);
  state.updateChannel = status.updateChannel || "stable";
  state.installTransmitSupport = Boolean(status.installTransmitSupport);
  if (response.session) {
    state.revision = Number(response.session.setupRevision);
    state.lastCompletedStep = response.session.lastCompletedStep;
  }
}

async function apiRequest(path, options = {}) {
  const headers = new Headers({ Accept: "application/json" });
  if (options.revision !== undefined) {
    headers.set("X-Aether-Setup-Revision", String(options.revision));
  }
  let body;
  if (options.body !== undefined) {
    const csrf = cookieValue(document.cookie, "__Host-AetherSdrSetupCsrf");
    if (!csrf) throw new Error("The setup CSRF cookie is unavailable.");
    headers.set("Content-Type", "application/json; charset=utf-8");
    headers.set("X-Aether-Setup-Csrf", csrf);
    body = JSON.stringify(options.body);
  }
  const response = await fetch(path, {
    method: options.method || "GET",
    credentials: "same-origin",
    cache: "no-store",
    redirect: "error",
    headers,
    body
  });
  const contentType = response.headers.get("content-type") || "";
  const payload = contentType.startsWith("application/json")
    ? await response.json()
    : null;
  if (!response.ok) {
    const error = new Error(payload?.code || `requestFailed:${response.status}`);
    error.status = response.status;
    error.payload = payload;
    throw error;
  }
  return payload;
}

function element(id) {
  return document.getElementById(id);
}

function setNotice(message, error = false) {
  const notice = element(error ? "error" : "notice");
  const other = element(error ? "notice" : "error");
  other.hidden = true;
  notice.textContent = message;
  notice.hidden = !message;
}

function setBusy(state, busy) {
  state.busy = busy;
  for (const control of document.querySelectorAll("button, input, select")) {
    if (control.id === "reload-page") continue;
    control.disabled = busy;
  }
}

function renderSteps(state) {
  const active = state.lockMode === "bootstrapRequired"
    ? "bootstrapClaim"
    : nextSetupStep(state.lastCompletedStep);
  const completedRank = stepRanks[state.lastCompletedStep] ?? 0;
  for (const item of document.querySelectorAll("#setup-steps li")) {
    const rank = stepRanks[item.dataset.step] ?? 0;
    item.classList.toggle("is-complete", rank <= completedRank);
    item.classList.toggle("is-active", item.dataset.step === active);
  }
}

function render(state) {
  element("revision-value").textContent = String(state.revision);
  element("status-summary").textContent = statusSummary(state);
  renderSteps(state);

  const claiming = state.lockMode === "bootstrapRequired";
  element("claim-panel").hidden = !claiming;
  element("workflow-panel").hidden = claiming || !state.sessionReady;
  element("session-recovery").hidden = claiming || state.sessionReady;

  if (claiming) {
    const bootstrapStatus = state.bootstrapTokenPresent
      ? `Token expires ${new Date(state.bootstrapTokenExpiresAt).toLocaleString()}.`
      : "Issue a token from the local interactive setup CLI before continuing.";
    element("bootstrap-status").textContent = bootstrapStatus;
    return;
  }
  if (!state.sessionReady) return;

  const active = nextSetupStep(state.lastCompletedStep);
  for (const section of document.querySelectorAll(".setup-step")) {
    section.hidden = section.id !== `step-${active.replace(/[A-Z]/g, letter => `-${letter.toLowerCase()}`)}`;
  }
  element("configured-panel").hidden = !state.preflight;
  if (active === "preflight" && state.preflight) {
    element("step-preflight").hidden = false;
  }
}

function updateTopologyHelp() {
  element("topology-help").textContent =
    topologyHelp[element("topology").value] || "";
}

function updatePinnedVisibility() {
  const pinned = element("update-channel").value === "pinned";
  element("pinned-release-row").hidden = !pinned;
  element("pinned-release").required = pinned;
  if (!pinned) element("pinned-release").value = "";
}

function prefill(root) {
  const data = root.dataset;
  element("canonical-public-url").value = data.canonicalAccessUrl;
  element("configuration-directory").value = data.defaultConfigurationDirectory;
  element("state-directory").value = data.defaultStateDirectory;
  element("secret-directory").value = data.defaultSecretDirectory;
  element("release-directory").value = data.defaultReleaseDirectory;
  element("backup-directory").value = data.defaultBackupDirectory;
  element("log-directory").value = data.defaultLogDirectory;
  element("topology").value = data.topology || "personalSingleStation";
  element("update-channel").value = data.updateChannel || "stable";
  for (const choice of document.querySelectorAll("input[name=installTransmitSupport]")) {
    choice.checked = choice.value === String(data.installTransmitSupport === "true");
  }
  updateTopologyHelp();
  updatePinnedVisibility();
}

async function resume(state) {
  try {
    const response = await apiRequest(endpoints.session, { revision: state.revision });
    applyStatus(state, response);
    state.sessionReady = true;
    setNotice("Browser setup session resumed.");
  } catch {
    state.sessionReady = false;
  }
  render(state);
}

async function mutate(state, kind, values) {
  const response = await apiRequest(endpoints[kind], {
    method: "POST",
    body: buildMutationBody(kind, state.revision, values)
  });
  applyStatus(state, response);
  state.sessionReady = true;
  state.preflight = null;
  setNotice(`${humanize(kind)} saved.`);
  render(state);
}

function listSection(title, values) {
  const section = document.createElement("section");
  section.className = "preflight-section";
  const heading = document.createElement("h3");
  heading.textContent = title;
  section.append(heading);
  const list = document.createElement("ul");
  for (const value of values || []) {
    const item = document.createElement("li");
    item.textContent = String(value);
    list.append(item);
  }
  section.append(list);
  return section;
}

function renderPreflight(report) {
  const target = element("preflight-report");
  target.replaceChildren();
  const meta = document.createElement("div");
  meta.className = "preflight-meta";
  for (const [label, value] of [
    ["Topology", humanize(report.topology)],
    ["Canonical URL", report.canonicalPublicUrl],
    ["Update channel", humanize(report.updateChannel)],
    ["TX support package", report.installTransmitSupport ? "Planned, disabled" : "Not planned"]
  ]) {
    const cell = document.createElement("div");
    const caption = document.createElement("span");
    caption.textContent = label;
    const strong = document.createElement("strong");
    strong.textContent = String(value);
    cell.append(caption, strong);
    meta.append(cell);
  }
  target.append(meta);
  for (const [title, values] of [
    ["Planned users", report.plannedUsers],
    ["Planned packages", report.plannedPackages],
    ["Planned ports", report.plannedPorts],
    ["Planned files", report.plannedFiles],
    ["Planned services", report.plannedServices],
    ["Proxy changes", report.plannedProxyChanges],
    ["Firewall expectations", report.firewallExpectations],
    ["Migrations", report.plannedMigrations],
    ["Warnings", report.warnings]
  ]) {
    target.append(listSection(title, values));
  }
  target.hidden = false;
}

function bind(state, root) {
  element("reload-page").addEventListener("click", () => window.location.reload());
  element("topology").addEventListener("change", updateTopologyHelp);
  element("update-channel").addEventListener("change", updatePinnedVisibility);

  element("claim-form").addEventListener("submit", async event => {
    event.preventDefault();
    const input = element("bootstrap-token");
    const token = input.value;
    input.value = "";
    setBusy(state, true);
    try {
      const response = await apiRequest(endpoints.claim, {
        method: "POST",
        body: { expectedRevision: state.revision, bootstrapToken: token }
      });
      applyStatus(state, response);
      state.sessionReady = true;
      setNotice("Installation claimed. Continue with topology.");
    } catch (error) {
      setNotice(error.status === 409
        ? "Setup changed before the claim completed. Reload the page."
        : "The bootstrap token was rejected or expired.", true);
    } finally {
      setBusy(state, false);
      render(state);
    }
  });

  element("topology-form").addEventListener("submit", event => {
    event.preventDefault();
    runMutation(state, "topology", { topology: element("topology").value });
  });
  element("public-url-form").addEventListener("submit", event => {
    event.preventDefault();
    runMutation(state, "publicUrl", {
      canonicalPublicUrl: root.dataset.canonicalAccessUrl
    });
  });
  element("paths-form").addEventListener("submit", event => {
    event.preventDefault();
    runMutation(state, "paths", {
      configurationDirectory: element("configuration-directory").value,
      stateDirectory: element("state-directory").value,
      secretDirectory: element("secret-directory").value,
      releaseDirectory: element("release-directory").value,
      backupDirectory: element("backup-directory").value,
      logDirectory: element("log-directory").value
    });
  });
  element("update-channel-form").addEventListener("submit", event => {
    event.preventDefault();
    runMutation(state, "updateChannel", {
      updateChannel: element("update-channel").value,
      pinnedRelease: element("pinned-release").value
    });
  });
  element("backup-form").addEventListener("submit", event => {
    event.preventDefault();
    runMutation(state, "backup", { confirmed: element("backup-confirmed").checked });
  });
  element("transmit-support-form").addEventListener("submit", event => {
    event.preventDefault();
    const selected = document.querySelector("input[name=installTransmitSupport]:checked");
    runMutation(state, "transmitSupport", {
      installTransmitSupport: selected?.value === "true",
      acknowledgedInstallationDoesNotEnableTransmit:
        element("tx-acknowledgement").checked
    });
  });

  element("run-preflight").addEventListener("click", async () => {
    setBusy(state, true);
    try {
      const response = await apiRequest(endpoints.preflight, { revision: state.revision });
      applyStatus(state, response);
      state.preflight = response.preflight;
      renderPreflight(response.preflight);
      setNotice("Preflight generated without changing the installation.");
    } catch (error) {
      setNotice(error.status === 401
        ? "The setup session is no longer valid."
        : "Preflight is not available for the current setup state.", true);
    } finally {
      setBusy(state, false);
      render(state);
    }
  });

  element("revoke-session").addEventListener("click", async () => {
    setBusy(state, true);
    try {
      await apiRequest(endpoints.revoke, {
        method: "POST",
        body: buildMutationBody("revoke", state.revision)
      });
      state.sessionReady = false;
      setNotice("Browser setup session ended.");
    } catch {
      setNotice("The browser setup session could not be ended cleanly.", true);
    } finally {
      setBusy(state, false);
      render(state);
    }
  });
}

async function runMutation(state, kind, values) {
  setBusy(state, true);
  try {
    await mutate(state, kind, values);
  } catch (error) {
    const message = error.status === 409
      ? "Setup changed or the requested step is not available. Reload the page."
      : error.status === 401
        ? "The setup session is no longer valid."
        : "The setup value was rejected. Review the fields and try again.";
    setNotice(message, true);
    if (error.status === 401) state.sessionReady = false;
  } finally {
    setBusy(state, false);
    render(state);
  }
}

async function start() {
  const root = document.documentElement;
  const state = initialState(root);
  prefill(root);
  bind(state, root);
  render(state);
  if (state.lockMode === "claimed") {
    await resume(state);
  }
}

if (typeof document !== "undefined") {
  start().catch(() => {
    const target = document.getElementById("error");
    if (target) {
      target.textContent = "The setup browser shell could not initialize.";
      target.hidden = false;
    }
  });
}
