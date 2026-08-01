# AetherSDR Web Client Prototype

This is a runnable, safety-limited prototype of the browser client described by
the historical [native AetherD headless-engine RFC](../../docs/reference/native-aetherd-headless-engine-design.md).
It demonstrates the browser experience, Active Directory/OIDC boundary,
isolated multi-user radio sessions, bounded streaming queues, and the
single-holder TX lease policy without changing the native radio engine.

## Safety and current status

- `Simulation` remains the safe production-config default. The Development
  profile currently enables an experimental, receive-only `FlexRx` adapter for
  `127.77.45.252`.
- `FlexRx` follows AetherSDR's TCP 4992 GUI registration, ephemeral UDP
  registration/firmware prime, VITA-49 FFT assembly, and paired
  panadapter/waterfall cleanup. It owns receive slices on its web pan and
  creates one default RX slice when needed. The development route enforces a
  1,200-byte radio network MTU so every FFT fragment survives the current
  tunnel.
- Each browser page owns an isolated `FlexRx` GUI registration and decodes its
  own 24 kHz stereo `remote_audio_rx` stream. The stream never carries
  microphone or transmit audio.
- Transmit is fail-closed. Phase 2F can validate a deliberate MOX, PTT, TUNE,
  microphone, or CW intent against exact ownership. Phase 2G additionally
  validates deterministic ECDSA-signed station-local command envelopes against
  exact station/radio/session/browser/lease/engine/FLEX-handle authority and a
  freshly Armed safety identity. Phase 2H can load a bounded station-scoped
  ECDSA P-256 public-key trust ring for signature verification. Phase 2I can
  separately load one station-scoped PKCS#8 ECDSA P-256 private key and construct
  a five-second signed envelope from an exact server-owned authority tuple.
  Phase 2J adds a station-scoped internal envelope coordinator that accepts only
  a fresh validated MOX/PTT intent, consumes its ID and owner sequence once,
  derives the signing tuple from server authority, and self-verifies the
  signature before a caller-owned command boundary. Phase 2K gives each radio
  session one internal composition object that attaches that coordinator to the
  session's existing disabled command boundary and derives the complete command
  authority from lifecycle-owned state. Phase 2L replaces the placeholder
  adapter with a typed per-session adapter composition that independently
  rechecks exact authority before an internal executor. Phase 2M attaches a
  per-session gate executor that maps validated SetTransmit true/false commands
  only to the existing ownership-safe gate. Phase 2N adds a lifecycle-owned
  safety-arm composition around the existing supervisor, but production attaches
  no arm authority or caller. The gate remains disabled and its production FLEX
  transport remains unavailable. Verification, signing, and submission all
  default disabled; no browser command ingress, arming capability, reachable
  keying command, or transmit-audio path is registered.
- The binary spectrum framing is experimental v0 and is not the future
  AetherD v1 wire format.
- Production radio integration waits for AetherD RFC steps 3-5: versioned
  control/auth, engine-side TX arbitration, and the remote binary data plane.
- This code follows the repository's `AGENTS.md`, Constitution, GPL-3.0
  license, and Code of Conduct. It does not copy proprietary FlexRadio code.

The intentional limitation matters: the tracking issue for the AetherD
migration is assigned to the AetherSDR maintainer, and the agent guide forbids
jumping ahead of the staged engine migration. This prototype stays outside the
engine and makes no claim to be a production protocol client.

## GUI behavior in this prototype

The first implementation pass ports AetherSDR's operator-console structure
rather than treating the radio as a generic web dashboard:

- `/` is a branded sign-in screen that starts the existing Microsoft Entra
  redirect and never collects a password;
- `/radios` is the authenticated radio desk. It discovers radios without
  consuming a FLEX GUI-client slot, then creates the browser's isolated radio
  session only after the operator selects one;
- `/admin` is a dedicated `Aether.Admin`-protected page for shared/exclusive
  access policy, reservations, active operators, forced session release,
  remote-station enrollment/disable/revocation, and diagnostics for station
  health, isolated remote receive tunnels, every browser-owned GUI client,
  and the radio-reported SmartSDR/Maestro/browser client roster.
  External-client details are labeled as radio-observed because the roster is
  available only while an AetherSDR web GUI is connected to that radio;
