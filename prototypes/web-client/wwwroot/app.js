import { WaterfallRenderer } from "./waterfall.js?v=reconnect-1";
import { AetherSMeter } from "./meter.js?v=needle-3";
import { RadioAudioPlayer } from "./audio.js?v=visibility-recovery-2";
import { RadioTransportClient } from "./radio-transport.js?v=background-delivery-1";
import { LocalMicrophoneMonitor } from "./microphone.js";
import {
  normalizeBandPlan,
  visibleBandSegments
} from "./band-plan.js?v=band-plan-1";
import {
  clampFilterEdgesForMode,
  filterEdgesForMode,
  formatFilterWidth,
  normalizeSpectrumMode,
  rxControlAvailability,
  signalDbmToMeterFraction,
  sliceFlagDirection,
  sliceFlagDirections,
  sliceSignalDbm
} from "./slice-controls.js?v=receive-fidelity-1";
import {
  clampPanCenter,
  formatFrequency,
  isFrequencyVisible,
  parseFrequency,
  resolveFrequencySliceId
} from "./frequency-controls.js?v=mobile-tune-2";
import {
  audioPanToSlider,
  rangeFillPercent,
  sliderToAudioPan
} from "./range-controls.js";
import {
  clampAppletRailWidth,
  shouldCloseToolPanel
} from "./layout-controls.js?v=tool-toggle-1";
import {
  buildPolicyRequest,
  formatClientCapacity,
  normalizeAdminMode
} from "./admin-controls.js?v=radio-admin-1";
import {
  ReconnectBackoff
} from "./radio-transport-core.js?v=background-delivery-1";
import {
  AdaptiveBandwidthController,
  formatTrafficRate
} from "./network-profile.js?v=network-profile-1";
import {
  BrowserTxController,
  txControlAvailability
} from "./tx-controls.js?v=tx-intent-validation-1";

const controlRole = "Aether.Control";
const adminRole = "Aether.Admin";
const liveSliceTuneIntervalMs = 100;
const livePanTuneIntervalMs = 100;
const livePanSettleMs = 160;
const minimumPanBandwidthHz = 10_000;
const maximumPanBandwidthHz = 14_000_000;
const dynamicStyleRules = new WeakMap();
let dynamicStyleId = 0;
const browserClientIdKey = "aether.web.browserClientId";
const sessionIdKey = "aether.web.sessionId";
const browserClientId = createBrowserClientId();

const state = {
  socket: null,
  session: null,
  radio: null,
  presence: [],
  radioSelection: null,
  adminRadios: [],
  adminRefreshing: false,
  networkTraffic: null,
  networkProfileInitialized: false,
  networkProfileTransition: false,
  lastNetworkDecision: "",
  tx: null,
  forcedDisconnect: false,
  activeSliceId: "A",
  activePanId: "",
  requestId: 0,
  lastRadioConnectionError: "",
  frequencyTimers: new Map(),
  liveSliceTuners: new Map(),
  livePanTuners: new Map(),
  bandPlanSegments: [],
  bandPlanNodes: new Map(),
  spectrumMode: normalizeSpectrumMode(
    window.localStorage.getItem("aether.web.spectrumMode")),
  displayFill:
    window.localStorage.getItem("aether.web.displayFill") !== "false",
  displayPeak:
    window.localStorage.getItem("aether.web.displayPeak") === "true",
  waterfallVisible:
    window.localStorage.getItem("aether.web.waterfallVisible") !== "false",
  collapsedSlices: readSlicePreferenceSet("aether.web.collapsedSlices"),
  lockedSlices: readSlicePreferenceSet("aether.web.lockedSlices"),
  activeTool: window.localStorage.getItem("aether.web.activeTool") || "dsp",
  toolOpen: window.localStorage.getItem("aether.web.toolOpen") === "true",
  activeApplet: window.localStorage.getItem("aether.web.activeApplet") || "rx",
  appletRailHidden: readAppletRailHiddenPreference()
};

const elements = {
  connectionDot: document.querySelector("#connection-dot"),
  connectionLabel: document.querySelector("#connection-label"),
  accountName: document.querySelector("#account-name"),
  accountInitial: document.querySelector("#account-initial"),
  radioMode: document.querySelector("#radio-mode"),
  radioSelector: document.querySelector("#radio-selector"),
  pcAudio: document.querySelector("#pc-audio"),
  masterVolume: document.querySelector("#master-volume"),
  headphoneVolume: document.querySelector("#headphone-volume"),
  addSlice: document.querySelector("#add-slice"),
  addPanButtons: [...document.querySelectorAll("[data-add-pan]")],
  panTabs: document.querySelector("#pan-tabs"),
  panLeft: document.querySelector("#pan-left"),
  panRight: document.querySelector("#pan-right"),
  operatorsButton: document.querySelector("#operators-button"),
  operatorsPopover: document.querySelector("#operators-popover"),
  appMenuPopover: document.querySelector("#app-menu-popover"),
  accountButton: document.querySelector("#account-button"),
  accountPopover: document.querySelector("#account-popover"),
  accountPopoverName: document.querySelector("#account-popover-name"),
  accountPopoverEmail: document.querySelector("#account-popover-email"),
  chooseRadioAction: document.querySelector("#choose-radio-action"),
  adminPageAction: document.querySelector("#admin-page-action"),
  signOutAction: document.querySelector("#sign-out-action"),
  sliceDeck: document.querySelector("#slice-deck"),
  spectrumCanvas: document.querySelector("#spectrum-canvas"),
  waterfallCanvas: document.querySelector("#waterfall-canvas"),
  activeSliceLabel: document.querySelector("#active-slice-label"),
  rxSliceChip: document.querySelector("#rx-slice-chip"),
  rxLockState: document.querySelector("#rx-lock-state"),
  rxAntennaLabel: document.querySelector("#rx-antenna-label"),
  rxFilterLabel: document.querySelector("#rx-filter-label"),
  balance: document.querySelector("#balance"),
  afMute: document.querySelector("#af-mute"),
  afGain: document.querySelector("#af-gain"),
  afGainValue: document.querySelector("#af-gain-value"),
  sqlToggle: document.querySelector("#sql-toggle"),
  squelch: document.querySelector("#squelch"),
  squelchValue: document.querySelector("#squelch-value"),
  agcMode: document.querySelector("#agc-mode"),
  agcThreshold: document.querySelector("#agc-threshold"),
  frequencyInput: document.querySelector("#frequency-input"),
  modeSelect: document.querySelector("#mode-select"),
  tuneStep: document.querySelector("#tune-step"),
  displayAverage: document.querySelector("#display-average"),
  displayFps: document.querySelector("#display-fps"),
  displayFloor: document.querySelector("#display-floor"),
  displayFill: document.querySelector("#display-fill"),
  displayPeak: document.querySelector("#display-peak"),
  displayWaterfall: document.querySelector("#display-waterfall"),
  displayWnb: document.querySelector("#display-wnb"),
  displayWnbLevel: document.querySelector("#display-wnb-level"),
  lowBandwidth: document.querySelector("#low-bandwidth"),
  networkProfileStatus: document.querySelector("#network-profile-status"),
  wnbStatus: document.querySelector("#wnb-status"),
  panadapter: document.querySelector(".panadapter"),
  panRange: document.querySelector("#pan-range"),
  bandPlan: document.querySelector("#band-plan"),
  radioModel: document.querySelector("#radio-model"),
  radioSerial: document.querySelector("#radio-serial"),
  waterfallSliceOverlays: document.querySelector("#waterfall-slice-overlays"),
  meterDbm: document.querySelector("#meter-dbm"),
  meterS: document.querySelector("#meter-s"),
  sessionId: document.querySelector("#session-id"),
  presenceCount: document.querySelector("#presence-count"),
  presenceList: document.querySelector("#presence-list"),
  toolFlyout: document.querySelector("#tool-flyout"),
  workspace: document.querySelector(".radio-workspace"),
  appletRail: document.querySelector("#applet-rail"),
  appletResizer: document.querySelector("#applet-resizer"),
  appletRailToggle: document.querySelector("#applet-rail-toggle"),
  toast: document.querySelector("#toast"),
  clock: document.querySelector("#clock"),
  footerConnection: document.querySelector("#footer-connection"),
  pcMic: document.querySelector("#pc-mic"),
  pcMicLevel: document.querySelector("#pc-mic-level"),
  pcMicDb: document.querySelector("#pc-mic-db"),
  txMox: document.querySelector("#tx-mox"),
  txTune: document.querySelector("#tx-tune"),
  txCwx: document.querySelector("#tx-cwx"),
  txLockNote: document.querySelector("#tx-lock-note"),
  txAuthorityPanel: document.querySelector("#tx-authority-panel"),
  txAuthorityState: document.querySelector("#tx-authority-state"),
  txAuthorityDetail: document.querySelector("#tx-authority-detail"),
  txLeaseToggle: document.querySelector("#tx-lease-toggle"),
  txIntentAction: document.querySelector("#tx-intent-action"),
  txIntentCwText: document.querySelector("#tx-intent-cw-text"),
  txIntentValidate: document.querySelector("#tx-intent-validate"),
  adminAppletTab: document.querySelector("#admin-applet-tab"),
  adminApplet: document.querySelector("#applet-admin"),
  adminRefresh: document.querySelector("#admin-refresh"),
  adminRadioList: document.querySelector("#admin-radio-list")
};

const renderer = new WaterfallRenderer(
  elements.spectrumCanvas,
  document.querySelector("#waterfall-canvas"));
const sMeter = new AetherSMeter(document.querySelector("#smeter-canvas"));
const reconnectBackoff = new ReconnectBackoff();
const adaptiveBandwidth = new AdaptiveBandwidthController();
const radioTransport = new RadioTransportClient(
  "/radio-transport-worker.js?v=background-delivery-1");
const audioPlayer = new RadioAudioPlayer();
audioPlayer.setTransportHandlers(
  port => radioTransport.attachAudioPort(port),
  (enabled, sliceAvailable) =>
    radioTransport.setAudioState(enabled, sliceAvailable));
radioTransport.onopen = handleTransportOpen;
radioTransport.ontext = handleTransportText;
radioTransport.onbinary = handleTransportBinary;
radioTransport.onclose = handleTransportClose;
radioTransport.onerror = handleTransportError;
radioTransport.onaudiodiagnostics = diagnostics => {
  audioPlayer.updateTransportDiagnostics(diagnostics);
};
radioTransport.onnetworkdiagnostics = handleNetworkDiagnostics;
const microphoneMonitor = new LocalMicrophoneMonitor((percent, db) => {
  setDynamicStyle(elements.pcMicLevel, "--mic-level", `${percent}%`);
  elements.pcMicLevel.setAttribute("aria-valuenow", String(percent));
  elements.pcMicDb.textContent =
    microphoneMonitor.enabled ? `${Math.round(db)} dB` : "OFF";
});
const txController = new BrowserTxController({
  send: message => send(message),
  nextRequestId,
  onChange: snapshot => {
    state.tx = snapshot;
    renderTxControls();
  }
});
state.tx = txController.snapshot();
sMeter.loadGeometry("/assets/s-meter-v1.json");
renderer.setRenderMode(state.spectrumMode);
renderer.setFillEnabled(state.displayFill);
renderer.setPeakEnabled(state.displayPeak);
renderer.setWaterfallEnabled(state.waterfallVisible);

renderer.onTune = frequencyHz => {
  requestSliceFrequency(state.activeSliceId, frequencyHz, true, true);
};
renderer.onStep = direction => tuneByStep(direction);
renderer.onSliceActivate = sliceId => activateSlice(sliceId);
renderer.onSliceTune = (sliceId, frequencyHz, final) => {
  handleDraggedSliceFrequency(sliceId, frequencyHz, final);
};
renderer.onSlicePreview = (sliceId, frequencyHz) => {
  previewSliceCard(sliceId, frequencyHz);
};
renderer.onFilterChange = (sliceId, filterLowHz, filterHighHz) => {
  const slice = state.radio?.slices.find(item => item.id === sliceId);
  if (!slice) {
    return;
  }
  const edges = clampFilterEdgesForMode(
    slice.mode,
    { filterLowHz, filterHighHz });
  slice.filterLowHz = edges.filterLowHz;
  slice.filterHighHz = edges.filterHighHz;
  renderAll();
  sendIntent("slice.set", sliceId, edges);
};
renderer.onPanPreview = centerFrequencyHz => {
  previewPanUi(centerFrequencyHz);
  const pan = activePan();
  if (pan && canControlRadio()) {
    queueLivePanCenter(pan.id, centerFrequencyHz);
  }
};
renderer.onPanCommit = centerFrequencyHz => {
  commitDraggedPanCenter(centerFrequencyHz);
};
renderer.onPanConfirmed = (centerFrequencyHz, bandwidthHz) => {
  const pan = activePan();
  if (pan) {
    pan.centerFrequencyHz = centerFrequencyHz;
    pan.bandwidthHz = bandwidthHz;
  }
  renderAll();
};
renderer.onZoom = (factor, anchorFraction) => {
  requestPanZoom(factor, anchorFraction);
};
renderer.isSliceLocked = sliceId => state.lockedSlices.has(sliceId);
renderer.onTuneBlocked = sliceId => {
  showToast(`Slice ${sliceId} is locked.`, true);
};

initialize().catch(error => {
  showToast(error.message || "Could not initialize the browser session.", true);
  setConnectionState("disconnected", "Unavailable");
});

document.addEventListener("visibilitychange", () => {
  updateAudioPageVisibility(document.visibilityState !== "hidden");
});

window.addEventListener("pageshow", () => {
  updateAudioPageVisibility(document.visibilityState !== "hidden");
});

window.addEventListener("pagehide", event => {
  updateAudioPageVisibility(false);
  if (!event.persisted &&
      state.socket?.readyState === WebSocket.OPEN) {
    txController.requestRelease();
    state.socket.close(1000, "Browser page closed.");
  }
});

let audioVisibilityUpdate = Promise.resolve();

function updateAudioPageVisibility(visible) {
  reportClientVisibility(visible);
  audioVisibilityUpdate = audioVisibilityUpdate
    .catch(() => {})
    .then(() => audioPlayer.setPageVisible(visible))
    .catch(error => {
      console.warn("Could not update PC audio page state.", error);
      if (visible && audioPlayer.enabled) {
        showToast(
          "PC audio is paused by the browser. Tap PC AUDIO to resume.",
          true);
      }
    });
}

