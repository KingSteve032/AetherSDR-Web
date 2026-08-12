import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import {
  buildMutationBody,
  cookieValue,
  nextSetupStep,
  setupSteps,
  statusSummary
} from "../wwwroot/setup.js";

const html = await readFile(
  new URL("../wwwroot/setup.html", import.meta.url),
  "utf8");
const source = await readFile(
  new URL("../wwwroot/setup.js", import.meta.url),
  "utf8");
const stylesheet = await readFile(
  new URL("../wwwroot/setup.css", import.meta.url),
  "utf8");

test("setup progression follows the persisted workflow order", () => {
  assert.deepEqual(setupSteps, [
    "bootstrapClaim",
    "topology",
    "publicUrl",
    "paths",
    "updateChannel",
    "backup",
    "transmitSupport",
    "preflight",
    "administrator"
  ]);
  assert.equal(nextSetupStep("none"), "bootstrapClaim");
  assert.equal(nextSetupStep("bootstrapClaim"), "topology");
  assert.equal(nextSetupStep("topology"), "publicUrl");
  assert.equal(nextSetupStep("publicUrl"), "paths");
  assert.equal(nextSetupStep("paths"), "updateChannel");
  assert.equal(nextSetupStep("updateChannel"), "backup");
  assert.equal(nextSetupStep("backup"), "transmitSupport");
  assert.equal(nextSetupStep("transmitSupport"), "preflight");
  assert.equal(nextSetupStep("preflight"), "administrator");
});

test("setup mutation bodies carry exact revisions and acknowledgements", () => {
  assert.deepEqual(
    buildMutationBody("topology", 4, { topology: "hybridGateway" }),
    { expectedRevision: 4, topology: "hybridGateway" });
  assert.deepEqual(
    buildMutationBody("updateChannel", 8, {
      updateChannel: "pinned",
      pinnedRelease: "v1.2.3"
    }),
    {
      expectedRevision: 8,
      updateChannel: "pinned",
      pinnedRelease: "v1.2.3"
    });
  assert.deepEqual(
    buildMutationBody("transmitSupport", 10, {
      installTransmitSupport: true,
      acknowledgedInstallationDoesNotEnableTransmit: true
    }),
    {
      expectedRevision: 10,
      installTransmitSupport: true,
      acknowledgedInstallationDoesNotEnableTransmit: true
    });
  assert.deepEqual(
    buildMutationBody("administratorEnrollment", 12, {
      userName: "admin",
      displayName: "Station Administrator",
      email: "",
      password: "not-a-default-password"
    }),
    {
      expectedRevision: 12,
      userName: "admin",
      displayName: "Station Administrator",
      email: null,
      password: "not-a-default-password"
    });
  assert.deepEqual(
    buildMutationBody("administratorConfirmation", 12, {
      totpCode: "123456"
    }),
    { expectedRevision: 12, totpCode: "123456" });
  assert.deepEqual(
    buildMutationBody("revoke", 12),
    { expectedRevision: 12 });
});

test("CSRF cookie parsing selects only the exact host cookie", () => {
  const cookie = [
    "other=value",
    "__Host-AetherSdrSetupCsrf=abc_DEF-123",
    "suffix__Host-AetherSdrSetupCsrf=wrong"
  ].join("; ");
  assert.equal(
    cookieValue(cookie, "__Host-AetherSdrSetupCsrf"),
    "abc_DEF-123");
  assert.equal(cookieValue(cookie, "missing"), "");
});

test("setup status copy distinguishes local claim and claimed workflow", () => {
  assert.equal(
    statusSummary({
      lockMode: "bootstrapRequired",
      bootstrapTokenPresent: false
    }),
    "A local bootstrap token must be issued before setup can be claimed.");
  assert.equal(
    statusSummary({
      lockMode: "bootstrapRequired",
      bootstrapTokenPresent: true
    }),
    "Waiting for the local bootstrap token.");
  assert.match(
    statusSummary({
      lockMode: "claimed",
      lastCompletedStep: "topology"
    }),
    /next step: Public Url/);
});

test("browser shell exposes every setup form without inline script or style", () => {
  for (const id of [
    "claim-form",
    "topology-form",
    "public-url-form",
    "paths-form",
    "update-channel-form",
    "backup-form",
    "transmit-support-form",
    "run-preflight",
    "continue-to-administrator",
    "administrator-enrollment-form",
    "administrator-confirmation-form",
    "revoke-session"
  ]) {
    assert.match(html, new RegExp(`id="${id}"`));
  }
  assert.match(html, /src="\/setup\/assets\/setup\.js"/);
  assert.match(html, /href="\/setup\/assets\/setup\.css"/);
  assert.doesNotMatch(html, /<script(?![^>]*src=)[^>]*>/);
  assert.doesNotMatch(html, /style="/);
  assert.match(stylesheet, /\.setup-step/);
  assert.match(
    html,
    /Issue a new bootstrap token from the local setup\s+CLI, reload this page, and reclaim the preserved workflow\./);
});

test("setup browser authority stays in cookies and request bodies only", () => {
  assert.match(source, /credentials: "same-origin"/);
  assert.match(source, /cache: "no-store"/);
  assert.match(source, /X-Aether-Setup-Revision/);
  assert.match(source, /X-Aether-Setup-Csrf/);
  assert.match(source, /input\.value = ""/);
  assert.doesNotMatch(source, /localStorage|sessionStorage|indexedDB/);
  assert.doesNotMatch(source, /bootstrapToken=.*(?:location|URL|searchParams)/);
  assert.doesNotMatch(source, /sessionToken=.*(?:location|URL|searchParams)/);
  assert.doesNotMatch(source, /console\.(?:log|info|warn|error)/);
  assert.match(source, /administrator-password"\)\.value = ""/);
  assert.match(source, /administrator-totp-code"\)\.value = ""/);
  assert.match(source, /pagehide/);
  assert.match(source, /replaceChildren\(\)/);
});

test("first administrator requires a unique password, TOTP, and recovery-code acknowledgement", () => {
  assert.match(html, /No default credential is created/);
  assert.match(html, /id="administrator-password"[\s\S]*minlength="12"/);
  assert.match(html, /id="administrator-totp-code"[\s\S]*pattern="\[0-9\]\{6\}"/);
  assert.match(html, /id="administrator-recovery-confirmation"/);
  assert.match(source, /\/setup\/api\/administrator\/enroll/);
  assert.match(source, /\/setup\/api\/administrator\/confirm/);
  assert.match(source, /otpauth:\/\/totp\/AetherSDR/);
  assert.doesNotMatch(html, /value="(?:admin|password|changeme)"/i);
});

test("TX support remains package intent with explicit non-enablement copy", () => {
  assert.match(
    html,
    /installing TX support does not enable transmit,[\s\S]*grant radio eligibility,[\s\S]*arm a watchdog,[\s\S]*browser TX authority/i);
  assert.match(
    source,
    /acknowledgedInstallationDoesNotEnableTransmit/);
  assert.doesNotMatch(source, /xmit|setTransmit|mox|ptt/i);
});
