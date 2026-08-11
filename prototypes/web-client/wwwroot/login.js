"use strict";

const guidance = document.querySelector("#login-guidance");
const status = document.querySelector("#login-status");
const localForm = document.querySelector("#local-login-form");
const mfaForm = document.querySelector("#local-mfa-form");
const mfaCancel = document.querySelector("#local-mfa-cancel");
const providerSection = document.querySelector("#external-login-section");
const providerSeparator = document.querySelector("#provider-separator");
const providerLink = document.querySelector("#external-login");
const providerLabel = document.querySelector("#external-login-label");
const username = document.querySelector("#local-username");
const password = document.querySelector("#local-password");
const verificationCode = document.querySelector("#local-verification-code");

let csrfHeader = "";
let csrfToken = "";
let challengeToken = "";
let safeReturnUrl = "/";

function setStatus(message, isError = false) {
  status.textContent = message;
  status.classList.toggle("is-error", isError);
}

function setFormPending(form, pending) {
  for (const control of form.elements) {
    control.disabled = pending;
  }
  form.setAttribute("aria-busy", pending ? "true" : "false");
}

async function readJson(response) {
  try {
    return await response.json();
  } catch {
    return {};
  }
}

async function postJson(path, body) {
  return fetch(path, {
    method: "POST",
    credentials: "same-origin",
    cache: "no-store",
    headers: {
      "Content-Type": "application/json",
      [csrfHeader]: csrfToken
    },
    body: JSON.stringify(body)
  });
}

function showPasswordStep(message = "") {
  challengeToken = "";
  mfaForm.hidden = true;
  localForm.hidden = false;
  verificationCode.value = "";
  setStatus(message, Boolean(message));
  username.focus();
}

function showMfaStep() {
  localForm.hidden = true;
  mfaForm.hidden = false;
  setStatus("");
  verificationCode.focus();
}

async function initialize() {
  const requestedReturnUrl =
    new URLSearchParams(window.location.search).get("returnUrl") || "/radios";
  const response = await fetch(
    `/api/auth/options?returnUrl=${encodeURIComponent(requestedReturnUrl)}`,
    {
      credentials: "same-origin",
      cache: "no-store",
      headers: { "Accept": "application/json" }
    });

  if (!response.ok) {
    throw new Error("Authentication options are unavailable.");
  }

  const options = await readJson(response);
  csrfHeader = options.antiforgery?.headerName || "";
  csrfToken = options.antiforgery?.requestToken || "";
  safeReturnUrl = options.returnUrl || "/";

  const localEnabled = options.localAccountsEnabled === true;
  const external = options.externalProvider;
  const developmentMode = options.developmentMode === true;

  if (localEnabled) {
    localForm.hidden = false;
    guidance.textContent =
      external
        ? "Use a station account or your configured external identity provider."
        : "Sign in with the local account created by your station administrator.";
  }

  if (external || developmentMode) {
    providerSection.hidden = false;
    providerLink.href =
      `/auth/login?returnUrl=${encodeURIComponent(safeReturnUrl)}`;
    providerLabel.textContent = developmentMode
      ? "Continue in development mode"
      : `Continue with ${external.displayName}`;
    providerSeparator.hidden = !localEnabled;
    if (!localEnabled) {
      guidance.textContent = developmentMode
        ? "Continue with the development identity configured for this host."
        : "Continue with the external identity provider configured by your station administrator.";
    }
  }

  if (!localEnabled && !external && !developmentMode) {
    throw new Error("No authentication method is available.");
  }

  if (!csrfHeader || !csrfToken) {
    throw new Error("The sign-in security token is unavailable.");
  }
}

localForm.addEventListener("submit", async event => {
  event.preventDefault();
  setFormPending(localForm, true);
  setStatus("");

  try {
    const response = await postJson("/api/auth/local/password", {
      userName: username.value,
      password: password.value
    });
    const result = await readJson(response);
    if (!response.ok || !result.challengeToken) {
      showPasswordStep(
        "Sign-in failed. Check your credentials and try again.");
      return;
    }

    challengeToken = result.challengeToken;
    showMfaStep();
  } catch {
    showPasswordStep(
      "Sign-in is temporarily unavailable. Try again.");
  } finally {
    password.value = "";
    setFormPending(localForm, false);
  }
});

mfaForm.addEventListener("submit", async event => {
  event.preventDefault();
  setFormPending(mfaForm, true);
  setStatus("");

  try {
    const response = await postJson("/api/auth/local/mfa", {
      challengeToken,
      verificationCode: verificationCode.value,
      returnUrl: safeReturnUrl
    });
    const result = await readJson(response);
    verificationCode.value = "";
    challengeToken = "";
    if (!response.ok || !result.redirectUrl) {
      showPasswordStep(
        "Verification failed. Start again with your username and password.");
      return;
    }

    window.location.assign(result.redirectUrl);
  } catch {
    challengeToken = "";
    showPasswordStep(
      "Verification is temporarily unavailable. Start again.");
  } finally {
    setFormPending(mfaForm, false);
  }
});

mfaCancel.addEventListener("click", () => {
  showPasswordStep();
});

initialize().catch(() => {
  guidance.textContent =
    "The configured authentication methods could not be loaded.";
  setStatus("Sign-in is temporarily unavailable.", true);
});