function reportClientVisibility(visible) {
  if (!state.socket || state.socket.readyState !== WebSocket.OPEN) {
    return;
  }

  state.socket.send(JSON.stringify({
    cmd: "client.visibility",
    visible: visible === true
  }));
}

async function initialize() {
  const response = await requestBrowserSession();
  if (response.status === 401) {
    window.location.assign(
      `/auth/login?returnUrl=${encodeURIComponent("/radio")}`);
    return;
  }
  if (!response.ok) {
    throw new Error(`Session request failed (${response.status}).`);
  }

  state.session = await response.json();
  window.sessionStorage.setItem(sessionIdKey, state.session.sessionId);
  await loadBandPlan();
  const displayName =
    state.session.user.name || state.session.user.email || "Operator";
  elements.accountName.textContent = displayName;
  elements.accountInitial.textContent =
    displayName.trim().charAt(0).toUpperCase();
  elements.accountPopoverName.textContent = displayName;
  elements.accountPopoverEmail.textContent =
    state.session.user.email || "";
  elements.radioMode.textContent = state.session.radioMode.toUpperCase();
  elements.sessionId.textContent = state.session.sessionId;

  configureAdminUi();
  wireControls();
  await refreshRadioSelector();
  restoreLayoutPreferences();
  updateClock();
  connect();
  window.setInterval(refreshRadioSelector, 3000);
  window.setInterval(updateClock, 1000);
  window.setInterval(reportAudioDiagnostics, 2000);
}

async function requestBrowserSession(useStoredSession = true) {
  const storedSessionId = useStoredSession
    ? window.sessionStorage.getItem(sessionIdKey)
    : "";
  const query = new URLSearchParams({ browserClientId });
  if (storedSessionId) {
    query.set("sessionId", storedSessionId);
  }
  let response = await fetch(`/api/session?${query}`, {
    credentials: "same-origin",
    headers: { Accept: "application/json" }
  });
  if (response.status === 404 && storedSessionId) {
    window.sessionStorage.removeItem(sessionIdKey);
    response = await requestBrowserSession(false);
  }
  return response;
}

async function refreshRadioSelector() {
  try {
    const sessionQuery = state.session?.sessionId
      ? `?sessionId=${encodeURIComponent(state.session.sessionId)}`
      : "";
    const response = await fetch(`/api/radios${sessionQuery}`, {
      credentials: "same-origin",
      headers: { Accept: "application/json" }
    });
    if (!response.ok) {
      throw new Error(`Radio discovery request failed (${response.status}).`);
    }
    state.radioSelection = await response.json();
    renderRadioSelector();
  } catch (error) {
    elements.radioSelector.title =
      error.message || "Radio discovery is temporarily unavailable.";
  }
}

function renderRadioSelector() {
  const selection = state.radioSelection;
  if (!selection || !Array.isArray(selection.radios)) {
    return;
  }

  elements.radioSelector.replaceChildren();
  for (const radio of selection.radios) {
    const option = document.createElement("option");
    option.value = radio.radioId;
    const capacityStatus =
      radio.availableClients === 0 ? " · NO OPEN GUI SLOTS" : "";
    option.textContent =
      `${radio.label}` +
      `${radio.online ? "" : " · OFFLINE"}` +
      capacityStatus;
    option.disabled =
      !radio.canSelect &&
      !radio.isSelected &&
      !radio.isConfiguredFallback;
    elements.radioSelector.append(option);
  }
  elements.radioSelector.value = selection.selectedRadioId;
  elements.radioSelector.disabled =
    !canControlRadio() || selection.radios.length === 0;
  const selected = selection.radios.find(radio => radio.isSelected);
  elements.radioSelector.title = selected
    ? `${selected.model} ${selected.serial} at ${selected.host}:${selected.port}`
    : "Select a Flex radio";
  syncToggleButton(
    elements.lowBandwidth,
    Boolean(selection.lowBandwidth));
  elements.lowBandwidth.disabled =
    !canControlRadio() || state.networkProfileTransition;
  if (!state.networkProfileInitialized) {
    if (selection.lowBandwidth) {
      adaptiveBandwidth.noteManualSelection(true);
    } else {
      adaptiveBandwidth.reset();
    }
    state.networkProfileInitialized = true;
  }
  renderNetworkProfileStatus();
}

async function selectRadio(radioId) {
  if (!radioId || !canControlRadio()) {
    renderRadioSelector();
    return;
  }

  elements.radioSelector.disabled = true;
  showToast("Opening your private session on the selected radio…");
  try {
    const response = await fetch("/api/radios/select", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        radioId,
        currentSessionId: state.session.sessionId,
        browserClientId
      })
    });
    const result = await response.json();
    if (!response.ok) {
      throw new Error(result.error || "The radio could not be selected.");
    }
    state.session.sessionId = result.sessionId;
    window.sessionStorage.setItem(sessionIdKey, result.sessionId);
    elements.sessionId.textContent = result.sessionId;
    if (result.connectionChanged) {
      reconnectBackoff.reset();
      audioPlayer.reset();
      connect(true);
    }
    await refreshRadioSelector();
    showToast(
      result.connectionChanged
        ? "Radio selected. Your private receive session is connecting…"
        : "That radio is already connected.");
  } catch (error) {
    showToast(error.message || "The radio could not be selected.", true);
    await refreshRadioSelector();
  }
}

async function setLowBandwidth(
  enabled,
  automatic = false,
  reason = "") {
  if (!canControlRadio()) {
    showToast("Your account has observe-only access.", true);
    return;
  }
  if (state.networkProfileTransition) {
    return;
  }

  state.networkProfileTransition = true;
  if (!automatic) {
    adaptiveBandwidth.noteManualSelection(enabled);
    state.lastNetworkDecision = enabled
      ? "Manual low-bandwidth hold"
      : "Adaptive monitoring resumed";
  }
  elements.lowBandwidth.disabled = true;
  showToast(
    automatic
      ? `${enabled ? "Network quality fell" : "Network quality recovered"}. ` +
        `${enabled ? "Reducing" : "Restoring"} display traffic…`
      : enabled
        ? "Enabling low-bandwidth VPN mode and reconnecting the radio…"
        : "Returning to adaptive normal bandwidth and reconnecting the radio…");
  try {
    const response = await fetch("/api/radio/low-bandwidth", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        enabled,
        sessionId: state.session.sessionId
      })
    });
    const result = await response.json();
    if (!response.ok) {
      throw new Error(
        result.error || "The connection profile could not be changed.");
    }
    await refreshRadioSelector();
    showToast(
      `${result.enabled
        ? "VPN low-bandwidth mode enabled"
        : "Normal-bandwidth mode enabled"} for your radio session.` +
      (automatic && reason ? ` Trigger: ${reason}.` : ""));
  } catch (error) {
    if (automatic) {
      adaptiveBandwidth.reset();
      state.lastNetworkDecision = "Automatic profile change failed";
    }
    showToast(
      error.message || "The connection profile could not be changed.",
      true);
    await refreshRadioSelector();
  } finally {
    state.networkProfileTransition = false;
    renderRadioSelector();
  }
}

function handleNetworkDiagnostics(traffic) {
  state.networkTraffic = traffic;
  const audio = audioPlayer.getDiagnostics(state.activeSliceId);
  const lowBandwidth = Boolean(state.radioSelection?.lowBandwidth);
  const pageVisible = audio.pageVisible;
  const action = adaptiveBandwidth.observe({
    traffic,
    missingPackets: audio.missingPackets,
    lowBandwidth,
    connected:
      state.radio?.connected === true &&
      state.socket?.readyState === WebSocket.OPEN,
    pageVisible
  });

  renderNetworkProfileStatus();
  if (state.socket?.readyState === WebSocket.OPEN) {
    state.socket.send(JSON.stringify({
      cmd: "diagnostics.network",
      profile: lowBandwidth ? "low" : "normal",
      adaptation: adaptiveBandwidth.manualLowBandwidth
        ? "manual"
        : "automatic",
      pageVisible,
      missingAudioPackets: audio.missingPackets,
      ...traffic
    }));
  }

  if (!action || state.networkProfileTransition || !canControlRadio()) {
    return;
  }

  state.lastNetworkDecision =
    `${action.enabled ? "Reduced traffic" : "Restored normal traffic"}: ` +
    action.reason;
  setLowBandwidth(action.enabled, true, action.reason);
}

function renderNetworkProfileStatus() {
  if (!elements.networkProfileStatus) {
    return;
  }

  const lowBandwidth = Boolean(state.radioSelection?.lowBandwidth);
  const profile = lowBandwidth ? "LOW" : "NORMAL";
  const control = adaptiveBandwidth.manualLowBandwidth
    ? "MANUAL HOLD"
    : "ADAPTIVE";
  const traffic = state.networkTraffic;
  const rate = traffic
    ? formatTrafficRate(traffic.bitsPerSecond)
    : "measuring…";
  elements.networkProfileStatus.textContent =
    `${control} · ${profile} · ${rate}`;
  elements.networkProfileStatus.title = traffic
    ? `${Math.round(traffic.maximumGapMilliseconds || 0)} ms max gap` +
      (state.lastNetworkDecision
        ? ` · ${state.lastNetworkDecision}`
        : "")
    : "Network measurements begin after the radio stream starts.";
}

function isAdministrator() {
  return Boolean(state.session?.user?.roles?.includes(adminRole));
}

function configureAdminUi() {
  elements.adminAppletTab.hidden = true;
  elements.adminApplet.hidden = true;
  elements.adminPageAction.hidden = !isAdministrator();
  if (state.activeApplet === "admin") {
    state.activeApplet = "rx";
    window.localStorage.setItem("aether.web.activeApplet", "rx");
  }
}

async function refreshAdminInventory(announce = false) {
  if (!isAdministrator() || state.adminRefreshing) {
    return;
  }

  state.adminRefreshing = true;
  elements.adminRefresh.disabled = true;
  try {
    const response = await fetch("/api/admin/radios", {
      credentials: "same-origin",
      headers: { Accept: "application/json" }
    });
    const result = await response.json();
    if (!response.ok) {
      throw new Error(result.error || "Radio allocation could not be loaded.");
    }
    state.adminRadios = Array.isArray(result.radios) ? result.radios : [];
    renderAdminInventory();
    if (announce) {
      showToast("Radio allocation refreshed.");
    }
  } catch (error) {
    elements.adminRadioList.replaceChildren();
    const message = document.createElement("div");
    message.className = "admin-empty error";
    message.textContent =
      error.message || "Radio allocation is temporarily unavailable.";
    elements.adminRadioList.append(message);
  } finally {
    state.adminRefreshing = false;
    elements.adminRefresh.disabled = false;
  }
}

function renderAdminInventory() {
  elements.adminRadioList.replaceChildren();
  if (state.adminRadios.length === 0) {
    const empty = document.createElement("div");
    empty.className = "admin-empty";
    empty.textContent = "No radios are currently in the server inventory.";
    elements.adminRadioList.append(empty);
    return;
  }

  state.adminRadios.forEach((radio, index) => {
    elements.adminRadioList.append(buildAdminRadioCard(radio, index));
  });
}

function buildAdminRadioCard(radio, index) {
  const card = document.createElement("article");
  card.className = "admin-radio-card";
  card.dataset.radioId = radio.radioId;

  const heading = document.createElement("div");
  heading.className = "admin-radio-heading";
  const identity = document.createElement("div");
  const title = document.createElement("strong");
  title.textContent = radio.label;
  const endpoint = document.createElement("small");
  endpoint.textContent =
    `${radio.serial || "No serial"} · ${radio.host}:${radio.port}`;
  identity.append(title, endpoint);
  const status = document.createElement("span");
  status.className =
    `admin-radio-status ${radio.online ? "online" : "offline"}`;
  status.textContent = radio.online ? "ONLINE" : "OFFLINE";
  heading.append(identity, status);

  const capacity = document.createElement("p");
  capacity.className = "admin-capacity";
  capacity.textContent =
    `${formatClientCapacity(
      radio.availableClients,
      radio.licensedClients)} · ` +
    `${radio.multiFlexEnabled ? "Multi-Flex enabled" : "Multi-Flex disabled"}`;

  const policyForm = document.createElement("div");
  policyForm.className = "admin-policy-form";
  const modeLabel = document.createElement("label");
  const modeCaption = document.createElement("span");
  modeCaption.textContent = "Access";
  const modeSelect = document.createElement("select");
  modeSelect.setAttribute("aria-label", `Access mode for ${radio.label}`);
  for (const [value, label] of [
    ["shared", "Shared"],
    ["exclusive", "Exclusive"]
  ]) {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = label;
    modeSelect.append(option);
  }
  modeSelect.value = normalizeAdminMode(radio.policy?.mode);
  modeLabel.append(modeCaption, modeSelect);

  const reservationLabel = document.createElement("label");
  reservationLabel.className = "admin-reservation";
  const reservationCaption = document.createElement("span");
  reservationCaption.textContent = "Reserved Entra account ID";
  const reservationInput = document.createElement("input");
  reservationInput.type = "text";
  reservationInput.maxLength = 256;
  reservationInput.placeholder = "Blank allows any authorized account";
  reservationInput.value = radio.policy?.reservedUserId || "";
  reservationInput.setAttribute(
    "aria-label",
    `Reserved account for ${radio.label}`);
  const suggestionId = `admin-radio-users-${index}`;
  reservationInput.setAttribute("list", suggestionId);
  const suggestions = document.createElement("datalist");
  suggestions.id = suggestionId;
  for (const operator of radio.operators || []) {
    const option = document.createElement("option");
    option.value = operator.userId;
    option.label = operator.displayName;
    suggestions.append(option);
  }
  reservationLabel.append(
    reservationCaption,
    reservationInput,
    suggestions);

  const save = document.createElement("button");
  save.type = "button";
  save.className = "admin-save";
  save.textContent = "SAVE POLICY";
  save.addEventListener("click", async () => {
    await saveAdminPolicy(
      radio.radioId,
      modeSelect.value,
      reservationInput.value,
      save);
  });
  policyForm.append(modeLabel, reservationLabel, save);

  const operatorSection = document.createElement("div");
  operatorSection.className = "admin-operators";
  const operatorHeading = document.createElement("strong");
  operatorHeading.textContent =
    `Active operators · ${(radio.operators || []).length}`;
  operatorSection.append(operatorHeading);
  if (!radio.operators?.length) {
    const empty = document.createElement("span");
    empty.className = "admin-no-operators";
    empty.textContent = "No web sessions are holding this radio.";
    operatorSection.append(empty);
  } else {
    for (const operator of radio.operators) {
      operatorSection.append(
        buildAdminOperatorRow(radio, operator));
    }
  }

  card.append(heading, capacity, policyForm, operatorSection);
  return card;
}