- the radio console uses one responsive implementation instead of a separate
  mobile client. On phones, the panadapter remains primary, the tool rail
  becomes a horizontal touch strip, and receiver applets become a collapsible
  bottom sheet with larger controls;
- a backgrounded phone keeps its authenticated radio session and
  radio-authoritative state, while the gateway pauses disposable spectrum and
  receive-audio delivery until the page returns;
- the panadapter and waterfall remain the dominant workspace;
- the left tool rail opens Band, ANT, DSP, Display, and DAX flyouts over the
  panadapter;
- Aether-style slice flags attach to their tuning lines, flip around the
  viewport center, follow the line during drag, collapse from the slice badge,
  expose per-slice tabs, and alternate sides when two slices share a frequency
  so both remain reachable on a phone;
- the 2D panadapter is the default, with a retained browser preference for
  switching to the bounded 3D stacked-trace view;
- the S-meter renders from AetherSDR's authoritative
  `resources/meterfaces/s-meter-v1.json` geometry, including its calibrated
  movement, RX/TX arcs, ticks, pivot, peak marker, and readouts;
- click-to-tune, mouse-wheel tuning, keyboard arrow tuning, direct MHz/Hz
  frequency entry, passband dragging, filter-edge dragging, step controls,
  mode changes, mode-aware filter presets, AF gain, audio balance, mute,
  squelch, AGC mode/threshold, receive antenna, and the radio-backed slice DSP
  controls are wired;
- the browser gives its owned slices stable A/B/C labels while retaining the
  radio's numeric slice IDs for every command, so another GUI client's slice A
  cannot make the first web slice open as B;
- receive DAX channel assignment and the Display flyout's FFT average, frame
  rate, dBm floor, and WNB state/level follow the radio's status and are never
  persisted by the browser. Fill, peak hold, waterfall visibility, and 2D/3D
  mode are functional device-local renderer preferences;
- the Display flyout shows the measured inbound traffic rate and current
  adaptive network profile. Sustained delivery gaps can move only that browser
  session into the lower-traffic profile; a manual VPN low-bandwidth selection
  remains a hold until the operator turns it off;
- returning from low bandwidth restores the radio-observed pre-low display
  frame rate without replaying browser-saved frequency, mode, filter, or
  panadapter state;
- Band flyout selections recenter the real panadapter instead of acting as
  cosmetic preview buttons;
- dragging empty spectrum or the waterfall pans the active display, while the
  left/right rail arrows move it by half a viewport. Drag gestures preview
  locally and issue one radio command on release instead of flooding the
  control connection;
- direct entry accepts forms such as `14.100`, `14.100.000`, `14100000`, and
  `7040`. An off-screen target uses the radio's authoritative recenter path so
  the operator is not confined to the initial 200 kHz span;
- `+RX` creates a real receiver slice, the slice × control deletes it, and
  frequency/mode/filter/AF/squelch/mute state converges from radio status;
- PC Audio starts only after its button is clicked, with independent
  per-browser master/headphone level and a low-latency AudioWorklet jitter
  buffer that trims backlog instead of drifting behind the spectrum;
- receive audio is dropped and the browser queue is cleared as soon as the
  user's radio snapshot has no slices;
- PC Mic starts only after its own button is clicked and displays local input
  level. Its analyser graph ends in the browser: microphone samples are not
  connected to speakers, WebSocket transport, or the radio;
- every range track follows its thumb, including centered and negative-value
  sliders, and range controls expose a larger pointer target without changing
  the compact Aether visual;
- the right applet strip jumps between meter, RX, tuner, TX, phone, P/CW, EQ,
  and web-session panels;
- the spectrum/waterfall divider is draggable, and device-local layout choices
  are retained by the browser;
- the incoming spectrum stream is decoupled from paint cadence: the 2D trace
  uses peak-preserving resampling, the optional stacked trace renders at 12 FPS
  with bounded history, the waterfall renders at 15 FPS with a reused color
  lookup row, and hidden tabs stop painting;
- the operator popover shows every authenticated operator using the selected
  physical radio and aggregates that operator's browser tabs into a connection
  count.

