# AetherSDR Web Boundary Design

## Goals

- Preserve the AetherSDR interaction model: slice cards, panadapter, waterfall,
  meters, receive controls, and a clear TX surface.
- Let authenticated people use independent radio sessions concurrently,
  subject to the radio's live GUI-client admission.
- Treat every browser page as a distinct FLEX GUI client, including two pages
  signed in with the same Entra identity.
- Keep every transmit decision below the browser boundary and fail closed.
- Reuse AetherD's canonical models and binary frames once protocol v1 exists.

## Deployment shape

```text
Browser users
    |
    | HTTPS + secure cookie + same-origin WebSocket
    v
AetherSDR Web Gateway
    |-- OIDC login (Microsoft Entra ID or AD FS)
    |-- app-role authorization
    |-- per-browser/per-radio GUI session registry
    |-- radio-wide operator presence and browser backpressure
    |-- AD identity -> short-lived AetherD credential exchange
    |
    | private WebSocket / WireGuard interface
    v
AetherD at the shack
    |-- canonical radio models
    |-- per-client capability grants
    |-- single-holder TX lease and force-unkey watchdog
    |-- binary spectrum/waterfall + Opus audio
    v
FlexRadio on the station LAN
```

The browser gateway is not a second radio engine. It translates authenticated
web sessions into AetherD client sessions and renders the resulting projection.
SmartSDR TCP/VITA-49 remains entirely inside the Flex backend.

For GUI and receive-path development, the prototype includes an isolated
`FlexRx` adapter that reads FFT, waterfall, meter, and audio packets directly
from a selected radio and republishes them in the experimental browser frames.
Supported receive-only controls are mapped back to SmartSDR commands. It is not
the final gateway architecture. Production transmit remains subject to the M7
station-local ownership, command, emergency-unkey, and independent-watchdog
boundaries.

## Standalone setup foundation

M8A introduces a versioned setup model before adding a network setup surface.
The setup model keeps these concerns explicit and separately testable:

- one canonical public AetherSDR URL, normalized to an HTTPS authority with no
  user information, path, query, or fragment;
- an installation topology profile that distinguishes personal, local-gateway,
  remote-gateway, hybrid-gateway, and remote-station-node roles;
- one `InstallationPaths` configuration object covering configuration, state,
  secrets, immutable releases, backups, and logs;
- a resumable, revisioned setup document whose completed-step marker never
  advances unless the data required by that step validates;
- an independent first-run lock that can be re-issued without discarding setup
  progress and becomes complete only after the first administrator exists; and
- a short-lived random bootstrap token revealed only to the local process. Only
  its SHA-256 digest and expiry are persisted, and successful claim clears all
  token material atomically.

The supported production defaults are `/etc/aethersdr`,
`/var/lib/aethersdr`, `/var/lib/aethersdr/secrets`,
`/opt/aethersdr/releases`, `/var/backups/aethersdr`, and
`/var/log/aethersdr`. Development uses one ignored `.aethersdr` tree under the
content root. Every configured override must be absolute and every directory
role must remain distinct.

Setup state lives under the resolved state directory at
`setup/installation.json`. Writes use a complete temporary document, durable
flush, and atomic replacement. On Unix, the setup directory and state file are
required to remain mode `0700` and `0600`. Unknown fields, unsupported schema
versions, stale revisions, non-canonical URLs, invalid topology values, and
inconsistent lock state fail closed.

This first foundation slice does not add an anonymous setup endpoint, local
account provider, installer mutation, Docker support, or any executable TX
surface. Runtime setup-only startup, claim-session protection, preflight, and
administrator creation are separate reviewed increments built on this state
boundary.

The next console-only increment adds two process-local commands over that state:
a redacted setup-status report and short-lived bootstrap-token issuance. Both
commands terminate before authentication, radio discovery, station sessions,
command transport, or TX supervision are configured. Status output reports only
progress and non-secret presence flags. Token issuance writes only the digest and
expiry, prints the raw token once, and is refused when standard output is
redirected so service logs and shell pipelines cannot silently retain it. No
HTTP, WebSocket, AetherRemote, browser, timer, or startup path can issue or reveal
a bootstrap token.

The setup-workflow increment adds one typed mutation boundary for topology,
canonical public URL, paths, update channel, backup confirmation, and the
explicit TX-support installation choice. Every mutation requires a claimed lock,
the exact expected revision, and the immediately preceding validated step.
Revisiting an earlier step preserves later data only when the resulting complete
state still validates. The preflight reader requires all choices, reads an
existing state without creating or modifying it, and reports planned service
users, repository-backed packages and service units, loopback/public ports,
files, proxy work, firewall expectations, migrations, and TX-support warnings.
It applies no package, service, proxy, firewall, migration, radio, or TX change.

The local setup-CLI increment exposes that boundary without adding a network
surface. Claim reads the bootstrap token from an interactive terminal with echo
disabled; there is no token-valued command-line option and redirected input is
rejected. Each configuration command loads the current state, submits that exact
revision to the workflow, and prints only the existing redacted status projection.
The paths command records the same resolved path layout used to locate the setup
store, and preflight serializes the read-only report without advancing setup
state. Every setup command still returns before authentication, hosted services,
radio discovery, station sessions, command transport, or TX supervision are
constructed.

The first-administrator handoff adds a dedicated typed transition to the terminal
setup state. It runs the same read-only preflight, sends the exact schema,
revision, creation identity, topology, and canonical URL to a trusted verifier,
and requires evidence for one durable, enabled subject holding the exact
`Aether.Admin` role. Unknown or duplicate roles, stale or mismatched setup
identity, invalid timestamps, verifier failure, and concurrent setup changes all
leave the lock claimed. A safe retry may verify an already-created administrator
against the new exact revision. The setup document stores no subject identity,
credential, provider secret, or role list; it advances only to `Administrator`
and records the completion timestamp after verification succeeds.

No administrator provider, dependency-injection registration, console command,
HTTP route, or normal-runtime caller is included in the handoff increment.
Production local-account creation remains M8D work, while installer mutation
remains separate reviewed work.

The setup claim-session increment provides the bearer boundary required before a
future browser setup center can expose any setup mutation. The service consumes
the short-lived bootstrap token through the existing claim operation and then
returns one 256-bit process-local bearer. Only its SHA-256 digest is retained in
memory; neither token nor digest is persisted. A new successful bootstrap claim
replaces the prior session, process restart loses it, and expiry is absolute and
never slides. The bearer is bound to the exact setup schema, creation identity,
claim timestamp, and revision. After one successful setup mutation, the caller
must rotate it across exactly one revision; skipped, concurrent, completed,
expired, restarted, replaced, or malformed sessions all return the same
unauthorized result. The setup document remains unchanged by validation,
rotation, and revocation.

This increment registers no service and adds no HTTP route, cookie, browser
asset, setup-only listener, account provider, installer mutation, radio caller,
or TX caller.

