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

`GET /healthz` additionally reports
`txGateLifecycleRegistered=true`, `txCommandTransportRegistered=false`, and
`txSafetySupervisorArmingAvailable=false`. The deployment gate requires these
values together with `transmitEnabled=false` and
`browserTxLeaseEnabled=false`.

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
{"id":10,"cmd":"tx.acquire","seconds":10}
{"id":11,"cmd":"tx.renew","leaseId":"<opaque-lease-id>","seconds":10}
{"id":12,"cmd":"tx.release","leaseId":"<opaque-lease-id>"}
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
single-radio ownership lease. Acquisition requires the dedicated
`Radio:BrowserTxLeaseEnabled` server switch, an authenticated transmit/admin
role, a connected radio session, fresh radio-authoritative idle occupancy, and
no lease held by another browser. `Radio:AllowTransmit` alone does not enable
lease acquisition or keying. Lease responses include the freshly derived
capability state. Holding a lease is not permission or a command to transmit;
MOX/PTT, microphone audio, TUNE, and CW remain unavailable and
`snapshot.canTransmit` remains `false`.

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