Controls whose AetherD model surface does not exist yet are visibly disabled
instead of behaving like cosmetic toggles. Slice collapse, VFO lock, and
active-slice focus are browser-local. Slice
create/remove, tuning, mode, filter, AF, balance, squelch, mute, AGC, receive
antenna, DAX receive assignment, supported radio DSP controls, and the
radio-backed AVG/FPS/FLOOR/WNB display settings stay synchronized within one
browser/radio GUI session in both `Simulation` and the receive-only `FlexRx`
adapter.
TX, split, tuner,
transmit-processing, and receive-EQ controls are disabled in every mode until
their safe engine surfaces exist.

## Run locally

Prerequisites:

- .NET 10 SDK
- A modern browser with Canvas and WebSocket support

```powershell
dotnet run --project prototypes/web-client/AetherSDR.Web.csproj `
  --launch-profile AetherSDR.Web
```

Open <http://127.0.0.1:5080>. The Development profile signs in a fixed local
operator with Observe and Control roles and takes the operator to the radio
desk. Development authentication is rejected if the ASP.NET Core environment
is not `Development`.

Run the safety and state tests:

```powershell
dotnet test prototypes/web-client/tests/AetherSDR.Web.Tests.csproj `
  --configuration Release
```

Run the focused browser-renderer tests:

```powershell
node --test prototypes/web-client/tests-ui/*.test.mjs
```

## Ubuntu 24.04 pilot service

Publish a self-contained build so the server does not require a machine-wide
.NET installation:

```powershell
dotnet publish prototypes/web-client/AetherSDR.Web.csproj `
  --configuration Release --runtime linux-x64 --self-contained true