The setup-only startup-planning increment combines the exact disabled legacy
configuration, unfinished setup eligibility, and completed normal-runtime
readiness into one typed decision without changing `Program.cs`. Setup-only and
normal runtime are mutually exclusive. Setup-only planning requires an existing,
valid, incomplete setup document, rejects completed setup and any selected
topology that does not run the web gateway here, and returns only the existing
redacted status projection. It never creates setup state, issues a bootstrap
token, constructs a claim session, or authorizes a network endpoint. Exact normal
runtime continues to delegate to the revision-, URL-, path-, topology-, role-,
and TX-support-bound readiness gate.

The planner registers no service and adds no configuration section, HTTP route,
cookie, browser asset, listener, account provider, installer mutation, radio
caller, or TX caller.

The setup HTTP-security increment defines the exact browser boundary that a later
setup-only host must apply. Each request is classified as an initial page read,
bootstrap claim, session read, or session mutation and is rejected for insecure
scheme, non-canonical authority or origin, cross-site fetch metadata, query
strings, unexpected or unbounded bodies, non-JSON mutation content, missing
session state, or malformed and mismatched CSRF evidence. Bootstrap claims are
limited to 4 KiB and five requests per minute; session mutations are limited to
16 KiB and thirty requests per minute. All fixed-window limit contracts use no
queue.

The same contract publishes a `__Host-` session cookie that is Secure, HttpOnly,
SameSite=Strict, path `/`, domainless, and bounded by the claim-session maximum
lifetime; a separate readable `__Host-` CSRF cookie has the same origin and
lifetime restrictions. CSRF values are independently generated with 256 bits of
entropy, encoded as canonical base64url, compared in fixed time, and redacted
from diagnostic rendering. Setup responses are no-store and carry a restrictive
CSP, no-referrer, nosniff, same-origin opener/resource policies, and a permissions
policy that disables browser device capabilities.

The policy is instantiated only by the explicit setup-only program composition.
The setup-only HTTP adapter translates it into response-header middleware, four
zero-queue fixed-window rate-limit policies, strict host-only cookie writes, and
eleven JSON-only routes. Security evaluation happens before any bounded request
body is read or deserialized. Unknown JSON members are rejected, query strings
remain forbidden, and bootstrap, session, and CSRF values are never serialized in
responses or accepted through URLs.

The setup-center application increment composes the redacted status projection,
HTTP-security policy, bootstrap claim, process-local claim session, ordered setup
workflow, and non-mutating preflight behind one endpoint-agnostic façade. Security
classification runs before state or token operations. Initial page reads return
only redacted status, the published security contract, and a fresh double-submit
CSRF value. Bootstrap claim consumes the local token only after the request passes
the canonical HTTPS/origin/fetch/body boundary, then returns one process-local
session issue plus a newly rotated CSRF value.

Session reads and preflight require the exact active bearer and setup revision.
Each repository-defined mutation type validates the same session-and-CSRF boundary,
applies one exact workflow step, requires one persisted revision advance, and then
rotates both session and CSRF authority. Once persistence succeeds, bearer rotation
is completed independently of caller cancellation so a canceled request cannot
leave a successfully advanced setup document paired with intentionally stale
browser authority. Replaced, revoked, stale, concurrent, completed, malformed, or
wrong-revision authority remains fail closed. The façade refuses completed setup
and any topology that does not run the gateway here, even if the process started
while an earlier setup state was eligible.

The setup-only program-composition increment now selects the unified host startup
plan before any normal authentication, radio, remote-station, watchdog, command,
or TX settings are read. `InstallationSetupOnly` is an explicit owned
configuration object. Disabled mode requires an empty access URL. Enabled mode
requires one exact canonical HTTPS access URL and remains mutually exclusive with
normal installation runtime. The public-URL workflow step must match that same
origin exactly.

An eligible setup-only plan registers only resolved installation paths, time, the
setup store, the HTTP-security policy, rate limiting, the setup-center application,
and a redacted composition report. The program builds the isolated host, maps
`GET /setup` plus claim, session, preflight, topology, public-URL, paths,
update-channel, backup, TX-support-choice, and revoke operations under
`/setup/api/`, and returns before normal service configuration. Composition
rejects any plan that is completed, not setup-only, missing paths or status, or
attempted after a normal authentication/radio/remote/watchdog service
registration. The default configuration remains disabled and the development
environment example preserves that default.

Session and preflight reads require the exact revision in
`X-Aether-Setup-Revision`. Mutations carry one exact expected revision in bounded
JSON, and claim or mutation success rotates both strict cookies. The HttpOnly
session bearer and readable CSRF token are written only as `__Host-` cookies;
response DTOs contain only redacted status and session metadata. Revocation first
validates the exact revision, then clears both cookies. Cleartext, foreign-origin,
cross-site, malformed, oversized, stale, and unauthorized requests fail closed.

The setup browser-shell increment maps one human-facing document at
`GET /setup/center` plus fixed CSS and module-script assets under
`/setup/assets/`. The JSON adapter and its eleven routes remain unchanged. Page
navigation passes through the existing page-read security classification, issues
only the readable CSRF cookie, and renders encoded redacted status plus resolved
default path suggestions into `data-*` attributes. The raw bootstrap token,
session bearer, persisted bootstrap digest, and CSRF value never appear in HTML.

The module implements bootstrap claim, exact session resume, topology, canonical
URL, path, update-channel, backup, TX-support-choice, preflight, and revocation
workflows. It submits credentials only in bounded JSON bodies or strict cookies,
uses no local storage, session storage, IndexedDB, inline script, inline style, or
token-bearing URL, and clears the bootstrap input before awaiting the claim. Each
mutation consumes the rotated revision and cookies returned by the existing
adapter. Preflight is rendered with DOM text nodes rather than HTML injection.

The shell stops after non-mutating preflight review. It creates no administrator,
account provider, package, service, proxy, firewall rule, migration, radio path,
watchdog path, command path, or TX caller. Process-local session loss remains
fail closed and directs the operator to issue a new bootstrap token locally,
reload, and reclaim the preserved workflow rather than manufacturing replacement
browser authority.

The M8A lifecycle-acceptance increment binds the running setup-only host to the
exact setup schema, creation timestamp, and startup revision. A setup-only hosted
monitor permits only monotonically increasing revisions for that same identity.
It stops the host when trusted first-administrator handoff completes, the selected
topology no longer runs the gateway here, the state document disappears or is
malformed, the setup identity is replaced, or the revision rolls backward.
Completion therefore disposes all process-local claim authority and a completed
installation cannot re-enter setup-only startup. Only the exact completed
normal-runtime binding may start afterward.

Automated acceptance covers the full configuration path, preflight, process
restart, old-session rejection, local recovery-token issuance, preserved-step
reclaim, trusted administrator evidence, lifecycle shutdown decision, setup-only
restart rejection, and exact normal-runtime admission. The published artifact
also carries a read-only TLS smoke script and clean Ubuntu 24.04 VM runbook. The
smoke script sends only GET requests and never claims or mutates setup. Production
administrator creation remains M8D work; native installer, proxy, service, and
firewall mutation remains M8C work.

