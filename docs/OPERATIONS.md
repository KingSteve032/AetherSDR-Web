# AetherSDR-Web Operations Runbook

Document version: 1

This runbook describes the supported M8G backup, restore, health, diagnostics,
and routine operations workflow. It does not authorize live RF or TX testing.

## Operational principles

- Treat the radio as authoritative for live radio state.
- Backup and restore are offline maintenance operations in production. The CLI
  refuses create/restore while any fixed AetherSDR or AetherRemote systemd unit
  is active; it never stops services itself.
- Backup passphrases are entered only at an interactive local terminal. There is
  no passphrase command-line option and redirected input is rejected.
- Restore never downloads a release. The exact signed current release and, when
  recorded, rollback release must already be installed in the immutable release
  directory before restore.
- Admin operational checks may make HTTPS requests only to the persisted
  canonical AetherSDR origin and fixed AetherSDR routes. They do not open a radio
  session, acquire a TX lease, send a radio command, or mutate release state.

## Encrypted backup

The supported encrypted backup contains the durable AetherSDR-owned authority
needed to restore the installation:

- gateway authentication configuration;
- local identity database, including local users, password hashes, MFA state,
  recovery-code state, and session-revocation state;
- ASP.NET Data Protection keys;
- radio access/onboarding/TX-policy state;
- durable administrative audit state;
- AetherRemote station enrollment/credential state and gateway broker
  credentials when those paths are owned by AetherSDR;
- signing/trust configuration and key files that live in the configured
  AetherSDR configuration/state/secret roots;
- installer-owned managed-Caddy state/configuration when the ownership marker
  proves that AetherSDR owns it; and
- the exact current and rollback release identities.

Release binaries, transient release downloads, logs, and ordinary backup files
are not recursively embedded.

The file format is schema-versioned and uses AES-256-GCM authenticated
encryption. The encryption key is derived from the interactive passphrase with
PBKDF2-HMAC-SHA256 and a per-backup random salt. A wrong passphrase or modified
ciphertext fails authentication before restore data is used.

### Create

Open an offline maintenance window appropriate to the installed topology. For a
full gateway/hybrid host, stop the units that exist on that host, for example:

```bash
sudo systemctl stop \
  aetherremote-agent.service \
  aetherremote-station-engine.service \
  aetherremote-broker.service \
  aetherremote-release-updater.service \
  aethersdr-release-updater.service \
  aethersdr-web.service
```

`systemctl stop` may report that topology-inapplicable units do not exist; that
is not a reason to invent or enable them. Then create the backup using the
installed gateway binary:

```bash
sudo /opt/aethersdr/current/gateway-web/AetherSDR.Web \
  --create-encrypted-backup
```

Enter and confirm the passphrase when prompted locally. The command emits a
redacted JSON summary and writes a new `.aebak` file in the configured backup
directory. It does not overwrite an existing backup.

Verify the backup before reopening normal service:

```bash
sudo /opt/aethersdr/current/gateway-web/AetherSDR.Web \
  --inspect-encrypted-backup \
  --backup-file /var/backups/aethersdr/<backup>.aebak
```

Start only the units expected for the selected topology after the backup is
verified.

## Restore on the same host

1. Stop all AetherSDR/AetherRemote units that exist on the host.
2. Ensure the exact current and recorded rollback release identities from the
   backup are still present under the configured immutable release root.
3. Inspect the backup with the passphrase.
4. Run restore:

```bash
sudo /opt/aethersdr/current/gateway-web/AetherSDR.Web \
  --restore-encrypted-backup \
  --backup-file /var/backups/aethersdr/<backup>.aebak
```

Restore stages complete replacement roots and uses a durable two-phase restore
journal. A pre-commit interruption is rolled back on the next explicit restore
attempt. Once the journal is durably committed, recovery completes cleanup and
never reverts the committed restored state. The `current` release symlink is
switched only after the durable roots and any managed proxy file are staged.

After restore, validate the operational checks in Admin before resuming normal
use.

## Migration to a replacement Ubuntu VM

1. Install the same supported Ubuntu Server architecture or another architecture
   for which the exact signed release is published.
2. Create the required dedicated `aethersdr` and/or `aetherremote` service
   accounts through the supported installer. Restore maps logical owners to the
   replacement host's local UIDs/GIDs; it does not copy numeric IDs from the old
   VM.
