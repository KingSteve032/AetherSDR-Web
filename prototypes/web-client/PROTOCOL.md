# Experimental Browser Prototype Protocol

This protocol exists only to exercise the web interface while AetherD protocol
v1 is under development. It must not be treated as stable or implemented by a
radio backend.

## WebSocket

- Path: `/ws/radio?sessionId=<opaque-session-id>`
- Required subprotocol: `aethersdr.experimental.v0`
- Authentication: same-origin server session cookie
- Origin: exact request origin or an entry in `AllowedOrigins`
- Maximum browser text message: 64 KiB
- Browser binary messages: rejected

## Radio selection

On page load the browser generates a random 128-bit browser client ID and calls
`GET /api/session?browserClientId=<32-hex-digits>`. The ID is stable for that
page's WebSocket recovery attempts but is not shared with another page.

The server listens for Flex discovery broadcasts on UDP 4992. `GET
/api/radios?sessionId=<opaque-session-id>` returns the discovered radios,
online/capacity state, and the configured-IP fallback used for routed/VPN
stations. A control-authorized user may select only one of those
server-discovered identities with:

```json
POST /api/radios/select
{"radioId":"flex:1121-1104-6700-2912",
 "currentSessionId":"5f8b...",
 "browserClientId":"8c9f..."}
```

The response contains the opaque session ID for the selected browser/radio
pair.
The browser opens a new WebSocket with that ID and closes its previous socket.
The same page and endpoint reuse an existing session during recovery; another
page receives a distinct FLEX GUI registration, radio connection, and state,
even for the same signed-in user. A session accepts only one active WebSocket.
Session IDs are ownership-checked at every HTTP and WebSocket boundary.
Arbitrary browser-supplied hosts are rejected. Discovery capacity is displayed
as a hint, while the live radio response to `client gui` is authoritative for
admission. A rejection never displaces an existing GUI client, and radio
selection never enables transmit.

The page sends `POST /api/session/release?sessionId=<opaque-session-id>` during
page teardown. If that signal is lost, a short idle timeout releases the FLEX
GUI registration.

Low-bandwidth mode is also session-scoped:

```json
POST /api/radio/low-bandwidth
{"enabled":true,"sessionId":"5f8b..."}
```

The browser measures received audio, spectrum, and text traffic in two-second
windows. It reports the current session profile and delivery health over the
authenticated radio WebSocket:

```json
{"cmd":"diagnostics.network","profile":"normal","adaptation":"automatic",
 "pageVisible":true,"sampleMilliseconds":2000,
 "receivedBytes":301250,"receivedMessages":124,
 "bytesPerSecond":150625,"bitsPerSecond":1205000,
 "audioBytesPerSecond":99500,"spectrumBytesPerSecond":51000,
 "textBytesPerSecond":125,"messagesPerSecond":62,
 "maximumGapMilliseconds":48,"audioPackets":100,
 "spectrumFrames":24,"textMessages":0,"missingAudioPackets":0}
```

The gateway boundary validates every field, rejects inconsistent component
rates, timestamps accepted reports, and exposes only that web client's latest
sample to Admin diagnostics. These reports are observational and never issue a
radio command.

While the page is foregrounded and connected, three consecutive samples with a
delivery gap of at least 300 ms or newly missing audio packets switch that
session to the low profile. An automatically selected low profile remains
active for at least two minutes and returns to normal only after 30 consecutive
samples with gaps no greater than 150 ms and no new missing audio packets.
Manual low-bandwidth selection is a hold and is never automatically undone;
turning it off resumes adaptive monitoring after a 60-second cooldown.

Changing profiles reconnects only the browser's receive GUI client. The
session, slices, frequencies, modes, filters, and other radio-authoritative
state remain intact. Because FLEX firmware 4.2.18 changes an owned panadapter's
live FPS during `client low_bw_connect`, an explicit return to normal restores
only the FPS observed immediately before that session entered low mode.

## Control messages

The server sends `welcome` with server-derived capabilities, a full radio
snapshot, current radio-wide operator presence, and any TX lease. Presence is
aggregated by authenticated identity for the selected physical radio and
includes `connectionCount`; it never carries another identity's session or
radio state.

`capabilities.transmit` remains `false` until a production keying path is
accepted. Phase 1 adds `capabilities.tx` for lease-only eligibility. It reports
whether the lease foundation is configured, whether the authenticated server
identity has `Aether.Transmit` or `Aether.Admin`, whether the radio is connected,
whether fresh radio-authoritative occupancy permits a lease, whether this
browser already holds it, and whether a lease is currently available. The same
object explicitly reports `keyingAvailable`, `microphoneAvailable`,
`tuneAvailable`, and `cwAvailable` as `false`. None of these fields are accepted
from browser input.

Phase 2A adds no browser message. Each administrative session diagnostic now
includes a read-only `txLifecycle` object with exact gateway/engine/session/
browser identities, current browser/authentication/radio/lease observations,
and the disabled command-gate and disarmed safety-supervisor states. It also
reports that production command and emergency-unkey transports are unavailable.
These fields are diagnostic projections only and are never accepted as authority
from a browser.

Phase 2B keeps the same browser protocol. Every parsed message on an admitted
browser WebSocket counts as an exact-connection authority observation, and every
successful station FLEX `ping` counts as an exact-handle engine observation.
The browser observation reflects the ClaimsPrincipal admitted for that socket;
it is not an independent mid-socket Entra token refresh. The `txLifecycle`
diagnostic adds monotonic sequence numbers and last-observed timestamps for
browser, engine, gateway, and lease observations. Wrong browser IDs and wrong
FLEX handles do not advance those counters. An exact unauthenticated browser
observation releases only that browser's lease and reaches the authentication-
loss monitor; it does not expose a new command or enable transmit. The admin
session grid displays this projection as `TX LIFECYCLE`.

Phase 2C adds no browser message. The `txLifecycle` projection now reports
whether its one-second watchdog is running, its evaluation sequence and last
evaluation time, per-boundary fresh/stale flags, and a current authority reason.
A tracked lease requires an exact browser observation within six seconds, exact
FLEX-handle heartbeat within ten seconds, and gateway observation within ten
seconds. Explicit engine/gateway disconnect or a stale boundary releases only
the lifecycle's exact tracked lease. Fresh observations after revocation never
restore that lease. The watchdog remains in-process and command-incapable; it is
not the independent emergency-unkey boundary.

Phase 2D adds a separate local process protocol without changing the browser
protocol. `AetherSDR.TxWatchdog --stdio` consumes one JSON object per line, with
a maximum of 4096 characters and protocol version 1. It supports only:

```json
{"protocolVersion":1,"requestId":"status-1","type":"status"}
{"protocolVersion":1,"requestId":"register-1","type":"register","sequence":1,
 "identity":{"radioId":"RADIO-A","sessionId":"session-a",
 "browserClientId":"browser-a","gatewayInstanceId":"gateway-a",
 "engineInstanceId":"engine-a","connectionClientId":"connection-a",
 "leaseId":"lease-a","stationClientHandle":305441741}}
```