The runtime-readiness increment defines the fail-closed binding required before a
normal runtime may admit the web gateway or a remote station node. The binding
carries the exact completed setup revision, runtime role, topology, canonical
public URL, resolved path layout, and TX-support installation choice. Evaluation
reads existing setup state without creating or modifying it and rejects
incomplete setup, stale revisions, topology or role mismatches, URL or path drift,
and TX-support installation drift. Missing or malformed setup state remains an
error rather than an implicit development fallback.

The startup-gate increment wires that check into `Program.cs` before authentication
settings, hosted services, radio discovery, station sessions, command transport,
or TX supervision are configured. `InstallationRuntime:Enabled` defaults false;
its disabled state permits only the exact empty binding defaults and does not even
resolve installation paths. When enabled for this web process, the role must be
`Gateway`, the selected topology must run a gateway here, and the exact completed
revision, canonical URL, resolved paths, and TX-support installation choice must
match persisted setup. Production standalone path resolution remains Linux-only.
The gate registers no service, endpoint, account provider, radio caller, or TX
caller and mutates no setup state.

## Signed release verification boundary

The first M8B increment defines a versioned signed-release manifest and a
fail-closed verifier over local immutable inputs only. The JSON envelope contains
one typed payload plus signature metadata. The signature covers the complete
payload together with the declared algorithm and key identifier through one
canonical UTF-8 serialization. Parsing rejects unknown fields, duplicate JSON
properties, integer enum values, comments, trailing commas, excessive depth, and
manifests larger than the bounded one-megabyte limit.

The payload binds one canonical release identity and strict semantic version to
Stable, Beta, or exact Pinned channel semantics and one supported architecture:
`linux-x64` or `linux-arm64`. It requires exactly one package identity and safe
relative package path for each of gateway/web, broker, AetherRemote agent, and
station engine. Duplicate identities, paths, or roles; missing or unexpected
roles; absolute or traversal paths; oversized declarations; local package-set
drift; length mismatch; and SHA-256 mismatch all fail closed.

Compatibility is explicit and signed. The verifier requires the local
configuration schema and protocol version to fall inside declared ranges, the
installed semantic version to satisfy the minimum previous-version transition,
and the target version to be newer. Configuration-schema changes require one
exact from/to migration declaration and a gateway restart declaration; declaring
no migration is valid only when the local and target schemas already match. A
host restart declaration must include every packaged service, preventing
contradictory restart metadata.

TX-support capability is descriptive only. Its versioned declaration must state
that verification enables no transmit function, grants no eligibility, creates
no browser TX authority, and arms no watchdog. A package may therefore be marked
TX-support-capable without changing any production TX gate, lease, ownership,
command, or watchdog state.

The verifier accepts a caller-supplied immutable public-key set and currently
supports only ECDSA P-256 with SHA-256 and fixed-width signatures. It reads no key
file, embeds no production trust anchor, and contains no signer. Its typed report
omits signature bytes, checksums, paths, and key identifiers; unverified manifest
metadata is not reflected before signature success.

This slice adds no network or GitHub client, polling loop, downloader, archive
extraction, installer, release-directory mutation, symlink switch, service
control, migration runner, backup/restore writer, CLI, Admin route, browser
control, radio caller, watchdog caller, or TX caller. Published bundles,
activation, rollback, and post-activation health checks remain separate reviewed
M8B increments.

The second M8B increment adds the production public-key trust composition without
adding an update caller. `ReleaseManifestTrust` is one strict configuration object
with a disabled default and a bounded key list. Normal-runtime startup rejects
unknown fields, unsupported algorithms, duplicate identifiers or files,
non-canonical paths, missing or oversized files, symlinks, writable-by-group or
writable-by-other Unix files/directories, multiple PEM blocks, private-key PEM,
invalid UTF-8, malformed key data, and non-P-256 keys. Setup-only startup still
returns before this normal-runtime configuration is read.

The registry copies each exact reviewed public key into one immutable verifier key
and exposes only redacted readiness diagnostics: enablement, availability, key
count, canonical key identifiers, algorithms, and short public-key fingerprints.
It does not expose configured paths or key bytes and contains no private-key or
signing method. The local verification service composes that registry with the
existing typed manifest verifier. Disabled or unavailable trust fails with a typed
report before manifest verification begins.

`Program.cs` constructs both objects at normal-runtime startup so malformed
production trust configuration fails closed even though no check/download/install
caller exists yet. Health reports only release-verification readiness and explicit
`false` values for network download, installation, and activation registration.
No package is opened from a path, no manifest is fetched, and no release, service,
configuration, radio, watchdog, command, lease, or TX state can be changed by this
composition.

The third M8B increment adds one local offline-directory verification boundary.
It accepts one canonical absolute directory containing exactly
`release-manifest.json` and four package files. The reader manually traverses a
bounded directory tree, rejects reparse points and symbolic links, requires safe
relative package paths, and rejects missing, extra, empty, or oversized entries.
On Unix, the bundle root, subdirectories, manifest, and packages must have no
owner, group, or other write bit, so the input is already immutable before it is
opened.

The manifest is copied under the existing one-megabyte bound. Packages are not
copied into process memory: each regular file is read sequentially through a
bounded buffer and reduced to an immutable relative path, exact length, and
SHA-256 digest. Length and last-write metadata are rechecked after the read, and
the root is revalidated before verification. The resulting snapshot is submitted
to the existing production-trust-backed verifier, which remains authoritative for
signature, channel, architecture, compatibility, package inventory, length, and
digest acceptance.

Normal-runtime composition registers only this typed reader service and redacted
health diagnostics. There is still no configured bundle path, startup scan,
polling loop, archive or package extraction, downloader, CLI, Admin route, browser
control, installer, staging write, release activation, symlink mutation, service
control, migration runner, backup/restore writer, radio caller, watchdog caller,
command or lease caller, or TX authority.

## Trust boundaries

### Browser

The browser is untrusted for authorization and TX safety. Disabled controls are
only a usability affordance. Every intent is re-authorized on the server.

### Web gateway

The gateway validates OIDC issuer/signature through the ASP.NET Core handler,
uses role claims for policy, validates WebSocket origin and message size,
allows only enumerated intent/property combinations, and bounds each client
queue.

The production gateway is allowed to request AetherD capabilities on behalf of
an authenticated user, but it is not allowed to manufacture capabilities.

### AetherD

AetherD is authoritative for the radio session, radio state, client
capability grants, TX lease, and force-unkey behavior. A malicious gateway or
browser must not be able to bypass those checks.

## Role and capability mapping

| AD app role | Gateway permission | Maximum AetherD grant |
|---|---|---|
| Observe | Subscribe to state and streams | Observe |
| Control | Send non-keying shared intents | Control |
| Transmit | Request TX lease | TX eligible, not automatically keyed |
| Admin | Manage sessions/policy | Explicitly configured administrative set |

`Aether.Transmit` is necessary but insufficient for transmit. The user must
also acquire the one active physical-radio lease, the engine must report TX
capability, the operator must deliberately initiate keying, and all interlock
checks must pass. SmartSDR, Maestro, hardware PTT, and other external FLEX
clients remain independent TX actors. FLEX `local_ptt` identifies which GUI
client owns local-PTT authority; it does not prove RF is keyed. A key request
therefore requires one fresh, exclusive Local PTT owner matching the exact
AetherSDR GUI handle, plus an idle radio-authoritative `interlock` state. Actual
TX ownership and every forced-unkey decision use `interlock` plus
`tx_client_handle`; AetherSDR may never force-unkey an external owner.

