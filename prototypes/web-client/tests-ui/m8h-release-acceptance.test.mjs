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
const packageBuilder = read("tools/release/build-github-release-assets.sh");
const uninstall = read("prototypes/web-client/deploy/uninstall-aethersdr.sh");
const bootstrap = read("prototypes/web-client/Radio/AetherRemoteBootstrap.cs");
const installerConsole = read("prototypes/web-client/Setup/InstallationInstallerConsole.cs");
const pointerSwitch = read("prototypes/web-client/Releases/VerifiedReleaseActivationCurrentPointerSwitch.cs");
const rollbackExecution = read("prototypes/web-client/Releases/VerifiedReleaseActivationRollbackExecution.cs");
const serviceControl = read("prototypes/web-client/Releases/VerifiedReleaseActivationServiceControlExecution.cs");
const stationInstaller = read("AetherRemote/deploy/install-from-gateway.sh");
const stationReleaseUpdate = read("AetherRemote/src/AetherRemote.Agent/StationReleaseUpdateService.cs");
const stationAgentSettings = read("AetherRemote/src/AetherRemote.Agent/AgentSettings.cs");
const stationAgentProgram = read("AetherRemote/src/AetherRemote.Agent/Program.cs");
const stationRootUpdater = read("AetherRemote/src/AetherRemote.Updater/StationReleaseUpdateUpdater.cs");
const installationBackup = read("prototypes/web-client/Operations/InstallationBackup.cs");
const program = read("prototypes/web-client/Program.cs");

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

test("M8H target acceptance proves anonymous login assets without exposing protected assets", () => {
  assert.match(standalone, /def verify_anonymous_login_surface\(\)/);
  assert.match(standalone, /PUBLIC_URL \+ "\/login\.js"/);
  assert.match(standalone, /response\.geturl\(\) != login_script_url/);
  assert.match(standalone, /"\/api\/auth\/options" not in body/);
  assert.match(standalone, /PUBLIC_URL \+ "\/styles\.css"/);
  assert.match(standalone, /parsed\.path != "\/login"/);
  assert.match(
    standalone,
    /assert_authority\(authority, "successful update"\)[\s\S]{0,180}wait_health\(\)[\s\S]{0,120}verify_anonymous_login_surface\(\)/);
});