function buildAdminOperatorRow(radio, operator) {
  const row = document.createElement("div");
  row.className = "admin-operator-row";
  const identity = document.createElement("div");
  const name = document.createElement("strong");
  name.textContent = operator.displayName;
  const detail = document.createElement("small");
  detail.textContent =
    `${operator.browserConnections} browser connection` +
    `${operator.browserConnections === 1 ? "" : "s"} · ` +
    `${operator.radioSessions} radio session` +
    `${operator.radioSessions === 1 ? "" : "s"}`;
  detail.title = operator.userId;
  identity.append(name, detail);
  const disconnect = document.createElement("button");
  disconnect.type = "button";
  disconnect.className = "admin-disconnect";
  disconnect.textContent = "DISCONNECT";
  disconnect.addEventListener("click", async () => {
    const confirmed = window.confirm(
      `Disconnect ${operator.displayName} from ${radio.label}? ` +
      "Their slices, audio, and radio stream will be released.");
    if (!confirmed) {
      return;
    }
    await forceDisconnectOperator(
      radio.radioId,
      operator.userId,
      operator.displayName,
      disconnect);
  });
  row.append(identity, disconnect);
  return row;
}

async function saveAdminPolicy(
  radioId,
  mode,
  reservedUserId,
  button) {
  button.disabled = true;
  try {
    const response = await fetch(
      `/api/admin/radios/${encodeURIComponent(radioId)}/policy`,
      {
        method: "POST",
        credentials: "same-origin",
        headers: {
          Accept: "application/json",
          "Content-Type": "application/json"
        },
        body: JSON.stringify(buildPolicyRequest(mode, reservedUserId))
      });
    const result = await response.json();
    if (!response.ok) {
      throw new Error(result.error || "The radio policy was not saved.");
    }
    showToast("Radio allocation policy saved.");
    await refreshAdminInventory();
  } catch (error) {
    showToast(error.message || "The radio policy was not saved.", true);
  } finally {
    button.disabled = false;
  }
}

async function forceDisconnectOperator(
  radioId,
  userId,
  displayName,
  button) {
  button.disabled = true;
  try {
    const response = await fetch(
      `/api/admin/radios/${encodeURIComponent(radioId)}` +
      `/operators/${encodeURIComponent(userId)}/disconnect`,
      {
        method: "POST",
        credentials: "same-origin",
        headers: { Accept: "application/json" }
      });
    const result = await response.json();
    if (!response.ok) {
      throw new Error(
        result.error || "The operator could not be disconnected.");
    }
    showToast(
      `${displayName} disconnected · ${result.radioSessions} radio session` +
      `${result.radioSessions === 1 ? "" : "s"} released.`);
    await refreshAdminInventory();
  } catch (error) {
    showToast(
      error.message || "The operator could not be disconnected.",
      true);
  } finally {
    button.disabled = false;
  }
}

function connect(replace = false) {
  if (!state.session?.sessionId) {
    return;
  }
  if (!replace &&
      (radioTransport.readyState === WebSocket.OPEN ||
       radioTransport.readyState === WebSocket.CONNECTING)) {
    return;
  }

  reconnectBackoff.cancel();
  setConnectionState("connecting", "Connecting");
  const scheme = window.location.protocol === "https:" ? "wss" : "ws";
  state.socket = radioTransport;
  radioTransport.connect(
    `${scheme}://${window.location.host}/ws/radio` +
      `?sessionId=${encodeURIComponent(state.session.sessionId)}`,
    "aethersdr.experimental.v0");
}

function handleTransportOpen() {
  setConnectionState("connecting", "Waiting for radio");
  showToast("Browser GUI client connected to the gateway.");
  send({ cmd: "hello", id: nextRequestId(), protocolVersion: 0 });
  reportClientVisibility(document.visibilityState !== "hidden");
}

function handleTransportBinary(buffer) {
  if (renderer.acceptFrame(buffer)) {
    updateSignalMeter();
  }
}

function handleTransportText(text) {
  let message;
  try {
    message = JSON.parse(text);
  } catch (error) {
    console.error("Invalid gateway JSON message.", error);
    showToast("The gateway sent malformed data.", true);
    return;
  }
  try {
    handleMessage(message);
  } catch (error) {
    console.error("Could not apply a gateway update.", error, message);
    showToast("A radio display update could not be applied.", true);
  }
}

function handleTransportClose(event) {
  cancelPendingRadioInput();
  txController.resetForDisconnect();
  if (state.forcedDisconnect) {
    reconnectBackoff.cancel();
    setConnectionState(
      "disconnected",
      "Disconnected by administrator");
    audioPlayer.reset();
    showToast(
      "An administrator released this radio session. Reload to reconnect.",
      true);
    return;
  }
  setConnectionState("disconnected", "Reconnecting");
  audioPlayer.reset();
  const delay = scheduleRadioRecovery();
  if (delay === null) {
    return;
  }
  showToast(
    event.code === 1000
      ? "Session closed."
        : `Connection lost. Retrying in ${Math.round(delay / 1000)}s…`,
    event.code !== 1000);
}

function handleTransportError() {
  setConnectionState("disconnected", "Connection error");
}

async function recoverRadioSession() {
  if (state.forcedDisconnect ||
      radioTransport.readyState === WebSocket.OPEN ||
      radioTransport.readyState === WebSocket.CONNECTING) {
    return;
  }

  try {
    const response = await requestBrowserSession();
    if (response.status === 401) {
      window.location.assign(
        `/auth/login?returnUrl=${encodeURIComponent("/radio")}`);
      return;
    }
    if (!response.ok) {
      throw new Error(`Session recovery failed (${response.status}).`);
    }

    const recovered = await response.json();
    state.session = recovered;
    window.sessionStorage.setItem(sessionIdKey, recovered.sessionId);
    elements.sessionId.textContent = recovered.sessionId;
    await refreshRadioSelector();
    connect();
  } catch (error) {
    const delay = scheduleRadioRecovery();
    if (delay === null) {
      return;
    }
    showToast(
      `${error.message || "Session recovery failed"} ` +
      `Retrying in ${Math.round(delay / 1000)}s...`,
      true);
  }
}

function scheduleRadioRecovery() {
  if (state.forcedDisconnect) {
    return null;
  }
  return reconnectBackoff.schedule(recoverRadioSession);
}

function cancelPendingRadioInput() {
  state.frequencyTimers.forEach(timer => {
    window.clearTimeout(timer);
  });
  state.frequencyTimers.clear();

  state.liveSliceTuners.forEach(tuner => {
    if (tuner.timer) {
      window.clearTimeout(tuner.timer);
    }
  });
  state.liveSliceTuners.clear();

  state.livePanTuners.forEach(tuner => {
    if (tuner.timer) {
      window.clearTimeout(tuner.timer);
    }
    if (tuner.settleTimer) {
      window.clearTimeout(tuner.settleTimer);
    }
  });
  state.livePanTuners.clear();
  renderer.cancelUserInteraction();
}

function updateRadioConnectionState() {
  if (!state.radio) {
    setConnectionState("connecting", "Waiting for radio");
    return;
  }

  if (state.radio.connected) {
    const recoveredFromRadioError =
      Boolean(state.lastRadioConnectionError);
    state.lastRadioConnectionError = "";
    setConnectionState("connected", "AetherSDR");
    if (recoveredFromRadioError) {
      showToast("Radio connection restored.");
    }
    return;
  }

  const waitingForSlot = state.radio.connectionState === "radio-busy";
  setConnectionState(
    waitingForSlot ? "disconnected" : "connecting",
    waitingForSlot ? "Radio GUI slots full" : "Waiting for radio");

  const connectionError = state.radio.connectionError || "";
  if (connectionError &&
      connectionError !== state.lastRadioConnectionError) {
    state.lastRadioConnectionError = connectionError;
    showToast(connectionError, true);
  }
}

function handleMessage(message) {
  if (message.type === "welcome") {
    reconnectBackoff.reset();
    txController.applyWelcome(message.capabilities?.tx);
    state.radio = message.snapshot;
    state.presence = message.presence || [];
    syncActivePan();
    state.activeSliceId =
      slicesForActivePan().find(
        slice => slice.id === state.radio.activeSliceId)?.id ??
      slicesForActivePan()[0]?.id ??
      "";
    renderAll();
    updateRadioConnectionState();
    return;
  }

  if (txController.handleMessage(message)) {
    showTxFeedback(message);
    return;
  }

  if (message.event === "presence") {
    state.presence = message.clients || [];
    renderPresence();
    return;
  }

  if (message.event === "admin.disconnected") {
    state.forcedDisconnect = true;
    showToast(
      message.reason ||
      "An administrator released this radio session.",
      true);
    return;
  }

  if (message.event === "snapshot" && message.snapshot) {
    const preferredSliceId = state.activeSliceId;
    const previousPan = activePan();
    const panMotionActive =
      renderer.isPanGestureActive() ||
      renderer.hasPendingPan();
    state.radio = message.snapshot;
    syncActivePan();
    if (panMotionActive && previousPan) {
      const pendingPan = panadapters().find(
        pan => pan.id === previousPan.id);
      if (pendingPan) {
        pendingPan.centerFrequencyHz = previousPan.centerFrequencyHz;
        pendingPan.bandwidthHz = previousPan.bandwidthHz;
        if (state.radio.panadapter?.id === pendingPan.id) {
          state.radio.panadapter.centerFrequencyHz =
            previousPan.centerFrequencyHz;
          state.radio.panadapter.bandwidthHz =
            previousPan.bandwidthHz;
        }
      }
    }
    const visibleSlices = slicesForActivePan();
    const preferredSlice = visibleSlices.find(
      slice => slice.id === preferredSliceId);
    state.activeSliceId =
      preferredSlice?.id ??
      visibleSlices.find(
        slice => slice.id === state.radio.activeSliceId)?.id ??
      visibleSlices[0]?.id ??
      "";
    setLocalActiveSlice(state.activeSliceId);
    if (panMotionActive) {
      const pan = activePan();
      if (pan) {
        renderer.configure(pan);
      }
      renderer.setSlices(slicesForActivePan(), state.activeSliceId);
    } else {
      renderAll();
    }
    updateRadioConnectionState();
    return;
  }

  if ((message.event === "changed" ||
       (message.ok === true && message.model && message.changes)) &&
      state.radio) {
    if (message.model === "panadapter") {
      const pan = panadapters().find(
        item => item.id === message.selector);
      if (pan) {
        const preservePendingFrequency =
          pan.id === state.activePanId &&
          (renderer.isPanGestureActive() || renderer.hasPendingPan()) &&
          (Object.hasOwn(message.changes, "centerFrequencyHz") ||
           Object.hasOwn(message.changes, "bandwidthHz"));
        const centerFrequencyHz = pan.centerFrequencyHz;
        const bandwidthHz = pan.bandwidthHz;
        Object.assign(pan, message.changes);
        if (state.radio.panadapter?.id === pan.id) {
          Object.assign(state.radio.panadapter, message.changes);
        }
        if (preservePendingFrequency) {
          pan.centerFrequencyHz = centerFrequencyHz;
          pan.bandwidthHz = bandwidthHz;
          if (state.radio.panadapter?.id === pan.id) {
            state.radio.panadapter.centerFrequencyHz = centerFrequencyHz;
            state.radio.panadapter.bandwidthHz = bandwidthHz;
          }
        }
        state.radio.version = message.version;
        if (!preservePendingFrequency) {
          renderAll();
        } else {
          renderer.configure(pan);
        }
      }
      return;
    }
    const slice = state.radio.slices.find(item => item.id === message.selector);
    if (slice) {
      Object.assign(slice, message.changes);
      if (Object.hasOwn(message.changes, "audioMute")) {
        slice.isMuted = Boolean(message.changes.audioMute);
      }
      if (message.changes.isActive) {
        state.radio.slices.forEach(item => {
          item.isActive = item.id === slice.id;
        });
        state.radio.activeSliceId = slice.id;
        state.activeSliceId = slice.id;
      }
      state.radio.version = message.version;
      renderAll();
    }
    return;
  }

  if (message.ok === false && message.error) {
    showToast(message.error, true);
  }
}

function createBrowserClientId() {
  const existing = window.sessionStorage.getItem(browserClientIdKey);
  if (/^[0-9a-f]{32}$/i.test(existing || "")) {
    return existing;
  }
  const bytes = new Uint8Array(16);
  window.crypto.getRandomValues(bytes);
  const next = [...bytes]
    .map(value => value.toString(16).padStart(2, "0"))
    .join("");
  window.sessionStorage.setItem(browserClientIdKey, next);
  return next;
}

function readAppletRailHiddenPreference() {
  const stored =
    window.localStorage.getItem("aether.web.appletRailHidden");
  if (stored === "true" || stored === "false") {
    return stored === "true";
  }
  return window.matchMedia("(max-width: 760px)").matches;
}

function renderAll() {
  if (!state.radio) {
    return;
  }
  syncActivePan();
  const hadAudioSlice = audioPlayer.sliceAvailable;
  const hasAudioSlice = state.radio.slices.length > 0;
  audioPlayer.setSliceAvailable(hasAudioSlice);
  if (audioPlayer.enabled && hadAudioSlice && !hasAudioSlice) {
    showToast("No receiver slices. PC audio queue cleared.");
  }
  renderSlices();
  renderPanTabs();
  renderControls();
  renderTxControls();
  renderPresence();
  renderSpectrumMode();
  renderDisplayControls();

  const pan = activePan();
  if (!pan) {
    renderer.setStreamId(0);
    renderer.setSlices([], "");
    resetSignalMeter();
    return;
  }
  renderer.configure(pan);
  renderer.setStreamId(pan.streamId);
  renderPanRange(pan.centerFrequencyHz);
  renderBandPlan(pan.centerFrequencyHz);
  elements.radioModel.textContent =
    state.radio.radioModel.replace(/\s*\(simulated\)\s*/i, "");
  elements.radioSerial.textContent = state.radio.serial;
  renderer.setSlices(slicesForActivePan(), state.activeSliceId);
  renderWaterfallSliceOverlays();
  updateSignalMeter();
}