## Session isolation and client projections

The radio remains authoritative for live state and client admission. The
prototype registry creates one aggregate per browser page and physical radio
endpoint. That aggregate owns a unique FLEX GUI client ID, coordinator, command
router, radio connection, slices, panadapters, and audio stream. Two pages
signed in as the same user therefore consume two radio GUI sessions, just as
two desktop clients would. A WebSocket reconnect from the same page reuses its
aggregate; no other page receives its session ID or state. New clients receive:

1. Protocol/capability `welcome`.
2. Full session snapshot.
3. Ordered model deltas.
4. Bounded binary stream frames where latest frame wins.

Operator presence is deliberately outside those per-browser aggregates. A
radio-keyed presence registry publishes one row per authenticated identity to
every session using the same physical radio. Multiple browser connections from
one identity are aggregated into a connection count; no slice, panadapter,
audio, or control state crosses the session boundary.

Administrators receive a read-only projection of each aggregate for diagnosis:
GUI/client identity, current transport and stream IDs, owned panadapters and
slices, last frame times, and browser queue pressure. The projection reads
existing session state only; it cannot mutate radio state or manufacture a
capability.

Discovery-reported `available_clients` and `mf_enable` values are displayed as
hints only because UDP discovery can be stale. The live `client gui` response
is the admission decision. A rejected page waits and retries without evicting
or taking over an existing SmartSDR, Maestro, or web GUI client.

If a client detects a version gap, it requests a fresh snapshot rather than
guessing or replaying stale local state over the radio.

## TX state machine

The M7 foundation implements a process-wide lease authority keyed by physical
radio, bounded opaque lease IDs, expiry/disconnect/session cleanup, and a
station-local occupancy registry driven by FLEX `interlock` state plus
`tx_client_handle`. The same fresh observation carries Local PTT authority, so
an idle radio cannot be keyed through AetherSDR while SmartSDR owns Local PTT.

A browser-inaccessible station TX gate now models key-pending, radio-confirmed
keyed, unkey-pending, and fault states. It requires the exact lease, session,
browser, FLEX handle, fresh idle interlock, and exclusive AetherSDR Local PTT
authority. A 100 ms private watchdog reconciles lease loss and bounded unkey
retries. Unknown network outcomes retain the guarded intent until the radio
interlock resolves ownership. Through Phase 2S, the real `xmit 1`/`xmit 0`
adapter was compiled only when `EnableTxHil=true`, and normal production
publishes contained neither command string. Phase 2T adds a separate reviewed
production adapter behind disabled configuration, an exact radio allowlist, and
the still-disabled command gate. Production therefore remains receive-only with
`CanTransmit=false`.

The Phase 2A production lifecycle registers the accepted command gate, safety
supervisor, and authentication/engine/gateway monitors once per isolated radio
session, but only behind purpose-built unavailable transports. The command gate
is always constructed with transmit disabled, the supervisor remains disarmed,
and no arm, key, unkey, microphone, TUNE, or CW caller is registered. A bounded
single-reader observation queue records exact gateway instance, engine instance,
session, browser connection, authentication, local FLEX handle, and lease
changes. Queue failure releases only that session's lease and marks the lifecycle
faulted. The read-only lifecycle snapshot is included in administrative session
diagnostics, while `/healthz` proves that the lifecycle is registered and both
command transport and supervisor arming remain unavailable.

Phase 2B adds monotonic exact-identity observation sequences and timestamps for
the gateway, browser authority, station FLEX heartbeat, and lease. Every parsed
message on an admitted browser WebSocket refreshes only its current connection
identity, and every successful station FLEX ping refreshes only the exact
connected FLEX handle. Browser freshness reflects the ClaimsPrincipal admitted
for that WebSocket; it does not independently refresh or revalidate an Entra
token mid-socket. Mismatched browser IDs and handles are ignored. An exact
authenticated-to-unauthenticated browser activity transition immediately
releases only that browser's physical-radio lease and is forwarded to the
accepted authentication-loss monitor. These observations are diagnostic and
authority-revoking only; they cannot arm the supervisor or reach either
unavailable transport. The admin session grid renders the gate/supervisor state,
per-boundary sequence counts, timestamps, and continued absence of TX transports.

Phase 2C adds a one-second, in-process stale-authority watchdog. A tracked lease
remains fresh only while the exact admitted browser principal has been observed
within six seconds, the exact connected FLEX handle has completed a station
heartbeat within ten seconds, and gateway activity has been observed within ten
seconds. Explicit engine or gateway disconnect releases the exact tracked lease
immediately; a stale boundary releases it on the next watchdog evaluation.
Mismatched or untracked browser leases are never released. Later fresh
observations update diagnostics but cannot recreate the revoked lease or TX
authority. This watchdog is an authority-revocation layer inside the running
gateway, not the future independent emergency-unkey process, and it cannot arm,
key, unkey, or reach either unavailable production transport.

Phase 2D introduces the first separate-process boundary without moving radio
authority into it. `AetherSDR.TxWatchdog` is a standalone console host with no
reference to the web gateway, TX gate, occupancy registry, HIL assembly, or FLEX
transport. Its versioned local stdio protocol accepts only bounded `status`,
`register`, `heartbeat`, and `disconnect` observations. Registration binds one
exact radio/session/browser/gateway/engine/connection/lease/FLEX-handle tuple and a
strictly increasing sequence; mismatched or stale observations are rejected.
Every new OS process creates a new host instance, starts empty and Disarmed, and
never restores or infers the prior process's observation state. The host keeps
the opaque lease ID only for internal exact-equality checks; wire responses
expose `leaseBound` and never echo the lease or full identity. The production
package contains the executable for independent artifact inspection, but the web
service does not launch or connect to it yet. It has no timer, lease operation,
arming operation, radio connection, command transport, or emergency action.

Phase 2E makes that process boundary live without adding a radio boundary. Each
isolated radio session supervises exactly one watchdog child inside the web
service's existing least-privileged systemd cgroup. Standard input and output
remain the private IPC transport; no listener, network socket, shared file, or
persistent authority store is introduced. The session starts the child before
its receive transport, validates a new empty `Disarmed` status, and only then
continues receive startup. A missing or invalid child degrades watchdog health
and is retried, but it does not block receive-only operation.

The gateway registers the child only after the exact browser, gateway, engine,
FLEX handle, and opaque lease identity are all current. Ordinary browser,
station-engine, and gateway observations can heartbeat only a Disarmed
registration; they cannot arm or renew a safety deadline. Phase 2V's
lifecycle-owned transaction participant alone may send an exact arm, safety
heartbeat, or disarm. Authority loss sends an exact disconnect. A Disarmed child
may be reset, but an armed child remains alive and disconnected until its
heartbeat deadline so controlling-process loss cannot erase the safety arm.
Child exit, malformed response, request-ID mismatch, stale or mismatched
identity, timeout, or reconciliation-required outcome publishes a loss event and
revokes only the tracked physical-radio lease.

