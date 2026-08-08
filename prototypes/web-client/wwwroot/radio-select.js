const browserClientKey = "aether.web.browserClientId";
const sessionIdKey = "aether.web.sessionId";
const controlRoles = new Set(["Aether.Control", "Aether.Admin"]);
const adminRole = "Aether.Admin";

const elements = {
  userName: document.querySelector("#user-name"),
  adminLink: document.querySelector("#admin-link"),
  refresh: document.querySelector("#refresh-radios"),
  notice: document.querySelector("#radio-notice"),
  list: document.querySelector("#radio-list"),
  lowBandwidth: document.querySelector("#low-bandwidth")
};

const state = {
  account: null,
  catalog: null,
  connectingRadioId: "",
  profileInitialized: false,
  refreshing: false,
  refreshTimer: 0
};

initialize().catch(error => {
  showNotice(error.message || "The radio desk could not be loaded.", true);
});

async function initialize() {
  state.account = await getJson("/api/account");
  const displayName =
    state.account.user.name || state.account.user.email || "Operator";
  elements.userName.textContent = displayName;
  elements.adminLink.hidden =
    !state.account.user.roles.includes(adminRole);
  elements.refresh.addEventListener("click", () => refreshCatalog(true));
  await refreshCatalog();
  state.refreshTimer = window.setInterval(refreshCatalog, 3000);
}

async function refreshCatalog(announce = false) {
  if (state.connectingRadioId || state.refreshing) {
    return;
  }
  state.refreshing = true;
  elements.refresh.disabled = true;
  try {
    state.catalog = await getJson("/api/radios/catalog");
    if (!state.profileInitialized) {
      elements.lowBandwidth.checked =
        Boolean(state.catalog.lowBandwidth);
      state.profileInitialized = true;
    }
    renderRadios();
    const online = state.catalog.radios.filter(radio => radio.online).length;
    const connectable = state.catalog.radios.filter(
      radio => radio.online && radio.canSelect).length;
    showNotice(
      `${online} of ${state.catalog.radios.length} radio` +
      `${state.catalog.radios.length === 1 ? "" : "s"} online` +
      ` / ${connectable} ready to connect` +
      (announce ? " · refreshed just now" : ""));
  } catch (error) {
    if (state.catalog) {
      showNotice("Radio discovery update delayed · retrying automatically");
    } else {
      showNotice(
        error.message || "Radio discovery is temporarily unavailable.",
        true);
    }
  } finally {
    state.refreshing = false;
    elements.refresh.disabled = false;
  }
}

function renderRadios() {
  elements.list.replaceChildren();
  const radios = Array.isArray(state.catalog?.radios)
    ? state.catalog.radios
    : [];
  if (radios.length === 0) {
    elements.list.append(
      createElement(
        "div",
        "empty-card",
        "No radios have been discovered locally or through a remote station."));
    return;
  }
  for (const radio of radios) {
    elements.list.append(buildRadioCard(radio));
  }
}

function buildRadioCard(radio) {
  const isRemote = radio.source === "remote";
  const card = createElement(
    "article",
    `radio-card${radio.online ? "" : " offline"}`);

  const heading = createElement("div", "radio-card-heading");
  const identity = document.createElement("div");
  identity.append(
    createElement("h2", "", radio.label || radio.model || "FlexRadio"),
    createElement(
      "small",
      "",
      [radio.model, radio.serial].filter(Boolean).join(" · ") ||
        "FlexRadio"));
  const status = createElement(
    "span",
    `status-pill${radio.online ? "" : " offline"}`,
    radio.online ? "ONLINE" : "OFFLINE");
  heading.append(identity, status);

  const metadata = createElement("div", "radio-meta");
  metadata.append(
    buildMeta(
      "SOURCE",
      isRemote
        ? `Remote / ${radio.stationId || "station"}`
        : `Local / ${radio.host}:${radio.port}`),
    buildMeta("MULTI-FLEX", radio.multiFlexEnabled ? "Enabled" : "Disabled"),
    buildMeta("GUI CLIENTS", capacityLabel(radio)));

  const canControl = state.account.user.roles.some(role =>
    controlRoles.has(role));
  const button = createElement(
    "button",
    "primary-action radio-connect",
    state.connectingRadioId === radio.radioId
      ? "Connecting…"
      : isRemote && !radio.canSelect
        ? "Receive tunnel next"
      : canControl
        ? "Connect"
        : "Observe access only");
  button.type = "button";
  button.disabled =
    !radio.online ||
    !radio.canSelect ||
    !canControl ||
    Boolean(state.connectingRadioId);
  button.title = !radio.online
    ? "This radio is no longer present on the network."
    : !radio.canSelect
      ? "The remote station is online; its receive tunnel is the next phase."
    : !canControl
      ? "A Control or Admin role is required to open a radio."
      : "The radio will make the final GUI-client admission decision.";
  button.addEventListener("click", () => connectToRadio(radio));

  card.append(heading, metadata, button);
  return card;
}