`heartbeat` and `disconnect` use the same exact identity object and a strictly
increasing positive sequence. Status carries no identity or sequence. Unknown
or duplicate properties, malformed JSON, unsupported versions, oversized
messages, mismatched identity, stale sequence, and heartbeat after disconnect
are rejected. Responses always project a Disarmed state, unavailable radio
command transport, unavailable arming, process instance ID, registration and
connection state, a `leaseBound` boolean, last accepted sequence, and last
observation. They never echo the opaque lease ID or the full authority identity. There is no arm,
lease mutation, key, unkey, timer, persistence, or radio command message. A process
restart creates a new host instance and an empty Disarmed snapshot.

Phase 2E keeps the browser protocol unchanged and makes the local process
protocol an active per-session supervision boundary. The gateway launches one
child for each isolated radio session and requires the startup `status` response
to be empty, sequence zero, and exactly `Disarmed`. A complete current authority
tuple sends `register`; later exact browser, engine, and gateway observations
send `heartbeat`. Lease release, authority loss, or identity change sends
`disconnect` and replaces the child with a new empty process. Session disposal
terminates the child directly and removes it from aggregate health. The gateway
never sends an authority-bearing request after a child restart until
a new exact lease is observed.

Responses are parsed under the same 4096-character limit and strict unique-
property rules as requests. The response request ID must match. Any malformed,
oversized, inconsistent, non-Disarmed, command-capable, or arming-capable
response fails closed. Child exit or IPC failure reports loss to the existing
lifecycle immediately, releases only that lifecycle's tracked lease, and starts
a bounded asynchronous retry. A ready response from the replacement process is
diagnostic only and cannot restore the released lease.

Admin-only `GET /api/admin/diagnostics/health` additionally reports
`txGateLifecycleRegistered=true`, `txLifecycleWatchdogRegistered=true`,
`txBrowserIntentProtocolVersion=1`,
`txBrowserIntentValidationRegistered=true`,
`txBrowserIntentCommandTransportRegistered=false`,
`txIndependentWatchdogHostPackaged=true`,
`txIndependentWatchdogSupervisionRegistered=true`, a supervised Disarmed state,
per-session running/connected/registered-identity counts, cumulative restart
count, `txIndependentWatchdogCommandTransportRegistered=false`,
`txIndependentWatchdogArmingAvailable=false`,
`txProductionCommandTransportRegistered=true`,
`txProductionCommandTransportConfiguredEnabled=false`,
`txProductionCommandTransportAllowedRadioCount=0`,
`txProductionCommandTransportAvailable=false`,
`txProductionCommandTransportSetTransmitAvailable=false`,
`txProductionCommandTransportWebSocketCallerRegistered=false`,
`txCommandTransportRegistered=true`,
`txCommandTransportAvailable=false`, and
`txSafetySupervisorArmingAvailable=false`. With browser TX leases disabled, the
deployment gate requires zero registered watchdog identities. The Admin
`txLifecycle.independentWatchdog` projection includes process ID, host instance,
IPC connection, registration, lease-bound state, last sequence, restart count,
last observation, and error. These are read-only diagnostics and are never
accepted from browser input.

Phase 2F introduced browser TX protocol version 1 for ownership and
validation-only deliberate intent; Phase 2Z upgrades the current browser TX wire
contract to version 2. Every current TX request has a positive JavaScript-safe
integer `id`, `protocolVersion:2`, and a strictly increasing positive
JavaScript-safe integer `sequence` scoped to the current admitted WebSocket. A
reconnect starts a new sequence at one and the browser discards every prior
opaque lease secret. The gateway rejects non-object roots, unknown or duplicate
properties, missing/defaulted fields, stale sequences, and replayed intent IDs
before a lease or authority operation. It keeps at most 64 intent IDs per
connection. The browser keeps at most 16 unanswered TX requests and refuses to
create an intent ID when a cryptographic random source is unavailable.

The browser may send:

```json
{"id":1,"cmd":"hello","protocolVersion":0}
{"id":2,"cmd":"subscribe"}
{"id":3,"cmd":"intent","action":"slice.set","selector":"A",
 "values":{"frequencyHz":14274000,"mode":"DIGU"}}
{"id":4,"cmd":"intent","action":"slice.create","selector":"",
 "values":{"frequencyHz":14300000,"mode":"USB","panId":"0x40000000"}}
{"id":5,"cmd":"intent","action":"slice.remove","selector":"B","values":{}}
{"id":6,"cmd":"intent","action":"pan.set","selector":"0x40000000",
 "values":{"centerFrequencyHz":14050000,"fftAverage":35,
           "framesPerSecond":15,"minDbm":-130,
           "wnbEnabled":true,"wnbLevel":50}}
{"id":7,"cmd":"intent","action":"pan.create","selector":"",
 "values":{"centerFrequencyHz":7074000}}
{"id":8,"cmd":"intent","action":"pan.remove",
 "selector":"0x40000001","values":{}}
{"id":9,"cmd":"ping"}
{"id":10,"cmd":"tx.acquire","protocolVersion":2,"sequence":1,"seconds":10}
{"id":11,"cmd":"tx.renew","protocolVersion":2,"sequence":2,
 "leaseId":"0123456789abcdef0123456789abcdef","seconds":10}
{"id":12,"cmd":"tx.intent","protocolVersion":2,"sequence":3,
 "leaseId":"0123456789abcdef0123456789abcdef",
 "intentId":"63b5e3e4-a3ac-45fd-8857-90387a00a50a",
 "action":"mox.set","values":{"enabled":true}}
{"id":13,"cmd":"tx.heartbeat","protocolVersion":2,"sequence":4,
 "leaseId":"0123456789abcdef0123456789abcdef"}
{"id":14,"cmd":"tx.release","protocolVersion":2,"sequence":5,
 "leaseId":"0123456789abcdef0123456789abcdef"}
{"cmd":"client.visibility","visible":false}
```

Allowed slice fields are `frequencyHz`, `mode`, `filterLowHz`,
`filterHighHz`, `afGain`, `audioPan`, `squelch`, `squelchEnabled`,
`audioMute`, `agcMode`, `agcThreshold`, `rxAntenna`, `daxChannel` (0-8),
the DSP toggles `nb`,
`nr`, `anf`, `nrl`, `nrs`, `rnn`, `nrf`, `anfl`, `anft`, and their supported
level fields. All ranges and enumerated values are validated at the gateway
boundary. `squelchEnabled` controls the radio gate independently from its
remembered threshold. Active-slice focus is browser-local and is never sent as
a radio property. `FlexRx` maps the supported intents, plus slice
create/remove, to receive-only SmartSDR commands and refreshes authoritative
slice status after accepted changes. Transmit-shaped intents and properties
remain rejected.

