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

The first browser-integration increment exposes only a separately configured
ownership lease. `Radio:BrowserTxLeaseEnabled` defaults to false and is distinct
from the reserved `Radio:AllowTransmit` switch. The gateway derives lease
eligibility from its authenticated role set, live connection state, fresh
radio-authoritative occupancy, and the process-wide physical-radio lease. The
welcome message keeps the compatibility keying capability false and separately
reports lease eligibility plus explicit false values for keying, microphone,
TUNE, and CW. A lease cannot reach the hidden command gate and is not operator
intent to transmit.

The next safety layer is an independent, station-local supervisor with no key
method and an unkey-only transport. Its arm is purpose-bound to one engine
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

The prototype uses Canvas 2D with a compact binary spectrum frame and performs
the waterfall scroll locally. Production can move to WebGL/WebGPU only after
measuring the Canvas implementation; rendering technology cannot change the
wire contract.

RX audio should use Opus frames decoded in an `AudioWorklet`, with a bounded
jitter buffer. Microphone capture and TX audio require a separate, explicit
operator permission and are out of scope until the engine TX gate is proven.
