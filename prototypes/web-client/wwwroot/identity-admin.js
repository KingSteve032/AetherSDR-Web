const api = Object.freeze({
  options: "/api/auth/options?returnUrl=%2Fadmin%23identity-administration",
  antiforgery: "/api/antiforgery",
  accounts: "/api/admin/identity/accounts?offset=0&limit=200",
  enrollments: "/api/admin/identity/accounts/enrollments",
  externalProvisioning:
    "/api/admin/identity/accounts/external-provisioning",
  localPasswordReauthentication:
    "/api/admin/identity/reauthenticate/local/password",
  localMfaReauthentication:
    "/api/admin/identity/reauthenticate/local/mfa",
  externalReauthentication:
    "/api/admin/identity/reauthenticate/external"
});

const roleNames = Object.freeze(["Observe", "Control", "Transmit", "Admin"]);

const elements = {
  section: document.querySelector("#identity-administration"),
  providerStatus: document.querySelector("#identity-provider-status"),
  reauthPanel: document.querySelector("#identity-reauth-panel"),
  reauthStatus: document.querySelector("#identity-reauth-status"),
  localPasswordForm: document.querySelector(
    "#identity-local-reauth-password-form"),
  localPassword: document.querySelector("#identity-local-reauth-password"),
  localMfaForm: document.querySelector("#identity-local-reauth-mfa-form"),
  localMfaCode: document.querySelector("#identity-local-reauth-code"),
  externalReauth: document.querySelector("#identity-external-reauth"),
  workspace: document.querySelector("#identity-workspace"),
  localEnrollmentForm: document.querySelector(
    "#identity-local-enrollment-form"),
  localUserName: document.querySelector("#identity-local-user-name"),
  localDisplayName: document.querySelector("#identity-local-display-name"),
  localEmail: document.querySelector("#identity-local-email"),
  localPasswordNew: document.querySelector("#identity-local-password"),
  externalProvisioningForm: document.querySelector(
    "#identity-external-provisioning-form"),
  externalUserName: document.querySelector("#identity-external-user-name"),
  externalDisplayName: document.querySelector(
    "#identity-external-display-name"),
  externalEmail: document.querySelector("#identity-external-email"),
  enrollmentResult: document.querySelector(
    "#identity-local-enrollment-result"),
  enrollmentSecret: document.querySelector("#identity-local-totp-secret"),
  enrollmentUri: document.querySelector("#identity-local-totp-uri"),
  recoveryCodes: document.querySelector("#identity-local-recovery-codes"),
  enrollmentConfirmationForm: document.querySelector(
    "#identity-local-enrollment-confirmation-form"),
  enrollmentCode: document.querySelector("#identity-local-enrollment-code"),
  recoveryConfirmed: document.querySelector(
    "#identity-local-recovery-confirmed"),
  refresh: document.querySelector("#identity-refresh"),
  accountList: document.querySelector("#identity-account-list")
};

const state = {
  options: null,
  challengeToken: null,
  pendingEnrollment: null,
  busy: false
};

initializeIdentityAdministration().catch(error => {
  showStatus(error.message || "Identity administration could not load.", true);
});

async function initializeIdentityAdministration() {
  state.options = await requestJson(api.options);
  if (state.options.developmentMode) {
    elements.section.hidden = true;
    return;
  }

  const local = Boolean(state.options.localAccountsEnabled);
  const external = state.options.externalProvider;
  elements.localPasswordForm.hidden = !local;
  elements.localEnrollmentForm.hidden = !local;
  elements.externalReauth.hidden = !external;
  elements.externalProvisioningForm.hidden = !external;
  elements.providerStatus.textContent = external
    ? `${local ? "LOCAL + " : ""}${external.displayName.toUpperCase()}`
    : "LOCAL ACCOUNTS";
  bindEvents();
  await loadAccounts();
}

function bindEvents() {
  elements.localPasswordForm.addEventListener("submit", reauthenticatePassword);
  elements.localMfaForm.addEventListener("submit", reauthenticateMfa);
  elements.externalReauth.addEventListener("click", () => {
    submitExternalNavigation(api.externalReauthentication);
  });
  elements.localEnrollmentForm.addEventListener("submit", enrollLocalAccount);
  elements.externalProvisioningForm.addEventListener(
    "submit",
    provisionExternalAccount);
  elements.enrollmentConfirmationForm.addEventListener(
    "submit",
    confirmLocalEnrollment);
  elements.refresh.addEventListener("click", loadAccounts);
  window.addEventListener("pagehide", clearTransientSecrets);
}