function renderPanTabs() {
  elements.panTabs.replaceChildren();
  const pans = panadapters();
  for (const [index, pan] of pans.entries()) {
    const tab = document.createElement("div");
    tab.className = "pan-tab";
    tab.setAttribute("role", "tab");
    tab.setAttribute("aria-selected", String(pan.id === state.activePanId));
    tab.tabIndex = pan.id === state.activePanId ? 0 : -1;
    tab.title = `Show ${pan.id}`;
    const label = document.createElement("span");
    label.textContent =
      `PAN ${index + 1} · ${(pan.centerFrequencyHz / 1e6).toFixed(3)}`;
    tab.append(label);
    tab.addEventListener("click", () => selectPan(pan.id));
    tab.addEventListener("keydown", event => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        selectPan(pan.id);
      }
    });

    if (pans.length > 1) {
      const close = document.createElement("button");
      close.type = "button";
      close.className = "pan-tab-close";
      close.textContent = "×";
      close.title = `Remove ${pan.id}`;
      close.setAttribute("aria-label", `Remove panadapter ${index + 1}`);
      close.addEventListener("click", event => {
        event.stopPropagation();
        sendIntent("pan.remove", pan.id, {});
      });
      tab.append(close);
    }
    elements.panTabs.append(tab);
  }
}

function selectPan(panId) {
  if (state.activePanId === panId) {
    return;
  }
  state.activePanId = panId;
  const slices = slicesForActivePan();
  state.activeSliceId =
    slices.find(slice => slice.isActive)?.id ??
    slices[0]?.id ??
    "";
  setLocalActiveSlice(state.activeSliceId);
  renderAll();
}

function renderSlices() {
  elements.sliceDeck.replaceChildren();
  const pan = activePan();
  const startHz = pan.centerFrequencyHz - (pan.bandwidthHz / 2);
  const visibleSlices = slicesForActivePan()
    .map(slice => ({
      slice,
      normalized:
        (slice.frequencyHz - startHz) / pan.bandwidthHz
    }))
    .filter(item =>
      item.normalized >= 0 && item.normalized <= 1);
  const directions = sliceFlagDirections(
    visibleSlices.map(item => item.normalized));

  for (const [index, item] of visibleSlices.entries()) {
    const { slice, normalized } = item;
    const collapsed = state.collapsedSlices.has(slice.id);
    const locked = state.lockedSlices.has(slice.id);
    const muted = Boolean(slice.isMuted);
    const direction = directions[index];
    const filterWidth = slice.filterHighHz - slice.filterLowHz;
    const card = document.createElement("article");
    card.dataset.sliceId = slice.id;
    card.className =
      `slice-card slice-${slice.id.toLowerCase()} flag-${direction}` +
      (slice.isActive ? " active" : "") +
      (collapsed ? " collapsed" : "") +
      (locked ? " locked" : "");
    setDynamicStyle(card, "--slice-x", `${normalized * 100}%`);
    card.tabIndex = 0;
    card.setAttribute("role", "group");
    card.setAttribute(
      "aria-label",
      `Slice ${slice.id}, ${formatFrequency(slice.frequencyHz)}, ${slice.mode}` +
      (slice.isActive ? ", active" : "") +
      (locked ? ", locked" : ""));

    if (collapsed) {
      card.innerHTML = `
        <div class="slice-collapsed-flag">
          <button type="button" class="slice-letter"
                  data-slice-action="collapse"
                  aria-label="Expand slice ${escapeHtml(slice.id)}">${escapeHtml(slice.id)}</button>
          <span class="collapsed-tx${slice.isTx ? " assigned" : ""}">TX</span>
        </div>
        <span class="collapsed-frequency">${formatFrequency(slice.frequencyHz)}</span>`;
    } else {
      card.innerHTML = `
        <div class="slice-side-actions">
          <button type="button" data-slice-action="lock"
                  class="${locked ? "active" : ""}"
                  aria-label="${locked ? "Unlock" : "Lock"} slice ${escapeHtml(slice.id)}"
                  aria-pressed="${locked}">${locked ? "●" : "○"}</button>
          <button type="button" data-slice-action="remove"
                  class="remove"
                  title="Delete slice ${escapeHtml(slice.id)}"
                  aria-label="Delete slice ${escapeHtml(slice.id)}">×</button>
        </div>
        <div class="slice-card-head">
          <span class="slice-flags">
            <button type="button" data-slice-action="antenna">${escapeHtml(displayAntenna(slice.rxAntenna))}</button>
            <span class="tx-antenna">${escapeHtml(displayAntenna(slice.rxAntenna))}</span>
            <button type="button" data-slice-action="filter">${formatFilterWidth(filterWidth)}</button>
          </span>
          <span class="slice-header-actions">
            <button type="button" data-slice-action="split"
                    title="Split transmit assignment is disabled in the GUI prototype"
                    disabled>${slice.isActive ? "SPLIT" : "SWAP"}</button>
            <span class="tx-flag${slice.isTx ? " assigned" : ""}">TX</span>
            <button type="button" class="slice-letter"
                    data-slice-action="collapse"
                    aria-label="Collapse slice ${escapeHtml(slice.id)}">${escapeHtml(slice.id)}</button>
          </span>
        </div>
        <input class="slice-card-frequency" type="text"
               inputmode="decimal"
               enterkeyhint="done"
               title="Type a frequency and press Enter"
               value="${formatFrequency(slice.frequencyHz)}"
               aria-label="Slice ${escapeHtml(slice.id)} frequency">
        <div class="slice-meter"
             role="meter"
             aria-label="Slice ${escapeHtml(slice.id)} signal meter"
             aria-valuemin="-127"
             aria-valuemax="-13"
             aria-valuenow="-127"
             aria-valuetext="No signal reading"><i></i></div>
        <div class="slice-card-foot" aria-label="Slice controls">
          <button type="button" data-slice-tab="audio"
                  class="${muted ? "muted" : ""}"
                  aria-label="${muted ? "Unmute" : "Mute"} slice audio">${muted ? "🔇" : "🔊"}</button>
          <button type="button" data-slice-tab="dsp">DSP</button>
          <button type="button" data-slice-tab="mode"
                  class="slice-mode">${escapeHtml(slice.mode)}</button>
          <button type="button" data-slice-tab="xrit">X/RIT</button>
          <button type="button" data-slice-tab="dax">DAX</button>
        </div>`;
    }

    const activate = () => activateSlice(slice.id);
    card.addEventListener("click", event => {
      if (!event.target.closest("button, input")) {
        activate();
      }
    });
    card.addEventListener("keydown", event => {
      if ((event.key === "Enter" || event.key === " ") &&
          event.target === card) {
        event.preventDefault();
        activate();
      }
    });
    card.querySelectorAll("[data-slice-action]").forEach(button => {
      button.addEventListener("click", event => {
        event.stopPropagation();
        handleSliceAction(button.dataset.sliceAction, slice);
      });
    });
    card.querySelectorAll("[data-slice-tab]").forEach(button => {
      button.addEventListener("click", event => {
        event.stopPropagation();
        handleSliceTab(button.dataset.sliceTab, slice);
      });
    });
    const frequency = card.querySelector(".slice-card-frequency");
    if (frequency) {
      frequency.addEventListener("click", event => {
        event.stopPropagation();
      });
      frequency.addEventListener("focus", () => {
        // Keep the focused input alive while a mobile keyboard edits it.
        // The committed tune carries isActive=true to the radio.
        activateSlice(slice.id, false, false);
        if (state.lockedSlices.has(slice.id)) {
          showToast(`Slice ${slice.id} is locked.`, true);
          frequency.blur();
          return;
        }
        frequency.select();
      });
      frequency.addEventListener("input", () => {
        delete frequency.dataset.lastCommittedHz;
      });
      frequency.addEventListener("keydown", event => {
        if (event.key === "Enter") {
          event.preventDefault();
          commitSliceFlagFrequency(slice.id, frequency);
        } else if (event.key === "Escape") {
          frequency.value = formatFrequency(slice.frequencyHz);
          frequency.blur();
        }
      });
      frequency.addEventListener("change", () => {
        commitSliceFlagFrequency(slice.id, frequency);
      });
      frequency.addEventListener("blur", () => {
        commitSliceFlagFrequency(slice.id, frequency);
      });
    }
    elements.sliceDeck.append(card);
  }
}

function previewSliceCard(
  sliceId,
  frequencyHz,
  centerFrequencyHz = activePan()?.centerFrequencyHz) {
  const pan = activePan();
  if (!pan) {
    return;
  }
  const card = Array.from(elements.sliceDeck.children).find(
    item => item.dataset.sliceId === sliceId);
  if (!card) {
    return;
  }

  const startHz = centerFrequencyHz - (pan.bandwidthHz / 2);
  const normalized = (frequencyHz - startHz) / pan.bandwidthHz;
  if (normalized < 0 || normalized > 1) {
    card.hidden = true;
    return;
  }
  card.hidden = false;
  const direction = sliceFlagDirection(normalized);
  card.classList.toggle("flag-left", direction === "left");
  card.classList.toggle("flag-right", direction === "right");
  setDynamicStyle(card, "--slice-x", `${normalized * 100}%`);

  const frequency = card.querySelector(".slice-card-frequency");
  if (frequency && document.activeElement !== frequency) {
    frequency.value = formatFrequency(frequencyHz);
  }
  const collapsedFrequency = card.querySelector(".collapsed-frequency");
  if (collapsedFrequency) {
    collapsedFrequency.textContent = formatFrequency(frequencyHz);
  }
}

function renderControls() {
  const slice = activeSlice();
  if (!slice) {
    elements.activeSliceLabel.textContent = "—";
    elements.rxSliceChip.textContent = "—";
    elements.rxFilterLabel.textContent = "—";
    elements.frequencyInput.value = "";
    elements.frequencyInput.disabled = true;
    elements.modeSelect.disabled = true;
    elements.afGain.disabled = true;
    elements.afMute.disabled = true;
    elements.sqlToggle.disabled = true;
    elements.squelch.disabled = true;
    elements.balance.disabled = true;
    elements.agcMode.disabled = true;
    elements.agcThreshold.disabled = true;
    document.querySelectorAll("[data-dax-channel]").forEach(button => {
      button.disabled = true;
    });
    renderSliceDsp(null);
    return;
  }

  elements.frequencyInput.disabled = false;
  elements.modeSelect.disabled = false;
  elements.afGain.disabled = false;
  elements.afMute.disabled = false;
  const rxAvailability = rxControlAvailability(
    slice.mode,
    state.radio?.radioModel);
  elements.sqlToggle.disabled = !rxAvailability.squelch;
  elements.squelch.disabled = !rxAvailability.squelch;
  const squelchTitle = rxAvailability.squelch
    ? ""
    : "Squelch is unavailable in digital and CW modes.";
  elements.sqlToggle.title = squelchTitle;
  elements.squelch.title = squelchTitle;
  elements.balance.disabled = false;
  elements.agcMode.disabled = false;
  elements.agcThreshold.disabled = false;
  document.querySelectorAll("[data-dax-channel]").forEach(button => {
    const channel = Number(button.dataset.daxChannel);
    const active = channel === Number(slice.daxChannel ?? 0);
    button.disabled = false;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", String(active));
  });
  elements.activeSliceLabel.textContent = slice.id;
  elements.rxSliceChip.textContent = slice.id;
  elements.rxSliceChip.classList.toggle("slice-b", slice.id === "B");
  const locked = state.lockedSlices.has(slice.id);
  elements.rxLockState.textContent = locked ? "🔒" : "🔓";
  elements.rxLockState.setAttribute(
    "aria-label",
    locked ? "VFO locked" : "VFO unlocked");
  const muted = Boolean(slice.isMuted);
  elements.afMute.classList.toggle("active", muted);
  elements.afMute.textContent = muted ? "AF 🔇" : "AF";
  elements.afMute.setAttribute("aria-pressed", String(muted));
  syncRangeControl(elements.afGain, slice.afGain, elements.afGainValue);
  syncRangeControl(elements.squelch, slice.squelch, elements.squelchValue);
  syncRangeControl(
    elements.balance,
    audioPanToSlider(slice.audioPan ?? 50));
  elements.rxAntennaLabel.textContent = displayAntenna(slice.rxAntenna);
  elements.agcMode.value = String(slice.agcMode || "MED").toUpperCase();
  syncRangeControl(elements.agcThreshold, slice.agcThreshold ?? 65);
  elements.sqlToggle.classList.toggle(
    "active",
    Boolean(slice.squelchEnabled));
  elements.sqlToggle.setAttribute(
    "aria-pressed",
    String(Boolean(slice.squelchEnabled)));
  renderSliceDsp(slice);
  document.querySelectorAll("[data-rx-antenna]").forEach(button => {
    const active =
      button.dataset.rxAntenna === (slice.rxAntenna || "ANT1");
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", String(active));
  });
  if (document.activeElement !== elements.frequencyInput) {
    elements.frequencyInput.value = formatFrequency(slice.frequencyHz);
  }
  elements.modeSelect.value = slice.mode;

  const width = slice.filterHighHz - slice.filterLowHz;
  elements.rxFilterLabel.textContent = formatFilterWidth(width);
  document.querySelectorAll("[data-filter]").forEach(button => {
    button.classList.toggle("active", Number(button.dataset.filter) === width);
  });
}