3. Install and verify the backup's exact signed current release and recorded
   rollback release through the supported release flow. Do not create arbitrary
   directories with those names.
4. Copy the encrypted `.aebak` file to the configured backup directory using a
   protected operator-controlled transfer.
5. Keep all AetherSDR/AetherRemote services stopped and run the restore command.
6. The persisted setup document is validated and its installation path object is
   remapped to the replacement host's configured `InstallationPaths`; the other
   restored authority remains from the encrypted backup.
7. Re-establish every external dependency below.
8. Start only the topology-appropriate services and run Admin connectivity
   checks. Resolve critical alerts before considering the replacement host
   operational.

## External dependencies and separately handled secrets

The encrypted backup records these as external dependencies rather than silently
claiming to own them:

- DNS records, registrar/account access, and public IP/NAT policy;
- signed release package bytes;
- reverse-proxy configuration and TLS private keys/certificates that are not
  proven installer-owned AetherSDR paths;
- Microsoft Entra ID / generic OIDC application registration, provider-side
  policy, redirect URI registration, and provider-side secret lifecycle; and
- infrastructure credentials or VPN/firewall state outside the configured
  AetherSDR roots.

If external TLS or IdP secrets are managed by another platform, restore them by
that platform's supported process. Never copy them into AetherSDR configuration
merely to make the backup self-contained.

## Admin operations readiness

Admin > **Health, backup, and diagnostics** provides passive checks for:

- completed setup and canonical public URL;
- storage free-space floor;
- encrypted backup readiness and age objective;
- active immutable release/update readiness;
- retained rollback release readiness;
- FLEX discovery/radio health;
- broker/station connectivity and signed AetherRemote compatibility;
- authentication callback configuration;
- browser WebSocket route registration; and
- per-radio TX policy prerequisites without acquiring a lease or transmitting.

**Run connectivity checks** is an explicit administrator action. It validates TLS
and certificate expiry, required security/proxy headers, the configured external
authentication callback when applicable, the browser WebSocket authentication
boundary, and the station WebSocket broker boundary when applicable. The probe
uses only the canonical HTTPS origin persisted by setup. Active checks and bundle
generation are rate-limited to six actions per authenticated administrator/user
partition per minute with no queue.

The readiness response includes aggregate metrics and actionable warning/critical
alerts. Critical checks make the overall readiness false.

## Diagnostic bundle

**Download diagnostic bundle** creates an in-memory ZIP bounded by
`Operations:MaximumDiagnosticBundleBytes` (8 MiB by default). It contains:

- bounded runtime/framework/architecture and setup state metadata;
- the redacted operations readiness snapshot;
- radio/station health and count projections without identifiers; and
- aggregate administrative action/result counts.

It intentionally does **not** include raw configuration, logs, environment
variables, URLs, request headers, passwords or password hashes, MFA secrets,
Data Protection keys, private/public key bytes, runtime or station credentials,
enrollment codes, auth client secrets, bearer/session/CSRF tokens, user/actor
identifiers, radio IDs/serials/nicknames, station IDs/instance IDs, IP addresses,
or file contents.

## Alerts and routine cadence

Defaults:

- backup age warning: 168 hours (7 days);
- certificate expiry warning: 21 days;
- storage critical threshold: less than 10% free on any installation filesystem;
- active connectivity timeout: 10 seconds.

Operational response:

- **critical storage/setup/public URL/update**: do not update or restore until the
  root cause is resolved;
- **stale/missing backup**: create and inspect a new encrypted backup during the
  next maintenance window;
- **certificate warning/failure**: renew/correct TLS and rerun active checks;
- **WebSocket/proxy/header warning**: reconcile the supported proxy configuration
  before remote/browser use; and
- **no rollback candidate**: retain the previous immutable release through the
  next successful update.

## Dependency/security scanning

CI runs `tools/security/validate-nuget-vulnerabilities.sh` after restore. It asks
NuGet for vulnerable direct and transitive dependencies across the solution and
fails the job when any advisory is reported. The check emits no credentials and
does not alter package versions.

## TX and radio safety

Operations diagnostics are not TX acceptance. They never key, unkey, acquire a
TX lease, arm a watchdog, or send a FLEX command. A radio marked `tx-eligible`
still requires the existing per-radio production TX preflight and all station
safety boundaries. Live RF/HIL acceptance remains operator-run under the project
HIL procedure.