async function loadAccounts() {
  if (state.busy) return;
  setBusy(true);
  try {
    const page = await requestJson(api.accounts);
    elements.workspace.hidden = false;
    showStatus(
      `Fresh administrator authority verified · ${page.totalCount} ${page.totalCount === 1 ? "account" : "accounts"}.`);
    renderAccounts(page.accounts || []);
  } catch (error) {
    if (error.status === 403) {
      elements.workspace.hidden = true;
      showStatus(
        "Reauthenticate as the current administrator to view or change identity authority.");
      return;
    }
    throw error;
  } finally {
    setBusy(false);
  }
}

async function reauthenticatePassword(event) {
  event.preventDefault();
  const password = elements.localPassword.value;
  elements.localPassword.value = "";
  setBusy(true);
  try {
    const response = await mutateJson(
      api.localPasswordReauthentication,
      "POST",
      { password });
    state.challengeToken = response.challengeToken;
    elements.localPasswordForm.hidden = true;
    elements.localMfaForm.hidden = false;
    showStatus("Password verified. Enter a current TOTP or recovery code.");
    elements.localMfaCode.focus();
  } catch (error) {
    showStatus(
      error.status === 401
        ? "Administrator password verification was rejected."
        : "Administrator password verification could not complete.",
      true);
  } finally {
    setBusy(false);
  }
}

async function reauthenticateMfa(event) {
  event.preventDefault();
  const verificationCode = elements.localMfaCode.value;
  elements.localMfaCode.value = "";
  const challengeToken = state.challengeToken;
  state.challengeToken = null;
  setBusy(true);
  try {
    await mutateJson(api.localMfaReauthentication, "POST", {
      challengeToken,
      verificationCode
    });
    state.options = await requestJson(api.options);
    elements.localMfaForm.hidden = true;
    elements.localPasswordForm.hidden = false;
    showStatus("Administrator reauthentication succeeded.");
  } catch (error) {
    elements.localMfaForm.hidden = true;
    elements.localPasswordForm.hidden = false;
    showStatus(
      error.status === 401
        ? "MFA verification was rejected. Start reauthentication again."
        : "MFA verification could not complete.",
      true);
    return;
  } finally {
    setBusy(false);
  }
  await loadAccounts();
}

async function enrollLocalAccount(event) {
  event.preventDefault();
  const password = elements.localPasswordNew.value;
  elements.localPasswordNew.value = "";
  const roles = selectedRoles("identity-local-role");
  if (roles.length === 0) {
    showStatus("Select at least one role for the local account.", true);
    return;
  }

  setBusy(true);
  try {
    const response = await mutateJson(api.enrollments, "POST", {
      userName: elements.localUserName.value,
      displayName: elements.localDisplayName.value,
      email: optionalValue(elements.localEmail.value),
      password,
      roles
    });
    state.pendingEnrollment = {
      userId: response.userId,
      enrollmentId: response.enrollmentId
    };
    renderEnrollmentSecrets(response, elements.localUserName.value);
    showStatus(
      "Local account is pending. Save the recovery codes and verify TOTP.");
  } catch (error) {
    handleAdministrationError(error, "Local account enrollment was rejected.");
  } finally {
    setBusy(false);
  }
}

async function confirmLocalEnrollment(event) {
  event.preventDefault();
  if (!state.pendingEnrollment) {
    showStatus("The local enrollment is no longer available.", true);
    return;
  }
  const totpCode = elements.enrollmentCode.value;
  elements.enrollmentCode.value = "";
  const pending = state.pendingEnrollment;
  setBusy(true);
  try {
    await mutateJson(
      `/api/admin/identity/accounts/${encodeURIComponent(pending.userId)}/enrollment-confirmation`,
      "POST",
      {
        enrollmentId: pending.enrollmentId,
        totpCode
      });
    clearEnrollmentSecrets();
    elements.localEnrollmentForm.reset();
    showStatus("Local account and TOTP enrollment completed.");
  } catch (error) {
    handleAdministrationError(
      error,
      "TOTP verification was rejected. Wait for a new code and try again.");
    return;
  } finally {
    setBusy(false);
  }
  await loadAccounts();
}

async function provisionExternalAccount(event) {
  event.preventDefault();
  const roles = selectedRoles("identity-external-role");
  if (roles.length === 0) {
    showStatus("Select at least one role for the external account.", true);
    return;
  }

  setBusy(true);
  try {
    await mutateJson(api.externalProvisioning, "POST", {
      userName: elements.externalUserName.value,
      displayName: elements.externalDisplayName.value,
      email: optionalValue(elements.externalEmail.value),
      roles
    });
    elements.externalProvisioningForm.reset();
    setDefaultObserve("identity-external-role");
    showStatus(
      "External account provisioned disabled. Link the provider before enabling it.");
  } catch (error) {
    handleAdministrationError(
      error,
      "External account provisioning was rejected.");
    return;
  } finally {
    setBusy(false);
  }
  await loadAccounts();
}