```

The pilot deployment keeps immutable releases under
`~/aethersdr/releases/`, atomically points `~/aethersdr/current` at the active
release, and uses the unit in
[`deploy/aethersdr-web.service`](deploy/aethersdr-web.service). Copy
[`deploy/environment.development.example`](deploy/environment.development.example)
to `~/.config/aethersdr-web/environment`, restrict it to the service account,
and replace its temporary development-auth values before production.

Before pushing a change that modifies the FlexWeb server, run the guarded
pre-push deployment gate from the exact working tree:

```bash
bash prototypes/web-client/deploy/validate-deploy-flexweb.sh
```

The gate has no skip-tests option. It builds the complete solution, runs the
server, independent-watchdog, TX-HIL isolation, AetherRemote, and browser test
suites, publishes the web gateway plus the command-incapable independent
watchdog, inspects both production binaries for forbidden TX/HIL command
strings, executes a Disarmed status probe against the published watchdog, and
requires the activated web service to supervise one private child per active
radio session. The child remains in the gateway's existing least-privileged
service cgroup and communicates only over redirected standard input/output; it
has no listener, FLEX connection, command transport, or arming surface. The gate
deploys FlexWeb through the `flexweb-gateway` SSH alias (resolving to
`flexweb@10.2.0.254`), and verifies internal and public fail-closed health. After
all local validation passes, it prompts once without echo for the FlexWeb sudo
password, validates it before activation, and reuses it only for the service
restart or automatic rollback. The existing configuration and credentials are
preserved, the previous immutable release remains available, and a failed
activation or health check rolls the `current` link back automatically. The
script never commits or pushes; a Browser Bridge acceptance pass against the
deployed site is required before Git publication. TX-lifecycle changes must
also keep the public and internal health contract at
`txGateLifecycleRegistered=true`, `txLifecycleWatchdogRegistered=true`,
`txBrowserIntentProtocolVersion=1`,
`txBrowserIntentValidationRegistered=true`,
`txBrowserIntentCommandTransportRegistered=false`,
`txStationCommandProtocolVersion=1`,
`txStationCommandBoundaryRegistered=true`,
`txStationCommandBoundaryEnabled=false`,
`txStationCommandTrustVerificationEnabled=false`,
`txStationCommandTrustedKeyCount=0`,
`txStationCommandSignatureVerificationAvailable=false`,
`txStationCommandSigningEnabled=false`,
`txStationCommandSigningKeyConfigured=false`,
`txStationCommandSigningAvailable=false`,
`txStationCommandEnvelopeCoordinatorRegistered=true`,
`txStationCommandSessionCompositionRegistered=true`,
`txStationCommandSessionCompositionBrowserIngressRegistered=false`,
`txStationCommandAdapterCompositionRegistered=true`,
`txStationCommandAdapterExecutorAttached=true`,
`txStationCommandAdapterExecutorRegistered=true`,
`txStationCommandGateExecutorRegistered=true`,
`txStationCommandGateExecutorTransmitEnabled=false`,
`txStationCommandGateExecutorCommandTransportAvailable=false`,
`txStationCommandGateExecutorSetTransmitAvailable=false`,
`txStationCommandGateExecutorBrowserIngressRegistered=false`,
`txStationCommandAdapterCompositionBrowserIngressRegistered=false`,
`txStationCommandSafetyArmCompositionRegistered=true`,
`txStationCommandSafetyArmAuthorityAttached=false`,
`txStationCommandSafetyArmAuthorityRegistered=false`,
`txStationCommandSafetyArmAvailable=false`,
`txStationCommandSafetyHeartbeatAvailable=false`,
`txStationCommandSafetyAbortAvailable=false`,
`txStationCommandSafetyArmCompositionBrowserIngressRegistered=false`,
`txStationCommandEnvelopeSubmissionEnabled=false`,
`txStationCommandEnvelopeSigningAvailable=false`,
`txStationCommandEnvelopeVerificationAvailable=false`,
`txStationCommandEnvelopeBoundaryAttached=false`,
`txStationCommandEnvelopeBoundaryVerificationAvailable=false`,
`txStationCommandEnvelopeSubmissionAvailable=false`,
`txStationCommandEnvelopeSubmissionRegistered=false`,
`txStationCommandAdapterRegistered=true`,
`txStationCommandArmingAvailable=false`,
`txStationCommandSetTransmitAvailable=false`,
`txIndependentWatchdogHostPackaged=true`,
`txIndependentWatchdogSupervisionRegistered=true`, a supervised Disarmed state,
non-negative process/session/restart counts, zero registered watchdog identities
while browser TX leases are disabled,
`txIndependentWatchdogCommandTransportRegistered=false`,
`txIndependentWatchdogArmingAvailable=false`,
`txCommandTransportRegistered=false`, and
`txSafetySupervisorArmingAvailable=false`. The repeatable browser procedure is
stored locally at
`~/.browser-bridge/playbooks/aethersdr-flexweb-post-deploy-acceptance.md`.

`IndependentTxWatchdog` configuration owns only supervision mechanics:
`Enabled`, an optional reviewed executable path, request timeout, and restart
delay. The default executable is
`watchdog/AetherSDR.TxWatchdog` beneath the active release. The configured path
must still name that reviewed executable. Disabling supervision is diagnostic
and receive-only; it never enables transmit. Process or IPC loss revokes only
the matching tracked lease and a replacement process starts empty and Disarmed.

`StationTxCommandTrust` is one owned configuration object for station-local
signature verification. `VerificationEnabled` defaults to false and `Keys`
defaults to empty. At most four keys may be configured for bounded rotation.
Each entry requires a unique canonical `KeyId` and an absolute canonical
`PublicKeyPath` naming exactly one UTF-8 `PUBLIC KEY` PEM block containing an
ECDSA P-256 SubjectPublicKeyInfo value. Private keys, other curves, extra PEM
blocks, invalid UTF-8, oversized files, duplicate IDs or paths, unknown
configuration properties, direct symbolic links, and key files or containing
directories writable by group/other users fail application startup. Malformed
key IDs are not echoed into startup errors. Key IDs and short public-key fingerprints may appear
in diagnostics; key paths and key material do not.

Environment-variable form for a reviewed public trust anchor is:

```bash
StationTxCommandTrust__VerificationEnabled=true
StationTxCommandTrust__Keys__0__KeyId=station-command-2026a
StationTxCommandTrust__Keys__0__PublicKeyPath=/var/lib/aethersdr-web/command-trust/station-command-2026a.pem
```

Enabling verification only makes the verifier ready. The station command
boundary remains disabled; the registered adapter still terminates at the
transmit-disabled gate, safety-arm authority remains absent, and arming plus
set-transmit remain unavailable. There is still no browser, HTTP, WebSocket,
AetherRemote, watchdog, or timer command ingress.

`StationTxCommandSigning` is a separate owned configuration object for one
station-local private signing key. `SigningEnabled` defaults to false, while
`KeyId` and `PrivateKeyPath` default empty. If either key field is configured,
the complete key is loaded and validated at startup even while signing remains
disabled, so a malformed staged private key cannot remain latent until a later
activation. The path must be absolute and canonical and name one regular,
non-symlink UTF-8 PEM file containing exactly one unencrypted PKCS#8 `PRIVATE
KEY` block for ECDSA P-256. On Unix the file must have mode 0400 or 0600, and its
immediate containing directory must not be writable by group or other users.
Public-only keys, encrypted private keys, other curves, extra PEM blocks,
trailing data, invalid UTF-8, oversized files, unknown configuration properties,
relative path segments, direct symbolic links, and unsafe permissions fail
application startup. Never commit this file or copy it into a publish tree.

Environment-variable form for a reviewed station signing key is:

```bash
StationTxCommandSigning__SigningEnabled=true
StationTxCommandSigning__KeyId=station-command-2026a
StationTxCommandSigning__PrivateKeyPath=/var/lib/aethersdr-web/command-signing/station-command-2026a.pem
```

The singleton authority owns and disposes the imported private key. It creates a
new canonical command UUID, strictly increasing process-local sequence, current
issue time, five-second expiry, key ID, and base64url ECDSA signature; callers
may supply only the exact station/radio/session/browser/lease/gateway/engine/FLEX
ownership tuple, the supported action, and its boolean value. Diagnostics expose
only disabled/readiness state, the key ID, and a short fingerprint of the public
key. They never expose the private-key path or key material. Production does not
inject the signer into radio sessions or the command boundary and exposes no
method that submits the resulting envelope, so signing readiness alone cannot
create command reachability.

`StationTxCommandEnvelopeCoordinator` is a third owned configuration object with
one `SubmissionEnabled` bit, defaulting to false. Its submission method remains
internal. Phase 2K passes the singleton through `RadioSessionRegistry` into one
session-owned composition object, not into `RadioCoordinator`, the WebSocket
endpoint, AetherRemote, the watchdog, a timer, or any browser/HTTP handler. The
composition owns the session's existing command boundary but adds no external
caller.

An internal request contains one already-validated operator intent and one
server-owned `StationTxCommandAuthority`. The intent must be a canonical,
positive-sequence MOX or PTT Boolean action observed within five seconds, with
at most one second of future clock skew. The coordinator derives every envelope
identity and the SetTransmit value from those two records; callers cannot supply
protocol version, key ID, command ID, envelope sequence, timestamps, signature,
or a prebuilt envelope. A bounded tracker consumes each intent ID once and
requires strictly increasing intent sequence per session/browser owner. Unknown
adapter outcomes, cancellation, boundary rejection, or signing failure do not
make the same intent retryable.

Before signing, submission requires the coordinator enable bit, a ready signer,
a ready verifier, an enabled caller-owned boundary, a registered adapter, fresh
arming, and SetTransmit capability. After signing, the coordinator decodes the
fixed-width P-256 signature and verifies it against the station trust ring before
calling the boundary, which independently revalidates the envelope, exact
authority, replay sequence, safety arm, and adapter.

The Phase 2K session composition accepts only the current connection identity,
the parsed MOX/PTT Boolean intent, its positive JavaScript-safe sequence, and the
server observation time. It resolves the canonical radio, session, stable
browser-page identity, exact active connection-owned lease, gateway instance,
engine instance, and FLEX handle from the production lifecycle. The gateway
instance remains the station-command identity used by that lifecycle's existing
boundary. A replaced connection, mismatched or expired lease, stale browser,
engine, or gateway observation, missing handle, unsupported action, cancellation,
or authority-resolution fault stops before the coordinator. No caller can
supply or override an authority field.

Health reports both station-scoped coordinator registration and per-session
composition registration, while browser ingress remains unregistered. The
attached production boundary is still disabled, and signer, verifier, adapter,
arming, and SetTransmit availability remain false under default configuration.
`txStationCommandEnvelopeSubmissionRegistered` remains false because there is
still no externally reachable submission route.

Phase 2L adds one `StationTxCommandAdapterComposition` beneath each session's
signed command boundary. It implements the existing internal adapter contract
and independently re-resolves current server-owned authority before any
execution attempt. Neither `RadioSessionRegistry` nor `RadioCoordinator` nor the
WebSocket endpoint accepts the executor type.

Phase 2M supplies that composition with one internal
`StationTxCommandGateExecutor` owned by the same lifecycle. The executor maps
only an already validated SetTransmit true command to `RequestKeyAsync` and a
false command to `RequestUnkeyAsync`, preserving exact lease, session, browser,
Local PTT, radio-occupancy, confirmation, and ownership-safe unkey rules in the
existing gate. It performs no retry and translates unknown gate command outcomes
back to unknown adapter outcomes for radio-state reconciliation.

The adapter composition still independently requires exact station/radio/
session/browser/lease/gateway/engine/FLEX-handle identity, a non-expired command
and lease, current authentication and observations, and a matching freshly Armed
safety identity. Key requires fresh idle occupancy and exclusive Local PTT for
the exact handle. Unkey permits only already-idle state or fresh proof that the
exact AetherSDR handle is the single TX owner; external, ambiguous, stale, or
replaced ownership cannot reach the gate.

Production creates the gate with `allowTransmit:false` and
`StationTxUnavailableCommandTransport`. Health and Admin therefore report the
executor and adapter registered while executor arming, SetTransmit, the boundary,
and submission remain unavailable. No browser, HTTP, WebSocket, AetherRemote,
watchdog, or timer caller is added, and the HIL-only FLEX command transport is
not imported into the normal build.

Phase 2N adds one `StationTxSafetyArmComposition` around each lifecycle's
existing unkey-only `StationTxSafetySupervisor`. Its typed requests accept only
the current connection identity plus a bounded heartbeat timeout or abort reason.
The composition re-resolves the exact radio/session/browser/lease/gateway/engine/
FLEX-handle authority before every operation and asks an optional internal
`IStationTxSafetyArmAuthority` to authorize that exact tuple. Arm additionally
requires fresh idle occupancy and exclusive Local PTT for the protected handle.
An idle heartbeat requires the same Local PTT owner; an active heartbeat or
abort requires fresh proof of the exact single AetherSDR TX owner, while an
already-idle abort may only clear the matching arm without a radio command.
Mismatched, expired, stale, disconnected, external, ambiguous, cancelled, or
faulted operations stop without retry.

Normal production constructs the composition with no arm authority and exposes
no lifecycle, registry, coordinator, WebSocket, HTTP, AetherRemote, watchdog, or
timer method that can invoke it. Health and Admin report composition registration
separately from arm-authority attachment, arm, heartbeat, and abort availability;
all four latter states remain false, the supervisor remains Disarmed, and the
composition owns no command or emergency-unkey transport.

Environment-variable form remains disabled by default:

```bash
StationTxCommandEnvelopeCoordinator__SubmissionEnabled=false
```

`Radio:BrowserTxLeaseEnabled` remains false by default. When deliberately enabled
for validation, the radio page reveals a **TX AUTHORITY** panel that can acquire,
automatically renew, and release the single physical-radio lease. Its messages
use protocol version 1, JavaScript-safe request and monotonic sequence numbers
bound to the current WebSocket, and an opaque lease secret returned only to the
holder. The browser bounds unanswered TX requests to 16. Disconnect or reconnect
clears that secret locally; rejected or unconfirmed renewal and unsupported
lease-event versions also discard it. The server refuses renewal after idle
occupancy or exact supervised lifecycle authority is lost and releases that
exact lease as `renewal-authority-lost`. Server expiry and disconnect release
remain authoritative.

The same panel can submit validation-only `mox.set`, `ptt.set`, `tune.set`,
`microphone.set`, and `cw.send` intents. A valid result says
`transport-unavailable`: the server proved the exact authenticated connection,
role, lease, idle occupancy, lifecycle/FLEX identity, and registered Disarmed
watchdog epoch, then stopped without invoking a command gate or radio transport.
The actual MOX, TUNE, and CWX controls remain hidden and disabled because the
server reports their executable capabilities false. PC MIC remains a local input
meter and no samples are transmitted. Admin shows the lease holder name, expiry
or revocation reason, and latest validation/denial outcome without exposing the
opaque lease ID.

The user service needs lingering to start at boot without an interactive SSH
login:

```bash
sudo loginctl enable-linger flexweb
systemctl --user enable --now aethersdr-web.service
```

For a no-sudo pilot only, `deploy/start-aethersdr-web.sh` can be launched by
the service account's crontab using `deploy/aethersdr-web.cron`. Remove that
fallback before enabling the managed user service so only one process owns the
HTTP and radio ports.

## Microsoft Entra ID / Active Directory authentication

The browser uses the backend-for-frontend pattern: the ASP.NET Core gateway is
the confidential OIDC client, the browser receives only a secure session
cookie, and authorization is enforced again at the WebSocket boundary.

For Microsoft Entra ID:

1. Register a single-tenant Web application.
2. Add redirect URI `https://radio.example.com/signin-oidc`.
3. Add front-channel logout URI
   `https://radio.example.com/signout-callback-oidc`.
