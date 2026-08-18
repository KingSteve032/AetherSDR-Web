# M8H Standalone Release Acceptance

Document version: 1

M8H separates acceptance evidence into two classes:

1. **automated packaged evidence** that is safe to run on disposable Ubuntu hosts
   without a radio; and
2. **operator-owned packaged evidence** that requires real browsers, VPN paths, or
   radio hardware and therefore must never be manufactured by CI or an autonomous
   agent.

A release candidate is not M8H-complete until both classes refer to the **same
production-signed candidate release identity and asset digests**.

## Automated packaged gate

`.github/workflows/standalone-release-acceptance.yml` builds acceptance-only signed
packages from the candidate source and runs them on fresh native Ubuntu Server
24.04 x64 and arm64 runners. The ephemeral acceptance signing private key exists
only inside the package job and is never uploaded.

The x64 and arm64 jobs exercise only packaged product binaries and must prove:

- setup-only HTTPS bootstrap and a protected local administrator with password,
  TOTP MFA, and recovery codes;
- receive-only installation with TX support not installed;
- standalone installation through the supported Ubuntu installer;
- TLS health through the installer-managed reverse proxy;
- exact signed update with the previous immutable release retained;
- freshly approved manual rollback;
- a deliberately broken signed gateway target that switches, fails service
  startup/health, clears only exact fixed-unit systemd failed/start-limit state,
  and automatically restores the previous release;
- byte-stable identity, Data Protection, setup, and installer-configuration
  authority through update and both rollback paths;
- encrypted backup created by the packaged gateway while excluding only the
  validated transient release-updater IPC directory, consuming the installer-owned
  Caddy `sha256=<digest>`/`plan=<id>` marker contract, preserving exact offline
  identity-database bytes with no SQLite sidecars and exact same-host setup bytes,
  destructive replacement of durable roots, restore through the packaged gateway,
  and authority-hash equality afterward;
  and
- supported uninstall that removes only proven installer integration while
  retaining durable data, encrypted backups, immutable releases, service users,
  and firewall policy.

The x64 remote-station job additionally uses a clean Ubuntu 24.04 systemd
container and must prove:

- a HybridGateway is installed from the package so locally owned gateway services and an independently station-owned remote Agent are exercised together;
- Admin creates a guided bootstrap command and one-time enrollment code;
- the station downloads its Agent/station-engine packages from its own gateway;
- the one-time code is entered only at the station terminal;
- a synthetic **receive-only discovery advertisement** is visible through Admin;
- the gateway advances locally while the dedicated updater's receive-only remote-station catalog observer proves the exact station reconnects after the broker restart;
- both the station Agent verifier and fixed-purpose root updater independently
  accept that target only when its signed package identities, canonical
  `packages/...` paths, lengths, and hashes match the ReleaseBuilder contract, the
  root updater accepts only the packager's deterministic GNU-tar `.`/`./` prefix plus
  safe bounded relative entries, fixed directory links switch through atomic Linux
  `rename(2)` replacement, and the restarted Agent derives the exact active release
  identity/version from matching root-owned Agent/engine links before confirmation,
  then the station updates and reconnects; and
- a later signed release whose verified Agent package contains a deliberately
  invalid-format `AetherRemote.Agent` executable reaches the real systemd startup
  boundary, cannot complete station startup, and rolls the station back without
  gateway shell/command authority.

Synthetic FLEX discovery is inventory-only. The acceptance jobs never open a
radio control session, acquire a TX lease, key/unkey, send a FLEX command, or emit
RF.

## Production-signed candidate gate

The automated acceptance release identities are deliberately ephemeral and must
never be published. Before final publication, create the normal protected draft
release and record its tag, commit, manifest digests, and four package digests for
both supported architectures. The operator checklist below must name that exact
candidate.

Do not substitute a source checkout, a different draft, an older hardware run, or
an acceptance-only ephemeral signature for production-candidate evidence.

## Operator packaged checklist

Record the following in an M8H evidence document. Every line requires an exact
candidate release identity, UTC timestamp, operator, topology, client/device, and
pass/fail outcome. Do not include passwords, MFA seeds, enrollment codes, private
keys, station credentials, session tokens, IP addresses that identify private
infrastructure, or other secret material.

### External infrastructure

- [ ] Existing Caddy: validate the packaged operator-owned configuration fragment,
      HTTPS, forwarded headers, browser WebSocket, and station broker prefix; prove
      the installer does not replace the operator's Caddy configuration.
- [ ] Existing Nginx: perform the same checks with the packaged reviewed Nginx
      fragment and operator-owned TLS material.
- [ ] Microsoft Entra ID: validate the production candidate's redirect URI,
      callback, sign-in, Admin authorization, and logout against an operator-owned
      test application registration.
- [ ] Generic OIDC: validate the same contract against an operator-owned supported
      OIDC provider.

### Packaged browser/device/VPN recovery

Against the installed production candidate, complete the supported matrix in
`docs/SUPPORT-MATRIX.md`:

- [ ] Desktop Chromium-class browser.
- [ ] Desktop Firefox.
- [ ] Desktop Safari where available.
- [ ] iPhone/iPad Safari.
- [ ] Android Chromium-class browser.
- [ ] Microsoft Surface / Windows touch browser.
- [ ] Direct LAN recovery after browser foreground/background transitions.
- [ ] WireGuard/VPN reconnect after a temporary path loss.
- [ ] Browser session reconnect after gateway service restart with receive state
      recovering safely and no browser-created TX authority.

### Packaged radio/hardware soak

This section is intentionally operator-run. Follow
`prototypes/web-client/tx-hil/README.md` and do not run it from CI.

- [ ] One-hour multi-client receive soak on the installed production candidate.
- [ ] At least two simultaneous supported browser clients where radio licensing
      permits it; record capacity/degraded behavior when it does not.
- [ ] Two distinct radios with different persisted TX policies; prove enabling one
      never grants browser TX eligibility for the other.
- [ ] Disable the TX-eligible radio and prove browser authority disappears; if an
      AetherSDR-owned transmission is intentionally active under the reviewed HIL
      procedure, prove only that owned transmission is safely handled.
- [ ] External SmartSDR/Maestro/hardware-PTT ownership remains external and is
      never force-cleared by AetherSDR.
- [ ] Packaged remote station with a physical FLEX radio appears in Admin and
      remains receive-capable through the supported station reconnect path.

### Backup/replacement host

- [ ] Create the encrypted backup from the installed production candidate during
      an offline maintenance window.
- [ ] Restore that exact backup onto a replacement supported Ubuntu VM with the
      exact candidate/rollback signed releases installed.
- [ ] Verify local users/MFA, station credentials, radio policies, trust/signing
      state, Data Protection keys, managed proxy state where owned, audit history,
      and release identities survive.
- [ ] Re-establish every external dependency listed by `docs/OPERATIONS.md` and
      rerun Admin operations/connectivity diagnostics.

## Evidence acceptance

M8H can be closed only when:

- normal CI is green on the candidate commit;
- native x64/arm64 packaged acceptance and clean remote-station acceptance are
  green on that commit;
- the production-signed draft release is immutable enough to identify exact asset
  digests for the operator run;
- all required operator checklist rows above are complete against those exact
  production candidate digests; and
- security review finds no secret material, arbitrary remote command channel,
  unexpected writable release path, or new TX caller.

A failure in any packaged check blocks publication. A hardware/VPN/device check
that has not been run is **pending evidence**, not a pass.