The `tx.acquire`, `tx.renew`, and `tx.release` messages manage only the
single-radio ownership lease. Durations are explicit whole seconds from 1
through 15; there is no default. Renew and release require the exact 32-character
lowercase hexadecimal opaque lease ID returned only to its holder. Acquisition
requires the dedicated `Radio:BrowserTxLeaseEnabled` server switch, current
authentication with transmit/admin role, the exact current WebSocket, a connected
radio session, fresh radio-authoritative idle occupancy, and no lease held by
another browser. `Radio:AllowTransmit` alone does not enable lease acquisition or
keying. Renewal additionally requires either fresh idle occupancy or fresh proof that
the same protected AetherSDR handle is the sole active TX owner. When the
production lifecycle is registered, the same exact fresh lease-bound watchdog
identity and transaction authority are required; a correctly armed watchdog is
expected during active TX. Loss of any boundary refuses renewal and releases
only that exact lease with reason `renewal-authority-lost`. The browser renews before
expiry, releases on deliberate page exit where possible, and always discards the
secret on disconnect. A rejected renewal, unsupported lease-event protocol, or
missing exact renewal response before the current expiry also discards the
local secret. Server disconnect and lease-expiry handling remain authoritative.

`tx.intent` supports only `mox.set`, `ptt.set`, `tune.set`,
`microphone.set`, and `cw.send`. The first four accept exactly one Boolean
`enabled` value. CW accepts exactly one conservative printable ASCII `text`
value from 1 through 32 characters; quote, backslash, and control characters are
rejected. Each request carries a unique bounded `intentId` and the
exact lease ID. The server re-derives authentication, role, current connection, radio state,
exact lifecycle lease/connection/FLEX handle, and the registered watchdog epoch.
Key requires fresh idle occupancy; unkey during active TX requires fresh proof
that the exact protected AetherSDR handle is the sole owner. Browser-supplied
identity or capability assertions are not accepted.

A fully valid Phase 2F intent returns `validated:true`,
`outcome:"transport-unavailable"`, and `ok:false`. This means deliberate intent
and exact ownership were proven, but no command was executed. The method never
invokes the hidden command gate or a radio transport. Lease, authentication,
connection, occupancy, lifecycle, expiry, and replay failures return
`validated:false`. Accepted requests always repeat their protocol sequence;
parse failures repeat it only when the incoming sequence itself is a valid
positive JavaScript-safe integer. Responses include freshly derived capability
state. `keyingAvailable`,
`microphoneAvailable`, `tuneAvailable`, and `cwAvailable` remain false,
`snapshot.canTransmit` remains false, and production still has no key, unkey,
TUNE, CW, or microphone-audio radio path.

Phase 2G adds an internal station-local command envelope protocol version 1. It
is not a browser, HTTP, WebSocket, or AetherRemote wire contract. The signed
payload is deterministic binary data with length-prefixed UTF-8 identifiers and
big-endian integers. It binds, in order: protocol version, signing key ID,
canonical command UUID, positive monotonic sequence, issue and expiry times,
station ID, radio ID, web session ID, browser client ID, opaque lease ID,
gateway instance ID, engine instance ID, protected FLEX client handle,
`SetTransmit`, and the Boolean enabled value. The signature is ECDSA P-256 with
SHA-256 in fixed-width IEEE P1363 form and is represented as base64url only at
the object boundary.

An envelope may live for at most 15 seconds, may be issued at most 5 seconds in
the future, and may be at most 30 seconds old. Before any adapter invocation the
station revalidates the exact station/radio/session/browser/lease/gateway/engine/
handle binding, lease expiry, fresh authentication and lifecycle observations,
fresh radio-authoritative idle occupancy, exclusive Local PTT authority for the
protected handle, and an exact freshly Armed safety-supervisor record. Sequence
values are consumed only after all validation and capability checks succeed;
replays and stale values fail closed. Audit history is capped at 256 records and
contains a short SHA-256 lease fingerprint rather than the lease secret or
signature.

Production capability negotiation reports protocol version 1 and
`boundaryRegistered:true`, while `boundaryEnabled`,
`commandAdapterRegistered`, `armingAvailable`, and `setTransmitAvailable`
remain false. Phase 2H adds a station-scoped trust ring that may independently
make `signatureVerificationAvailable:true` after reviewed configuration loads at
least one exact ECDSA P-256 public key. This readiness bit does not make an
envelope reachable and is not combined with the disabled boundary or unavailable
adapter to manufacture command capability.

`StationTxCommandTrust` accepts at most four exact key ID/path entries. Key IDs
are case-sensitive canonical ASCII tokens. Each absolute path must contain no
relative segments and must name a bounded regular, non-symlink UTF-8 file in a
regular, non-symlink containing directory. On Unix, neither the file nor that
directory may be writable by group or other users. The file must contain exactly
one `PUBLIC KEY` PEM block whose decoded SubjectPublicKeyInfo is ECDSA P-256;
private keys, other curves, duplicate IDs or paths, extra blocks or trailing
data, malformed UTF-8, unknown configuration properties, and oversized files
fail startup. Invalid key IDs are rejected without reflecting their untrusted
text into errors. Multiple keys support a bounded rotation window; envelope `keyId` selects exactly one verifier and an
unknown or mismatched key still fails signature validation.

Default production configuration keeps trust verification disabled with zero
keys, so `signatureVerificationAvailable` is false. The independent watchdog and
remote gateway have no reference to the adapter interface or an envelope-submit
method and cannot bypass the safety-supervisor validation.

Phase 2I adds an internal station-local signing authority for constructing this
same version-1 envelope, not a new wire protocol. `StationTxCommandSigning`
accepts one enable bit, one canonical key ID, and one absolute canonical private
key path. A configured key is loaded even while signing is disabled. The bounded
regular, non-symlink file must be exact UTF-8 with one unencrypted PKCS#8
`PRIVATE KEY` PEM block for ECDSA P-256; on Unix it must be mode 0400 or 0600,
and its regular immediate containing directory cannot be writable by group or
other users. Public-only or encrypted keys, unsupported curves, path indirection,
extra blocks, trailing data, malformed UTF-8, unknown properties, and unsafe
permissions fail startup.

A signing request cannot supply protocol version, key ID, command UUID,
sequence, issue time, expiry, or signature. It supplies only the exact server-
owned station/radio/session/browser/lease/gateway/engine/FLEX-handle tuple,
`SetTransmit`, and the Boolean value. The authority supplies protocol version 1,
a canonical random command UUID, a strictly increasing process-local sequence,
the current UTC issue time, a fixed five-second expiry, the configured key ID,
and an ECDSA P-256/SHA-256 fixed-width P1363 signature encoded as unpadded
base64url. Signing is serialized with key disposal under the same lock.