4. Define these user/group app roles with the exact values below.
5. Assign users or, preferably, station security groups to the roles.
6. Enable **Assignment required** on the enterprise application.
7. Put the client secret in a secret store or owner-only file, never in this
   repository. `deploy/set-client-secret.sh` writes the file without echoing
   the secret to the terminal.

| App role value | Purpose |
|---|---|
| `Aether.Observe` | View radio state, spectrum, waterfall, meters, and presence |
| `Aether.Control` | Change RX state in the user's assigned radio session |
| `Aether.Transmit` | Become eligible to request the single-holder TX lease |
| `Aether.Admin` | Manage the gateway and all non-keying controls |

Example production environment:

```text
ASPNETCORE_ENVIRONMENT=Production
Auth__Mode=Oidc
Auth__Authority=https://login.microsoftonline.com/<tenant-id>/v2.0
Auth__ClientId=<application-client-id>
Auth__ClientSecretFile=/home/flexweb/.config/aethersdr-web/client-secret
Auth__NameClaimType=name
Auth__RoleClaimType=roles
AllowedHosts=radio.example.com
AllowedOrigins__0=https://radio.example.com
ReverseProxy__Enabled=true
ReverseProxy__KnownProxies__0=<exact-proxy-LAN-IP>
DataProtection__KeyPath=/var/lib/aethersdr-web/keys
RadioAccess__PolicyPath=/var/lib/aethersdr-web/radio-access.json
```

