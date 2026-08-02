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
the final gateway architecture, and no transmit command exists. It will be
replaced by the AetherD stream boundary as that RFC lands.

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
interlock resolves ownership. The real `xmit 1`/`xmit 0` adapter is compiled
only when `EnableTxHil=true`; normal production publishes contain neither
command string. Production therefore remains receive-only with
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
FLEX handle, and opaque lease identity are all current. Browser activity,
station-engine heartbeat, and gateway heartbeat advance the same exact child
epoch. Lease release or incomplete authority sends an exact disconnect and
replaces the child with a new empty process. Child exit, malformed response,
request-ID mismatch, stale or mismatched identity, timeout, or rejected request
publishes a loss event immediately; the in-process lifecycle releases only its
tracked physical-radio lease before the bounded restart delay. A restarted child
has a new host instance, sequence zero, and no registered identity. A later ready
or heartbeat observation cannot recreate the released lease. Session disposal
stops the child and removes it from aggregate health.

The gateway parses child responses with the same strict 4096-character boundary
as requests. Every accepted response must remain exactly `Disarmed` with reason
`command-incapable-skeleton`, unavailable command transport, unavailable arming,
and internally consistent registration fields. The watchdog still has no FLEX
reference, socket, key, unkey, emergency transport, arming operation, timer, or
operator-facing control. Phase 2E proves supervision and fail-closed authority
revocation only; it is not production emergency-unkey integration.

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
unkey transports are absent, and no operation caller exists. Diagnostics
therefore keep arm, heartbeat, abort, boundary execution, SetTransmit, and
submission unavailable with zero attempts. The supervisor remains Disarmed, the
independent watchdog remains command-incapable, and no browser, HTTP, WebSocket,
AetherRemote, watchdog, reconnect, or timer caller can invoke the composition.

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
Production keeps that adapter execution-disabled and exposes no caller.

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