Production health reports signing enablement, whether a key was configured,
and signing availability. The authority is resolved only for startup validation
and is not injected into a radio session, command boundary,
browser/HTTP/WebSocket route, AetherRemote transport, watchdog, or timer.
Therefore even `signingAvailable:true` cannot enable the still-disabled
boundary, unavailable adapter, arming, or set-transmit path.

Phase 2J adds no wire message. It defines an internal station-local envelope
coordination contract with one `StationTxCommandEnvelopeCoordinator` submission
enable bit. The request contains exactly one `StationTxValidatedOperatorIntent`
and one server-owned `StationTxCommandAuthority`; it cannot contain a prebuilt
envelope. The intent carries a canonical ID, positive browser intent sequence,
MOX or PTT kind, Boolean enabled value, and observed-at timestamp. It is accepted
only within five seconds of observation with at most one second of future clock
skew. TUNE, microphone, and CW do not map to the version-1 SetTransmit command.

The coordinator derives the signing request's station, radio, session, browser,
lease, gateway, engine, FLEX handle, action, and Boolean value from the authority
and validated intent. It first requires submission enablement, signer and trust
verifier readiness, an enabled caller-owned command boundary, registered
adapter, arming, and SetTransmit capability. The generated unpadded base64url
signature must decode canonically to exactly 64 P-256 P1363 bytes and verify
against the coordinator's trust ring before the same envelope is passed to the
boundary for independent protocol, signature, authority, freshness, safety,
replay, and adapter validation.

Intent replay state is process-local and bounded to 256 live intent IDs and 128
live session/browser owners. An intent ID is consumed once, and each owner must
advance its intent sequence strictly. Entries expire only after the complete
freshness/skew window. Once consumption occurs, cancellation, signing failure,
boundary rejection, adapter rejection, or unknown adapter outcome cannot cause
an automatic retry; another operation requires a new deliberate intent ID and
higher browser sequence.

Phase 2K also adds no wire message. Each radio session owns one internal
`StationTxCommandSessionComposition`. Its request contains only the current
WebSocket connection ID, the already-parsed browser intent, the positive
JavaScript-safe browser intent sequence, and the server observation time. The
composition resolves every `StationTxCommandAuthority` field from the production
lifecycle: gateway station identity, canonical radio, session, stable
browser-page identity, exact active connection-owned lease and expiry, engine
instance, FLEX handle, authentication and freshness flags, radio-authoritative
occupancy, and safety snapshot. The browser cannot send this request or provide
an authority field.

`RadioSessionRegistry` passes the station-scoped coordinator through an internal
submitter interface to the lifecycle composition. The composition attaches the
session's existing command boundary, but `RadioCoordinator`, the WebSocket
endpoint, AetherRemote, watchdog, and timers receive no submitter or submission
method. A replaced connection, missing/mismatched/expired lease, stale authority,
missing FLEX handle, unsupported non-MOX/PTT action, missing Boolean, invalid
sequence, cancellation, or resolver failure stops before forwarding. A forwarded
fault or unknown outcome is recorded and never retried automatically.

Production health reports
`txStationCommandEnvelopeCoordinatorRegistered:true`,
`txStationCommandSessionCompositionRegistered:true`, and
`txStationCommandSessionCompositionBrowserIngressRegistered:false`.
Submission remains disabled and unavailable. Per-session Admin diagnostics show
coordinator/boundary attachment, authority availability, attempt/forward counts,
last bounded outcome, and fail-closed reason without lease IDs, key paths,
signatures, or key material. The existing
`txStationCommandEnvelopeSubmissionRegistered:false` field continues to mean no
browser, HTTP, WebSocket, AetherRemote, watchdog, or other externally reachable
envelope-submission route exists.

Phase 2L adds no wire message. Each production lifecycle constructs one internal
`StationTxCommandAdapterComposition` and passes it to the session's signed
command boundary as the `IStationTxCommandAdapter`. Neither the session registry
nor browser control types accept the internal executor interface.

Phase 2M also adds no wire message. Each lifecycle creates one
`StationTxCommandGateExecutor` around its existing per-session
`StationTxCommandGate` and supplies that executor to the adapter composition. A
validated `StationTxCommandAction.SetTransmit` with `Enabled:true` maps only to
`RequestKeyAsync(leaseId, sessionId, browserClientId)`; `Enabled:false` maps only
to the matching `RequestUnkeyAsync`. No other action is accepted. The executor
performs one gate call and has no retry behavior.

The executor capability snapshot has only registered, arming, SetTransmit, and
bounded reason fields. Production reports the executor and adapter registered,
but the gate was created with `allowTransmit:false` and an unavailable command
transport, so arming and SetTransmit remain false. Health reports
`txStationCommandAdapterCompositionRegistered:true`,
`txStationCommandAdapterExecutorAttached:true`,
`txStationCommandAdapterExecutorRegistered:true`,
`txStationCommandGateExecutorRegistered:true`,
`txStationCommandGateExecutorTransmitEnabled:false`,
`txStationCommandGateExecutorCommandTransportAvailable:false`,
`txStationCommandGateExecutorSetTransmitAvailable:false`,
`txStationCommandGateExecutorBrowserIngressRegistered:false`, and
`txStationCommandAdapterCompositionBrowserIngressRegistered:false`.

Before the executor call, the composition re-resolves current
`StationTxCommandAuthority` from the lifecycle and compares every validated
command identity exactly. It also rechecks bounded command lifetime, lease
expiry, authentication/freshness, and a matching Armed safety heartbeat. A key
request requires fresh idle occupancy and exclusive Local PTT ownership for the
protected handle. An unkey request permits only already-idle state or fresh
proof that the protected handle is the sole AetherSDR TX owner. The signed
boundary independently applies the same key/unkey occupancy distinction before
calling the adapter. External, ambiguous, stale, or replaced ownership is
rejected before the gate.

Gate success becomes an accepted adapter result. Gate rejection remains a known
rejection. `key_command_outcome_unknown` and
`unkey_command_outcome_unknown` remain unknown adapter outcomes so the gate can
retain guarded intent and reconcile against later radio state. Cancellation and
exceptions propagate after bounded diagnostics are updated, and neither the
composition nor executor retries automatically.

Phase 2N adds no wire message. Each lifecycle constructs one internal
`StationTxSafetyArmComposition` around its existing
`StationTxSafetySupervisor`. Typed arm and heartbeat requests contain only the
current connection ID and a timeout bounded to the supervisor's 250 ms through
5 second range. A typed abort request contains only the current connection ID
and a bounded reason. All station/radio/session/browser/lease/gateway/engine/
FLEX-handle fields are re-resolved from lifecycle-owned authority and cannot be
supplied by a caller.