function buildMeta(label, value) {
  const item = document.createElement("div");
  item.append(
    createElement("span", "", label),
    createElement("strong", "", value));
  return item;
}

function capacityLabel(radio) {
  const available = Number(radio.availableClients);
  const licensed = Number(radio.licensedClients);
  if (available < 0 || licensed < 0) {
    return "Radio decides";
  }
  return `${available} of ${licensed} free`;
}

async function connectToRadio(radio) {
  if (state.connectingRadioId) {
    return;
  }
  state.connectingRadioId = radio.radioId;
  window.clearInterval(state.refreshTimer);
  renderRadios();
  showNotice(`Opening a private GUI client on ${radio.label}…`);

  try {
    const browserClientId = getBrowserClientId();
    const lowBandwidth = elements.lowBandwidth.checked;
    let currentSessionId =
      window.sessionStorage.getItem(sessionIdKey) || null;
    let result = await selectRadio(
      radio.radioId,
      browserClientId,
      currentSessionId,
      lowBandwidth);
    if (!result.ok && currentSessionId && result.status === 400) {
      window.sessionStorage.removeItem(sessionIdKey);
      currentSessionId = null;
      result = await selectRadio(
        radio.radioId,
        browserClientId,
        null,
        lowBandwidth);
    }
    const payload = await readJson(result);
    if (!result.ok) {
      throw new Error(
        payload.error || "The radio did not accept this GUI client.");
    }

    window.sessionStorage.setItem(sessionIdKey, payload.sessionId);
    window.location.assign("/radio");
  } catch (error) {
    state.connectingRadioId = "";
    renderRadios();
    showNotice(
      error.message || "The radio did not accept this GUI client.",
      true);
    state.refreshTimer = window.setInterval(refreshCatalog, 3000);
  }
}

async function selectRadio(
  radioId,
  browserClientId,
  currentSessionId,
  lowBandwidth) {
  const antiforgeryHeaders = await getAntiforgeryHeaders();
  return fetch("/api/radios/select", {
    method: "POST",
    credentials: "same-origin",
    headers: {
      Accept: "application/json",
      "Content-Type": "application/json",
      ...antiforgeryHeaders
    },
    body: JSON.stringify({
      radioId,
      browserClientId,
      currentSessionId,
      lowBandwidth
    })
  });
}

function getBrowserClientId() {
  const existing = window.sessionStorage.getItem(browserClientKey);
  if (/^[0-9a-f]{32}$/i.test(existing || "")) {
    return existing;
  }
  const next = crypto.randomUUID().replaceAll("-", "");
  window.sessionStorage.setItem(browserClientKey, next);
  return next;
}

async function getAntiforgeryHeaders() {
  const token = await getJson("/api/antiforgery");
  if (token.headerName !== "X-Aether-CSRF" || !token.requestToken) {
    throw new Error("The request security token is invalid.");
  }
  return { [token.headerName]: token.requestToken };
}

async function getJson(url) {
  const response = await fetch(url, {
    credentials: "same-origin",
    headers: { Accept: "application/json" }
  });
  if (response.status === 401) {
    window.location.assign(
      `/auth/login?returnUrl=${encodeURIComponent("/radios")}`);
    throw new Error("Sign-in is required.");
  }
  const result = await readJson(response);
  if (!response.ok) {
    throw new Error(result.error || `Request failed (${response.status}).`);
  }
  return result;
}

async function readJson(response) {
  const text = await response.text();
  if (!text) {
    return {};
  }
  try {
    return JSON.parse(text);
  } catch {
    return {};
  }
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
