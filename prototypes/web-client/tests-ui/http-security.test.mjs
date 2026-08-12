import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const program = await readFile(
  new URL("../Program.cs", import.meta.url),
  "utf8");
const app = await readFile(
  new URL("../wwwroot/app.js", import.meta.url),
  "utf8");
const admin = await readFile(
  new URL("../wwwroot/admin-page.js", import.meta.url),
  "utf8");
const radioSelect = await readFile(
  new URL("../wwwroot/radio-select.js", import.meta.url),
  "utf8");

function routeBlock(method, path) {
  const marker = `app.Map${method}(\n        "${path}",`;
  const start = program.indexOf(marker);
  assert.notEqual(start, -1, `missing ${method} ${path}`);
  const end = program.indexOf("\n\napp.Map", start + marker.length);
  return program.slice(start, end === -1 ? program.length : end);
}

test("every authenticated HTTP mutation requires shared antiforgery validation", () => {
  const mutations = [
    ["Post", "/auth/logout"],
    ["Post", "/api/radios/select"],
    ["Post", "/api/session/release"],
    ["Post", "/api/radio/low-bandwidth"],
    ["Post", "/api/admin/stations/enrollment-codes"],
    ["Post", "/api/admin/stations/{stationId}/{action}"],
    ["Post", "/api/admin/radios/{radioId}/identity"],
    ["Post", "/api/admin/radios/{radioId}/transmit-policy"],
    ["Post", "/api/admin/radios/{radioId}/policy"],
    ["Post", "/api/admin/radios/{radioId}/operators/{userId}/disconnect"]
  ];

  for (const [method, path] of mutations) {
    assert.match(
      routeBlock(method, path),
      /\.RequireAetherAntiforgery\(\)/,
      `${method} ${path} lacks antiforgery validation`);
  }
});

test("radio TX onboarding requires reauthentication and exact preflight", () => {
  const transition = routeBlock(
    "Post",
    "/api/admin/radios/{radioId}/transmit-policy");
  assert.match(
    transition,
    /authority\.RequireFreshAsync/);
  assert.match(
    transition,
    /RadioTransmitOnboardingPreflight\.Evaluate/);
  assert.match(
    transition,
    /ApplyTransmitPolicyAsync/);
  assert.match(
    transition,
    /\.RequireAuthorization\(AetherPolicies\.Admin\)/);
  assert.match(
    transition,
    /\.RequireAetherAntiforgery\(\)/);
});

test("logout GET is confirmation-only and logout mutation is POST-only", () => {
  assert.doesNotMatch(routeBlock("Get", "/auth/logout"), /Results\.SignOut/);
  assert.match(routeBlock("Post", "/auth/logout"), /Results\.SignOut/);
});

test("anonymous health is minimal and detailed health is Admin-only", () => {
  const publicHealth = routeBlock("Get", "/healthz");
  assert.match(publicHealth, /status = "ok"/);
  assert.match(publicHealth, /\.AllowAnonymous\(\)/);
  assert.doesNotMatch(publicHealth, /releaseManifest|txIndependentWatchdog/);

  const diagnostics = routeBlock(
    "Get",
    "/api/admin/diagnostics/health");
  assert.match(
    diagnostics,
    /\.RequireAuthorization\(AetherPolicies\.Admin\)/);
});

test("all authenticated browser mutation clients send the shared token", () => {
  for (const source of [app, admin, radioSelect]) {
    assert.match(source, /\/api\/antiforgery/);
    assert.match(source, /X-Aether-CSRF/);
    assert.match(source, /requestToken/);
  }

  assert.match(
    app,
    /\/api\/session\/release[\s\S]{0,300}headers: antiforgeryHeaders/);
  assert.match(
    admin,
    /async function postJson[\s\S]{0,300}\.\.\.antiforgeryHeaders/);
  assert.match(
    radioSelect,
    /async function selectRadio[\s\S]{0,500}\.\.\.antiforgeryHeaders/);
});
