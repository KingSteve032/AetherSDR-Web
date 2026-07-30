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
- Transmit is fail-closed. No MOX, PTT, TUNE, ATU, or CW keying request can
  reach a radio.
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