The gateway parses child responses with the same strict 4096-character boundary
as requests. Protocol version 2 permits only `Disarmed`, `Armed`, `Unkeying`, or
`ReconciliationRequired` with internally consistent registration, deadline, and
bounded one-shot outcome fields. The process has no key, unkey, lease, reset,
retry, or arbitrary-command request. Its optional TCP adapter sends the fixed
`xmit 0` only after fresh client/interlock status names the exact protected
handle as current TX owner; idle or mismatched ownership sends no command. After
dispatch, the arm clears only when the matching response and a fresh radio-idle
interlock observation both arrive. Missing idle confirmation is an unknown
outcome and remains reconciliation-required.

The first browser-integration increment exposes only a separately configured
ownership lease. `Radio:BrowserTxLeaseEnabled` defaults to false and is distinct
from the reserved `Radio:AllowTransmit` switch. The gateway derives lease
eligibility from its authenticated role set, exact live connection state, fresh
radio-authoritative occupancy, and the process-wide physical-radio lease. The
welcome message keeps the compatibility keying capability false and separately
reports lease eligibility plus explicit false values for keying, microphone,
TUNE, and CW. A lease cannot reach the hidden command gate and is not operator
intent to transmit.

Phase 2F adds deliberate browser intent validation without adding command
execution. TX ownership messages use their own strict version-1 envelope,
JavaScript-safe positive request/sequence numbers, monotonic per-WebSocket
sequence, bounded replay set, exact opaque lease ID, and unique intent ID. A
reconnect discards the browser's lease secret and starts a new sequence; the
server remains authoritative for disconnect release and expiry. Unknown fields,
duplicate JSON properties, non-object roots, stale sequence, replayed intent ID,
invalid duration, malformed lease ID, and invalid action payload fail before
authority evaluation. The browser bounds outstanding TX requests to 16 and
cannot generate an intent ID without a cryptographic random source.

The validation boundary never trusts a browser-supplied radio, session, user,
role, client, engine, FLEX handle, lease holder, occupancy, or capability. It
re-derives the current authenticated connection and requires the exact lease,
fresh idle occupancy, matching production lifecycle connection and FLEX handle,
and the same registered, connected, lease-bound Disarmed watchdog epoch. Only
then can a deliberate `mox.set`, `ptt.set`, `tune.set`, `microphone.set`, or
`cw.send` request become `validated`. The only successful Phase 2F terminal
outcome is still `transport-unavailable`; the result is `ok:false`, no command
gate method is called, and no radio transport exists.

The browser keeps the real MOX, TUNE, and CWX controls hidden and disabled unless
the server separately grants the corresponding executable capability. Phase 2F
grants only `intentValidationAvailable` after exact authority. A separate,
clearly labeled validation-only panel may acquire/release the lease and submit a
dry-run intent; it is hidden under the default production configuration. A
renewal may extend authority only while fresh idle occupancy and the exact
watchdog-bound lifecycle still hold. Authority loss releases that exact lease as
`renewal-authority-lost`. The browser also discards its local secret if renewal
is rejected, an unsupported lease-event version arrives, or no exact renewal
response is confirmed before the current server expiry. Local PC microphone
metering remains browser-only and no microphone samples enter the TX protocol.
Admin diagnostics show the lease holder name, expiry or revocation reason, and
latest validated/denied intent outcome without exposing the opaque lease ID.

Phase 2G seals a separate station-local command boundary without registering a
radio command adapter. It uses a deterministic version-1 signing payload and
ECDSA P-256 verification over the exact command ID, monotonic sequence, bounded
issue/expiry times, station, radio, web session, browser client, lease, gateway
instance, engine instance, protected FLEX handle, action, and enabled value.
The boundary revalidates fresh authentication, lifecycle authority,
radio-authoritative idle occupancy, exclusive Local PTT authority, and an exact
freshly Armed safety-supervisor identity before an adapter can be called. Replay,
clock, signature, identity, lease, occupancy, and supervisor failures consume no
radio command path. Audit records are bounded and store only a short lease
fingerprint, never the opaque lease secret or signature.

Production constructs this boundary disabled with no verification key, no
adapter, no arming capability, and no set-transmit capability. It has no browser,
HTTP, WebSocket, AetherRemote, watchdog, or timer entry point. Health and Admin
diagnostics expose only those fail-closed capability bits. Unit tests may use an
in-memory recording adapter to prove that only a fully signed and exactly bound
command reaches the adapter interface; this adapter is never registered in a
production publish.

Phase 2H adds an immutable station-scoped public-key trust ring without adding a
command source or transport. `StationTxCommandTrust` owns the complete setting:
verification enablement plus at most four key ID/path entries for bounded key
rotation. Startup loads every configured trust anchor even while verification is
disabled, so an invalid staged key cannot remain latent until activation. Each
anchor must be an exact ECDSA P-256 SubjectPublicKeyInfo `PUBLIC KEY` PEM in a
bounded regular file and regular containing directory that are not writable by
group or other users. Direct symbolic links, relative path segments, duplicate
IDs or paths, private keys, unsupported curves, multiple PEM blocks, trailing
data, malformed UTF-8, unknown configuration properties, and oversized files
fail startup. Invalid key IDs are rejected without echoing their untrusted text
into startup errors.

The singleton registry owns and disposes the imported public keys. Per-session
command boundaries receive only its verifier interface; they do not receive key
paths, key bytes, a signer, or a method that accepts an envelope. When reviewed
configuration enables verification, health and Admin may report `signature
available`, but `boundaryEnabled`, `commandAdapterRegistered`, `armingAvailable`,
and `setTransmitAvailable` remain false. This deliberately proves trust-anchor
readiness independently from command reachability.

Phase 2I adds a separate station-scoped private signing authority without adding
a command source or destination. `StationTxCommandSigning` owns one enable bit,
one canonical key ID, and one absolute private-key path. A configured key is
loaded even while signing is disabled. The file must be one exact UTF-8,
unencrypted PKCS#8 ECDSA P-256 `PRIVATE KEY` PEM in a bounded regular,
non-symlink file; Unix mode must be 0400 or 0600 and the immediate containing
directory cannot be writable by group or other users. Public-only keys,
encrypted keys, other curves, extra PEM blocks, trailing data, invalid UTF-8,
unknown properties, path indirection, and unsafe permissions fail startup.

The singleton authority owns and disposes the private key and serializes signing
under one lock because the imported `ECDsa` object is not shared concurrently.
Its internal request contains only the exact station/radio/session/browser/
lease/gateway/engine/FLEX tuple, the supported action, and its boolean value.
The authority itself supplies a canonical command UUID, a strictly increasing
process-local sequence, current issue time, five-second expiry, configured key
ID, and base64url P-256/SHA-256 signature over the existing version-1 payload.
Diagnostics expose only enablement, readiness, key ID, and a short public-key
fingerprint. The private path and private material never leave the authority.