Before one optional authority call, the composition rejects missing or replaced
connections, expired leases, stale authentication or observations, missing
handles, stale occupancy, radio mismatch, and non-exact ownership. Arm requires
fresh idle occupancy, exclusive Local PTT for the protected handle, and a
Disarmed supervisor. Heartbeat requires the exact Armed identity and a current
heartbeat deadline; idle heartbeat also requires Local PTT to remain exact,
while active heartbeat requires the exact single AetherSDR TX owner. Abort
requires the exact arm and permits only idle or that exact TX owner. The optional
`IStationTxSafetyArmAuthority` receives the resolved authority and operation,
then the composition forwards at most one supervisor call. Rejection,
cancellation, exception, or unknown supervisor state never creates an automatic
retry.

Phase 2O also adds no wire message. Each lifecycle attaches one internal
`StationTxSafetyArmAuthority` to the Phase 2N composition. On every authorization
it independently reads the signed boundary, adapter composition, gate executor,
command gate, safety supervisor, and a newly resolved current authority. The
supplied and current station/radio/session/browser/lease-expiry/gateway/engine/
FLEX-handle tuples must match exactly. Arm additionally requires the complete
normal command path, an idle gate, fresh idle occupancy, exclusive Local PTT,
and a Disarmed supervisor. Heartbeat requires that command path to remain ready,
the exact current arm and deadline, and idle/Local-PTT or exact active TX
ownership. Abort does not require the normal command path to remain available,
but still requires the exact active arm and ownership-safe idle or exact
single-owner AetherSDR TX state. One request causes one dependency read and no
retry.

Production exposes no submission or control route to this composition. Health
reports
`txStationCommandSafetyArmCompositionRegistered:true`,
`txStationCommandSafetyArmAuthorityAttached:true`,
`txStationCommandSafetyArmAuthorityRegistered:true`,
`txStationCommandSafetyArmAuthorityBoundaryEnabled:false`,
`txStationCommandSafetyArmAuthorityCommandTransportAvailable:false`,
`txStationCommandSafetyArmAuthoritySetTransmitAvailable:false`,
`txStationCommandSafetyArmAuthorityBrowserIngressRegistered:false`,
`txStationCommandSafetyArmAvailable:false`,
`txStationCommandSafetyHeartbeatAvailable:false`,
`txStationCommandSafetyAbortAvailable:false`, and
`txStationCommandSafetyArmCompositionBrowserIngressRegistered:false`.
Per-session diagnostics publish the authority dependency matrix and bounded
attempt/accepted/rejected counters separately from the composition's bounded
attempt/forward/accepted/rejected counters. Neither adds lease IDs or ownership
secrets to Admin text.

Phase 2P adds no wire message and no browser command. Each lifecycle constructs
one internal `StationTxCommandTransactionComposition` above the existing
safety-arm and command-session compositions. A typed submit request contains
only the current connection ID, one parsed MOX/PTT Boolean intent, its positive
browser sequence, server observation time, and a heartbeat timeout within the
existing 250 ms through 5 second range. Heartbeat and abort requests contain only
the exact current connection ID plus a bounded timeout or reason. All authority,
safety, envelope, signature, command, and FLEX fields remain server-owned.

For a true command, the transaction resolves current authority, forwards one
arm, re-resolves and compares the stable station/radio/session/browser/lease-
expiry/gateway/engine/FLEX-handle tuple, requires the new Armed safety identity
to match, and forwards one signed command. A known command rejection forwards
one ownership-safe abort cleanup. `adapter_outcome_unknown`, cancellation, or
exception retains the arm, marks reconciliation required, and never retries.

For a false command, an exact active transaction is required. The transaction
forwards one heartbeat, one signed unkey, and—only after confirmed acceptance—
one arm cleanup. Known unkey rejection retains the arm. Unknown command or
cleanup outcome retains it for reconciliation. Operations are serialized and a
second key while active is rejected.

Production exposes no external operation route. Phase 2Q removes the older
internal lifecycle method that returned a command-session composition result.
The lifecycle now accepts only the typed transaction submit, heartbeat, and abort
records and delegates them directly to the transaction composition. No browser,
WebSocket, HTTP, AetherRemote, registry, coordinator, watchdog, reconnect, or
timer type receives those records or results.

Health reports
`txStationCommandTransactionCompositionRegistered:true`,
`txStationCommandTransactionLifecycleBoundaryRegistered:true`,
`txStationCommandDirectSessionSubmissionRegistered:false`,
`txStationCommandTransactionSafetyArmAttached:true`,
`txStationCommandTransactionCommandCompositionAttached:true`,
`txStationCommandTransactionKeyAvailable:false`,
`txStationCommandTransactionHeartbeatAvailable:false`,
`txStationCommandTransactionUnkeyAvailable:false`,
`txStationCommandTransactionAbortAvailable:false`,
`txStationCommandTransactionActive:false`,
`txStationCommandTransactionReconciliationRequired:false`,
`txStationCommandTransactionBrowserIngressRegistered:false`, and
`txStationCommandTransactionLifecycleBrowserIngressRegistered:false`.

Phase 2R adds an internal `BrowserTxTransactionIngressRequest` containing the
current connection ID, the already parsed `BrowserTxRequest`, and the exact
server-produced `BrowserTxIntentResult`. The adapter requires `Validated:true`,
`Ok:false`, the exact `transport-unavailable` validation-only outcome, current
intent-validation capability, and exact sequence, intent ID, and action equality.
Validation older than two seconds or more than one second in the future is
rejected. Only Boolean `mox.set` and `ptt.set` are accepted. The transaction
observation time comes from the server validation result and the heartbeat
timeout is fixed server-side at five seconds. TUNE, microphone, CW, missing
values, mismatches, and unavailable key or unkey capability are rejected without
forwarding. A forwarded request is sent
once; accepted, rejected, and unknown transaction outcomes are preserved with no
retry.

Production sets ingress execution false and does not provide a WebSocket, HTTP,
AetherRemote, watchdog, reconnect, or timer caller. Health reports
`txBrowserTxTransactionIngressRegistered:true`,
`txBrowserTxTransactionIngressExecutionEnabled:false`,
`txBrowserTxTransactionIngressBoundaryAttached:true`, key and unkey availability
false, and every caller-registration field false. Per-session diagnostics add
only bounded state, attempt/forward/outcome counts, and a bounded reason; active
identity and lease values are not rendered in Admin text.

Phase 2S adds no wire message. An internal
`StationTxProductionReadinessDiagnostics` object reports `Registered`, one
`Ready` boolean, the first blocking `Reason`, the Boolean state of every existing
infrastructure prerequisite, and an ordered `MissingPrerequisites` list. Health
publishes `txProductionReadinessPolicyRegistered:true`, readiness false, reason
`transmit-disabled`, the complete missing list, lifecycle ingress registered,
and WebSocket caller registered false. Each session snapshot carries the same
policy result. The lifecycle's new internal
`ExecuteBrowserTxTransactionIngressAsync` operation accepts only
`BrowserTxTransactionIngressRequest` plus cancellation and returns only
`BrowserTxTransactionIngressResult`; no HTTP or WebSocket contract exposes it.