function renderAccounts(accounts) {
  elements.accountList.replaceChildren();
  if (accounts.length === 0) {
    elements.accountList.append(
      createElement("p", "muted", "No identity accounts are available."));
    return;
  }
  for (const account of accounts) {
    elements.accountList.append(renderAccount(account));
  }
}

function renderAccount(account) {
  const card = createElement("article", "identity-account-card");
  const heading = createElement("div", "identity-account-heading");
  const identity = document.createElement("div");
  identity.append(
    createElement("h4", "", account.displayName || account.userName),
    createElement(
      "p",
      "muted",
      `@${account.userName}${account.email ? ` · ${account.email}` : ""}`));
  const badges = createElement("div", "identity-account-badges");
  badges.append(
    createElement(
      "span",
      `identity-badge ${account.enabled ? "is-enabled" : "is-disabled"}`,
      account.enabled ? "ENABLED" : "DISABLED"),
    createElement(
      "span",
      "identity-badge",
      `AUTHORITY V${account.authorityVersion}`));
  if (account.hasLocalPassword) {
    badges.append(createElement("span", "identity-badge", "LOCAL + TOTP"));
  }
  for (const providerId of account.externalProviderIds || []) {
    badges.append(createElement("span", "identity-badge", providerId));
  }
  heading.append(identity, badges);

  const roles = createElement("fieldset", "identity-account-roles");
  const legend = document.createElement("legend");
  legend.textContent = "Roles";
  roles.append(legend);
  for (const role of roleNames) {
    const label = document.createElement("label");
    const input = document.createElement("input");
    input.type = "checkbox";
    input.value = role;
    input.checked = (account.roles || []).includes(role);
    label.append(input, document.createTextNode(role));
    roles.append(label);
  }

  const actions = createElement("div", "identity-account-actions");
  actions.append(
    actionButton("Save roles", async () => {
      const selected = Array.from(
        roles.querySelectorAll("input:checked"),
        input => input.value);
      if (selected.length === 0) {
        showStatus("Every account requires at least one role.", true);
        return;
      }
      await mutateAccount(
        account,
        `/api/admin/identity/accounts/${encodeURIComponent(account.userId)}/roles`,
        "PUT",
        { roles: selected },
        "Account roles updated.");
    }),
    actionButton(account.enabled ? "Disable account" : "Enable account", async () => {
      await mutateAccount(
        account,
        `/api/admin/identity/accounts/${encodeURIComponent(account.userId)}/enabled`,
        "PUT",
        { enabled: !account.enabled },
        account.enabled ? "Account disabled." : "Account enabled.",
        account.enabled);
    }),
    actionButton("Revoke all sessions", async () => {
      await mutateAccount(
        account,
        `/api/admin/identity/accounts/${encodeURIComponent(account.userId)}/sessions/revoke`,
        "POST",
        {},
        "All account sessions revoked.",
        true);
    }));

  const provider = state.options.externalProvider;
  if (provider) {
    const linked = (account.externalProviderIds || []).includes(provider.id);
    actions.append(
      linked
        ? actionButton("Unlink provider", async () => {
            await mutateAccount(
              account,
              `/api/admin/identity/accounts/${encodeURIComponent(account.userId)}/external-identities/${encodeURIComponent(provider.id)}`,
              "DELETE",
              undefined,
              "External provider unlinked.",
              true);
          }, true)
        : actionButton("Link provider", () => {
            submitExternalNavigation(
              `/api/admin/identity/accounts/${encodeURIComponent(account.userId)}/external-identities/link`);
          }));
  }

  card.append(heading, roles, actions);
  if (state.options.localAccountsEnabled && account.hasLocalPassword) {
    const reset = createElement("form", "identity-account-reset");
    reset.autocomplete = "off";
    const input = document.createElement("input");
    input.type = "password";
    input.minLength = 12;
    input.maxLength = 256;
    input.autocomplete = "new-password";
    input.placeholder = "New unique password";
    input.required = true;
    const button = actionButton("Reset password", () => {});
    button.type = "submit";
    reset.append(input, button);
    reset.addEventListener("submit", async event => {
      event.preventDefault();
      const password = input.value;
      input.value = "";
      await mutateAccount(
        account,
        `/api/admin/identity/accounts/${encodeURIComponent(account.userId)}/password-reset`,
        "POST",
        { password },
        "Password reset and active sessions revoked.",
        true);
    });
    card.append(reset);
  }
  return card;
}

