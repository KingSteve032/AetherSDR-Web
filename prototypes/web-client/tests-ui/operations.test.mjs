import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const program = await readFile(new URL("../Program.cs", import.meta.url), "utf8");
const backup = await readFile(
  new URL("../Operations/InstallationBackup.cs", import.meta.url),
  "utf8");
const consoleSource = await readFile(
  new URL("../Operations/OperationsConsole.cs", import.meta.url),
  "utf8");
const readiness = await readFile(
  new URL("../Operations/OperationalReadiness.cs", import.meta.url),
  "utf8");
const diagnostics = await readFile(
  new URL("../Operations/DiagnosticBundle.cs", import.meta.url),
  "utf8");
const adminPage = await readFile(
  new URL("../wwwroot/admin-page.js", import.meta.url),
  "utf8");
const workflow = await readFile(
  new URL("../../../.github/workflows/ci.yml", import.meta.url),
  "utf8");

test("encrypted backup CLI never accepts a passphrase as an argument", () => {
  assert.match(consoleSource, /--create-encrypted-backup/);
  assert.match(consoleSource, /--restore-encrypted-backup/);
  assert.doesNotMatch(consoleSource, /PassphraseSwitch|--passphrase|--password/);
  assert.match(consoleSource, /Console\.IsInputRedirected/);
  assert.match(consoleSource, /InstallationSetupConsoleSecretReader\.ReadAsync/);
  assert.match(consoleSource, /\/usr\/bin\/systemctl/);
  assert.match(consoleSource, /EnsureOfflineMaintenanceWindowAsync/);
  assert.doesNotMatch(consoleSource, /\/bin\/sh|bash -c|UseShellExecute = true/);
  assert.match(backup, /AesGcm/);
  assert.match(backup, /Pbkdf2Iterations = 600_000/);
  assert.match(backup, /CryptographicOperations\.ZeroMemory/);
});

test("operations endpoints are admin-only and active checks require antiforgery", () => {
  const passive = program.indexOf('"/api/admin/diagnostics/operations"');
  const active = program.indexOf('"/api/admin/diagnostics/operations/run"');
  const bundle = program.indexOf('"/api/admin/diagnostics/bundle"');
  assert.ok(passive >= 0 && active > passive && bundle > active);
  assert.match(
    program.slice(passive, active),
    /RequireAuthorization\(AetherPolicies\.Admin\)/);
  assert.match(
    program.slice(active, bundle),
    /RequireAuthorization\(AetherPolicies\.Admin\)[\s\S]*RequireAetherAntiforgery\(\)[\s\S]*RequireRateLimiting\("admin-operations"\)/);
  assert.match(
    program.slice(bundle, program.indexOf('"/healthz"', bundle)),
    /RequireAuthorization\(AetherPolicies\.Admin\)[\s\S]*RequireRateLimiting\("admin-operations"\)/);
});

test("active operations checks are fixed-origin probes with no radio or TX command surface", () => {
  assert.match(readiness, /CanonicalPublicUrl\.Parse/);
  assert.match(readiness, /new Uri\(origin, "\/healthz"\)/);
  assert.match(readiness, /authenticationCallbackPath/);
  assert.match(readiness, /ProbeHttpBoundaryAsync/);
  assert.match(readiness, /new Uri\(origin, "\/ws\/radio"\)/);
  assert.match(
    readiness,
    /new Uri\(origin, "\/aetherremote\/broker\/station\/v1"\)/);
  assert.doesNotMatch(readiness, /xmit 1|xmit 0|SetTransmitAsync|Acquire.*Lease|ProcessStartInfo/);
});

test("diagnostic bundle excludes raw identifiers and secret-bearing sources", () => {
  const radioRecord = diagnostics.match(
    /internal sealed record DiagnosticRadioSummary\(([\s\S]*?)\);/)?.[1] ?? "";
  const stationRecord = diagnostics.match(
    /internal sealed record DiagnosticStationSummary\(([\s\S]*?)\);/)?.[1] ?? "";
  assert.doesNotMatch(radioRecord, /RadioId|Serial|Nickname|Label|User|Actor|Target/);
  assert.doesNotMatch(stationRecord, /StationId|InstanceId|Serial|Credential|Token|Address/);
  assert.doesNotMatch(
    diagnostics,
    /Environment\.GetEnvironmentVariable|File\.ReadAllText|File\.ReadAllBytes|LogDirectory/);
  assert.match(diagnostics, /strongly redacted/i);
  for (const secret of [
    "passwords",
    "MFA seeds",
    "private signing keys",
    "runtime/admin credentials",
    "enrollment codes",
    "authentication client secrets"
  ]) {
    assert.match(diagnostics, new RegExp(secret, "i"));
  }
});

test("Admin exposes explicit operations and diagnostic bundle controls", () => {
  assert.match(adminPage, /\/api\/admin\/diagnostics\/operations\/run/);
  assert.match(adminPage, /\/api\/admin\/diagnostics\/bundle/);
  assert.match(adminPage, /postJson\([\s\S]*diagnostics\/operations\/run/);
  assert.doesNotMatch(adminPage, /innerHTML\s*=/);
});

test("CI fails closed on direct and transitive NuGet vulnerability scanning", () => {
  assert.match(workflow, /Scan NuGet dependencies for vulnerabilities/);
  assert.match(workflow, /validate-nuget-vulnerabilities\.sh/);
});