Phase 2T adds no browser or network wire message. The internal
`IStationTxCommandTransport.SetTransmitAsync` contract now requires
`Enabled`, the exact non-zero `ExpectedClientHandle`, and cancellation. The gate
passes the handle it stored in the guarded intent. The production FLEX router's
`SendForClientAsync` compares that expected handle with the currently attached
handle while holding the control-session lock, then captures that exact control
session. A replaced handle is a known rejection before any command write;
I/O and timeout after a write begins remain unknown outcomes. No automatic retry
is added.

`StationTxCommandTransport` configuration has `Enabled`, bounded
`AllowedRadioIds`, and `CommandTimeoutMilliseconds`. Defaults are false, empty,
and 2000. `Enabled:true` with an empty allowlist, duplicate or malformed IDs,
more than 16 IDs, or a timeout outside 250-5000 ms fails startup. Only a local
`FlexRx` session can become eligible. Health reports
`txProductionCommandTransportRegistered:true`, configured enabled false,
allowed-radio count zero, timeout 2000, availability and SetTransmit false,
reason `transport-disabled`, and WebSocket caller false. The compatibility field
`txCommandTransportRegistered` becomes true while the separate
`txCommandTransportAvailable` field remains false.

Each session snapshot includes bounded production-transport registration,
eligibility, allowlist, channel/handle availability, attempt/forward/key/unkey/
accepted/rejected/unknown counters, last operation/outcome/reason, and no radio
allowlist values or command text.

Phase 2U adds no browser wire message. The internal emergency interface becomes
`RequestUnkeyAsync(ExpectedProtectedClientHandle, CancellationToken)` and has no
key or Boolean transmit method. `StationTxEmergencyUnkeyTransport` has its own
disabled `Enabled`, bounded exact `AllowedRadioIds`, and bounded command timeout.
Health reports registration true, configured enabled false, allowlist count zero,
timeout 2000, availability/unkey false, reason `transport-disabled`, and
WebSocket caller false. Each session adds bounded emergency eligibility,
channel/handle state, attempt/forward/outcome counters, and reason.

`IndependentTxWatchdog` adds disabled radio-command transport configuration with
an exact radio allowlist and bounded timeout. Only an eligible local `FlexRx`
session may launch the child with the strict `--unkey-enabled`, radio ID, IPv4
host, port, and timeout arguments. Phase 2V upgrades the watchdog protocol to version 2. It permits only `status`,
`register`, `arm`, `heartbeat`, `disarm`, and `disconnect`; there remains no
`key`, `unkey`, lease, reset, retry, or arbitrary-command request. `arm` requires
an exact registered identity, a strictly increasing sequence, and a bounded
`heartbeatTimeoutMilliseconds` from 250 through 5000. An armed heartbeat carries
a fresh bounded timeout; a Disarmed registration heartbeat carries none and
cannot arm or renew safety authority. Disconnect preserves an active arm until
its deadline.

The child TCP adapter still exposes one unkey-only operation. It sends the fixed
`C1|sub client all` and `C2|sub tx all` observers, then requires fresh interlock
state naming the exact protected handle as current TX owner before sending the
single fixed `C3|xmit 0`. Idle state succeeds without C3. A different, missing,
ambiguous, or unconfirmed owner rejects before C3. Deadline expiry performs at
most one attempt. After C3, a matching successful command response and fresh
`READY` or `RECEIVE` interlock status are both required before the child returns
Disarmed. Known rejection, missing idle confirmation, or another unknown outcome
returns `ReconciliationRequired` with no retry.

Protocol-v2 snapshots add `armed`, arm/heartbeat/deadline timestamps, bounded
heartbeat timeout, unkey attempt/accepted/rejected/unknown counters, and the last
bounded outcome/reason. The full authority identity remains non-serialized.
Health adds protocol version, explicit arming registration/configuration,
armed/reconciliation process counts, and aggregate unkey attempts. Production
defaults require arming configured false, arming unavailable, zero armed
processes, zero reconciliation-required processes, zero unkey attempts, and no
WebSocket caller.

Phase 2W adds no browser wire message. Each lifecycle snapshot adds
`productionActivation` with `registered`, `readinessEvaluationAttached`,
`activationAvailable`, `reason`, and the complete nested readiness result. The
composition is diagnostic-only and has no callable activation operation. Health
adds `txProductionActivationCompositionRegistered:true`,
`txProductionActivationAvailable:false`, reason `transmit-disabled`, and
`txProductionActivationCallerRegistered:false` for the default production
configuration.

Phase 2X adds no browser wire message. Configuration adds the nested
`StationTxProductionActivation` object with one Boolean `Enabled` field. Health
adds configuration registration, request, validity, reason, and the deterministic
missing-prerequisite list, plus confirmation that the configuration interlock is
attached to the activation composition. Lifecycle `productionActivation` adds
`configurationInterlockAttached`, `activationRequested`, `configurationValid`,
and the complete nested configuration result. Default reason becomes
`activation-not-requested`; the nested readiness result still reports
`transmit-disabled`. An invalid requested configuration fails startup before any
listener or session is created.

Phase 2Y also adds no browser wire message. Lifecycle `productionActivation`
adds `activationPlanAttached`, `activationPlanAvailable`,
`activationPlanApplied`, and a nested `plan` diagnostic. The nested immutable
plan contains only four Boolean switch intentions:
`commandBoundaryEnabled`, `commandGateTransmitEnabled`,
`browserTransactionIngressExecutionEnabled`, and
`browserKeyingCapabilityEnabled`. Default production reports the planner
registered and attached, plan unavailable and unapplied, every switch false,
reason `activation-not-requested`, and no plan caller.

Phase 2Z upgrades the browser TX protocol to version 2 and adds one strict
message:

```json
{"id":14,"cmd":"tx.heartbeat","protocolVersion":2,"sequence":5,
 "leaseId":"0123456789abcdef0123456789abcdef"}
```

Version 2 is required for acquire, renew, release, intent, and heartbeat; version
1 TX envelopes fail closed. `tx.heartbeat` accepts exactly `id`, `cmd`,
`protocolVersion`, `sequence`, and the lowercase 32-character opaque lease ID.
It carries no identity, timeout, action, or capability assertion. The server
re-derives the authenticated current connection and exact active transaction,
then renews the local and independent safety deadline for the fixed five-second
maximum. The browser sends at most one heartbeat request at a time every two
seconds only after a radio-confirmed key result. Ordinary WebSocket `ping`, lease
renewal, reconnect, lifecycle observation, and status traffic do not count.