Production resolves this authority at startup solely to validate configuration
and publish fail-closed health bits. The signer is not injected into a radio
session, lifecycle, command boundary, browser route, HTTP/WebSocket endpoint,
AetherRemote path, watchdog, or timer. There is no externally reachable
envelope-submission method, and the boundary, adapter, arming, and set-transmit
capabilities remain false. This proves private-key readiness independently from
both command reachability and public-key verification readiness.

Phase 2J adds a station-scoped internal envelope coordinator.
`StationTxCommandEnvelopeCoordinator` owns one submission enable bit and defaults
false. The singleton receives the signer and trust verifier only; it does not own
a radio boundary or adapter. Its public surface exposes diagnostics only. The
internal submission method requires a caller-owned boundary, one server-owned
`StationTxCommandAuthority`, and one fresh already-validated operator intent.
Only MOX/PTT Boolean intent is accepted; TUNE, microphone, and CW remain outside
SetTransmit. Intent IDs are canonical, intent sequence is positive, and
observation age is limited to five seconds with one second of future clock skew.

The coordinator derives every signed identity and Boolean value from the
validated intent plus authority; callers cannot supply an envelope, signature,
key ID, command ID, command sequence, or timestamp. A bounded in-memory replay
tracker consumes each intent ID once and requires strictly increasing intent
sequence for each session/browser owner. Cancellation, unknown adapter outcome,
boundary rejection, or signing failure never makes that intent retryable. Before
signing, the coordinator requires submission enabled, signer and verifier ready,
an enabled caller boundary, registered adapter, arming capability, and
SetTransmit availability. It then self-verifies the generated fixed-width P-256
signature against the station trust ring before the boundary independently
revalidates the envelope and exact authority.

Phase 2K adds one internal `StationTxCommandSessionComposition` to every radio
session. `RadioSessionRegistry` passes the station-scoped coordinator into the
session lifecycle through an internal submitter interface. The lifecycle owns
its existing disabled command boundary and the composition attaches that exact
boundary to the coordinator. Neither `RadioCoordinator` nor the WebSocket
endpoint receives the coordinator, submitter, composition, or submission
method.

The composition request contains only the current WebSocket connection ID, the
already-parsed browser intent, its positive JavaScript-safe sequence, and the
server observation time. It derives the station-command identity, canonical
radio, session, stable browser-page identity, exact active connection-owned
lease and expiry, gateway instance, engine instance, and FLEX handle from the
lifecycle. The gateway instance remains the station identity already owned by
the lifecycle command boundary. Radio-authoritative occupancy and the safety
snapshot are read directly from their station-owned registries. A browser cannot
supply or override any command-authority field.

Connection replacement, missing or mismatched lease, lease expiry, stale
browser/engine/gateway observations, missing FLEX handle, unsupported action,
missing Boolean value, cancellation, or authority-resolution failure stops
before coordinator submission. The composition does not retry an unknown or
faulted submitter outcome. Its diagnostics report whether coordinator, boundary,
authority, and submission are available plus bounded attempt/forward/outcome
counts; lease IDs, signatures, key paths, and key material are not exposed.

Production now reports coordinator and per-session composition registration,
but submission remains disabled, the attached boundary remains disabled, and
signer, verifier, arming, and SetTransmit capabilities remain unavailable under
default configuration. The Phase 2M adapter is registered only because its
executor terminates at the disabled command gate. There is still no browser,
HTTP, WebSocket, AetherRemote, watchdog, or timer submission caller, so the
external envelope-submission route remains absent and no FLEX command or RF path
can be invoked.

Phase 2L adds one `StationTxCommandAdapterComposition` beneath each session's
signed command boundary. It implements `IStationTxCommandAdapter`, treats a
validated command as a request rather than fresh authority, and re-resolves the
current lifecycle-owned authority immediately before delegation. The session
registry, radio coordinator, WebSocket endpoint, AetherRemote, watchdog, and
timers do not accept the executor type.

Phase 2M adds one lifecycle-owned `StationTxCommandGateExecutor` implementing the
internal executor contract. A validated SetTransmit true command maps only to
`StationTxCommandGate.RequestKeyAsync`; false maps only to
`RequestUnkeyAsync`. The executor owns no FLEX router, safety supervisor, lease,
occupancy registry, browser route, retry loop, or timer. Gate rejection remains
a known adapter rejection, while the two unknown command-outcome codes remain
unknown so radio-authoritative reconciliation continues in the gate.

The adapter composition independently checks the exact station, canonical radio,
web session, stable browser identity, active lease and expiry, gateway, engine,
FLEX handle, authentication/freshness flags, and matching freshly Armed safety
identity. A key request additionally requires fresh idle occupancy and exclusive
Local PTT for that exact handle. An unkey request instead permits only already
idle state or fresh proof that the exact handle is the single AetherSDR TX owner.
External, ambiguous, stale, or replaced ownership stops before the gate. The
command must remain inside its signed lifetime, and mismatch, capability loss,
cancellation, rejection, unknown outcome, or exception never causes an executor
retry. Diagnostics publish only attachment/readiness and bounded
attempt/forward/outcome counts.

Production constructs the gate with `allowTransmit:false` and the unavailable
command transport. Consequently the gate executor and command adapter report
registered, while executor arming, SetTransmit, boundary execution, and envelope
submission remain false. The HIL-only FLEX command transport is not linked into
the normal production path.

Phase 2N adds one lifecycle-owned `StationTxSafetyArmComposition` around the
existing supervisor. Its request records contain no station, radio, session,
browser, lease, gateway, engine, or FLEX-handle fields. A request may carry only
the current connection identity plus a bounded heartbeat timeout or abort
reason. The composition re-resolves the complete `StationTxCommandAuthority`
from lifecycle state, validates it against the supervisor and fresh occupancy,
and asks an optional internal `IStationTxSafetyArmAuthority` to authorize the
exact operation before forwarding one call to the supervisor. It performs no
retry and does not expose a lifecycle method or external route.

Arm requires a current authenticated lease, fresh browser/engine/gateway
observations, fresh idle occupancy, exclusive Local PTT for the protected handle,
and a Disarmed supervisor on the same radio. Heartbeat requires the exact active
arm; while idle it also requires Local PTT to remain exact, and while transmitting
it requires the protected handle to be the fresh single AetherSDR owner. Abort
requires the exact active arm and permits only already-idle state or that same
exact transmit owner. External, ambiguous, stale, expired, replaced, or
mismatched authority stops before the supervisor. An idle abort clears only the
matching arm without a radio command.

Phase 2O attaches one lifecycle-owned `StationTxSafetyArmAuthority`. Its
capability snapshot reads the signed command boundary, adapter composition, gate
executor, command gate, supervisor, and a newly resolved lifecycle authority.
It independently compares the complete station/radio/session/browser/lease/
gateway/engine/FLEX-handle tuple before any authorization. Arm requires the full
normal command path plus idle/Local-PTT readiness. Heartbeat requires that path
to remain ready and the safety identity to remain exact and fresh. Abort remains
independent of normal command-path availability so a later capability loss
cannot remove the ownership-safe abort decision; it still requires the exact
active arm and idle or exact single-owner AetherSDR TX state.

