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

`GET /healthz` additionally reports
`txGateLifecycleRegistered=true`, `txLifecycleWatchdogRegistered=true`,
`txBrowserIntentProtocolVersion=1`,
`txBrowserIntentValidationRegistered=true`,
`txBrowserIntentCommandTransportRegistered=false`,
`txIndependentWatchdogHostPackaged=true`,
`txIndependentWatchdogSupervisionRegistered=true`, a supervised Disarmed state,
per-session running/connected/registered-identity counts, cumulative restart
count, `txIndependentWatchdogCommandTransportRegistered=false`,
`txIndependentWatchdogArmingAvailable=false`,
`txCommandTransportRegistered=false`, and
`txSafetySupervisorArmingAvailable=false`. With browser TX leases disabled, the
deployment gate requires zero registered watchdog identities. The Admin
`txLifecycle.independentWatchdog` projection includes process ID, host instance,
IPC connection, registration, lease-bound state, last sequence, restart count,
last observation, and error. These are read-only diagnostics and are never
accepted from browser input.

Phase 2F adds a separate browser TX protocol version 1 for ownership and
validation-only deliberate intent. Every request has a positive JavaScript-safe
integer `id`, `protocolVersion:1`, and a strictly increasing positive
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
{"id":10,"cmd":"tx.acquire","protocolVersion":1,"sequence":1,"seconds":10}
{"id":11,"cmd":"tx.renew","protocolVersion":1,"sequence":2,
 "leaseId":"0123456789abcdef0123456789abcdef","seconds":10}
{"id":12,"cmd":"tx.intent","protocolVersion":1,"sequence":3,
 "leaseId":"0123456789abcdef0123456789abcdef",
 "intentId":"63b5e3e4-a3ac-45fd-8857-90387a00a50a",
 "action":"mox.set","values":{"enabled":true}}
{"id":13,"cmd":"tx.release","protocolVersion":1,"sequence":4,
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
keying. Renewal additionally requires fresh idle occupancy and, when the
production lifecycle is registered, the same exact fresh lease-bound Disarmed
watchdog authority. Loss of either boundary refuses renewal and releases only
that exact lease with reason `renewal-authority-lost`. The browser renews before
expiry, releases on deliberate page exit where possible, and always discards the
secret on disconnect. A rejected renewal, unsupported lease-event protocol, or
missing exact renewal response before the current expiry also discards the
local secret. Server disconnect and lease-expiry handling remain authoritative.

`tx.intent` supports only `mox.set`, `ptt.set`, `tune.set`,
`microphone.set`, and `cw.send`. The first four accept exactly one Boolean
`enabled` value. CW accepts exactly one conservative printable ASCII `text`
value from 1 through 32 characters; quote, backslash, and control characters are
rejected. Each request carries a unique bounded `intentId` and the
exact lease ID. The server re-derives authentication, role, current connection,
radio state, fresh idle occupancy, exact lifecycle lease/connection/FLEX handle,
and a connected registered Disarmed watchdog epoch. Browser-supplied identity or
capability assertions are not accepted.

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
command boundary as the `IStationTxCommandAdapter`. The composition delegates
only to an optional `IStationTxCommandAdapterExecutor`; normal production session
construction supplies `null`, and neither the session registry nor browser
control types accept that executor interface.

The executor capability snapshot has only registered, arming, SetTransmit, and
bounded reason fields. Composition registration does not imply adapter
registration. Without an attached registered executor,
`IStationTxCommandAdapter.IsRegistered`, `ArmingAvailable`, and
`SupportsSetTransmit` remain false. Health reports
`txStationCommandAdapterCompositionRegistered:true`,
`txStationCommandAdapterExecutorAttached:false`,
`txStationCommandAdapterExecutorRegistered:false`, and
`txStationCommandAdapterCompositionBrowserIngressRegistered:false`.

Before any future executor call, the composition re-resolves current
`StationTxCommandAuthority` from the lifecycle and compares every validated
command identity exactly. It also rechecks bounded command lifetime, lease
expiry, authentication/freshness, fresh idle occupancy, exclusive Local PTT
ownership for the protected handle, and a matching Armed safety heartbeat.
Failure returns a rejected transport outcome before forwarding. Cancellation
and executor exceptions propagate after bounded diagnostics are updated;
rejected and unknown outcomes remain distinct and are never retried by the
composition.

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
