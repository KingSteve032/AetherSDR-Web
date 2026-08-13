import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const installer = await readFile(
  new URL("../../../AetherRemote/deploy/install-from-gateway.sh", import.meta.url),
  "utf8");
const caddy = await readFile(
  new URL("../deploy/installer/proxy/Caddyfile.template", import.meta.url),
  "utf8");
const nginx = await readFile(
  new URL("../deploy/installer/proxy/nginx-aethersdr.conf.template", import.meta.url),
  "utf8");
const releaseBuilder = await readFile(
  new URL("../../../tools/release/build-github-release-assets.sh", import.meta.url),
  "utf8");
const updaterUnit = await readFile(
  new URL("../../../AetherRemote/deploy/aetherremote-release-updater.service", import.meta.url),
  "utf8");
const agentUnit = await readFile(
  new URL("../../../AetherRemote/deploy/aetherremote-agent.service", import.meta.url),
  "utf8");
const stationEngineUnit = await readFile(
  new URL("../../../AetherRemote/deploy/aetherremote-station-engine.service", import.meta.url),
  "utf8");
const program = await readFile(
  new URL("../Program.cs", import.meta.url),
  "utf8");

test("station bootstrap keeps enrollment code out of command arguments and history", () => {
  assert.doesNotMatch(installer, /--enrollment(?:-code)?/i);
  assert.match(
    installer,
    /IFS= read -r -s enrollment_code <\/dev\/tty/);
  assert.match(installer, /--proto '=https'/);
  assert.match(installer, /--tlsv1\.2/);
  assert.doesNotMatch(installer, /(?:^|\s)(?:-k|--insecure)(?:\s|$)/m);
  assert.match(installer, /--release-key-sha256/);
  assert.match(installer, /openssl dgst -sha256 -verify/);
  assert.match(installer, /sha256sum "\$\{agent_archive\}"/);
  assert.match(installer, /sha256sum "\$\{engine_archive\}"/);
  assert.match(installer, /broker_status/);
  assert.match(installer, /== "401"/);
});

test("bootstrap installs only deployment assets already inside the signed Agent package", () => {
  for (const asset of [
    "aetherremote-agent.service",
    "aetherremote-station-engine.service",
    "aetherremote-release-updater.service",
    "enroll-station.sh"
  ]) {
    assert.match(releaseBuilder, new RegExp(asset.replaceAll(".", "\\.")));
    assert.match(installer, new RegExp(asset.replaceAll(".", "\\.")));
  }
  assert.match(installer, /txSupport/);
  assert.match(installer, /enablesTransmit/);
  assert.match(installer, /Radio__AllowTransmit|"AllowTransmit": false/);
});

test("managed proxies expose only a prefixed station broker route", () => {
  assert.match(
    caddy,
    /handle_path \/aetherremote\/broker\/\* \{[\s\S]*reverse_proxy 127\.0\.0\.1:5090/);
  assert.match(
    nginx,
    /location \/aetherremote\/broker\/ \{[\s\S]*proxy_pass http:\/\/127\.0\.0\.1:5090\//);
  assert.match(caddy, /reverse_proxy 127\.0\.0\.1:5080/);
  assert.match(nginx, /proxy_pass http:\/\/127\.0\.0\.1:5080/);
  assert.doesNotMatch(caddy, /:5090\s+\{/);
  assert.doesNotMatch(nginx, /listen\s+5090/);
});

test("station receive engine uses the dedicated service-boundary host role", () => {
  assert.match(
    stationEngineUnit,
    /^Environment=InstallationServiceHost__Role=StationEngine$/m);
  assert.match(
    program,
    /if \(authenticationTopology\.Mode != AetherAuthenticationMode\.ServiceBoundary\)[\s\S]*AddScoped<AetherAuthenticationSessionService>/);
});

test("release updater is a hardened system service with no network family", () => {
  assert.match(updaterUnit, /^User=root$/m);
  assert.match(updaterUnit, /^Group=aetherremote$/m);
  assert.match(updaterUnit, /^NoNewPrivileges=true$/m);
  assert.match(updaterUnit, /^ProtectSystem=strict$/m);
  assert.match(updaterUnit, /^RestrictAddressFamilies=AF_UNIX$/m);
  assert.match(
    updaterUnit,
    /^ReadWritePaths=\/opt\/aetherremote \/var\/lib\/aetherremote \/etc\/systemd\/system$/m);
  assert.doesNotMatch(updaterUnit, /AF_INET|AF_INET6|\/bin\/sh|bash -c/);
});

test("Agent depends on updater and may write only its private release staging path", () => {
  assert.match(agentUnit, /^Requires=aetherremote-release-updater\.service$/m);
  assert.match(
    agentUnit,
    /^ReadWritePaths=\/var\/lib\/aetherremote\/release-staging$/m);
});

test("bootstrap waits for the fixed-purpose release updater before Agent startup", () => {
  assert.match(installer, /wait_release_updater_ready\(\)/);
  assert.match(
    installer,
    /systemctl is-active --quiet aetherremote-release-updater\.service/);
  assert.match(
    installer,
    /\[\[ -S \/run\/aetherremote-release-updater\/release\.sock \]\]/);
  assert.match(
    installer,
    /wait_release_updater_ready\n\s*systemctl restart aetherremote-agent\.service/);
});