Production reports the authority attached and registered, but the signed
boundary is disabled, the gate has `allowTransmit:false`, command and emergency
unkey transports default unavailable, and no operation caller exists.
Diagnostics therefore keep arm, heartbeat, abort, boundary execution,
SetTransmit, and submission unavailable with zero attempts. Both supervisors
remain Disarmed; the independent watchdog may report a configured unkey-only
transport but has no invocation request. No browser, HTTP, WebSocket,
AetherRemote, reconnect, or timer caller can invoke the composition.

Phase 2P adds one lifecycle-owned `StationTxCommandTransactionComposition`
above the safety-arm and signed-command compositions. It accepts only a current
connection identity, one already-validated MOX/PTT Boolean intent with sequence
and observation time, and a bounded heartbeat timeout. It serializes all
operations through one lane and resolves lifecycle authority before arming,
after arming, and before later active-transaction operations. Browser input can
never supply a radio, session, lease, gateway, engine, FLEX handle, safety
identity, signature, command ID, or envelope.

A key transaction arms once, verifies that the stable station/radio/session/
browser/lease-expiry/gateway/engine/FLEX-handle tuple is unchanged and that the
new safety identity is exact, then submits one signed command. Known rejection
performs one ownership-safe abort cleanup. Unknown command outcome,
cancellation, or exception retains the arm and moves diagnostics to
`reconciling`; no automatic retry or success inference occurs. A second key is
rejected while a transaction is active.

An unkey transaction requires that exact active transaction, refreshes one
safety heartbeat, submits one false command, and clears the arm only after
confirmed acceptance. Known rejection retains the arm. Unknown command or
cleanup outcome retains it for reconciliation. Explicit heartbeat and abort
operations remain internal and exact-connection-bound. Production constructs
the composition for diagnostics only: submission, boundary, gate, and transports
remain disabled, no operation caller exists, and key, heartbeat, unkey, and
abort capabilities all remain false with zero attempts.

Phase 2Q removes the older internal lifecycle method that delegated directly to
the command-session composition. The lifecycle now exposes only three internal,
typed transaction operations: submit a validated key/unkey intent, refresh the
exact active transaction heartbeat, or abort the exact active transaction. Each
method delegates immediately to `StationTxCommandTransactionComposition` and
returns its accepted/rejected/unknown result. No method returns a command-session
result, and no registry, coordinator, WebSocket, HTTP, AetherRemote, watchdog,
reconnect, timer, or browser type receives a transaction request or result.
Production still has no caller, and all operations stop at disabled prerequisites
before arm or command forwarding.

Phase 2R places a typed browser-intent ingress adapter inside the lifecycle but
leaves it execution-disabled. The adapter requires the parsed request and the
server validation result to match exactly by sequence, intent ID, and action. It
also requires the validation-only outcome and current intent-validation capability,
rejects validation older than two seconds or more than one second in the future,
accepts only Boolean MOX/PTT, derives the five-second transaction heartbeat bound
server-side, checks current key/unkey capability, forwards at most once, and
preserves unknown outcomes for reconciliation. TUNE, microphone, CW, missing or
mismatched values, and stale/unavailable validation or transaction capability fail before the
transaction boundary. No coordinator, WebSocket, HTTP route, reconnect path,
timer, watchdog, or AetherRemote type receives the adapter, request, or result.

Phase 2S introduces one pure production-readiness policy rather than allowing
individual callers to infer readiness from partial capabilities. The policy
consumes existing configuration and live infrastructure facts only: transmit and
browser-lease configuration; coordinator attachment and submission; signing and
verification; boundary, adapter, gate, command transport, SetTransmit, and
emergency-unkey availability; safety-arm authority registration; and independent
watchdog supervision, process, IPC, unkey transport, and arming state. It returns
one readiness decision plus a deterministic complete list of missing
prerequisites. It owns no lease, browser identity, transaction, retry, or radio
operation. The lifecycle also gains one internal typed ingress operation that can
only delegate a `BrowserTxTransactionIngressRequest` to the Phase 2R adapter.
At the Phase 2S checkpoint, production kept that adapter execution-disabled and
exposed no caller; Phase 2Z later binds its single WebSocket caller conditionally.

Phase 2T introduces one production-primary command transport without connecting
it to browser execution. `StationTxCommandTransport` is one owned configuration
object with a disabled default, an exact bounded radio allowlist, and a bounded
command timeout. A local `FlexRx` session constructs the adapter; remote and
simulation sessions are ineligible. The adapter remains unavailable unless the
feature switch is enabled, the exact normalized radio ID is allowlisted, the
FLEX command router is attached, and the router has a non-zero client handle.
Every send receives the exact expected handle from the command gate. The router
checks that expected handle while holding the same lock that captures the
control session, preventing a detach/reconnect race from redirecting a command
to a replacement FLEX client. The adapter performs one send only, distinguishes
known FLEX rejection from unknown socket/timeout outcomes, propagates caller
cancellation, and bounds untrusted result text.

The primary adapter is registered in the lifecycle but the Phase 2T command gate
is still constructed transmit-disabled. Browser ingress remains execution-
disabled and callerless, and signing/submission/boundary prerequisites remain
disabled.

Phase 2U adds two separate unkey-only transports. The per-session emergency
adapter shares the exact-handle FLEX router but exposes only
`RequestUnkeyAsync(expectedProtectedClientHandle)`. The independent watchdog
adapter owns a minimal TCP client with no arbitrary-command or key method; its
only encoded radio command is `xmit 0`. The web process supplies the watchdog
endpoint only after global enablement, exact radio allowlisting, and local
`FlexRx` eligibility all match.

Phase 2V adds a separate disabled arming switch and protocol-v2 one-shot deadline
controller. `StationTxIndependentSafetyArmParticipant` wraps the existing
lifecycle safety participant inside the transaction composition. It resolves the
exact watchdog identity from current lifecycle authority, arms the independent
process before the local supervisor, renews it only from transaction safety
heartbeats, and disarms it only after local radio-confirmed Disarmed state. A
local-arm failure attempts to clear the independent arm; a rejected or unknown
independent unkey remains reconciliation-required. No browser, HTTP, WebSocket,
AetherRemote, reconnect, or ordinary lifecycle heartbeat receives these methods.

Phase 2W introduces a read-only production activation composition between the
lifecycle and the Phase 2S readiness policy. The composition owns no authority,
configuration, lease, transaction, or radio operation. Its only dependency is a
provider for the current typed infrastructure prerequisites, and every snapshot
re-evaluates the deterministic readiness policy rather than caching a prior
result. Diagnostics distinguish composition attachment from activation
availability and preserve the policy's exact first blocking reason. At the Phase
2W checkpoint, production health declared the composition registered, activation
unavailable, reason `transmit-disabled`, and no registered activation caller.