function renderTxControls() {
  const snapshot = state.tx || txController.snapshot();
  const capability = snapshot.capability;
  const availability = txControlAvailability(capability);
  const hasLeaseSecret = Boolean(snapshot.lease?.leaseId);
  const requestPending = Number(snapshot.pendingCount) > 0;

  elements.txAuthorityPanel.hidden = !availability.showAuthorityPanel;
  elements.txAuthorityState.textContent =
    String(capability.state || "unavailable")
      .replaceAll("-", " ")
      .toUpperCase();
  elements.txAuthorityDetail.textContent = capability.message;
  elements.txLeaseToggle.textContent = hasLeaseSecret
    ? "RELEASE LEASE"
    : "ACQUIRE LEASE";
  elements.txLeaseToggle.disabled = hasLeaseSecret
    ? !availability.canReleaseLease
    : requestPending || !availability.canAcquireLease;

  const validationEnabled =
    hasLeaseSecret &&
    availability.canValidateIntent &&
    !requestPending;
  elements.txIntentAction.disabled = !validationEnabled;
  elements.txIntentValidate.disabled = !validationEnabled;
  const cwSelected =
    elements.txIntentAction.value === "cw.send:text";
  elements.txIntentCwText.hidden = !cwSelected;
  elements.txIntentCwText.disabled = !validationEnabled || !cwSelected;

  elements.txMox.hidden = !availability.enableMox;
  elements.txMox.disabled = !availability.enableMox;
  elements.txTune.hidden = !availability.enableTune;
  elements.txTune.disabled = !availability.enableTune;
  elements.txCwx.hidden = !availability.enableCw;
  elements.txCwx.disabled = !availability.enableCw;
  elements.txLockNote.textContent = validationEnabled
    ? "Exact deliberate intent may be validated. No radio command transport is connected."
    : "No browser transmit path is connected.";
}

function showTxFeedback(message) {
  const result = state.tx?.lastResult;
  if (!result || message.event) {
    return;
  }

  if (result.kind === "acquire" && message.ok === true) {
    showToast(
      "TX ownership lease acquired. Radio command transport remains unavailable.");
    return;
  }
  if (result.kind === "renew" && message.ok === true) {
    return;
  }
  if (result.kind === "release" && message.ok === true) {
    showToast("TX ownership lease released.");
    return;
  }
  if (result.validated && result.outcome === "transport-unavailable") {
    showToast(
      `${result.action || "TX intent"} validated; no radio command was sent.`);
    return;
  }
  if (result.error) {
    showToast(result.error, true);
  }
}

function requestSelectedTxValidation() {
  const [action, value] = elements.txIntentAction.value.split(":", 2);
  const values = action === "cw.send"
    ? { text: elements.txIntentCwText.value.trim() }
    : { enabled: value === "on" };
  if (!txController.requestIntent(action, values)) {
    showToast(
      "An exact current TX lease and fresh validated authority are required.",
      true);
  }
}

function renderPresence() {
  elements.presenceCount.textContent = state.presence.length;
  elements.presenceList.replaceChildren();

  for (const person of state.presence) {
    const role =
      person.roles.includes(adminRole) ? "Admin" :
      person.roles.includes("Aether.Transmit") ? "Transmit" :
      person.roles.includes(controlRole) ? "Control" : "Observe";
    const connectionCount = Math.max(1, Number(person.connectionCount) || 1);
    const connectionLabel =
      connectionCount === 1 ? "" : ` · ${connectionCount} connections`;
    const row = document.createElement("div");
    row.className = "presence-person";
    row.innerHTML = `
      <span class="presence-avatar">${escapeHtml(person.displayName.charAt(0).toUpperCase())}</span>
      <div>
        <strong>${escapeHtml(person.displayName)}</strong>
        <small>${escapeHtml(person.userId)}${connectionLabel}</small>
      </div>
      <span class="presence-role">${role}</span>`;
    elements.presenceList.append(row);
  }
}

function renderWaterfallSliceOverlays(
  centerFrequencyHz = activePan()?.centerFrequencyHz) {
  const pan = activePan();
  if (!pan || !Number.isFinite(Number(centerFrequencyHz))) {
    elements.waterfallSliceOverlays.replaceChildren();
    return;
  }
  const startHz = centerFrequencyHz - (pan.bandwidthHz / 2);
  elements.waterfallSliceOverlays.replaceChildren();
  for (const slice of slicesForActivePan()) {
    const normalized = (slice.frequencyHz - startHz) / pan.bandwidthHz;
    if (normalized < 0 || normalized > 1) {
      continue;
    }
    const width =
      ((slice.filterHighHz - slice.filterLowHz) / pan.bandwidthHz) * 100;
    const line = document.createElement("span");
    line.className =
      `waterfall-slice-line slice-${slice.id.toLowerCase()}` +
      (slice.isActive ? " active" : "");
    line.dataset.sliceId = slice.id;
    setDynamicStyle(line, "--x", `${normalized * 100}%`);
    const passbandPx =
      (Math.max(.2, width) / 100) *
      Math.max(1, elements.waterfallSliceOverlays.clientWidth);
    setDynamicStyle(line, "--passband", `${passbandPx}px`);
    elements.waterfallSliceOverlays.append(line);
  }
}

function wireControls() {
  audioPlayer.setVolume(
    Number(elements.masterVolume.value),
    Number(elements.headphoneVolume.value));
  const updateAudioVolume = () => {
    audioPlayer.setVolume(
      Number(elements.masterVolume.value),
      Number(elements.headphoneVolume.value));
  };
  elements.masterVolume.addEventListener("input", updateAudioVolume);
  elements.headphoneVolume.addEventListener("input", updateAudioVolume);
  elements.pcAudio.addEventListener("click", async () => {
    if (audioPlayer.enabled && audioPlayer.recoveryPending) {
      try {
        const recovered = await audioPlayer.resumeFromUserGesture();
        showToast(recovered
          ? "PC receive audio resumed."
          : "PC receive audio is already recovering.");
      } catch (error) {
        showToast(
          error.message || "Tap PC AUDIO again to resume sound.",
          true);
      }
      return;
    }

    const enable = !elements.pcAudio.classList.contains("active");
    try {
      await audioPlayer.setEnabled(enable);
      elements.pcAudio.classList.toggle("active", enable);
      elements.pcAudio.setAttribute("aria-pressed", String(enable));
      showToast(enable
        ? "PC receive audio enabled."
        : "PC receive audio muted.");
    } catch (error) {
      elements.pcAudio.classList.remove("active");
      elements.pcAudio.setAttribute("aria-pressed", "false");
      showToast(error.message || "Could not start PC audio.", true);
    }
  });
  elements.txLeaseToggle.addEventListener("click", () => {
    const snapshot = state.tx || txController.snapshot();
    const requested = snapshot.lease
      ? txController.requestRelease()
      : txController.requestAcquire();
    if (!requested) {
      showToast(
        snapshot.capability.message ||
        "TX ownership lease is unavailable.",
        true);
    }
  });
  elements.txIntentAction.addEventListener("change", renderTxControls);
  elements.txIntentValidate.addEventListener(
    "click",
    requestSelectedTxValidation);
  elements.txMox.addEventListener("click", () => {
    const enabled = !elements.txMox.classList.contains("active");
    txController.requestIntent("mox.set", { enabled });
  });
  elements.txTune.addEventListener("click", () => {
    const enabled = !elements.txTune.classList.contains("active");
    txController.requestIntent("tune.set", { enabled });
  });
  elements.txCwx.addEventListener("click", () => {
    txController.requestIntent("cw.send", {
      text: elements.txIntentCwText.value.trim()
    });
  });
  elements.pcMic.addEventListener("click", async () => {
    const enable = !elements.pcMic.classList.contains("active");
    try {
      await microphoneMonitor.setEnabled(enable);
      elements.pcMic.classList.toggle("active", enable);
      elements.pcMic.setAttribute("aria-pressed", String(enable));
      showToast(enable
        ? "Local PC microphone meter enabled. Audio is not sent to the radio."
        : "Local PC microphone meter stopped.");
    } catch (error) {
      elements.pcMic.classList.remove("active");
      elements.pcMic.setAttribute("aria-pressed", "false");
      showToast(
        error.message || "Could not start the local microphone meter.",
        true);
    }
  });
  elements.radioSelector.addEventListener("change", async () => {
    await selectRadio(elements.radioSelector.value);
  });
  elements.lowBandwidth.addEventListener("click", async () => {
    await setLowBandwidth(
      !Boolean(state.radioSelection?.lowBandwidth));
  });
  elements.adminRefresh.addEventListener("click", async () => {
    await refreshAdminInventory(true);
  });

  elements.addSlice.addEventListener("click", () => {
    const slice = activeSlice();
    const pan = activePan();
    const halfBandwidth = (pan?.bandwidthHz ?? 0) / 2;
    const sliceIsVisible =
      slice &&
      pan &&
      slice.frequencyHz >= pan.centerFrequencyHz - halfBandwidth &&
      slice.frequencyHz <= pan.centerFrequencyHz + halfBandwidth;
    sendIntent("slice.create", "", {
      frequencyHz:
        sliceIsVisible ? slice.frequencyHz : pan?.centerFrequencyHz,
      mode: slice?.mode ?? "USB",
      panId: pan?.id
    });
  });
  elements.addPanButtons.forEach(button => {
    button.addEventListener("click", () => {
      const pan = activePan();
      if (!pan) {
        return;
      }
      sendIntent("pan.create", "", {
        centerFrequencyHz: pan.centerFrequencyHz
      });
    });
  });
  elements.panLeft.addEventListener("click", () => movePan(-1));
  elements.panRight.addEventListener("click", () => movePan(1));
  document.querySelectorAll("[data-pan-zoom]").forEach(button => {
    button.addEventListener("click", () => {
      requestPanZoom(
        button.dataset.panZoom === "in" ? (1 / 1.5) : 1.5);
    });
  });

  document.querySelectorAll("[data-spectrum-mode]").forEach(button => {
    button.addEventListener("click", () => {
      setSpectrumMode(button.dataset.spectrumMode);
    });
  });

  document.querySelectorAll("input[type=\"range\"]").forEach(input => {
    updateRangeFill(input);
    input.addEventListener("input", () => {
      updateRangeFill(input);
      const output =
        (input.id
          ? document.querySelector(`output[for="${input.id}"]`)
          : null) ??
        input.closest("label")?.querySelector("output") ??
        input.parentElement?.querySelector("output");
      if (output) {
        output.textContent = input.value;
      }
    });
  });

  elements.afGain.addEventListener("change", () => {
    sendIntent("slice.set", state.activeSliceId, {
      afGain: Number(elements.afGain.value)
    });
  });

  elements.afMute.addEventListener("click", () => {
    const slice = activeSlice();
    if (!slice) {
      return;
    }
    sendIntent("slice.set", slice.id, {
      audioMute: !Boolean(slice.isMuted)
    });
  });

  elements.squelch.addEventListener("change", () => {
    sendIntent("slice.set", state.activeSliceId, {
      squelch: Number(elements.squelch.value)
    });
  });
  elements.balance.addEventListener("change", () => {
    sendIntent("slice.set", state.activeSliceId, {
      audioPan: sliderToAudioPan(elements.balance.value)
    });
  });
  elements.sqlToggle.addEventListener("click", () => {
    const slice = activeSlice();
    if (!slice) {
      return;
    }
    sendIntent("slice.set", slice.id, {
      squelchEnabled: !Boolean(slice.squelchEnabled)
    });
  });
  elements.agcMode.addEventListener("change", () => {
    sendIntent("slice.set", state.activeSliceId, {
      agcMode: elements.agcMode.value
    });
  });
  elements.agcThreshold.addEventListener("change", () => {
    sendIntent("slice.set", state.activeSliceId, {
      agcThreshold: Number(elements.agcThreshold.value)
    });
  });

  document.querySelectorAll("[data-dax-channel]").forEach(button => {
    button.addEventListener("click", () => {
      sendIntent("slice.set", state.activeSliceId, {
        daxChannel: Number(button.dataset.daxChannel)
      });
    });
  });

  elements.displayAverage.addEventListener("change", () => {
    sendIntent("pan.set", activePan()?.id ?? "", {
      fftAverage: Number(elements.displayAverage.value)
    });
  });
  elements.displayFps.addEventListener("change", () => {
    sendIntent("pan.set", activePan()?.id ?? "", {
      framesPerSecond: Number(elements.displayFps.value)
    });
  });
  elements.displayFloor.addEventListener("change", () => {
    sendIntent("pan.set", activePan()?.id ?? "", {
      minDbm: Number(elements.displayFloor.value)
    });
  });
  elements.displayFill.addEventListener("click", () => {
    state.displayFill = !state.displayFill;
    window.localStorage.setItem(
      "aether.web.displayFill",
      String(state.displayFill));
    renderer.setFillEnabled(state.displayFill);
    renderDisplayControls();
  });
  elements.displayPeak.addEventListener("click", () => {
    state.displayPeak = !state.displayPeak;
    window.localStorage.setItem(
      "aether.web.displayPeak",
      String(state.displayPeak));
    renderer.setPeakEnabled(state.displayPeak);
    renderDisplayControls();
  });
  elements.displayWaterfall.addEventListener("click", () => {
    state.waterfallVisible = !state.waterfallVisible;
    window.localStorage.setItem(
      "aether.web.waterfallVisible",
      String(state.waterfallVisible));
    renderer.setWaterfallEnabled(state.waterfallVisible);
    renderDisplayControls();
    window.requestAnimationFrame(() => renderer.resize());
  });
  elements.displayWnb.addEventListener("click", () => {
    const pan = activePan();
    if (!pan) {
      return;
    }
    sendIntent("pan.set", pan.id, {
      wnbEnabled: !Boolean(pan.wnbEnabled)
    });
  });
  elements.displayWnbLevel.addEventListener("change", () => {
    sendIntent("pan.set", activePan()?.id ?? "", {
      wnbLevel: Number(elements.displayWnbLevel.value)
    });
  });

  document.querySelectorAll("[data-slice-toggle]").forEach(button => {
    button.addEventListener("click", () => {
      const slice = activeSlice();
      const property = button.dataset.sliceToggle;
      if (!slice || !property) {
        return;
      }
      sendIntent("slice.set", slice.id, {
        [property]: !Boolean(slice[property])
      });
    });
  });

  document.querySelectorAll("[data-slice-level]").forEach(input => {
    input.addEventListener("change", () => {
      const property = input.dataset.sliceLevel;
      if (!property) {
        return;
      }
      sendIntent("slice.set", state.activeSliceId, {
        [property]: Number(input.value)
      });
    });
  });

  document.querySelectorAll("[data-rx-antenna]").forEach(button => {
    button.addEventListener("click", () => {
      sendIntent("slice.set", state.activeSliceId, {
        rxAntenna: button.dataset.rxAntenna
      });
    });
  });

  elements.frequencyInput.addEventListener("focus", () => {
    elements.frequencyInput.select();
  });
  elements.frequencyInput.addEventListener("input", () => {
    delete elements.frequencyInput.dataset.lastCommittedHz;
  });
  elements.frequencyInput.addEventListener("blur", () => {
    if (elements.frequencyInput.dataset.skipBlurCommit === "true") {
      delete elements.frequencyInput.dataset.skipBlurCommit;
      return;
    }
    commitFrequency();
  });
  elements.frequencyInput.addEventListener("change", () => {
    commitFrequency();
  });
  elements.frequencyInput.addEventListener("keydown", event => {
    if (event.key === "Enter") {
      event.preventDefault();
      commitFrequency();
      elements.frequencyInput.dataset.skipBlurCommit = "true";
      elements.frequencyInput.blur();
    } else if (event.key === "Escape") {
      event.preventDefault();
      elements.frequencyInput.dataset.skipBlurCommit = "true";
      elements.frequencyInput.value = formatFrequency(
        activeSlice()?.frequencyHz ?? 0);
      elements.frequencyInput.blur();
    } else if (event.key === "ArrowUp" || event.key === "ArrowDown") {
      event.preventDefault();
      tuneByStep(event.key === "ArrowUp" ? 1 : -1);
    }
  });

  elements.modeSelect.addEventListener("change", () => {
    sendIntent("slice.set", state.activeSliceId, {
      mode: elements.modeSelect.value
    });
  });

  document.querySelectorAll("[data-step-direction]").forEach(button => {
    button.addEventListener("click", () => {
      tuneByStep(Number(button.dataset.stepDirection));
    });
  });

  document.querySelectorAll("[data-filter]").forEach(button => {
    button.addEventListener("click", () => {
      const width = Number(button.dataset.filter);
      const slice = activeSlice();
      if (!slice) {
        return;
      }
      sendIntent(
        "slice.set",
        state.activeSliceId,
        filterEdgesForMode(slice.mode, width));
    });
  });

  document.querySelectorAll("[data-tool-panel]").forEach(button => {
    button.addEventListener("click", () => {
      const panelName = button.dataset.toolPanel;
      if (shouldCloseToolPanel(
          state.toolOpen,
          state.activeTool,
          panelName)) {
        closeToolPanel();
        return;
      }
      showToolPanel(panelName);
    });
  });
  document.querySelectorAll(".flyout-close").forEach(button => {
    button.addEventListener("click", closeToolPanel);
  });

  document.querySelectorAll("[data-band-frequency]").forEach(button => {
    button.addEventListener("click", () => {
      const group = button.parentElement;
      group.querySelectorAll("button").forEach(item => {
        item.classList.toggle("active", item === button);
      });
      selectBand(
        button.dataset.bandKey,
        Number(button.dataset.bandFrequency));
      showToast(`${button.textContent.trim()} band selected.`);
    });
  });

  document.querySelectorAll("[data-applet]").forEach(button => {
    button.addEventListener("click", () => focusApplet(button.dataset.applet));
  });

  document.querySelectorAll("[data-menu]").forEach(button => {
    button.addEventListener("click", event => {
      openAppMenu(event.currentTarget, button.dataset.menu);
    });
  });

  elements.operatorsButton.addEventListener("click", event => {
    event.stopPropagation();
    const willOpen = elements.operatorsPopover.hidden;
    elements.operatorsPopover.hidden = !willOpen;
    elements.operatorsButton.setAttribute("aria-expanded", String(willOpen));
    elements.appMenuPopover.hidden = true;
    elements.accountPopover.hidden = true;
    elements.accountButton.setAttribute("aria-expanded", "false");
  });

  elements.accountButton.addEventListener("click", event => {
    event.stopPropagation();
    const willOpen = elements.accountPopover.hidden;
    elements.accountPopover.hidden = !willOpen;
    elements.accountButton.setAttribute("aria-expanded", String(willOpen));
    elements.operatorsPopover.hidden = true;
    elements.operatorsButton.setAttribute("aria-expanded", "false");
    elements.appMenuPopover.hidden = true;
  });
  elements.chooseRadioAction.addEventListener("click", async () => {
    await leaveRadioConsole("/radios");
  });
  elements.adminPageAction.addEventListener("click", async () => {
    await leaveRadioConsole("/admin");
  });
  elements.signOutAction.addEventListener("click", async event => {
    event.preventDefault();
    await leaveRadioConsole("/auth/logout");
  });

  document.addEventListener("click", event => {
    if (!elements.operatorsPopover.contains(event.target) &&
        !elements.operatorsButton.contains(event.target)) {
      elements.operatorsPopover.hidden = true;
      elements.operatorsButton.setAttribute("aria-expanded", "false");
    }
    if (!elements.accountPopover.contains(event.target) &&
        !elements.accountButton.contains(event.target)) {
      elements.accountPopover.hidden = true;
      elements.accountButton.setAttribute("aria-expanded", "false");
    }
    if (!elements.appMenuPopover.contains(event.target) &&
        !event.target.closest("[data-menu]")) {
      elements.appMenuPopover.hidden = true;
    }
  });

  wireSpectrumResizer();
  wireAppletRail();
}

