import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../..");
const read = (relative) => fs.readFileSync(path.join(root, relative), "utf8");

const workflow = read(".github/workflows/standalone-release-acceptance.yml");
const standalone = read("tools/release/run-standalone-release-acceptance.py");
const remote = read("tools/release/run-remote-station-acceptance.py");
const setup = read("tools/release/standalone_acceptance_setup.py");
const assets = read("tools/release/build-standalone-acceptance-assets.sh");
const uninstall = read("prototypes/web-client/deploy/uninstall-aethersdr.sh");
const bootstrap = read("prototypes/web-client/Radio/AetherRemoteBootstrap.cs");
const installerConsole = read("prototypes/web-client/Setup/InstallationInstallerConsole.cs");

test("M8H native acceptance runs packaged artifacts on both supported Ubuntu architectures", () => {
  assert.match(workflow, /runner: ubuntu-24\.04\n/);
  assert.match(workflow, /runner: ubuntu-24\.04-arm/);
  assert.match(workflow, /linux-x64/);
  assert.match(workflow, /linux-arm64/);
  assert.match(workflow, /run-standalone-release-acceptance\.py/);
  assert.doesNotMatch(workflow, /dotnet run --project prototypes\/web-client\/AetherSDR\.Web/);
});

test("M8H acceptance never authorizes live RF or TX control", () => {
  for (const source of [standalone, remote, setup, assets]) {
    assert.doesNotMatch(source, /xmit 1|xmit 0|SetTransmitAsync|AcquireTxLease|GateTransmit/);
  }
  assert.match(standalone, /transmitSupportInstalled": False/);
  assert.match(standalone, /"liveRfPerformed": False/);
  assert.match(remote, /"liveRfPerformed": False/);
  assert.match(standalone, /standalone installer enabled a TX authority/);
});

test("packaged setup host uses the exact canonical HTTPS authority", () => {
  assert.match(setup, /"Kestrel__Certificates__Default__Path"/);
  assert.match(setup, /"Kestrel__Certificates__Default__Password"/);
  assert.doesNotMatch(setup, /ASPNETCORE_Kestrel__/);
  assert.match(setup, /cwd=binary\.parent/);
  assert.match(setup, /canonical_origin != public_url/);
  assert.match(setup, /SetupClient\(canonical_origin, public_host, public_port\)/);
  assert.match(setup, /"ASPNETCORE_URLS": f"https:\/\/127\.0\.0\.1:\{public_port\}"/);
  assert.doesNotMatch(setup, /ORIGIN = "https:\/\/127\.0\.0\.1/);
});

test("packaged installer harness follows the exact installer console JSON contract", () => {
  assert.match(standalone, /plan_payload\["PlanId"\]/);
  assert.match(standalone, /result\.get\("Outcome", ""\)/);
  assert.doesNotMatch(standalone, /plan_payload\["planId"\]|result\.get\("outcome", ""\)/);
  assert.match(installerConsole, /private static readonly JsonSerializerOptions JsonOptions = new\(\)/);
  assert.doesNotMatch(installerConsole, /PropertyNamingPolicy\s*=/);
});

test("M8H deliberate failures corrupt only packaged startup configuration", () => {
  assert.match(assets, /appsettings\.json/);
  assert.match(assets, /M8H deliberately invalid startup JSON/);
  assert.doesNotMatch(assets, /AetherSDR\.Web.*printf|station.*xmit|radio.*command/i);
});

test("supported uninstall preserves durable authority and immutable releases", () => {
  assert.match(uninstall, /durableDataPreserved/);
  assert.match(uninstall, /immutableReleasesPreserved/);
  assert.match(uninstall, /serviceUsersPreserved/);
  assert.match(uninstall, /firewallPolicyPreserved/);
  assert.doesNotMatch(uninstall, /rm[^\n]*(\/etc\/aethersdr|\/var\/lib\/aethersdr|\/var\/backups\/aethersdr|\/opt\/aethersdr\/releases)/);
  assert.doesNotMatch(uninstall, /userdel|groupdel|ufw.*delete|ufw.*reset/);
});

test("AetherRemote bootstrap consumes the canonical verified persistent bundle manifest", () => {
  assert.match(bootstrap, /LocalOfflineReleaseBundleVerificationService\.ManifestFileName/);
  assert.doesNotMatch(bootstrap, /\$"release-manifest-\{architectureToken\}\.json"/);
});

test("remote acceptance uses guided enrollment and synthetic receive-only discovery", () => {
  assert.match(remote, /\/api\/admin\/stations\/bootstrap/);
  assert.match(remote, /\/api\/admin\/stations\/enrollment-codes/);
  assert.match(remote, /M8H-REMOTE-1/);
  assert.match(remote, /stationFailedUpdateRolledBack/);
  assert.doesNotMatch(remote, /\/api\/radio|\/ws\/radio/);
});