Phase 2X inserts a feature-owned static configuration interlock ahead of that
composition. `StationTxProductionActivation:Enabled` is a request to assemble
reviewed configuration, not an execution switch. When requested, startup
requires local `FlexRx` mode, explicit transmit and browser-lease opt-ins,
configured trust and signing keys, envelope submission, allowlisted primary and
emergency transports, and supervised watchdog unkey plus arming. Missing fields
fail startup in deterministic order. The default unrequested state is valid and
keeps the activation composition unavailable at `activation-not-requested` while
retaining the nested dynamic readiness result for diagnosis. No caller, command,
lease, gate, transport, watchdog operation, or radio authority is added.

Phase 2Y adds an immutable activation-plan layer between the static interlock and
the read-only activation composition. The plan has exactly four Boolean switch
intentions—command boundary, command-gate transmit, browser transaction ingress
execution, and browser keying-capability projection—and produces either all four
true after a valid explicit request or all four false.

Phase 2Z adds a single immutable per-session binding between that plan and the
four existing runtime constructor switches. The binder requires a complete plan,
a local `FlexRx` endpoint, explicit transmit configuration, and browser lease
configuration; it rejects partial plans and binds all four false for remote,
simulation, absent, or incomplete sessions. The lifecycle receives one binding
before it constructs the gate, command boundary, or browser ingress, so no later
request can mutate activation state. Browser capability is projected from that
same binding plus fresh dynamic readiness and exact session authority.

The only new caller is browser TX protocol v2. A strict `tx.intent` for Boolean
MOX/PTT delegates the unchanged parsed request and server validation through the
existing transaction ingress. The transaction still arms local and independent
safety before key, signs and verifies the station command envelope, traverses
the command gate, and confirms radio state. A strict `tx.heartbeat` may renew
only the active transaction owned by the same authenticated connection and
opaque lease. It runs every two seconds with a five-second maximum watchdog
deadline; ordinary socket keepalive, lease renewal, reconnect, timer, and status
traffic cannot renew TX authority. Active lease renewal and unkey are accepted
only while fresh occupancy proves the exact protected AetherSDR handle is the
sole TX owner.

Normal web artifact inspection now requires exactly one reviewed `xmit 1`, one
runtime-deduplicated reviewed `xmit 0`, and type markers for both the primary and
emergency transports. The watchdog artifact requires exactly one reviewed
`xmit 0` and zero `xmit 1`; both artifacts still reject HIL process, CWX, and
TX-audio surfaces. Thus source and binary contain the approved primary and
safety primitives. Default configuration still creates no executable production
TX or unkey path because the activation request, transmit/lease opt-ins, primary
and emergency transports, watchdog arming, signing, submission, and binding all
remain disabled. A reviewed complete configuration can bind the existing path
without creating a second gate or transport.

The independent, station-local supervisor has no key method and an unkey-only
transport. Its arm is purpose-bound to one engine
instance, lease, session/browser owner, exact protected FLEX client handle, and
bounded heartbeat deadline. A separate non-GUI FLEX observer may classify the
engine handle as external relative to itself; the supervisor therefore compares
the fresh single TX occupant directly with the protected arm handle rather than
trusting observer-relative ownership labels. It can issue unkey only for that
exact handle. SmartSDR, Maestro, hardware PTT, ambiguous/stale ownership, or a
replaced handle is never globally unkeyed. A newly started supervisor begins
disarmed and never infers ownership of an already-active transmission.

Phase 3B closes the cross-process cleanup gap after an accepted independent-
watchdog deadline unkey. Each active transaction captures the exact watchdog
host instance and cumulative accepted-unkey count that existed before keying.
Only a later accepted `deadline-unkey-accepted` result from that same watchdog
host, with a strictly greater count, the exact radio/session/connection/lease/
gateway/engine/FLEX-handle identity, and fresh radio-authoritative idle may enter
cleanup. Stale counts, a restarted watchdog, identity mismatch, non-idle or stale
radio state, and incomplete watchdog authority leave the transaction active in
explicit reconciliation.

The cleanup participant is lifecycle-only and executes while the transaction's
single-operation lock is held. It owns no radio command transport. It first
proves that any remaining gate intent and local safety arm belong to the same
active transaction, then asks the gate to consume the already-observed idle state
and asks the local supervisor to reset from that same fresh idle evidence.
Neither operation can key or unkey. The transaction is cleared only after a
second fresh-idle check plus fully empty `Idle` gate and `Disarmed` safety
snapshots. Lease release may arrive before this reconciliation; the lifecycle
retains the exact registered watchdog identity long enough to reconcile, then
disconnects and resets the watchdog registration. No browser command, heartbeat,
retry, or inferred success is introduced.

A separate engine-connection monitor has no radio command transport. It binds
to the active supervisor arm's exact engine instance, lease, and protected FLEX
handle, and may signal `station-engine-connection-lost` only after observing
that exact identity connected and then disconnected. Startup while disconnected,
mismatched identity, stale reports, and repeated disconnected reports cannot
invent ownership or create duplicate immediate unkeys.

HIL covers both connection boundaries. The first injects loss of only the
engine TX command channel while retaining a status and cleanup session for
evidence. The second launches the engine as a separate one-time child process,
binds it through a 30-second mode-0600 plan to the authorizing parent and exact
radio topology, and terminates the entire process tree. The parent accepts the verified OS-process exit only after seeing the exact
child connected, and signals `station-engine-connection-lost` immediately after
that exit. FLEX roster disappearance is still required as a later postcondition
and cleanup proof, but it does not delay emergency reconciliation. TCP closure
may make the radio idle autonomously; otherwise the observer may issue one
unkey only while fresh occupancy still proves the dead child's exact FLEX
handle is the sole TX owner. After idle and old-handle removal, HIL launches a
fresh replacement engine under a new one-time plan. Its PID, engine instance,
session, browser identity, lease, and FLEX handle must all differ from the dead
engine. It may reconcile only from fresh idle and must exit with zero key and
zero unkey commands, no active TX intent, no inherited resources, and the known
station baseline restored. A new cleanup/identification session starts only
after that replacement check and never inherits TX ownership from either
process.

Production states:

```text
No capability
    -> TX denied

Eligible, no lease
    -> may request lease

Lease held, idle, exact AetherSDR Local PTT authority
    -> deliberate keying intent may be evaluated

Lease held, keyed
    -> watchdog + interlock + authenticated client heartbeat

lease/auth/client loss or ambiguous state
    -> force-unkey locally at AetherD only when ownership proves AetherSDR keyed

external SmartSDR/Maestro Local PTT authority or active external TX
    -> browser key denied; never unkey the external owner
```

No reconnect, model reconciliation, timer, profile load, or status echo is an
operator keying intent.

## Browser rendering

The prototype uses one Canvas 2D spectrum path with a compact binary frame and
performs the waterfall scroll locally. The former stacked-trace selector,
browser preference, trace-history buffer, and alternate drawing path are
removed, so there is no dormant second renderer. Startup deletes the obsolete
preference key but never reads or writes it. Production can move to
WebGL/WebGPU only after measuring the Canvas implementation; rendering
technology cannot change the wire contract.

RX audio should use Opus frames decoded in an `AudioWorklet`, with a bounded
jitter buffer. Microphone capture and TX audio require a separate, explicit
operator permission and are out of scope until the engine TX gate is proven.
