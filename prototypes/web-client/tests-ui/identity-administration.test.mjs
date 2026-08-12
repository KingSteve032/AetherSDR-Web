import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const html = await readFile(
  new URL("../wwwroot/admin.html", import.meta.url),
  "utf8");
const source = await readFile(
  new URL("../wwwroot/identity-admin.js", import.meta.url),
  "utf8");
const stylesheet = await readFile(
  new URL("../wwwroot/portal.css", import.meta.url),
  "utf8");
const adapter = await readFile(
  new URL(
    "../Auth/Identity/AetherIdentityAdministrationHttpAdapter.cs",
    import.meta.url),
  "utf8");
const optionsAdapter = await readFile(
  new URL(
    "../Auth/Identity/AetherLocalAuthenticationHttpAdapter.cs",
    import.meta.url),
  "utf8");

test("Admin exposes bounded local and external identity workflows", () => {
  for (const id of [
    "identity-administration",
    "identity-local-reauth-password-form",
    "identity-local-reauth-mfa-form",
    "identity-external-reauth",
    "identity-local-enrollment-form",
    "identity-external-provisioning-form",
    "identity-local-enrollment-confirmation-form",
    "identity-account-list"
  ]) {
    assert.match(html, new RegExp(`id="${id}"`));
  }
  assert.match(html, /identity-admin\.js\?v=m8d-identity-admin-1/);
  assert.match(stylesheet, /\.identity-account-card/);
  assert.match(stylesheet, /\.identity-role-fieldset/);
});

test("identity clients use exact antiforgery and fresh reauthentication paths", () => {
  assert.match(source, /\/api\/antiforgery/);
  assert.match(source, /\/reauthenticate\/local\/password/);
  assert.match(source, /\/reauthenticate\/local\/mfa/);
  assert.match(source, /\/reauthenticate\/external/);
  assert.match(source, /credentials: "same-origin"/);
  assert.match(source, /cache: "no-store"/);
  assert.match(source, /redirect: "error"/);
  assert.match(source, /formFieldName/);
  assert.match(source, /hiddenInput\("ReturnUrl", "\/admin#identity-administration"\)/);
  assert.match(adapter, /ReadExternalNavigationRequestAsync/);
  assert.match(adapter, /form\.Count != 2/);
  assert.match(adapter, /form\.ContainsKey\(tokens\.FormFieldName\)/);
});

test("account controls cover every durable authority mutation", () => {
  for (const path of [
    "accounts?offset=0&limit=200",
    "accounts/enrollments",
    "external-provisioning",
    "enrollment-confirmation",
    "password-reset",
    "/roles",
    "/enabled",
    "/sessions/revoke",
    "external-identities/link"
  ]) {
    assert.match(source, new RegExp(path.replace(/[?]/g, "\\?")));
  }
  for (const role of ["Observe", "Control", "Transmit", "Admin"]) {
    assert.match(html, new RegExp(`value="${role}"`));
  }
  assert.match(optionsAdapter, /id = provider\.ProviderId/);
});

test("identity secrets remain in memory and are actively cleared", () => {
  assert.doesNotMatch(source, /localStorage|sessionStorage|indexedDB/);
  assert.doesNotMatch(source, /console\.(?:log|info|warn|error)/);
  assert.match(source, /pagehide/);
  assert.match(source, /state\.challengeToken = null/);
  assert.match(source, /localPassword\.value = ""/);
  assert.match(source, /localPasswordNew\.value = ""/);
  assert.match(source, /enrollmentSecret\.textContent = ""/);
  assert.match(source, /recoveryCodes\.replaceChildren\(\)/);
  assert.doesNotMatch(html, /type="password"[^>]*value=/i);
});

test("external accounts are described as disabled and passwordless until linked", () => {
  assert.match(
    html,
    /account starts disabled and without a password[\s\S]*Link the[\s\S]*provider before enabling/i);
  assert.match(source, /Link provider/);
  assert.match(source, /Unlink provider/);
  assert.match(source, /Enable account/);
  assert.match(source, /Disable account/);
  assert.match(source, /Revoke all sessions/);
});