function restoreLayoutPreferences() {
  setSpectrumMode(state.spectrumMode, false);
  if (state.toolOpen) {
    showToolPanel(state.activeTool, false);
  } else {
    closeToolPanel();
  }
  focusApplet(state.activeApplet, false);
  const storedRailWidth = Number(
    window.localStorage.getItem("aether.web.appletRailWidth"));
  setAppletRailWidth(storedRailWidth);
  setAppletRailHidden(state.appletRailHidden, false);
  const storedHeight = Number(
    window.localStorage.getItem("aether.web.spectrumHeight"));
  if (Number.isFinite(storedHeight) && storedHeight >= 130) {
    setDynamicStyle(
      document.documentElement,
      "--spectrum-height",
      `${storedHeight}px`);
  }
}

function showToolPanel(panelName, announce = true) {
  const page = document.querySelector(`[data-tool-page="${panelName}"]`);
  if (!page) {
    return;
  }
  state.activeTool = panelName;
  state.toolOpen = true;
  window.localStorage.setItem("aether.web.activeTool", panelName);
  window.localStorage.setItem("aether.web.toolOpen", "true");
  elements.toolFlyout.classList.remove("closed");
  document.querySelectorAll("[data-tool-page]").forEach(item => {
    item.hidden = item !== page;
  });
  document.querySelectorAll("[data-tool-panel]").forEach(button => {
    button.classList.toggle("active", button.dataset.toolPanel === panelName);
  });
  if (announce) {
    showToast(`${page.querySelector(".flyout-title span").textContent.trim()} controls opened.`);
  }
}

function closeToolPanel() {
  state.toolOpen = false;
  window.localStorage.setItem("aether.web.toolOpen", "false");
  elements.toolFlyout.classList.add("closed");
  document.querySelectorAll("[data-tool-panel]").forEach(button => {
    button.classList.remove("active");
  });
}

function focusApplet(appletName, scroll = true) {
  const applet = document.querySelector(`#applet-${appletName}`);
  if (!applet || applet.hidden) {
    return;
  }
  state.activeApplet = appletName;
  if (scroll &&
      window.matchMedia("(max-width: 760px)").matches &&
      state.appletRailHidden) {
    setAppletRailHidden(false);
  }
  window.localStorage.setItem("aether.web.activeApplet", appletName);
  document.querySelectorAll(".applet").forEach(item => {
    item.classList.toggle("active-applet", item === applet);
  });
  document.querySelectorAll("[data-applet]").forEach(button => {
    button.classList.toggle("active", button.dataset.applet === appletName);
  });
  if (scroll) {
    applet.scrollIntoView({ behavior: "smooth", block: "start" });
  }
}