async function mutateAccount(
  account,
  path,
  method,
  body,
  successMessage,
  destructive = false) {
  if (destructive &&
      !window.confirm(`Apply this authority change to ${account.userName}?`)) {
    return;
  }
  setBusy(true);
  try {
    const result = await mutateJson(path, method, body);
    const revoked = Number(result.revokedSessionCount || 0);
    showStatus(
      revoked > 0
        ? `${successMessage} ${revoked} active ${revoked === 1 ? "session" : "sessions"} revoked.`
        : successMessage);
  } catch (error) {
    handleAdministrationError(error, "The account authority change was rejected.");
    return;
  } finally {
    setBusy(false);
  }
  await loadAccounts();
}

function actionButton(label, action, danger = false) {
  const button = createElement(
    "button",
    danger ? "danger-action" : "secondary-action",
    label);
  button.type = "button";
  button.addEventListener("click", () => {
    Promise.resolve(action()).catch(error => {
      handleAdministrationError(error, "The identity action could not complete.");
    });
  });
  return button;
}

function selectedRoles(name) {
  return Array.from(
    document.querySelectorAll(`input[name="${name}"]:checked`),
    input => input.value);
}

function setDefaultObserve(name) {
  for (const input of document.querySelectorAll(`input[name="${name}"]`)) {
    input.checked = input.value === "Observe";
  }
}

function renderEnrollmentSecrets(response, userName) {
  elements.enrollmentSecret.textContent = response.sharedSecretBase32;
  elements.enrollmentUri.textContent = authenticatorUri(
    userName,
    response.sharedSecretBase32);
  elements.recoveryCodes.replaceChildren();
  for (const code of response.recoveryCodes || []) {
    elements.recoveryCodes.append(createElement("li", "", String(code)));
  }
  elements.enrollmentResult.hidden = false;
  elements.recoveryConfirmed.checked = false;
}

function clearEnrollmentSecrets() {
  state.pendingEnrollment = null;
  elements.enrollmentSecret.textContent = "";
  elements.enrollmentUri.textContent = "";
  elements.recoveryCodes.replaceChildren();
  elements.enrollmentCode.value = "";
  elements.enrollmentResult.hidden = true;
}

function clearTransientSecrets() {
  state.challengeToken = null;
  elements.localPassword.value = "";
  elements.localMfaCode.value = "";
  elements.localPasswordNew.value = "";
  clearEnrollmentSecrets();
}

function authenticatorUri(userName, sharedSecret) {
  return `otpauth://totp/AetherSDR:${encodeURIComponent(userName)}?secret=${encodeURIComponent(sharedSecret)}&issuer=AetherSDR&algorithm=SHA1&digits=6&period=30`;
}

async function submitExternalNavigation(path) {
  setBusy(true);
  try {
    const token = await requestJson(api.antiforgery);
    const form = document.createElement("form");
    form.method = "post";
    form.action = path;
    form.hidden = true;
    form.append(
      hiddenInput(token.formFieldName, token.requestToken),
      hiddenInput("ReturnUrl", "/admin#identity-administration"));
    document.body.append(form);
    form.submit();
  } catch (error) {
    setBusy(false);
    handleAdministrationError(
      error,
      "External provider navigation could not start.");
  }
}

function hiddenInput(name, value) {
  const input = document.createElement("input");
  input.type = "hidden";
  input.name = name;
  input.value = value;
  return input;
}

async function mutateJson(path, method, body) {
  const antiforgery = await requestJson(api.antiforgery);
  const headers = {
    Accept: "application/json",
    [antiforgery.headerName]: antiforgery.requestToken
  };
  const options = {
    method,
    credentials: "same-origin",
    cache: "no-store",
    redirect: "error",
    headers
  };
  if (body !== undefined) {
    headers["Content-Type"] = "application/json; charset=utf-8";
    options.body = JSON.stringify(body);
  }
  return requestJson(path, options);
}

async function requestJson(path, options = {}) {
  const response = await fetch(path, {
    method: options.method || "GET",
    credentials: "same-origin",
    cache: "no-store",
    redirect: options.redirect || "error",
    headers: options.headers || { Accept: "application/json" },
    body: options.body
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

function handleAdministrationError(error, fallback) {
  if (error.status === 403) {
    elements.workspace.hidden = true;
    showStatus(
      "Administrator reauthentication expired. Verify again before changing authority.",
      true);
    return;
  }
  showStatus(fallback, true);
}

function optionalValue(value) {
  const trimmed = String(value || "").trim();
  return trimmed || null;
}

function showStatus(message, error = false) {
  elements.reauthStatus.textContent = message;
  elements.reauthStatus.classList.toggle("is-error", error);
}

function setBusy(busy) {
  state.busy = busy;
  for (const control of elements.section.querySelectorAll(
    "button, input")) {
    control.disabled = busy;
  }
}

function createElement(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}