Lifecycle `productionActivation` also adds `activationBindingAttached`,
`activationBindingApplied`, and a nested `binding` diagnostic. The binding
contains the same four Boolean switches as the plan and is applied only as an
all-or-nothing set for an eligible local `FlexRx` session. With the binding and
dynamic readiness complete, Boolean `mox.set` and `ptt.set` intents delegate
through browser transaction ingress. Key requires fresh idle occupancy; unkey
and lease renewal during active TX require fresh proof that the exact protected
AetherSDR FLEX handle is the sole owner. External, stale, ambiguous, replaced,
or ownerless TX never receives browser unkey authority. When executable MOX/PTT
capability is projected, the browser disables the older **VALIDATE ONLY** selector
so an operator cannot mistake an executable `tx.intent` for a dry run.
`tune.set`, `microphone.set`, and `cw.send` remain non-executable in Phase 2Z.

Default production still reports the binding unapplied, all four switches false,
the command gate transmit-disabled, browser ingress execution-disabled, and no
WebSocket transaction caller. The normal web binary contains exactly one
reviewed `xmit 1`, one runtime-deduplicated reviewed `xmit 0`, and type markers
for both the primary and emergency transports. The watchdog binary contains
exactly one reviewed `xmit 0` and zero `xmit 1`; HIL process, CWX, and TX-audio
markers remain forbidden in both production artifacts.

`client.visibility` accepts only a JSON boolean. A hidden browser keeps its
authenticated WebSocket, radio session, text responses, snapshots, presence,
and radio-authoritative state, but the gateway does not enqueue spectrum or
receive-audio binary frames for it. Binary frames already queued when the
visibility change arrives are discarded before WebSocket delivery. Setting
`visible` back to `true` resumes fresh frames; the browser re-baselines audio
sequence tracking before playback so the intentional pause is not reported as
network loss.

`pan.set` accepts `centerFrequencyHz`, `fftAverage` (0-100),
`framesPerSecond` (1-30), and `minDbm` (-200 through -1 and below the
radio-reported maximum), plus `wnbEnabled` and `wnbLevel` (0-100). The live
adapter maps them to the owned display-pan command. An in-span slice tune uses
`autopan=0`; an off-screen absolute tune
uses the radio's recentering tune form, and subsequent pan and slice status
remain authoritative.

Each snapshot contains the primary `panadapter` for compatibility and an
ordered `panadapters` collection. Every pan includes its radio display ID and
numeric VITA stream ID. `pan.create` creates and configures a radio-owned
panafall; `pan.remove` removes its slices and display streams, but refuses to
remove the final panadapter. The browser renders one selected pan at a time.

The server pushes:

```json
{"event":"changed","sessionId":"radio-1","model":"slice",
 "selector":"A","version":2,"changes":{"frequencyHz":14274000}}
{"event":"presence","clients":[{"userId":"...","displayName":"Operator",
 "roles":["Aether.Control"],"connectedAt":"...","connectionCount":2}]}
{"event":"snapshot","snapshot":{"connected":true}}
```

A snapshot also carries `connectionState` and `connectionError`. A
`radio-busy` state means the live radio rejected the GUI registration; the
gateway retries without estimating or overriding the radio's client limit.

## AetherRemote bootstrap and signed station updates

The public gateway bootstrap document at `GET /.well-known/aethersdr` is not a
station credential protocol. It contains only bounded non-secret release,
protocol, broker-route, enrollment, verification-key, installer, manifest, and
package metadata derived from the active locally verified signed release. The
one-time enrollment code is created through the protected Admin boundary and is
never embedded in this document, an installer URL, or an installation command.

A station link still uses the version-1 `aetherremote.station.v1` WebSocket
subprotocol. `station.hello` may additionally report `releaseIdentity` and
`stationEngineVersion`. A station advertising `release-update-v1` must provide
both exact values; a legacy station without the capability may omit them. Link
tokens continue to bind the exact advertised capability set.

A signed update request is fixed-purpose:

```json
{"type":"broker.release.update",
 "correlationId":"0123456789abcdef0123456789abcdef",
 "releaseIdentity":"aethersdr-8.5.0"}
```

The broker can send it only to a station whose authenticated link grants
`release-update-v1`. `correlationId` is one canonical 32-character lowercase
hex value and `releaseIdentity` accepts only the bounded canonical release
identity grammar. There is deliberately no URL, path, executable, shell,
service name, command, argument list, environment, or arbitrary payload field.

After the station has either started the requested signed release or restored
its prior release, it reports exactly one durable completion shape:

```json
{"type":"station.release.update-result",
 "correlationId":"0123456789abcdef0123456789abcdef",
 "releaseIdentity":"aethersdr-8.5.0",
 "succeeded":true,
 "outcome":"confirmed",
 "activeReleaseIdentity":"aethersdr-8.5.0",
 "rolledBack":false}
```

A rollback completion uses `succeeded:false`, `outcome:"startup-rollback"`,
`rolledBack:true`, and names the prior release in `activeReleaseIdentity`. The
broker accepts a result only when correlation, authenticated station, requested
release, and result shape match a bounded tracked request. It retains recent
completed request identity briefly so a reconnect may repeat the exact same
result. An altered duplicate or an untracked correlation is rejected.

For each accepted exact completion the broker sends:

```json
{"type":"broker.release.update-ack",
 "correlationId":"0123456789abcdef0123456789abcdef",
 "releaseIdentity":"aethersdr-8.5.0"}
```

The station does not discard its durable local completion merely because a
WebSocket send completed. It clears that pending completion only after this
application-level acknowledgement exactly matches the reported correlation and
target release and the station-local updater has durably acknowledged it.
This makes completion delivery idempotent across Agent or socket failure.

The Agent-to-root-updater local protocol is owner-scoped Unix IPC with fixed
messages only. Requests use `type:"local.release.update"`, exact correlation,
exact release identity, and one action from `apply`, `rollback`, `confirm`, or
`acknowledge`. `apply` consumes only the fixed private staging directory for the
correlation; the message cannot provide its path. `confirm` recovers the durable
successful or rollback completion after Agent restart. `acknowledge` marks that
completion durably acknowledged after the broker ACK; a repeated acknowledgement
is idempotent. A subsequent startup removes acknowledged completion evidence.
The root updater exposes no network transport or arbitrary command surface.

None of these messages grant radio command capability, browser TX authority,
TX lease ownership, watchdog arming, keying, or unkeying.

## M8G encrypted backup and operations contracts

### Encrypted backup file schema 1

A `.aebak` file is a local operator-controlled artifact, not an HTTP protocol.
All multibyte integers in its fixed header are little-endian:

| Offset | Size | Meaning |
|---:|---:|---|
| 0 | 8 | ASCII magic `AETHBKP1` |
| 8 | 4 | Backup schema version `1` |
| 12 | 4 | PBKDF2 iteration count `600000` |
| 16 | 16 | Random PBKDF2 salt |
| 32 | 12 | Random AES-GCM nonce |
| 44 | 8 | Ciphertext byte length |
| 52 | variable | AES-256-GCM ciphertext of the bounded Brotli-compressed JSON payload |
| end - 16 | 16 | AES-GCM authentication tag |

