import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import {
  buildPolicyRequest,
  formatAuditAction,
  formatAuditResult,
  formatClientCapacity,
  formatEnrollmentPurpose,
  formatStationCredentialSource,
  normalizeAdminMode,
  normalizeReservation,
  normalizeStationId,
  stationIdValid
} from "../wwwroot/admin-controls.js";

const adminHtml = await readFile(
  new URL("../wwwroot/admin.html", import.meta.url),
  "utf8");
const adminPageSource = await readFile(
  new URL("../wwwroot/admin-page.js", import.meta.url),
  "utf8");
const portalCss = await readFile(
  new URL("../wwwroot/portal.css", import.meta.url),
  "utf8");

test("admin access mode accepts only the exclusive opt-in", () => {
  assert.equal(normalizeAdminMode("exclusive"), "exclusive");
  assert.equal(normalizeAdminMode(" EXCLUSIVE "), "exclusive");
  assert.equal(normalizeAdminMode("anything-else"), "shared");
});

test("blank reservations are removed from policy requests", () => {
  assert.equal(normalizeReservation("   "), null);
  assert.equal(normalizeReservation(" operator-a "), "operator-a");
  assert.deepEqual(
    buildPolicyRequest("exclusive", " operator-a "),
    {
      mode: "exclusive",
      reservedUserId: "operator-a"
    });
});

test("radio client capacity has a safe unknown state", () => {
  assert.equal(
    formatClientCapacity(2, 4),
    "2 of 4 client slots available");
  assert.equal(
    formatClientCapacity(-1, -1),
    "Client capacity unavailable");
});

test("administrative audit actions use operator-facing labels", () => {
  assert.equal(
    formatAuditAction("radio.policy.update"),
    "Radio policy changed");
  assert.equal(
    formatAuditAction("radio.operator.force_disconnect"),
    "Operator released");
  assert.equal(
    formatAuditAction("station.enrollment_code.create"),
    "Station enrollment code created");
  assert.equal(
    formatAuditAction("station.credential.revoke"),
    "Station credential revoked");
  assert.equal(
    formatAuditAction("unknown"),
    "Administrative action");
  assert.equal(formatAuditResult("succeeded"), "SUCCEEDED");
  assert.equal(formatAuditResult("failed"), "FAILED");
});

test("station enrollment IDs and labels are constrained", () => {
  assert.equal(normalizeStationId(" odu-campus "), "odu-campus");
  assert.equal(stationIdValid("odu-campus"), true);
  assert.equal(stationIdValid("ODU:campus_2.0"), true);
  assert.equal(stationIdValid("space station"), false);
  assert.equal(stationIdValid("-leading"), false);
  assert.equal(stationIdValid("x".repeat(65)), false);
  assert.equal(
    formatStationCredentialSource("imported"),
    "Imported from existing setup");
  assert.equal(
    formatStationCredentialSource("enrolled"),
    "Enrolled with one-time code");
  assert.equal(formatEnrollmentPurpose("rotate"), "credential rotation");
  assert.equal(formatEnrollmentPurpose("reenroll"), "re-enrollment");
  assert.equal(formatEnrollmentPurpose("enroll"), "new enrollment");
});

test("Admin page revisions load connection diagnostics and styles together", () => {
  assert.match(
    adminHtml,
    /src="\/admin-page\.js\?v=m7-tx-lifecycle-heartbeat-1"/);
  assert.match(
    adminHtml,
    /href="\/portal\.css\?v=m6-wan-soak-1"/);
  assert.match(adminHtml, /id="admin-station-list"/);
  assert.match(adminHtml, /id="summary-external-clients"/);
  assert.match(adminHtml, /id="admin-enrollment-form"/);
  assert.match(adminHtml, /id="admin-credential-list"/);
});

test("Admin station security never puts enrollment codes in URLs", () => {
  assert.match(
    adminPageSource,
    /\/api\/admin\/stations\/enrollment-codes/);
  assert.match(
    adminPageSource,
    /Copy this code now\. It is shown only in this browser and works once\./);
  assert.doesNotMatch(
    adminPageSource,
    /enrollmentCode=.*(?:fetch|location)/);
  assert.match(
    adminPageSource,
    /sudo aetherremote-enroll \$\{window\.location\.origin\}/);
});

test("Admin radio inventory surfaces operational health", () => {
  assert.match(adminPageSource, /normalizeRadioHealth\(radio\)/);
  assert.match(adminPageSource, /health\.state\.toUpperCase\(\)/);
  assert.match(adminPageSource, /last stream/);
  assert.match(adminPageSource, /queue \$\{health\.queueDepth\}/);
});

test("Admin radio inventory surfaces bounded capacity history", () => {
  assert.match(adminPageSource, /function buildCapacityHistory\(radio\)/);
  assert.match(adminPageSource, /CLIENT CAPACITY HISTORY/);
  assert.match(adminPageSource, /history\.slice\(-8\)\.reverse\(\)/);
  assert.match(adminPageSource, /sample\.availableClients/);
  assert.match(adminPageSource, /sample\.licensedClients/);
});

test("Admin audit result pills center their labels without grid stretching", () => {
  assert.match(
    portalCss,
    /\.admin-audit-event \.status-pill \{[\s\S]*?display: inline-flex;/);
  assert.match(
    portalCss,
    /\.admin-audit-event \.status-pill \{[\s\S]*?align-self: start;/);
  assert.match(
    portalCss,
    /\.admin-audit-event \.status-pill \{[\s\S]*?justify-content: center;/);
  assert.match(
    portalCss,
    /\.admin-audit-event \.status-pill \{[\s\S]*?line-height: 1;/);
});

test("Admin connection inventory labels radio-observed external clients", () => {
  assert.match(
    adminPageSource,
    /FLEX GUI CONNECTIONS[\s\S]*?EXTERNAL/);
  assert.match(
    adminPageSource,
    /client\.browserOwned \? "WEB" : "EXTERNAL"/);
  assert.match(
    adminPageSource,
    /The radio reports connection details while an AetherSDR web GUI is/);
});

test("Admin session diagnostics surface radio-authoritative TX occupancy", () => {
  assert.match(adminPageSource, /TX OCCUPANCY/);
  assert.match(adminPageSource, /function formatTxOccupancy\(occupancy\)/);
  assert.match(adminPageSource, /External TX/);
  assert.match(adminPageSource, /AetherSDR TX/);
  assert.match(
    adminPageSource,
    /No fresh radio-authoritative interlock observation/);
  assert.match(adminPageSource, /Local PTT owner/);
  assert.match(adminPageSource, /PTT AUTHORITY/);
  assert.match(adminPageSource, /function formatPttAuthority\(occupancy\)/);
  assert.match(adminPageSource, /No fresh FLEX Local PTT owner/);
  assert.match(adminPageSource, /occupancy\?\.localPttOwners/);
});

test("Admin station cards include health and isolated receive sessions", () => {
  assert.match(adminPageSource, /function buildStationCard\(station\)/);
  assert.match(adminPageSource, /LAST CHECK-IN/);
  assert.match(adminPageSource, /REMOTE RECEIVE SESSIONS/);
  assert.match(adminPageSource, /One isolated GUI client per browser/);
});

test("Admin station cards surface bounded link recovery telemetry", () => {
  assert.match(adminPageSource, /LINK RECOVERY/);
  assert.match(adminPageSource, /formatStationConnectionCount/);
  assert.match(adminPageSource, /lastRecoveryMilliseconds/);
  assert.match(adminPageSource, /heartbeat timeout/);
  assert.match(adminPageSource, /No reconnect recorded for this broker process/);
});