The production deployment uses `deploy/aethersdr-web.service` as a
root-installed system unit while the process itself runs as the unprivileged
`flexweb` account. The unit starts after `network-online.target`, restarts on
failure, writes to the persistent system journal, exposes only the required
network address families, and makes the host filesystem read-only except for
the owner-only Data Protection key and radio-policy directories.

Accounts assigned `Aether.Admin` get a **Radio allocation** applet. It reports
discovered capacity and active operators, applies persistent shared or
exclusive access per radio, optionally reserves a radio to one Entra object
ID, and can immediately release a selected operator's browser and radio
sessions. These rules are enforced when the server creates the physical radio
session; hiding the applet is not the security boundary. Administrators bypass
radio reservations so a bad policy cannot lock them out of the control plane.
Policy changes affect new sessions. Existing operators remain connected until
they leave or an administrator explicitly disconnects them.

The same Admin page manages remote station device identity. Enrollment codes
are random, single-use, and short-lived. The browser displays a code only in
the Admin session that created it; the gateway does not put it in a URL.
Disabling or revoking a station closes that station's outbound link and remote
receive sessions without disturbing other stations or local radios. Revocation
requires a fresh one-time enrollment, while disable can be reversed.

Microsoft recommends app roles for application authorization. Groups can be
assigned to those roles, avoiding direct dependence on tenant-specific group
IDs and JWT group-overage behavior:

