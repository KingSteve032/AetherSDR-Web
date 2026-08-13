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
const pointerSwitch = read("prototypes/web-client/Releases/VerifiedReleaseActivationCurrentPointerSwitch.cs");
const stationInstaller = read("AetherRemote/deploy/install-from-gateway.sh");

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
  assert.match(setup, /"installTransmitSupport": True/);
  assert.match(setup, /"acknowledgedInstallationDoesNotEnableTransmit": True/);
  assert.match(setup, /"transmitSupportInstalled": True/);
  assert.match(setup, /"transmitEnabled": False/);
  assert.match(standalone, /Radio__AllowTransmit=true/);
  assert.match(standalone, /Radio__BrowserTxLeaseEnabled=true/);
  assert.match(standalone, /StationTxProductionActivation__Enabled=true/);
  assert.match(standalone, /"transmitSupportInstalled": True/);
  assert.match(standalone, /"liveRfPerformed": False/);
  assert.match(remote, /"liveRfPerformed": False/);
  assert.match(standalone, /standalone installer enabled a TX authority/);
});

test("interactive acceptance diagnostics redact operator responses and credential-shaped output", () => {
  assert.match(standalone, /def redact_interactive_diagnostics\(/);
  assert.match(standalone, /redacted = redacted\.replace\(response, "<redacted-response>"\)/);
  assert.match(standalone, /release_updater_failure_diagnostic\(\)/);
  assert.match(standalone, /release_activation_failure_diagnostic\(\)/);
  assert.match(standalone, /journalctl.*aethersdr-release-updater\.service/);
  assert.match(standalone, /--- installed service diagnostic ---/);
  assert.match(standalone, /diagnostic = redact_interactive_diagnostics\(diagnostic, prompt_responses\)/);
  assert.doesNotMatch(standalone, /interactive command exited.*text\[-4000:\]/);
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
  assert.match(standalone, /def decode_environment_file_value\(/);
  assert.match(standalone, /values\[key\] = decode_environment_file_value\(value\)/);
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
  assert.match(stationInstaller, /packages\/\[A-Za-z0-9\._-\]\{1,160\}/);
  assert.doesNotMatch(stationInstaller, /or "\/" in name/);
});

test("runtime update bundles are staged outside protected home paths", () => {
  assert.match(standalone, /\/var\/lib\/aethersdr\/m8h-release-inputs/);
  assert.match(standalone, /\/etc\/aethersdr\/release-trust/);
  assert.match(remote, /target_bundle = seed_bootstrap_bundle\(target_bundle, common\.TARGET_ID\)/);
  assert.match(remote, /station_failure_bundle = seed_bootstrap_bundle/);
});

test("station bootstrap accepts only the deterministic archive root directory entry", () => {
  assert.match(read("AetherRemote/deploy/install-from-gateway.sh"), /if not parts and member\.isdir\(\):\n\s+continue/);
});

test("pointer switch revalidates extracted releases with the extracted-tree directory bound", () => {
  assert.match(pointerSwitch, /activation\.UsesExtractedRoleTree/);
  assert.match(pointerSwitch, /MaximumExtractedDirectoryCount \+ 1/);
  assert.match(pointerSwitch, /: MaximumDirectoryCount/);
});

test("release updater readiness requires a protocol handshake after restart", () => {
  assert.match(standalone, /def wait_release_updater_ready\(/);
  assert.match(standalone, /--release-transaction-status/);
  assert.match(standalone, /failureCode.*executionDisabled/);
  assert.doesNotMatch(standalone, /while time\.monotonic\(\) < deadline and not socket\.exists\(\)/);
});

test("remote acceptance captures bounded station service startup diagnostics", () => {
  for (const service of [
    "aetherremote-station-engine.service",
    "aetherremote-agent.service",
    "aetherremote-release-updater.service"
  ]) {
    assert.match(remote, new RegExp(service.replaceAll(".", "\\.")));
  }
  assert.match(remote, /systemctl", "status", service/);
  assert.match(remote, /journalctl", "-u", service/);
  assert.match(remote, /redact_interactive_diagnostics/);
});

test("remote acceptance waits boundedly for discovered radio inventory after station connect", () => {
  assert.match(remote, /def poll_station_radio\(/);
  assert.match(remote, /deadline = time\.monotonic\(\) \+ timeout/);
  assert.match(remote, /last_state == "online"/);
  assert.match(remote, /radio\.get\("serial"\) == serial/);
  assert.match(remote, /station = poll_station_radio\(/);
  assert.match(remote, /socket\.SO_BROADCAST/);
});

test("remote acceptance uses guided enrollment and station-owned signed updates", () => {
  assert.match(remote, /\/api\/admin\/stations\/bootstrap/);
  assert.match(remote, /\/api\/admin\/stations\/enrollment-codes/);
  assert.match(remote, /M8H-REMOTE-1/);
  assert.match(remote, /broker_release_update\(common\.TARGET_ID\)/);
  assert.match(remote, /broker_release_update\(STATION_FAILURE_ID\)/);
  assert.match(remote, /stationFailedUpdateRolledBack/);
  assert.doesNotMatch(remote, /common\.activate\(/);
  assert.doesNotMatch(remote, /\/api\/radio|\/ws\/radio/);
});
