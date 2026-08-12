# M8G Operations Capability Release Notes

Release-note version: 1
Milestone: M8G — Backup, restore, diagnostics, and operations

## Added

- Supported encrypted backup/inspect/restore CLI for AetherSDR-owned durable
  configuration, local identity/MFA state, Data Protection keys, radio policy,
  station credentials, trust/signing state, audit state, managed proxy state, and
  current/rollback release identities.
- AES-256-GCM authenticated backup format with PBKDF2-HMAC-SHA256 passphrase
  derivation, random salt/nonce, bounded decompression, path validation, logical
  service-owner restoration, and a durable two-phase restore journal.
- Replacement-host path remapping for the validated setup document and explicit
  external-dependency reporting for DNS, external proxy/TLS material, provider
  registration/secrets, and signed release package bytes.
- Production offline-maintenance enforcement: backup creation and restore refuse
  to run while fixed AetherSDR/AetherRemote services are active and never stop a
  service autonomously.
- Admin operations readiness with storage, backup age, release/update/rollback,
  radio discovery, station broker, AetherRemote compatibility, authentication,
  browser WebSocket, and TX-prerequisite checks.
- Explicit canonical-origin connectivity checks for TLS/certificate expiry,
  security/proxy headers, external authentication callback routing, browser
  WebSocket authentication boundary, and station broker WebSocket boundary.
- Aggregate operational metrics and actionable warning/critical alerts.
- Strongly redacted downloadable diagnostic ZIP with no raw logs, configuration,
  URLs, credentials, tokens, key bytes, user/radio/station identifiers, serials,
  addresses, or file contents.
- Setup Center post-install operations checklist covering the same operational
  acceptance surface without weakening setup-only isolation.
- CI NuGet vulnerability scanning for direct and transitive packages.
- Versioned operations runbook and server/browser/device/proxy/topology support
  matrix.

## Safety

- No backup, restore, readiness, active diagnostic, or diagnostic-bundle path can
  acquire a TX lease, key/unkey a radio, arm a watchdog, or send a FLEX command.
- Active network checks are administrator-triggered, same-origin constrained to
  the persisted canonical HTTPS gateway, use fixed paths, require antiforgery,
  and are rate-limited.
- Backup passphrases are not accepted as command-line arguments.
- Restore never downloads or trusts caller-selected release URLs; exact signed
  release identities must already be installed through the supported release
  flow.

## Operational compatibility

See `docs/SUPPORT-MATRIX.md`. Ubuntu Server 24.04 `linux-x64` and `linux-arm64`,
the five documented station topologies, installer-managed Caddy, and reviewed
existing Caddy/Nginx integration are the release support targets. M8H retains the
final packaged clean-host, browser/mobile, VPN, and hardware-soak acceptance
obligations.

## Migration/rollback

This milestone does not change the application configuration schema. Backup
schema 1 is self-describing and restore is atomic at each owned root plus the
`current` release pointer. A prepared-but-uncommitted restore is rolled back from
the durable restore journal; a committed restore is never automatically reverted
and recovery only completes cleanup.