- [Add app roles and receive them in a token](https://learn.microsoft.com/en-us/entra/identity-platform/howto-add-app-roles-in-apps)
- [Configure group claims and app roles](https://learn.microsoft.com/en-us/security/zero-trust/develop/configure-tokens-group-claims-app-roles)
- [Configure OIDC web authentication in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)

For on-premises Active Directory, use AD FS as the OIDC authority. Set
`Auth__RoleClaimType` to the claim type emitted by that relying-party
configuration. Microsoft recommends AD FS/OIDC when Windows Authentication
would otherwise need to cross a proxy or load balancer:

- [Windows Authentication and AD FS/OIDC guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/windowsauth?view=aspnetcore-10.0)

## Network placement

Deploy the gateway beside AetherD on the shack LAN. Terminate HTTPS at the
gateway or a tightly configured reverse proxy, and prefer the WireGuard
topology accepted in the AetherD RFC. Do not expose the AetherD listener
directly to the public internet.

The public browser origin must appear in `AllowedOrigins`. WebSocket requests
without an `Origin` header, with an unlisted foreign origin, or without the
expected subprotocol are rejected before a radio session is created.

## Multi-user behavior

- Flex discovery is shared server-wide, but selection and connection state are
  not.
- The session key is the authenticated user, a random browser-page ID, and the
  physical radio endpoint. Every browser page is a separate FLEX GUI client,
  including two pages signed in as the same user.
- Every web GUI client gets a separate TCP/UDP radio connection, slices,
  panadapters, receive-audio stream, coordinator, and low-bandwidth setting,
  matching the isolation of SmartSDR or Maestro GUI clients.
- A radio session owns every slice, display, and audio stream it creates and
  removes them during teardown. Page teardown releases it immediately; a lost
  teardown signal falls back to a 60-second mobile reconnect grace period.
- Discovery capacity is a display hint, not a gateway limit. The live
  `client gui` response decides whether the radio can admit the web client, so
  SmartSDR, Maestro, and browser clients all compete under the radio's own
  Multi-Flex rules.
- Administrators can mark a radio shared or exclusive and reserve it to one
  Entra account. The policy file is replaced atomically so a crash cannot
  expose a partially written allocation.
- Every page gets a full snapshot followed by versioned deltas for its isolated
  session. Presence is shared read-only across sessions selecting the same
  physical radio; radio state and control messages remain isolated.
- Each browser has a bounded 64-message outbound queue. Old stream frames
  are dropped instead of allowing a slow browser to stall the engine.
- The protected admin page reports the current FLEX client handle, GUI ID,
  UDP port, pan/waterfall/audio stream IDs, web-to-radio slice mapping,
  connection attempts, frame activity, measured browser queue drops, and the
  latest per-browser audio/display/text traffic rates and delivery gap for each
  isolated session. These values are observational and never issue radio
  commands.
- While at least one web radio session is active, Admin also reports the
  radio-authoritative GUI-client roster and labels each handle as
  browser-owned or external.
- A disconnected browser releases its TX lease immediately.

Run the focused concurrent-session isolation soak test:

```powershell
dotnet test prototypes/web-client/tests/AetherSDR.Web.Tests.csproj `
  --configuration Release --filter Category=Soak
```

The test repeatedly tunes two independent simulated GUI clients, verifies that
their snapshots and outbound messages never cross, exercises reconnect reuse,
and confirms both transports continue producing independent spectrum frames.

The production gateway must exchange the signed-in AD identity for a separate,
short-lived AetherD client credential and capability grant. It must not reuse
SmartLink identity or forward a Microsoft token as if it were an AetherD
credential; that separation is an accepted RFC decision.

## Project milestones

The web-specific roadmap, active milestones, acceptance criteria, and completed
work are tracked in [`MILESTONES.md`](MILESTONES.md). Receive fidelity is
tracked in **M2**, while mobile and constrained-network recovery has started in
**M3**. Transmit remains blocked until the AetherD engine boundary and
hardware-in-the-loop TX-safety milestones are complete.

See [DESIGN.md](DESIGN.md) and [PROTOCOL.md](PROTOCOL.md) for the boundary and
prototype framing details.