test("M8H deliberate failures reach the packaged gateway and Agent startup boundaries", () => {
  assert.match(assets, /repack_invalid_gateway_startup\(/);
  assert.match(assets, /appsettings\.json/);
  assert.match(assets, /M8H deliberately invalid startup JSON/);
  assert.match(assets, /repack_invalid_agent_startup\(/);
  assert.match(assets, /M8H deliberately invalid Agent executable format/);
  assert.match(assets, /chmod 0755 -- "\$\{root\}\/AetherRemote\.Agent"/);
  assert.match(assets, /aetherremote-agent-\$\{runtime\}\.tar\.gz/);
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

test("runtime update bundles are staged outside protected durable state", () => {
  assert.match(standalone, /\/var\/lib\/aethersdr-m8h-release-inputs/);
  assert.doesNotMatch(standalone, /\/var\/lib\/aethersdr\/m8h-release-inputs/);
  assert.match(standalone, /root\.chmod\(0o700\)/);
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

test("rollback clears only exact fixed-unit systemd failure state before restored starts", () => {
  assert.match(serviceControl, /ResetUnitFailureAsync/);
  assert.match(serviceControl, /"reset-failed"/);
  assert.match(rollbackExecution, /ResetUnitFailureAsync\(/);
  assert.match(rollbackExecution, /if \(!reset\.Succeeded \|\|/);
  assert.doesNotMatch(serviceControl, /\/bin\/sh|bash -c|UseShellExecute = true/);
});

test("station Agent and root updater require canonical ReleaseBuilder package declarations", () => {
  for (const source of [stationReleaseUpdate, stationRootUpdater]) {
    assert.match(source, /ExpectedPackageDeclaration\(/);
    assert.match(source, /"gateway-web"/);
    assert.match(source, /packages\/aethersdr-gateway-\{architecture\}\.tar\.gz/);
    assert.match(source, /packages\/aetherremote-agent-\{architecture\}\.tar\.gz/);
    assert.match(source, /packages\/aethersdr-station-engine-\{architecture\}\.tar\.gz/);
    assert.match(source, /packageIdentity/);
    assert.doesNotMatch(source, /SafeFileNamePattern/);
  }
  assert.match(stationRootUpdater, /Architecture\.X64 => \("linuxX64", "linux-x64"\)/);
  assert.match(stationRootUpdater, /Architecture\.Arm64 => \("linuxArm64", "linux-arm64"\)/);
});

test("station root updater accepts only the deterministic GNU-tar dot prefix", () => {
  assert.match(packageBuilder, /--directory="\$\{source_directory\}" \. \|/);
  assert.match(stationRootUpdater, /NormalizeArchiveEntryName\(/);
  assert.match(stationRootUpdater, /normalized is "\." or "\.\/"/);
  assert.match(stationRootUpdater, /normalized\.StartsWith\("\.\/"/);
  assert.match(stationRootUpdater, /part\.Length == 0 \|\| part is "\." or "\.\."/);
});

test("station Agent derives active release metadata from fixed root-owned release links", () => {
  assert.match(stationAgentProgram, /AgentRunningReleaseMetadata\.Reconcile\(agentSettings\)/);
  assert.match(stationAgentSettings, /DefaultReleaseRoot = "\/opt\/aetherremote\/releases"/);
  assert.match(stationAgentSettings, /DefaultAgentLink = "\/opt\/aetherremote\/agent"/);
  assert.match(stationAgentSettings, /DefaultEngineLink = "\/opt\/aetherremote\/station-engine"/);
  assert.match(stationAgentSettings, /Agent and station-engine release links do not identify the same active release/);
  assert.match(stationAgentSettings, /settings\.ReleaseIdentity = identity/);
  assert.match(stationAgentSettings, /settings\.StationEngineVersion = version/);
});

test("station root updater atomically replaces fixed directory symlink entries", () => {
  assert.match(stationRootUpdater, /DllImport\("libc", EntryPoint = "rename"/);
  assert.match(stationRootUpdater, /Rename\(temporary, link\)/);
  assert.match(stationRootUpdater, /replaced\.LinkTarget/);
  assert.doesNotMatch(stationRootUpdater, /File\.Move\(temporary, link/);
});

test("encrypted backup consumes the installer-owned managed Caddy marker contract", () => {
  assert.match(installationBackup, /ParseManagedMarkerDigest\(/);
  assert.match(installationBackup, /const string Prefix = "sha256="/);
  assert.match(installationBackup, /marker\.IndexOf\('\\n'\)/);
  assert.match(installationBackup, /ReadStableFileAsync\([\s\S]{0,120}stateMarker/);
});

test("encrypted backup preserves exact offline identity bytes and rejects SQLite sidecars", () => {
  assert.match(installationBackup, /ReadStableIdentityDatabaseAsync\(/);
  assert.match(installationBackup, /IdentityDatabasePath \+ "-wal"/);
  assert.match(installationBackup, /IdentityDatabasePath \+ "-shm"/);
  assert.match(installationBackup, /IdentityDatabasePath \+ "-journal"/);
  assert.match(installationBackup, /offline identity database without SQLite sidecar files/);
  assert.doesNotMatch(installationBackup, /BackupDatabase\(/);
});

test("same-host encrypted restore preserves exact setup bytes while replacement-host restore remaps paths", () => {
  assert.match(installationBackup, /if \(state\.Paths == m_paths\)/);
  assert.match(installationBackup, /InstallationSetupState remapped = state with \{ Paths = m_paths \}/);
});

test("encrypted backup excludes only the validated transient release-updater IPC directory", () => {
  assert.match(installationBackup, /ReleaseUpdateSupervisor\.DirectoryName/);
  assert.match(installationBackup, /ValidateExcludedReleaseSupervisorRuntime/);
  assert.match(installationBackup, /directory\.LinkTarget is not null/);
  assert.match(installationBackup, /FileAttributes\.ReparsePoint/);
  assert.match(installationBackup, /m_paths\.ReleaseDownloadDirectory/);
});

test("release updater starts only the remote station catalog observer needed for Hybrid health", () => {
  assert.match(program, /ReleaseUpdateConsoleCommandKind\.TransactionSupervisor/);
  assert.match(program, /GetRequiredService<RemoteStationCatalogService>\(\)/);
  assert.match(program, /remoteStationCatalog\.StartAsync\(CancellationToken\.None\)/);
  assert.match(program, /remoteStationCatalog\.StopAsync\(CancellationToken\.None\)/);
  assert.doesNotMatch(program, /TransactionSupervisor[\s\S]{0,800}app\.RunAsync\(/);
});

test("release updater readiness requires a protocol handshake after restart", () => {
  assert.match(standalone, /def wait_release_updater_ready\(/);
  assert.match(standalone, /--release-transaction-status/);
  assert.match(standalone, /failureCode.*executionDisabled/);
  assert.ok((standalone.match(/wait_release_updater_ready\(/g) ?? []).length >= 4);
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
  assert.match(remote, /def station_service_diagnostics\(/);
  assert.ok((remote.match(/station_service_diagnostics\(common\)/g) ?? []).length >= 3);
  assert.match(remote, /station signed update did not succeed:[\s\S]{0,500}station_service_diagnostics\(common\)/);
});

test("remote acceptance waits boundedly for discovered radio inventory after station connect", () => {
  assert.match(remote, /def poll_station_radio\(/);
  assert.match(remote, /deadline = time\.monotonic\(\) \+ timeout/);
  assert.match(remote, /last_state == "online"/);
  assert.match(remote, /radio\.get\("serial"\) == serial/);
  assert.match(remote, /station = poll_station_radio\(/);
  assert.match(remote, /socket\.SO_BROADCAST/);
});

test("remote acceptance preflights exact signed release publication from the station network boundary", () => {
  assert.match(remote, /def station_release_publication_preflight\(/);
  assert.match(remote, /https:\/\/aethersdr\.test\/\.well-known\/aethersdr/);
  assert.match(remote, /"--max-filesize", "1048576"/);
  assert.match(remote, /"--range", "0-0"/);
  assert.match(remote, /"--write-out", "%\{http_code\}"/);
  assert.match(remote, /\.strip\(\) != "206"/);
  assert.match(remote, /\/aetherremote\/releases\/\{identity\}\/linux-x64\/manifest/);
  assert.ok((remote.match(/station_release_publication_preflight\(common,/g) ?? []).length >= 2);
});

test("remote acceptance uses guided enrollment and station-owned signed updates", () => {
  assert.match(remote, /M8H_SETUP_TOPOLOGY.*hybridGateway/);
  assert.match(remote, /write_update_dropin\(public_key, STATION_ID\)/);
  assert.match(remote, /\/api\/admin\/stations\/bootstrap/);
  assert.match(remote, /\/api\/admin\/stations\/enrollment-codes/);
  assert.match(remote, /M8H-REMOTE-1/);
  assert.match(remote, /gateway_target = common\.activate\(/);
  assert.match(remote, /gateway_station_failure = common\.activate\(/);
  assert.match(remote, /broker_release_update\(common\.TARGET_ID\)/);
  assert.match(remote, /broker_release_update\(STATION_FAILURE_ID\)/);
  assert.match(remote, /stationFailedUpdateRolledBack/);
  assert.doesNotMatch(remote, /\/api\/radio|\/ws\/radio/);
});