function wireAppletRail() {
  let pointerId = null;
  let startX = 0;
  let startWidth = 0;

  elements.appletRailToggle.addEventListener("click", () => {
    setAppletRailHidden(!state.appletRailHidden);
  });

  elements.appletResizer.addEventListener("pointerdown", event => {
    if (state.appletRailHidden) {
      return;
    }
    pointerId = event.pointerId;
    startX = event.clientX;
    startWidth = elements.appletRail.getBoundingClientRect().width;
    elements.appletResizer.setPointerCapture(pointerId);
    elements.workspace.classList.add("applet-rail-resizing");
    event.preventDefault();
  });
  elements.appletResizer.addEventListener("pointermove", event => {
    if (pointerId !== event.pointerId) {
      return;
    }
    setAppletRailWidth(startWidth + (startX - event.clientX));
  });
  const finishResize = event => {
    if (pointerId !== event.pointerId) {
      return;
    }
    if (elements.appletResizer.hasPointerCapture(pointerId)) {
      elements.appletResizer.releasePointerCapture(pointerId);
    }
    pointerId = null;
    elements.workspace.classList.remove("applet-rail-resizing");
  };
  elements.appletResizer.addEventListener("pointerup", finishResize);
  elements.appletResizer.addEventListener("pointercancel", finishResize);
  elements.appletResizer.addEventListener("keydown", event => {
    if (state.appletRailHidden ||
        (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) {
      return;
    }
    const direction = event.key === "ArrowLeft" ? 1 : -1;
    setAppletRailWidth(
      elements.appletRail.getBoundingClientRect().width +
        (direction * 16));
    event.preventDefault();
  });
}

function setAppletRailWidth(width) {
  const nextWidth = clampAppletRailWidth(width, window.innerWidth);
  setDynamicStyle(
    document.documentElement,
    "--right-rail",
    `${nextWidth}px`);
  window.localStorage.setItem(
    "aether.web.appletRailWidth",
    String(nextWidth));
}

function setAppletRailHidden(hidden, persist = true) {
  state.appletRailHidden = Boolean(hidden);
  const mobile = window.matchMedia("(max-width: 760px)").matches;
  elements.workspace.classList.toggle(
    "applet-rail-hidden",
    state.appletRailHidden);
  elements.appletRailToggle.textContent =
    mobile
      ? (state.appletRailHidden ? "▲" : "▼")
      : (state.appletRailHidden ? "◀" : "▶");
  elements.appletRailToggle.title =
    state.appletRailHidden
      ? "Show receiver controls"
      : "Hide receiver controls";
  elements.appletRailToggle.setAttribute(
    "aria-expanded",
    String(!state.appletRailHidden));
  if (persist) {
    window.localStorage.setItem(
      "aether.web.appletRailHidden",
      String(state.appletRailHidden));
  }
}

function openAppMenu(anchor, menuName) {
  const menuItems = {
    radio: ["Connect to radio…", "Radio setup…", "Network diagnostics…"],
    settings: ["Audio", "Display", "Keyboard and controls", "Theme"],
    profiles: ["Global profile", "Transmit profile", "Microphone profile"],
    view: ["Full screen", "Reset panel layout", "Show operator session"],
    help: ["Getting started", "Keyboard shortcuts", "About AetherSDR Web"]
  };
  if (isAdministrator()) {
    menuItems.radio.push("Radio allocation...");
  }
  elements.appMenuPopover.replaceChildren();
  for (const label of menuItems[menuName] || []) {
    const item = document.createElement("button");
    item.type = "button";
    item.textContent = label;
    item.addEventListener("click", () => {
      elements.appMenuPopover.hidden = true;
      if (label === "Show operator session") {
        elements.operatorsPopover.hidden = false;
        elements.operatorsButton.setAttribute("aria-expanded", "true");
      } else if (label === "Reset panel layout") {
        removeDynamicStyle(document.documentElement, "--spectrum-height");
        window.localStorage.removeItem("aether.web.spectrumHeight");
        closeToolPanel();
        focusApplet("rx");
        showToast("Panel layout reset.");
      } else if (label === "Radio allocation...") {
        leaveRadioConsole("/admin");
      } else if (label.startsWith("Connect to radio")) {
        leaveRadioConsole("/radios");
      } else {
        showToast(`${label.replace("…", "")} is staged for a later GUI pass.`);
      }
    });
    elements.appMenuPopover.append(item);
  }
  setDynamicStyle(elements.appMenuPopover, "left", `${anchor.offsetLeft}px`);
  elements.appMenuPopover.hidden = false;
  elements.operatorsPopover.hidden = true;
}

async function leaveRadioConsole(destination) {
  const sessionId = state.session?.sessionId;
  state.forcedDisconnect = true;
  if (state.socket &&
      (state.socket.readyState === WebSocket.OPEN ||
       state.socket.readyState === WebSocket.CONNECTING)) {
    state.socket.close(1000, "Leaving radio console.");
  }
  if (sessionId) {
    try {
      await fetch(
        `/api/session/release?sessionId=${encodeURIComponent(sessionId)}`,
        {
          method: "POST",
          credentials: "same-origin",
          keepalive: true
        });
    } catch {
      // The registry's idle cleanup remains the fail-safe if navigation wins.
    }
  }
  window.sessionStorage.removeItem(sessionIdKey);
  window.location.assign(destination);
}

function wireSpectrumResizer() {
  const handle = document.querySelector("#spectrum-resizer");
  const spectrum = document.querySelector("#spectrum-wrap");
  const waterfall = document.querySelector(".waterfall-wrap");
  let dragging = false;

  handle.addEventListener("pointerdown", event => {
    dragging = true;
    handle.setPointerCapture(event.pointerId);
    event.preventDefault();
  });

  handle.addEventListener("pointermove", event => {
    if (!dragging) {
      return;
    }
    const top = spectrum.getBoundingClientRect().top;
    const available =
      spectrum.getBoundingClientRect().height + waterfall.getBoundingClientRect().height;
    const height = Math.round(Math.max(130, Math.min(available - 120, event.clientY - top)));
    setDynamicStyle(document.documentElement, "--spectrum-height", `${height}px`);
    window.localStorage.setItem("aether.web.spectrumHeight", String(height));
  });

  handle.addEventListener("pointerup", event => {
    dragging = false;
    handle.releasePointerCapture(event.pointerId);
  });
}

function activateSlice(
  sliceId,
  renderSliceDeck = true,
  notifyRadio = true) {
  const slice =
    state.radio?.slices.find(item => item.id === sliceId);
  if (!slice || state.activeSliceId === sliceId) {
    return;
  }
  state.activeSliceId = sliceId;
  setLocalActiveSlice(sliceId);
  if (renderSliceDeck) {
    renderAll();
  } else {
    renderer.setSlices(slicesForActivePan(), state.activeSliceId);
    renderControls();
    updateSignalMeter();
  }
  if (notifyRadio && canControlRadio()) {
    sendIntent("slice.set", sliceId, { isActive: true });
  }
}

function setLocalActiveSlice(sliceId) {
  if (!state.radio) {
    return;
  }
  state.radio.activeSliceId = sliceId;
  state.radio.slices.forEach(slice => {
    slice.isActive = slice.id === sliceId;
  });
}

function handleSliceAction(action, slice) {
  if (action === "remove") {
    sendIntent("slice.remove", slice.id, {});
    return;
  }

  activateSlice(slice.id);
  if (action === "collapse") {
    toggleSlicePreference(
      state.collapsedSlices,
      slice.id,
      "aether.web.collapsedSlices");
    renderSlices();
    return;
  }
  if (action === "lock") {
    const locked = toggleSlicePreference(
      state.lockedSlices,
      slice.id,
      "aether.web.lockedSlices");
    showToast(`Slice ${slice.id} ${locked ? "locked" : "unlocked"}.`);
    renderSlices();
    return;
  }
  if (action === "antenna") {
    showToolPanel("antenna");
    return;
  }
  if (action === "filter") {
    focusApplet("rx");
    document.querySelector(".filter-presets .active")?.focus();
    return;
  }
  if (action === "split") {
    showToast("Split assignment stays disabled until the radio control backend is connected.");
  }
}

function handleSliceTab(tab, slice) {
  activateSlice(slice.id);
  if (tab === "audio") {
    sendIntent("slice.set", slice.id, {
      audioMute: !Boolean(slice.isMuted)
    });
    focusApplet("rx");
  } else if (tab === "dsp") {
    showToolPanel("dsp");
  } else if (tab === "mode") {
    focusApplet("rx");
    elements.modeSelect.focus();
  } else if (tab === "dax") {
    showToolPanel("dax");
  } else if (tab === "xrit") {
    showToast("X/RIT controls are staged for the radio backend pass.");
  }
}

function commitSliceFlagFrequency(sliceId, input) {
  const frequencyHz = parseFrequency(input.value);
  const slice = state.radio?.slices.find(item => item.id === sliceId);
  if (!frequencyHz) {
    input.value = slice ? formatFrequency(slice.frequencyHz) : input.value;
    showToast(
      "Enter a frequency such as 14.100, 14.100.000, or 14100000.",
      true);
    return;
  }
  if (input.dataset.lastCommittedHz === String(frequencyHz)) {
    return;
  }
  input.dataset.lastCommittedHz = String(frequencyHz);
  requestSliceFrequency(sliceId, frequencyHz, true, true);
}

function commitFrequency() {
  const frequencyHz = parseFrequency(elements.frequencyInput.value);
  if (!frequencyHz) {
    showToast(
      "Enter a frequency such as 14.100, 14.100.000, or 14100000.",
      true);
    renderControls();
    return;
  }
  if (elements.frequencyInput.dataset.lastCommittedHz ===
      String(frequencyHz)) {
    return;
  }
  elements.frequencyInput.dataset.lastCommittedHz = String(frequencyHz);
  requestSliceFrequency(state.activeSliceId, frequencyHz, true, true);
}

function tuneByStep(direction) {
  const slice = activeSlice();
  if (!slice) {
    return;
  }
  const step = Number(elements.tuneStep.value) || 500;
  const frequencyHz = Math.round((slice.frequencyHz + (direction * step)) / step) * step;
  requestSliceFrequency(state.activeSliceId, frequencyHz, true, true);
}

function requestSliceFrequency(
  sliceId,
  frequencyHz,
  announceLock = true,
  immediate = false) {
  const availableSlices = slicesForActivePan();
  const targetSliceId = resolveFrequencySliceId(
    sliceId,
    availableSlices,
    state.radio?.activeSliceId);
  const slice = availableSlices.find(item => item.id === targetSliceId);
  if (!slice || !targetSliceId) {
    showToast("No receiver slice is available to tune.", true);
    return;
  }
  if (state.lockedSlices.has(targetSliceId)) {
    if (announceLock) {
      showToast(`Slice ${targetSliceId} is locked.`, true);
    }
    return;
  }
  if (!canControlRadio()) {
    showToast("Your account has observe-only access.", true);
    return;
  }

  const roundedFrequencyHz = Math.round(frequencyHz);
  const pan = activePan();
  if (targetSliceId === state.activeSliceId ||
      slice.isActive) {
    audioPlayer.reset();
  }
  state.activeSliceId = targetSliceId;
  setLocalActiveSlice(targetSliceId);
  slice.frequencyHz = roundedFrequencyHz;
  if (pan &&
      !isFrequencyVisible(
        roundedFrequencyHz,
        pan)) {
    pan.centerFrequencyHz = clampPanCenter(
      roundedFrequencyHz,
      pan.bandwidthHz);
    renderer.configure(pan);
  }
  renderAll();

  const existingTimer = state.frequencyTimers.get(targetSliceId);
  if (existingTimer) {
    window.clearTimeout(existingTimer);
  }
  if (immediate) {
    state.frequencyTimers.delete(targetSliceId);
    sendSliceFrequencyIntent(targetSliceId, roundedFrequencyHz);
    return;
  }
  state.frequencyTimers.set(
    targetSliceId,
    window.setTimeout(() => {
      state.frequencyTimers.delete(targetSliceId);
      sendSliceFrequencyIntent(targetSliceId, roundedFrequencyHz);
    }, 70));
}

function handleDraggedSliceFrequency(sliceId, frequencyHz, final) {
  const roundedFrequencyHz = Math.round(frequencyHz);
  const availableSlices = slicesForActivePan();
  const targetSliceId = resolveFrequencySliceId(
    sliceId,
    availableSlices,
    state.radio?.activeSliceId);
  const slice = availableSlices.find(item => item.id === targetSliceId);
  if (!slice || !targetSliceId) {
    if (final) {
      showToast("No receiver slice is available to tune.", true);
    }
    return;
  }
  state.activeSliceId = targetSliceId;
  setLocalActiveSlice(targetSliceId);
  slice.frequencyHz = roundedFrequencyHz;
  previewSliceCard(targetSliceId, roundedFrequencyHz);
  if (document.activeElement !== elements.frequencyInput) {
    elements.frequencyInput.value = formatFrequency(roundedFrequencyHz);
  }
  updateSignalMeter();

  if (state.lockedSlices.has(targetSliceId) || !canControlRadio()) {
    if (final) {
      showToast(
        state.lockedSlices.has(targetSliceId)
          ? `Slice ${targetSliceId} is locked.`
          : "Your account has observe-only access.",
        true);
      renderAll();
    }
    return;
  }

  if (final) {
    audioPlayer.reset();
    finishLiveSliceTune(targetSliceId, roundedFrequencyHz);
    renderAll();
    return;
  }
  queueLiveSliceTune(targetSliceId, roundedFrequencyHz);
}

function queueLiveSliceTune(sliceId, frequencyHz) {
  const now = performance.now();
  const tuner = state.liveSliceTuners.get(sliceId) ?? {
    lastSentAt: Number.NEGATIVE_INFINITY,
    lastSentHz: null,
    pendingHz: null,
    timer: 0
  };
  tuner.pendingHz = frequencyHz;
  const remaining = liveSliceTuneIntervalMs - (now - tuner.lastSentAt);
  if (remaining <= 0) {
    sendLiveSliceTune(sliceId, tuner);
  } else if (!tuner.timer) {
    tuner.timer = window.setTimeout(() => {
      tuner.timer = 0;
      sendLiveSliceTune(sliceId, tuner);
    }, remaining);
  }
  state.liveSliceTuners.set(sliceId, tuner);
}

function sendLiveSliceTune(sliceId, tuner) {
  if (tuner.pendingHz === null ||
      !canControlRadio() ||
      state.lockedSlices.has(sliceId)) {
    return;
  }
  const frequencyHz = tuner.pendingHz;
  tuner.pendingHz = null;
  tuner.lastSentAt = performance.now();
  tuner.lastSentHz = frequencyHz;
  if (sliceId === state.activeSliceId) {
    audioPlayer.reset();
  }
  sendSliceFrequencyIntent(sliceId, frequencyHz);
}

function finishLiveSliceTune(sliceId, frequencyHz) {
  const tuner = state.liveSliceTuners.get(sliceId);
  if (tuner?.timer) {
    window.clearTimeout(tuner.timer);
  }
  if (!tuner || tuner.lastSentHz !== frequencyHz) {
    sendSliceFrequencyIntent(sliceId, frequencyHz);
  }
  state.liveSliceTuners.delete(sliceId);
}

function sendSliceFrequencyIntent(sliceId, frequencyHz) {
  sendIntent("slice.set", sliceId, {
    isActive: true,
    frequencyHz
  });
}

function commitDraggedPanCenter(centerFrequencyHz) {
  const pan = activePan();
  if (!pan) {
    return;
  }
  if (!canControlRadio()) {
    renderer.cancelPendingPan(pan.centerFrequencyHz);
    renderAll();
    showToast("Your account has observe-only access.", true);
    return;
  }

  const center = clampPanCenter(
    centerFrequencyHz,
    pan.bandwidthHz);
  pan.centerFrequencyHz = center;
  finishLivePanCenter(pan.id, center);
  renderAll();
}

function queueLivePanCenter(panId, centerFrequencyHz) {
  const now = performance.now();
  const tuner = state.livePanTuners.get(panId) ?? {
    lastSentAt: Number.NEGATIVE_INFINITY,
    lastSentHz: null,
    latestHz: null,
    pendingHz: null,
    timer: 0,
    settleTimer: 0
  };
  tuner.latestHz = centerFrequencyHz;
  tuner.pendingHz = centerFrequencyHz;
  const remaining = livePanTuneIntervalMs - (now - tuner.lastSentAt);
  if (remaining <= 0) {
    sendLivePanCenter(panId, tuner);
  } else if (!tuner.timer) {
    tuner.timer = window.setTimeout(() => {
      tuner.timer = 0;
      sendLivePanCenter(panId, tuner);
    }, remaining);
  }

  if (tuner.settleTimer) {
    window.clearTimeout(tuner.settleTimer);
  }
  tuner.settleTimer = window.setTimeout(() => {
    tuner.settleTimer = 0;
    if (tuner.latestHz !== null &&
        tuner.lastSentHz !== tuner.latestHz) {
      tuner.pendingHz = tuner.latestHz;
      sendLivePanCenter(panId, tuner);
    }
  }, livePanSettleMs);
  state.livePanTuners.set(panId, tuner);
}

function sendLivePanCenter(panId, tuner) {
  if (tuner.pendingHz === null || !canControlRadio()) {
    return;
  }
  const centerFrequencyHz = tuner.pendingHz;
  tuner.pendingHz = null;
  tuner.lastSentAt = performance.now();
  tuner.lastSentHz = centerFrequencyHz;
  sendIntent("pan.set", panId, { centerFrequencyHz });
}

function finishLivePanCenter(panId, centerFrequencyHz) {
  const tuner = state.livePanTuners.get(panId);
  if (tuner?.timer) {
    window.clearTimeout(tuner.timer);
  }
  if (tuner?.settleTimer) {
    window.clearTimeout(tuner.settleTimer);
  }
  if (!tuner || tuner.lastSentHz !== centerFrequencyHz) {
    sendIntent("pan.set", panId, { centerFrequencyHz });
  }
  state.livePanTuners.delete(panId);
}

function requestPanCenter(centerFrequencyHz) {
  const pan = activePan();
  if (!pan || !canControlRadio()) {
    if (!canControlRadio()) {
      showToast("Your account has observe-only access.", true);
    }
    return;
  }

  const center = clampPanCenter(
    centerFrequencyHz,
    pan.bandwidthHz);
  renderer.previewExternalPanCenter(center);
  pan.centerFrequencyHz = center;
  renderAll();
  sendIntent("pan.set", pan.id, { centerFrequencyHz: center });
}

function movePan(direction) {
  const pan = activePan();
  if (!pan) {
    return;
  }
  requestPanCenter(
    pan.centerFrequencyHz +
    (Number(direction) * pan.bandwidthHz * .5));
}

function requestPanZoom(factor, anchorFraction = null) {
  const pan = activePan();
  if (!pan) {
    return;
  }
  if (!canControlRadio()) {
    showToast("Your account has observe-only access.", true);
    return;
  }

  const bandwidthHz = Math.round(Math.max(
    minimumPanBandwidthHz,
    Math.min(
      maximumPanBandwidthHz,
      pan.bandwidthHz * Number(factor))));
  if (bandwidthHz === pan.bandwidthHz) {
    return;
  }

  let centerFrequencyHz = pan.centerFrequencyHz;
  if (Number.isFinite(anchorFraction)) {
    const fraction = Math.max(0, Math.min(1, anchorFraction));
    const anchorFrequencyHz =
      pan.centerFrequencyHz -
      (pan.bandwidthHz / 2) +
      (fraction * pan.bandwidthHz);
    centerFrequencyHz =
      anchorFrequencyHz -
      ((fraction - .5) * bandwidthHz);
  } else if (factor < 1 && activeSlice()) {
    // Aether centers repeated zoom-in button presses on the active slice.
    centerFrequencyHz = activeSlice().frequencyHz;
  }
  centerFrequencyHz = clampPanCenter(
    centerFrequencyHz,
    bandwidthHz);

  renderer.previewExternalFrequencyRange(
    centerFrequencyHz,
    bandwidthHz,
    true);
  pan.centerFrequencyHz = centerFrequencyHz;
  pan.bandwidthHz = bandwidthHz;
  renderAll();
  sendIntent("pan.set", pan.id, {
    centerFrequencyHz,
    bandwidthHz
  });
}

function selectBand(bandKey, previewFrequencyHz) {
  const pan = activePan();
  if (!pan) {
    return;
  }
  if (!canControlRadio()) {
    showToast("Your account has observe-only access.", true);
    return;
  }

  const previewCenterHz = clampPanCenter(
    previewFrequencyHz,
    pan.bandwidthHz);
  renderer.previewExternalPanCenter(previewCenterHz, true);
  previewPanUi(previewCenterHz);
  sendIntent("pan.set", pan.id, { bandKey });
}

function renderPanRange(centerFrequencyHz) {
  const bandwidthHz = activePan()?.bandwidthHz ?? 0;
  const start = (centerFrequencyHz - (bandwidthHz / 2)) / 1e6;
  const end = (centerFrequencyHz + (bandwidthHz / 2)) / 1e6;
  elements.panRange.textContent =
    `${start.toFixed(3)} — ${end.toFixed(3)} MHz`;
}

function previewPanUi(centerFrequencyHz) {
  renderPanRange(centerFrequencyHz);
  renderBandPlan(centerFrequencyHz);
  for (const slice of slicesForActivePan()) {
    previewSliceCard(
      slice.id,
      slice.frequencyHz,
      centerFrequencyHz);
  }
  renderWaterfallSliceOverlays(centerFrequencyHz);
}

function renderBandPlan(centerFrequencyHz) {
  const pan = activePan();
  if (!pan) {
    state.bandPlanNodes.forEach(node => {
      node.hidden = true;
    });
    return;
  }

  ensureBandPlanNodes();
  state.bandPlanNodes.forEach(node => {
    node.hidden = true;
  });
  const segments = visibleBandSegments(
    state.bandPlanSegments,
    centerFrequencyHz,
    pan.bandwidthHz,
    elements.bandPlan.clientWidth);
  for (const segment of segments) {
    const node = state.bandPlanNodes.get(bandPlanSegmentKey(segment));
    if (!node) {
      continue;
    }
    node.hidden = false;
    setDynamicStyle(node, "--segment-left", String(segment.left));
    setDynamicStyle(node, "--segment-width", String(segment.width));
    node.querySelector(".band-plan-label").hidden = !segment.showLabel;
    node.querySelector(".band-license").hidden = !segment.showLicense;
  }
}

function ensureBandPlanNodes() {
  if (state.bandPlanNodes.size > 0 ||
      state.bandPlanSegments.length === 0) {
    return;
  }
  const nodes = state.bandPlanSegments.map(segment => {
    const node = document.createElement("span");
    node.className = "band-segment";
    node.hidden = true;
    node.title =
      `${segment.label}` +
      (segment.license ? ` · ${segment.license}` : "");
    setDynamicStyle(node, "--segment-color", segment.color);

    const label = document.createElement("span");
    label.className = "band-plan-label";
    label.textContent = segment.label;
    node.append(label);

    const license = document.createElement("small");
    license.className = "band-license";
    license.textContent = segment.license;
    node.append(license);

    state.bandPlanNodes.set(bandPlanSegmentKey(segment), node);
    return node;
  });
  elements.bandPlan.replaceChildren(...nodes);
}

function bandPlanSegmentKey(segment) {
  return `${segment.lowHz}:${segment.highHz}:${segment.label}:${segment.license}`;
}

async function loadBandPlan() {
  try {
    const response = await fetch("/assets/bandplans/arrl-us.json", {
      credentials: "same-origin",
      headers: { Accept: "application/json" }
    });
    if (!response.ok) {
      throw new Error(`Band-plan request failed (${response.status}).`);
    }
    state.bandPlanSegments = normalizeBandPlan(await response.json());
  } catch (error) {
    state.bandPlanSegments = [];
    showToast(error.message || "The band plan could not be loaded.", true);
  }
}

function setSpectrumMode(mode, announce = true) {
  state.spectrumMode = normalizeSpectrumMode(mode);
  window.localStorage.setItem(
    "aether.web.spectrumMode",
    state.spectrumMode);
  renderer.setRenderMode(state.spectrumMode);
  renderSpectrumMode();
  if (announce) {
    showToast(
      state.spectrumMode === "2d"
        ? "2D panadapter selected."
        : "3D stacked panadapter selected.");
  }
}

function renderSpectrumMode() {
  document.querySelectorAll("[data-spectrum-mode]").forEach(button => {
    const active = button.dataset.spectrumMode === state.spectrumMode;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", String(active));
  });
  const source =
    state.session?.radioMode?.toLowerCase() === "flexrx"
      ? "Flex radio"
      : "simulated radio";
  elements.spectrumCanvas.setAttribute(
    "aria-label",
    `Live ${source} spectrum in ${state.spectrumMode.toUpperCase()} mode. ` +
    "Click to tune, drag a slice to move it, drag empty spectrum to pan, " +
    "use the mouse wheel to step the active slice, or hold Control while " +
    "scrolling to zoom around the pointer.");
  elements.waterfallCanvas.setAttribute(
    "aria-label",
    `Live ${source} waterfall. Drag left or right to pan across the band, ` +
    "or hold Control while scrolling to zoom around the pointer.");
}

function renderDisplayControls() {
  syncToggleButton(elements.displayFill, state.displayFill);
  syncToggleButton(elements.displayPeak, state.displayPeak);
  syncToggleButton(elements.displayWaterfall, state.waterfallVisible);
  elements.panadapter.classList.toggle(
    "waterfall-hidden",
    !state.waterfallVisible);

  const pan = activePan();
  if (!pan) {
    elements.displayAverage.disabled = true;
    elements.displayFps.disabled = true;
    elements.displayFloor.disabled = true;
    elements.displayWnb.disabled = true;
    elements.displayWnbLevel.disabled = true;
    elements.wnbStatus.textContent = "WNB —";
    return;
  }

  const radioControlsDisabled = !canControlRadio();
  elements.displayAverage.disabled = radioControlsDisabled;
  elements.displayFps.disabled = radioControlsDisabled;
  elements.displayFloor.disabled = radioControlsDisabled;
  elements.displayWnb.disabled = radioControlsDisabled;
  elements.displayWnbLevel.disabled = radioControlsDisabled;
  syncRangeControl(
    elements.displayAverage,
    pan.fftAverage ?? 35,
    document.querySelector("output[for=\"display-average\"]"));
  syncRangeControl(
    elements.displayFps,
    pan.framesPerSecond ?? 15,
    document.querySelector("output[for=\"display-fps\"]"));
  syncRangeControl(
    elements.displayFloor,
    pan.minDbm ?? -120,
    document.querySelector("output[for=\"display-floor\"]"));
  syncRangeControl(
    elements.displayWnbLevel,
    pan.wnbLevel ?? 50,
    document.querySelector("output[for=\"display-wnb-level\"]"));
  syncToggleButton(elements.displayWnb, Boolean(pan.wnbEnabled));
  elements.wnbStatus.textContent =
    pan.wnbEnabled ? `WNB ${pan.wnbLevel ?? 50}` : "WNB OFF";
}

function syncToggleButton(button, active) {
  button.classList.toggle("active", active);
  button.setAttribute("aria-pressed", String(active));
}

function toggleSlicePreference(set, sliceId, storageKey) {
  const enabled = !set.has(sliceId);
  if (enabled) {
    set.add(sliceId);
  } else {
    set.delete(sliceId);
  }
  window.localStorage.setItem(storageKey, JSON.stringify([...set]));
  return enabled;
}

function sendIntent(action, selector, values) {
  if (!canControlRadio()) {
    showToast("Your account has observe-only access.", true);
    return;
  }

  send({
    cmd: "intent",
    id: nextRequestId(),
    action,
    selector,
    values
  });
}

function canControlRadio() {
  return Boolean(state.session?.user?.roles?.some(
    role => role === controlRole || role === adminRole));
}

function send(message) {
  if (!state.socket || state.socket.readyState !== WebSocket.OPEN) {
    showToast("The radio session is not connected.", true);
    return false;
  }
  state.socket.send(JSON.stringify(message));
  return true;
}

function reportAudioDiagnostics() {
  if (!state.socket || state.socket.readyState !== WebSocket.OPEN) {
    return;
  }
  audioPlayer.setDeliveryPath(radioTransport.mode);
  radioTransport.requestAudioDiagnostics();
  radioTransport.requestNetworkDiagnostics();
  state.socket.send(JSON.stringify({
    cmd: "diagnostics.audio",
    ...audioPlayer.getDiagnostics(state.activeSliceId)
  }));
}

function nextRequestId() {
  state.requestId += 1;
  return state.requestId;
}

function panadapters() {
  if (!state.radio) {
    return [];
  }
  return Array.isArray(state.radio.panadapters) &&
    state.radio.panadapters.length > 0
    ? state.radio.panadapters
    : state.radio.panadapter
      ? [state.radio.panadapter]
      : [];
}

function syncActivePan() {
  const pans = panadapters();
  if (!pans.some(pan => pan.id === state.activePanId)) {
    state.activePanId = pans[0]?.id ?? "";
  }
}

function activePan() {
  const pans = panadapters();
  return pans.find(pan => pan.id === state.activePanId) ?? pans[0];
}

function slicesForActivePan() {
  const pan = activePan();
  if (!pan || !state.radio) {
    return [];
  }
  return state.radio.slices.filter(slice =>
    !Number(slice.panStreamId) ||
    !Number(pan.streamId) ||
    Number(slice.panStreamId) === Number(pan.streamId));
}

function activeSlice() {
  const slices = slicesForActivePan();
  return slices.find(slice => slice.id === state.activeSliceId) ??
    slices[0];
}

function setConnectionState(kind, label) {
  elements.connectionDot.className =
    `status-dot${kind === "connected" ? " connected" :
      kind === "disconnected" ? " disconnected" : ""}`;
  elements.connectionLabel.textContent = label;
  elements.footerConnection.textContent =
    kind === "connected" ? "LIVE" :
    kind === "disconnected" ? "OFFLINE" : "CONNECTING";
  elements.footerConnection.classList.toggle("ok", kind === "connected");
}

function showToast(message, error = false) {
  elements.toast.textContent = message;
  elements.toast.classList.toggle("error", error);
}

function updateRangeFill(input) {
  const percent = rangeFillPercent(input.min, input.max, input.value);
  setDynamicStyle(
    input,
    "--range-fill",
    `${percent}%`
  );
}

function syncRangeControl(input, value, output = null) {
  if (document.activeElement !== input) {
    input.value = String(value);
  }
  if (output) {
    output.textContent = input.value;
  }
  updateRangeFill(input);
}

function renderSliceDsp(slice) {
  const disabled = !slice || !canControlRadio();
  const availability = rxControlAvailability(
    slice?.mode,
    state.radio?.radioModel);
  document.querySelectorAll("[data-slice-toggle]").forEach(button => {
    const property = button.dataset.sliceToggle;
    const active = Boolean(property && slice?.[property]);
    const supported = property ? availability[property] !== false : true;
    button.disabled = disabled || !supported;
    button.title = supported
      ? ""
      : `${button.textContent.trim()} is unavailable for this radio or mode.`;
    button.classList.toggle("active", active);
    button.setAttribute("aria-pressed", String(active));
  });
  document.querySelectorAll("[data-slice-level]").forEach(input => {
    const property = input.dataset.sliceLevel;
    const control = property?.replace(/Level$/, "");
    const supported = control ? availability[control] !== false : true;
    input.disabled = disabled || !supported;
    input.title = supported
      ? ""
      : "This DSP control is unavailable for the selected radio or mode.";
    if (!property || !Number.isFinite(Number(slice?.[property]))) {
      return;
    }
    const output = input.closest("label")?.querySelector("output");
    syncRangeControl(input, slice[property], output);
  });
}

function displayAntenna(value) {
  return String(value || "ANT1").replaceAll("_", " ");
}

function updateSignalMeter() {
  const slice = activeSlice();
  const pan = activePan();
  updateSliceMeters(pan);
  if (!slice || !pan || !renderer.bins?.length) {
    resetSignalMeter();
    return;
  }

  const signalDbm = sliceSignalDbm(slice, pan, renderer.bins);
  if (!Number.isFinite(signalDbm)) {
    resetSignalMeter();
    return;
  }

  const dbm = Math.round(signalDbm);
  const sValue = signalLevelText(dbm);
  sMeter.setDbm(dbm);
  elements.meterDbm.textContent = `${dbm} dBm`;
  elements.meterS.textContent = sValue;
}

function updateSliceMeters(pan = activePan()) {
  for (const card of elements.sliceDeck.children) {
    const meter = card.querySelector(".slice-meter");
    if (!meter) {
      continue;
    }
    const slice = state.radio?.slices.find(
      item => item.id === card.dataset.sliceId);
    const signalDbm = sliceSignalDbm(slice, pan, renderer.bins);
    const available = Number.isFinite(signalDbm);
    const dbm = available ? Math.round(signalDbm) : -127;
    const fraction = signalDbmToMeterFraction(dbm);
    setDynamicStyle(
      meter,
      "--slice-meter-fill",
      `${(fraction * 100).toFixed(2)}%`);
    meter.setAttribute("aria-valuenow", String(dbm));
    meter.setAttribute(
      "aria-valuetext",
      available
        ? `${signalLevelText(dbm)}, ${dbm} dBm`
        : "No signal reading");
    meter.classList.toggle("unavailable", !available);
  }
}

function signalLevelText(dbm) {
  return dbm <= -73
    ? `S${Math.max(0, Math.min(9, Math.round((dbm + 127) / 6)))}`
    : `S9+${Math.round(dbm + 73)}`;
}

function resetSignalMeter() {
  sMeter.setIdle();
  elements.meterDbm.textContent = "— dBm";
  elements.meterS.textContent = "NO SLICE";
}

function updateClock() {
  elements.clock.textContent = new Intl.DateTimeFormat(
    undefined,
    { hour: "2-digit", minute: "2-digit", second: "2-digit" })
    .format(new Date());
}

function readSlicePreferenceSet(storageKey) {
  try {
    const value = JSON.parse(window.localStorage.getItem(storageKey) || "[]");
    return new Set(
      Array.isArray(value)
        ? value.filter(item => typeof item === "string")
        : []);
  } catch {
    return new Set();
  }
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#039;");
}

function setDynamicStyle(element, property, value) {
  const rule = dynamicStyleRule(element);
  rule?.style.setProperty(property, value);
}

function removeDynamicStyle(element, property) {
  dynamicStyleRules.get(element)?.style.removeProperty(property);
}

function dynamicStyleRule(element) {
  const existing = dynamicStyleRules.get(element);
  if (existing) {
    return existing;
  }

  const stylesheet = Array.from(document.styleSheets).find(sheet => {
    if (!sheet.href) {
      return false;
    }
    try {
      return new URL(sheet.href).pathname.endsWith("/styles.css");
    } catch {
      return false;
    }
  });
  if (!stylesheet) {
    return null;
  }

  dynamicStyleId += 1;
  const token = `aether-dynamic-${dynamicStyleId}`;
  element.dataset.dynamicStyle = token;
  const ruleIndex = stylesheet.insertRule(
    `[data-dynamic-style="${token}"] {}`,
    stylesheet.cssRules.length);
  const rule = stylesheet.cssRules[ruleIndex];
  dynamicStyleRules.set(element, rule);
  return rule;
}