The 52-byte header is authenticated as AES-GCM associated data. The 32-byte key
is derived with PBKDF2-HMAC-SHA256 from the interactive passphrase and header
salt. Decryption rejects wrong magic/schema/iteration count, noncanonical or
oversized length, wrong passphrase, or any header/ciphertext/tag modification
before payload data is accepted.

The decrypted payload is strict schema-versioned JSON with bounded logical roots,
relative regular-file/directory entries, SHA-256 content digests, logical service
owners, setup revision/topology, current/rollback release identities, optional
installer-owned managed-proxy configuration, and human-readable external
dependency descriptions. Unknown JSON members, path escape, links/reparse points,
unsupported owners/modes, duplicate roots/entries, bad hashes/lengths, and excess
entry/byte counts are rejected. Numeric source-host UIDs/GIDs and absolute source
root paths are not backup authority.

### Admin operations HTTP contract

All routes below require `Aether.Admin`:

- `GET /api/admin/diagnostics/operations` returns passive schema-1 readiness,
  alerts, and aggregate metrics. It performs no outward probe.
- `POST /api/admin/diagnostics/operations/run` requires the normal AetherSDR
  antiforgery token and the `admin-operations` rate limit. It may probe only the
  persisted canonical HTTPS origin and fixed health, configured auth-callback,
  `/ws/radio`, and `/aetherremote/broker/station/v1` routes as applicable.
- `GET /api/admin/diagnostics/bundle` is `admin-operations` rate-limited and
  returns `application/zip` containing only the strongly redacted support
  projections described in `docs/OPERATIONS.md`.

`OperationsReadinessSnapshot` contains a schema version, observation time,
`ready`, whether active connectivity has been checked, bounded `checks`, bounded
`alerts`, and aggregate metrics. Check states are exactly `healthy`, `warning`,
`failed`, or `not-applicable`; alert severities are exactly `info`, `warning`, or
`critical`. Failed checks become critical alerts. No readiness or diagnostic
message grants radio authority, TX authority, release approval, service-control
authority, or arbitrary network-target selection.

The Setup Center does not import these normal-runtime services. Its existing
non-mutating preflight response gains `postInstallOperationalChecks`, a text-only
list of the fixed checks an administrator must perform after installation.

## M8H packaged-release acceptance contracts

M8H does not add a radio wire protocol. It tightens the installed release and
local updater contracts exercised around the existing signed manifest protocols.

Activation configuration-backup manifest schema `3` extends every authenticated
file/directory entry with the original Linux UID and GID. This metadata is valid
only for same-host activation rollback. The privileged fixed-purpose updater
reapplies and re-reads that ownership before a restored source is admitted.
Replacement-host backup/restore remains M8G schema 1 and deliberately maps logical
service owners instead of copying numeric IDs.

The physical activation-backup source count is two or three. Configuration and
state are always separate sources. When the configured secret directory is a
validated descendant of the state directory, its bytes and ownership are covered
by the state source and no overlapping secret root is created. A secret directory
outside state remains a third source. Migration and rollback plans must preserve
that exact source inventory.

The local release supervisor control endpoint remains AF_UNIX-only. On the
standalone host the updater service runs as root with primary group `aethersdr`;
its directory is mode `0770` and socket mode `0660`. The gateway/updater request
schema remains the existing fixed release-transaction schema and gains no path,
URL, executable, shell, service-name, radio, or TX field. The privileged process
controls installed system units rather than a user service manager. A successful
activation keeps the same supervisor process alive so the exact in-memory rollback
authority remains usable. If a later `prepare` arrives while that completed
transaction is still rollbackable, the supervisor returns one
`transactionAlreadyActive` reload-boundary report, exits without executing the new
prepare, and systemd `Restart=always` reloads the updater from the current immutable
release. The client waits until status shows the recovered completed transaction
with no reconstructed rollback authority, then retries that unchanged prepare once.
Completed rollback exits after its response; reconciliation and host-restart states
do not trigger this reload. In supervisor mode only the existing receive-only
`RemoteStationCatalogService` background observer is started, so Hybrid health can
read fresh loopback broker/station state without starting the web host or any radio
or TX hosted-service surface.

Persistent verified release bundles use one canonical on-disk shape for both
release installation and AetherRemote publication:

```text
<release-download-root>/<releaseIdentity>-<architecture>/
  release-manifest.json
  packages/
    <four signed package archives>
```

AetherRemote bootstrap therefore resolves `release-manifest.json`; it never
constructs a second architecture-suffixed manifest name inside that directory.

The M8H acceptance-only release identities/signatures are CI fixtures and are not
production release authority. Their deliberate failure variants modify only the
packaged `appsettings.json` of the component whose post-switch health must fail.
No acceptance message or artifact grants a radio command, TX lease, watchdog arm,
key/unkey operation, arbitrary remote command, or shell transport.

## Spectrum binary frame

All integer fields use little-endian byte order.

| Offset | Size | Meaning |
|---:|---:|---|
| 0 | 4 | ASCII magic `AETF` |
| 4 | 1 | Experimental version `0` |
| 5 | 1 | Spectrum frame version `2` |
| 6 | 2 | Unsigned bin count |
| 8 | 4 | Unsigned sequence |
| 12 | 8 | Signed center frequency in Hz |
| 20 | 4 | Unsigned panadapter VITA stream ID |
| 24 | `bins * 2` | Signed 16-bit bins in tenths of dBm |

The receiver rejects bin counts outside 64-8192 and frames whose exact size
does not match the declared count. The browser discards frames whose stream ID
does not match its selected panadapter. A slow visible browser drops old stream
frames through its bounded queue. A hidden browser receives no spectrum frames
until it becomes visible again. Version 1 frames without a stream ID remain
readable for compatibility with older local prototype builds.

This layout is deliberately simple and does not claim compatibility with
VITA-49 or the future AetherD binary data plane.

## Receive-audio binary frame

Receive audio uses a separate little-endian `AETA/v0` frame. The gateway
decodes the Flex `remote_audio_rx` VITA-49 stream and fans out one bounded PCM
stream to every browser. Browsers start playback only after the operator clicks
**PC Audio**, satisfying browser autoplay requirements.

| Offset | Size | Meaning |
|---:|---:|---|
| 0 | 4 | ASCII magic `AETA` |
| 4 | 1 | Experimental version `0` |
| 5 | 1 | Channel count `2` |
| 6 | 2 | Unsigned sample rate (`24000`) |
| 8 | 4 | Unsigned sequence |
| 12 | 4 | Unsigned stereo frame count |
| 16 | `frames * 4` | Interleaved signed 16-bit little-endian stereo PCM |
