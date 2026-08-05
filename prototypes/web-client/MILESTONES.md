# AetherSDR Web Milestones

This roadmap covers the standalone AetherSDR-Web suite. The native desktop
application is maintained separately and is used only as behavioral reference.

Milestones are completed by evidence, not by implementation alone. A milestone
needs automated coverage, a real-radio check where applicable, and operator
confirmation that its acceptance criteria hold. The radio remains
authoritative for live state and GUI-client admission. Transmit stays
fail-closed until the dedicated TX-safety milestone is complete.

## Status summary

| Milestone | Status | Outcome |
|---|---|---|
| M0 — Browser console foundation | Complete | Responsive authenticated RX console |
| M1 — Multi-client correctness | Complete | Independent SmartSDR and web GUI clients |
| M2 — Receive fidelity | Complete | Correct, low-latency tuning, meters, DSP, and audio |
| M3 — Mobile and constrained networks | Complete | Reliable operation over phones and VPNs |
| M4 — Multi-radio administration | Complete | Observable and manageable radio fleet |
| M5 — AetherD engine boundary | Blocked on RFC stages 3–5 | Versioned production engine connection |
| M6 — Remote station connectivity | Complete | Secure access to radios on other networks |
| M7 — Transmit safety | Active | Intentional, leased, fail-closed transmit |
| M8 — Production release | Planned | Supported deployment, upgrades, and recovery |

## M0 — Browser console foundation

Status: **Complete**

Delivered:

- Microsoft Entra ID sign-in with app-role authorization.
- Authenticated radio selector and responsive desktop/mobile console.
- Panadapter, waterfall, Aether-style slices, S-meter, receive controls, PC
  audio, local-only PC microphone meter, and low-bandwidth profile.
- Protected Admin page with access policies, reservations, active web
  operators, forced release, and browser-session diagnostics.
- Receive-only production pilot on Ubuntu Server 24.04.

## M1 — Multi-client correctness

Status: **Complete**

Goal: every browser page behaves as its own FLEX GUI client and coexists with
SmartSDR, Maestro, and other browser clients without crossing state.

Delivered:

- A unique FLEX TCP/UDP connection, GUI client ID, slices, displays, audio
  stream, coordinator, and bounded browser queue per browser page.
- Radio-authoritative admission instead of a gateway-imposed client limit.
- Per-session Admin diagnostics and queue-drop counters.
- Radio-reported GUI-client inventory with browser-owned and external client
  classification, confirmed against one SmartSDR client and one web client on
  2026-07-27.
- Real-radio SmartSDR/web isolation confirmed on 2026-07-27: tuning, mode,
  filters, slices, pan/zoom, selected-slice audio, and final web-slice removal
  remained independent while both clients stayed connected.
- Radio-authoritative client admission confirmed on 2026-07-27: with SmartSDR
  and one web GUI client admitted, a third client was rejected without
  interrupting either healthy client.
- Reconnect and cleanup confirmed on 2026-07-27: an in-grace web reload reused
  its session, idle expiry released the web GUI client and radio resources,
  SmartSDR remained connected, and the next web connection received a fresh
  session.
- Automated two-session isolation soak and queue-saturation coverage.
- Sixty-second reconnect grace followed by deterministic resource release.
- One-hour hardware soak completed on 2026-07-27: one uninterrupted web-radio
  session ran for 60 minutes 31 seconds on the same service process and FLEX
  TCP connection with zero disconnects, transport retries, registration
  rejections, stream failures, logged queue drops, warnings, or errors.

Remaining:

- [x] Show the radio-reported GUI-client roster in Admin, including external
  SmartSDR/Maestro clients and browser-owned clients.
- [x] Prove frequency, mode, filter, slice, panadapter, waterfall, meter, and
  audio isolation with one SmartSDR client and one web client.
- [x] Verify a third GUI client is rejected cleanly when the radio is full,
  without disturbing admitted clients.
- [x] Verify browser reconnect inside the grace period reuses its session and
  expiry releases the radio slot and owned resources.
- [x] Complete a one-hour two-client hardware soak with zero crossed control
  messages, unexplained retunes, queue drops, or orphaned radio resources.

Acceptance criteria:

- Each admitted browser has a distinct radio client handle and GUI client ID.
- Admin identifies every radio-reported GUI client as browser-owned or
  external.
- Commands and status from one client never mutate another client's owned
  slices, displays, or audio selection.
- Radio refusal and client loss produce clear states and never evict a healthy
  client.

## M2 — Receive fidelity

Status: **Complete**

Goal: the browser sounds and responds like a dependable receive client.

Scope:

- Typed, clicked, wheel, keyboard, and dragged tuning converge on the same
  radio-authoritative frequency path.
- Selected-slice audio follows frequency, mode, filter, mute, and slice
  deletion without a stale or mixed feed.
- S-meter and displayed signal timing align with received spectrum and audio.
- Supported NB, NR, ANF, AGC, squelch, antenna, DAX, and display controls
  reconcile from radio status.
- Pan and waterfall remain continuous while panning, zooming, and changing
  bands.

Evidence delivered:

- Per-session Admin timing now distinguishes an accepted browser tune from
  the matching radio `RF_frequency` status instead of treating command
  acknowledgement as confirmation.
- FLEX firmware 4.2.18's missing tune echo is handled by one debounced,
  radio-authoritative slice refresh after tuning settles. A live text-entry
  tune on 2026-07-27 confirmed browser request to matching radio status in
  264 ms with zero browser-queue drops.
- A collapsed receiver rail no longer lets a zero-width S-meter canvas abort
  snapshot processing; the live client reached Connected with no new browser
  errors after the fix.
- Service restart recovery adopts only panadapters whose radio-reported
  `client_handle` matches the reconnecting GUI client, preserves their live
  frequency/display state, and reactivates their FFT stream with client pixel
  dimensions. Live FFT and audio both resumed from the restored FLEX session.
- Per-browser Admin audio diagnostics now report estimated playback latency,
  queued audio, underruns, latency trims, deliberate clears, malformed or
  missing AETA packets, and the maximum browser arrival gap. Reports are
  boundary-validated, server-timestamped, and scoped to the reporting web
  socket.
- A live normal-bandwidth run on 2026-07-27 measured zero missing AETA packets,
  zero gateway queue drops, and roughly 62-67 ms estimated browser playback
  latency, while browser delivery still paused for up to 325 ms and produced
  underruns. A larger adaptive-buffer experiment increased latency to
  96-124 ms without eliminating underruns, so it was rejected and rolled back.
- The authenticated radio WebSocket now runs in a dedicated Web Worker. AETA
  audio is decoded there and delivered through a direct `MessagePort` to the
  AudioWorklet, so spectrum rendering and other main-thread UI work cannot
  interrupt normal audio delivery. If Worker startup fails, the client falls
  back to the previous main-thread path without stranding the radio page.
- A foreground normal-bandwidth comparison on 2026-07-27 reduced the maximum
  browser arrival gap from 325 ms to 60 ms with zero missing AETA packets and
  zero gateway queue drops. The tuned 45 ms source queue reported 103 ms
  estimated end-to-end playback latency and only two cumulative underruns.
  Moving the radio page into the background still produced a 697 ms scheduling
  pause; background/foreground re-prime behavior belongs to M3 and must not be
  hidden behind a permanently large latency buffer.
- Slice selection now changes the active controls and S-meter source without
  clearing the radio's mixed `remote_audio_rx` stream. Per-slice mute, gain,
  and balance remain radio-authoritative, so every unmuted slice contributes
  simultaneously and deleting one slice removes only that radio-side source.
- The formerly static strip beneath each floating slice frequency now matches
  AetherSDR's S0-to-S9-to-S9+60 scale and reads that slice's own FFT passband.
  A live two-slice check on 2026-07-27 held A at 14.074329 MHz and B at
  14.100000 MHz, produced independent S9+6 and S4 readings, and confirmed that
  muting B left A unmuted. Slice flags are clamped on-screen and the active
  flag is raised above overlaps so their controls remain reachable.
- Release `20260728-m2-receive-fidelity-5` makes the RX header's filter width
  follow the selected radio slice instead of showing a fixed 2.7K label.
  Real-radio DIGU/USB checks confirmed typed slice tuning, 2.4K filter status,
  NB/NR/ANF/NRL, AGC, WNB, receive antenna, DAX, and band-memory reconciliation.
  Squelch and voice-only ANF controls are now disabled in digital/CW modes, and
  FLEX-8000-only NRS/RNN/NRF controls are disabled on the FLEX-6700 rather than
  sending commands the radio rejects.
- A live final-slice removal parked the S-meter at “no slice selected,” stopped
  browser audio traffic at 0 b/s, cleared the worklet queue, and left PC Audio
  armed for the next slice. Creating a receiver again produced Slice A, not B,
  and audio resumed from the new radio-authoritative source.
- Mobile panadapter zoom now stays above the receiver sheet and changes the
  radio span without resizing or shifting the canvases. A 200 kHz → 133 kHz →
  200 kHz round trip preserved Slice A at 14.074000 MHz, DIGU, and 2.4K; a
  20m → 40m → 20m round trip restored the same 20m slice state.
- Final hands-off operator acceptance completed on 2026-07-30 using Chrome on
  a Surface Pro 8 with PSOC2 at 14.074 MHz. During 15 minutes of ordinary
  foreground listening, the operator reported only a couple of minor audible
  dropouts and judged SmartSDR over the same VPN path to be worse. No missing
  packets were observed, gateway drops remained at zero, and 44 AudioWorklet
  underruns were not materially audible. Tune, filter, and mode behavior sounded
  equivalent to SmartSDR. Final-slice deletion and fresh-slice recovery were
  exercised successfully, and the operator marked the run as a pass.

Remaining:

- [x] Complete one final hands-off operator listen-through against SmartSDR.
  The 2026-07-30 operator run confirmed the accumulated AudioWorklet underrun
  counter did not translate into materially audible failure during ordinary
  foreground use.

Acceptance criteria:

- No audible audio remains after the final owned slice is removed.
- No stale-frequency audio survives a tune.
- All receive controls either work and reconcile from radio status or are
  visibly disabled.
- A real-radio comparison against SmartSDR completes without unexplained
  frequency, filter, meter, or audio-source disagreement.

## M3 — Mobile and constrained networks

Status: **Complete**

Goal: a phone or VPN client can recover from ordinary network variation
without losing or corrupting radio state.

Scope:

- Mobile frequency-entry and selected-slice audio parity.
- Measured normal and low-bandwidth spectrum, waterfall, and audio rates.
- Adaptive frame rate, bin count, waterfall cadence, and bounded jitter.
- WebSocket reconnect, browser background/foreground, VPN interruption, and
  proxy restart recovery.
- Touch sizing and layout checks at supported mobile breakpoints.

Evidence delivered:

- Browser backgrounding now pauses only local audio delivery. The authenticated
  radio WebSocket, GUI client, slices, frequencies, and radio-authoritative
  state remain alive.
- Returning to the foreground clears stale queued samples, resumes the existing
  audio context, and then re-enables worker delivery. Playback must collect a
  fresh 45 ms jitter cushion before it starts, preventing a delayed burst of
  background audio.
- Back-forward-cache page suspension no longer deliberately closes the radio
  connection; a real page close still releases it through the existing
  session lifecycle.
- Per-browser Admin diagnostics distinguish foreground, background-paused, and
  re-priming playback and count background pauses and successful foreground
  recoveries.
- A physical phone return test found that release
  `20260727-m3-audio-recovery-1` trusted `AudioContext.resume()` without
  confirming that the browser audio clock had restarted. Release
  `20260727-m3-audio-recovery-2` now requires clock progress, retries with a
  forced suspend/resume cycle, and keeps the active PC Audio button available
  as the user-gesture recovery control instead of requiring a page refresh.
- Automated lifecycle, frozen-clock, stale-queue, fresh-buffer,
  diagnostics-validation, and Admin-format coverage passes. Production loaded
  the corrected versioned client and kept zero service warnings or restarts
  after deployment. A repeat physical phone-lock recovery check confirmed that
  audio resumes without refreshing the page.
- Release `20260727-m3-reconnect-1` cancels unfinished slice, panadapter, and
  typed-frequency gestures when transport drops, uses one capped exponential
  reconnect schedule, and falls back to the main-thread transport if a live
  worker fails. This prevents stale browser input from overwriting newer
  radio-authoritative state after recovery.
- The repeatable ten-interruption regression now proves session/state reuse,
  rejects overlapping sockets, and checks that no duplicate browser
  connections are admitted. The full suite passes with 121 server and 88
  browser tests.
- Two controlled production gateway restarts recovered automatically to LIVE
  at 14.100 MHz with one slice and no manual refresh. A same-process browser
  reload retained the radio session and Admin reported `1 recovered`, `2 of 2
  admitted`, `0 overlapping`, and a 423 ms last recovery. Gateway process
  restarts create a new in-memory web session by design, while the
  radio-authoritative slice frequency remains intact.
- Production finished on PID 32292 with zero service restarts, no warning-level
  log entries after deployment, and a healthy fail-closed RX response:
  `{"status":"ok","radioMode":"FlexRx","transmitEnabled":false}`.
- Release `20260727-m3-network-profile-3` measures inbound browser traffic in
  two-second windows and shows the current profile and aggregate rate in the
  Display flyout. Admin breaks the same boundary-validated sample into audio,
  display, and text traffic for each browser connection.
- Adaptive mode requires three consecutive foreground samples with a delivery
  gap of at least 300 ms or newly missing audio before selecting low bandwidth.
  Automatic low mode holds for at least two minutes and requires 30 consecutive
  healthy samples at or below 150 ms before returning to normal. Manual low
  mode is an operator hold and is never automatically undone.
- A production FLEX-6700 measurement on 2026-07-27 recorded normal traffic at
  1.19-1.24 Mb/s, including roughly 791-797 kb/s audio and 405-409 kb/s
  display. Low mode measured 0.83-0.88 Mb/s, including roughly 794 kb/s audio
  and 42 kb/s display: about a 90% display-traffic reduction and a 31% total
  reduction while retaining the full receive-audio stream.
- A real-radio normal/low/normal round trip retained session
  `8352ef5ec1a8ccd920e14df4496eb5ae`, its single 14.100 MHz slice, and live
  receive state while changing the owned panadapter from 25 to 5 and back to
  25 FPS. The normal return restores only the radio-observed pre-low FPS; it
  does not replay stale browser state over the radio.
- Release `20260727-m3-mobile-parity-2` keeps a phone's floating frequency
  editor alive while the operator focuses and commits it, raises open tool
  panels above the mobile pan tabs so their close button remains touchable,
  and alternates coincident slice flags instead of covering one with another.
- A production 390x844 real-radio check created Slice B beside Slice A at
  14.100 MHz with two non-overlapping 176 px controls, then committed Slice B
  to 14.074 MHz from its floating editor. The radio echoed B -> slice 1 in
  270 ms, the main receiver followed B, and browser audio reported Slice B as
  playing with zero underruns. Transmit remained fail-closed.
- Release `20260728-m3-background-delivery-2` adds a boundary-validated browser
  visibility message. Hidden pages keep their authenticated WebSocket, radio
  session, text state, and GUI-client ownership while the gateway stops
  enqueueing disposable spectrum and audio frames and drains any binary
  backlog already waiting for that page.
- Foreground recovery now re-baselines worker-side audio sequencing before
  delivery resumes, so an intentional background pause cannot become a false
  missing-packet or arrival-gap alarm. Coverage passes with 122 server and 89
  browser tests.
- The production FLEX-6700 foreground baseline for session `d792ce8a` reported
  `0 missing`, queue `0 / 64`, `0 dropped`, normal traffic at 1.19 Mb/s, and a
  healthy fail-closed RX service. A subsequent physical-phone check confirmed
  that PC Audio resumes without a page refresh after locking or backgrounding
  the phone, completing the browser-visibility acceptance check.
- The first deployment archive for this work included a non-executable
  apphost, producing a visible systemd `203/EXEC` failure. The executable was
  restored immediately, the delta was rebuilt to inherit the known-good Linux
  apphost, and final release `20260728-m3-background-delivery-2` started with
  zero service restarts.
- Release `20260728-m2-receive-fidelity-5` closes the remaining phone-layout
  gaps: zoom controls are anchored above the collapsed or expanded receiver
  sheet, and Band, ANT, DSP, Display, and DAX panels stack above that sheet so
  every option and close control remains touchable. Live phone-sized checks
  exercised panel open/close behavior, WNB, antenna, DAX, zoom, and band
  changes. The final suite passes with 122 server and 94 browser tests.

Acceptance criteria:

- Low-bandwidth mode has published measured traffic rates. **Met
  2026-07-27.**
- Reconnect never restores stale client state over newer radio state.
- Ten network interruptions recover without orphaned streams or client slots.

## M4 — Multi-radio administration

Status: **Complete**

Goal: administrators can understand and safely manage several radios and
operators from one control plane.

Scope:

- Radio-wide connected-client inventory and capacity history.
- Per-radio health, session age, stream activity, and queue pressure.
- Durable shared/exclusive policy and Entra-account reservation.
- Administrative audit trail for policy changes and forced releases.
- Clear offline, busy, degraded, and reconnecting states.

Evidence delivered:

- Release `20260728-m4-admin-audit-2` adds a bounded, durable administrative
  audit store beside the existing persistent radio-policy file. Each record
  carries the Entra administrator ID and display name, action, radio, optional
  target account, UTC timestamp, result, and a sanitized outcome summary.
- Shared/exclusive changes, reservation changes, and forced operator releases
  record both successful and failed outcomes. The Admin page shows the 50 most
  recent actions newest-first without exposing the audit endpoint outside the
  administrator role.
- Audit updates write a same-directory temporary file, flush it to disk, and
  atomically replace the prior snapshot. Linux creates the file owner-only and
  production confirmed mode `600`. Both audit and radio-policy stores restore
  their prior in-memory snapshot if persistence fails.
- A live no-op shared-policy save on `PSOC1WINLINK` recorded administrator
  Steven Griggs (KC4CAW), the target radio, success, and the exact UTC-backed
  event time. The event remained visible after a service restart, proving that
  history is outside the immutable release directory.
- The follow-up service restart returned the browser-owned GUI client to
  Connected while the external SmartSDR client remained in the radio roster.
  Production finished on release `20260728-m4-admin-audit-2` with zero service
  restarts, no warning-level journal entries, and the fail-closed RX health
  response. The regression suite passes with 128 server and 96 browser tests.

Production verification on 2026-07-29:

- Release `20260729-m4-radio-health-agent-033-1` derives one radio-wide
  healthy, busy, degraded, reconnecting, or offline state from radio
  reachability and client capacity, browser-session connection state and age,
  transport heartbeat/stream freshness, and bounded browser-queue pressure.
  Admin displays the state and its supporting session age, last-stream age,
  queue depth, and drop count.
- The release passed 146 server tests, 102 browser tests, and 31 AetherRemote
  tests before activation. A cache-busted production Admin load classified
  ODU-6400, ODU-6600M, and PSOC2/HF/XVTR as healthy and correctly classified
  the single-client PSOC1WINLINK radio as busy while its only GUI slot was in
  use.
- A deliberate Admin release of Steven Griggs's PSOC2 test session released
  exactly one browser connection and one radio session, changed the radio page
  to `An administrator released this radio session`, and added a successful
  durable audit event at 2026-07-29 17:26:58 UTC.
- A controlled restart of only `aetherremote-agent.service` changed its PID
  from 11954 to 12550 and re-established the outbound station link in about
  two seconds. The ODU-6400 browser path was interrupted and its station-side
  projection was released, while an independent PSOC2 browser session stayed
  live with fresh stream activity and no change in client capacity. Both radios
  returned to healthy with 2-of-2 GUI slots free after test cleanup.

Capacity history completed on 2026-07-29:

- Release `20260729-m4-capacity-history-1` samples the server radio catalog every
  15 seconds, records capacity or reachability changes plus 15-minute
  checkpoints, retains at most 24 hours, and enforces a hard cap of 256 samples
  per radio. Admin exposes the chronological server-owned history in a
  collapsed section and renders only the eight most recent samples.
- Deterministic coverage proves change-only sampling, periodic checkpoints,
  hard bounding, and expiration. The candidate passed 148 server and 103
  browser tests before deployment.
- Live production acceptance on PSOC2 recorded the full radio-reported client
  transition `2/2 -> 1/2 -> 2/2` while one browser session connected and then
  expired normally. Admin rendered all three chronological samples, and the
  final state had zero operators, zero sessions, and full client capacity.
- Final reservation-denial acceptance completed on 2026-07-30 using the
  non-administrator account `REMOTES@w4car.org` against PSOC2/HF/XVTR. The
  server returned `This radio is reserved for another account.`, created no
  denied browser/radio session, and left existing sessions unaffected. Radio
  capacity remained unchanged during the denial. The policy audit identified
  Steven Griggs (KC4CAW), PSOC2/HF/XVTR, FLEX-6700 at 10.2.0.12, and target
  account object ID `817cf887-bb22-49fd-9686-802602761bbe`, with a succeeded
  result for setting the reservation. Clearing the reservation also succeeded
  and produced the expected audit outcome.

Remaining:

- [x] Add radio-wide client-capacity history instead of only the current
  radio-reported snapshot.
- [x] Classify and surface per-radio healthy, busy, degraded, reconnecting, and
  offline states from session age, stream activity, and queue pressure.
- [x] Exercise a controlled failure/restart of one radio path and prove that a
  second radio's sessions and streams are untouched.
- [x] Complete live acceptance checks for reservation denial and an intentional
  forced operator release, including their success/failure audit outcomes.
  The 2026-07-30 non-administrator denial completed the remaining reservation
  path; the forced-release success path was already complete.

Acceptance criteria:

- Every administrative action records who, what, when, and the result.
- One radio failing or restarting does not disturb sessions on another radio.
- Policy storage and release activation remain atomic.

## M5 — AetherD engine boundary

Status: **Blocked on accepted RFC stages 3–5**

Goal: replace the prototype's direct receive-only FLEX adapter with the
versioned AetherD control, authentication, and binary data-plane boundary.

Acceptance criteria:

- The browser gateway contains no vendor radio protocol implementation.
- Snapshot/delta convergence and stream backpressure work across the versioned
  AetherD protocol.
- Entra identity is exchanged for a short-lived, capability-limited AetherD
  credential; Microsoft tokens are never forwarded as engine credentials.

## M6 — Remote station connectivity

Status: **Complete**

Goal: securely connect radios that are not on the gateway LAN without exposing
radio control ports to the public Internet or sending timing-sensitive raw
radio traffic through an avoidable WAN hop.

Evidence delivered:

- Independent AetherRemote broker and station-agent services now maintain one
  authenticated outbound TLS WebSocket from the `odu-campus` station network
  to the central broker. The station requires no inbound firewall rule, routed
  VPN, or public radio port.
- The authenticated central selector shows ODU-6400 and ODU-6600M inventory
  from the station agent without receiving either radio's private LAN address.
  A broker restart briefly marked inventory unavailable, then the agent
  reconnected with a fresh instance and the selector returned to four radios
  online without restarting the web gateway.
- Receive-admission protocol 0.2.0 creates only an opaque session ID, stable
  radio ID, GUI-client UUID, and low-bandwidth flag. The station agent resolves
  the advertised identity and performs the enumerated FLEX `client gui`
  registration locally; no raw SmartSDR line, arbitrary command, or LAN route
  crosses the WAN.
- Live ODU-6400 admission on 2026-07-28 returned FLEX client handle
  `24964633`, changed that radio from 2-of-2 to 1-of-2 free, and returned to
  2-of-2 with zero broker sessions after explicit close.
- A separate low-bandwidth ODU-6600M admission returned FLEX client handle
  `090a32aa`, changed only the 6600M from 2-of-2 to 1-of-2 free while the
  ODU-6400 remained 2-of-2, then restored both radios to 2-of-2 with zero
  lingering sessions. Broker, agent, and web gateway restart counters remained
  zero during both checks.
- Remote selector buttons now open a usable, receive-only browser session with
  normalized control snapshots, bounded spectrum/waterfall and audio frames,
  live meters, and enumerated slice/pan controls.
- AetherRemote 0.3.0 adds the bounded receive projection data plane. A
  loopback-only station engine terminates SmartSDR TCP/VITA-49 beside the
  radios; the outbound station link carries only normalized snapshots,
  AETF/AETA spectrum/audio frames, and enumerated slice/pan receive intents.
  The central gateway never learns a station-LAN radio address.
- Remote browser sessions use distinct browser GUI-client UUIDs and the broker
  opens a distinct station receive session for each one. The radio remains the
  live admission authority, and closing or losing the station link
  deterministically releases the projected session.
- Protocol and broker integration tests exercise projected text, binary
  spectrum, and receive-control round trips. Gateway tests prove projected
  snapshots cannot enable transmit and transmit intents are rejected before
  any network send.
- The 2026-07-28 production verification opened ODU-6600M directly in the
  manual VPN profile as client handle `09a95435`. The station reported one
  isolated low-bandwidth session at 5 FPS with live spectrum and audio frames,
  while ODU-6400 remained 2-of-2 free. Explicit release restored both remote
  radios to 2-of-2 and left zero broker and station sessions.
- AetherRemote broker 0.3.2 drains late frames and acknowledgements from
  canceled admissions instead of dropping the station link. The final broker,
  agent, and gateway logs contained no warnings, and all three services
  remained online with zero restarts after activation.
- AetherRemote broker 0.3.3 adds a fail-closed station watchdog with separate
  degraded and disconnect thresholds. A production frozen-link test moved
  `odu-campus` from online to degraded to offline, removed its admitted receive
  session at the disconnect threshold, and left both local radios available.
  The selector showed two local radios online and both ODU radios offline
  during the outage.
- Resuming the station produced a new authenticated instance and a fresh
  radio-authorized browser session with live spectrum at approximately
  834 kb/s. Explicit release returned ODU-6400 and ODU-6600M to 2-of-2 free,
  with zero broker sessions. The broker, web gateway, and station engine
  recorded zero restarts; only the intentionally interrupted station agent
  recorded its earlier controlled restart.
- Production release `20260728-m6-admin-connections-2` adds an Admin-only
  station health view with agent version, instance, last check-in, heartbeat
  and inventory progress, station radios, and isolated remote receive
  sessions. The gateway validates and bounds both broker management feeds and
  never projects the station's private LAN address.
- A live narrow-viewport Admin check at 526 CSS pixels had no horizontal
  overflow. It identified the existing `SmartSDR-Win` GUI as an external
  radio client, including station label, FLEX handle, opaque client ID, and
  Local PTT status, while keeping the AetherSDR-owned GUI and Entra operator
  attribution separate.
- A temporary ODU-6400 browser admission appeared under `odu-campus` as one
  isolated web tunnel with FLEX handle `57810815`, moved only that radio from
  2-of-2 to 1-of-2 free, and appeared in the normal per-radio session
  diagnostics. Cleanup returned the broker to zero receive sessions.
- Radio reconnect recovery now replaces the stale failure footer with
  `Radio connection restored.` after a radio-authoritative connected snapshot.
- The final staged build passes 25 AetherRemote tests, 141 web gateway tests,
  and 99 browser-side control/rendering tests.

Hardening verified on 2026-07-29:

- The station agent now advertises its compiled assembly version instead of a
  hand-maintained configuration value. Agent version `0.3.3` is declared in
  the build, and the runtime config, installer, and example config can no
  longer pin a newly upgraded station to an older reported version. All 31
  AetherRemote tests pass.
- Release `20260729-m4-radio-health-agent-033-1` installed the agent on
  `odu-campus`; both the broker management feed and production Admin page now
  report version `0.3.3`. A later controlled agent restart reconnected with a
  fresh process and station instance without disturbing the local PSOC2 path.

Link-recovery telemetry staged on 2026-07-29:

- Candidate `m6-link-recovery-1` keeps recovery history in the broker without
  changing the station wire protocol. Each station snapshot now includes a
  bounded connection count, last disconnect time and fixed reason, last
  recovery time, and measured outage duration. Watchdog timeouts retain the
  specific `heartbeat_timeout` reason instead of being overwritten by later
  socket disposal.
- The web gateway validates connection counts, timestamps, reason identifiers,
  consistency, and a seven-day duration ceiling before exposing the data.
  Admin adds a Link Recovery metric with reconnect count, outage duration,
  recovery age, and a human-readable reason. Older broker snapshots remain
  compatible and default to an initial link with no recorded recovery.
- The candidate passes 32 AetherRemote tests, 149 web gateway tests, and 104
  browser tests.
- Production release `20260729-m6-link-recovery-1` completed a full-stack
  activation with configuration and credentials preserved. The broker first
  accepted the existing agent connection, then measured the station-agent
  upgrade as one reconnect with a 2.6-second outage and the fixed reason
  `station link closed`. Admin rendered the same result as `1 reconnects`,
  while both ODU radios remained healthy at 2-of-2 free with zero receive
  tunnels. Web, broker, station engine, and agent services remained active with
  zero unexpected restarts and no warning-level journal entries.

Constrained-WAN tooling staged on 2026-07-29:

- Candidate `m6-wan-soak-1` installs a root-owned station helper that applies
  Linux `netem` only to outbound IPv4 TCP traffic for the authenticated broker
  endpoint. SSH and all other station traffic remain on the normal `fq_codel`
  path. The helper refuses unexpected qdisc layouts, validates all host, IP,
  port, interface, profile, and duration inputs, records active state under
  `/run`, and restores the normal root qdisc on completion or interruption.
- Mild, constrained, and severe profiles provide repeatable delay, jitter,
  loss, and rate ceilings. The full-stack installer checksums and syntax-checks
  the helper, validates a least-privilege sudoers rule, installs it as
  `/usr/local/sbin/aetherremote-wan-soak`, and verifies the inactive status.
- The same candidate fixes the Admin audit result badge so the success or
  failure label sizes to its contents and is centered instead of stretching to
  the two-line activity row. The staged tree passes 32 AetherRemote tests, 149
  web gateway tests, and 105 browser tests.
- Production release `20260729-m6-wan-soak-1` installed the guarded helper and
  badge fix. Browser inspection confirmed the `SUCCEEDED` label in a 26-pixel
  content-sized flex pill centered in both axes, and the deployed stylesheet
  carried the expected `m6-wan-soak-1` revision.
- A five-minute mild soak applied 40 ms delay with 10 ms jitter, 0.2% loss, and
  an 8 Mbit/s ceiling only to the station broker flow. Simultaneous PSOC2 and
  ODU-6400 sessions remained healthy with zero browser reconnects, missing
  audio packets, queue drops, or service warnings. The helper restored the
  original root `fq_codel` with zero backlog, and both sessions released back
  to 2-of-2 free with no orphaned resources.
- A constrained soak applied 90 ms delay with 25 ms jitter, 1% loss, and a
  3 Mbit/s ceiling. ODU-6400 stayed admitted while the station link recovered
  once in 2.005 seconds; the browser recorded zero reconnects, the remote
  projection reopened transparently, and automatic adaptation moved traffic to
  the low profile at roughly 361 kbit/s. Audio reported zero missing packets,
  browser queues remained 0-of-64 with zero drops, and the independent PSOC2
  session remained healthy throughout. Final broker state was online with both
  ODU radios 2-of-2 free and zero receive sessions; gateway cleanup removed
  both isolated test sessions deterministically.
- The constrained run exposed an agent send race: a broker-response send could
  reach `ClientWebSocket.SendAsync` after the socket had entered `Aborted`.
  Agent `0.3.4` checks state after acquiring the shared send gate and normalizes
  a close during send to an I/O disconnect. Deterministic tests cover
  abort-while-waiting and abort-during-send without invoking a send on a
  known-aborted socket. All 34 AetherRemote tests pass.
- Production release `20260729-m6-agent-send-guard-034-1` installed agent
  `0.3.4`. A fresh five-minute constrained repeat held 90 ms delay with 25 ms
  jitter, 1% loss, and a 3 Mbit/s ceiling while PSOC2 and ODU-6400 remained
  connected. The station connection count stayed at 4, the remote projection
  stayed on transport attempt 1, and both browser sessions recorded zero
  reconnects, missing audio packets, underruns, queue drops, or queue backlog.
  The agent emitted no `Aborted`, invalid-state, WebSocket, or station-link
  failure warning during the window. The helper restored `fq_codel`
  automatically; both radios returned to 2-of-2 free with zero operators,
  browser sessions, or remote receive tunnels, and both station services
  remained active with zero process restarts.

Least-privilege security review staged on 2026-07-29:

- The broker intentionally listens on both loopback and the station-facing
  `10.2.0.254:5090` address so the reverse proxy can reach `/station/v1`.
  Before hardening, that LAN-facing listener also returned authentication-aware
  responses for `/api/stations`, `/api/receive-sessions`, and `/receive/v1`,
  making the full management surface reachable before bearer validation.
- Candidate `m6-broker-loopback-boundary-1` adds a central fail-closed network
  boundary: every broker `/api/*` request and `/receive/v1` WebSocket must
  originate from IPv4, IPv6, or IPv4-mapped loopback. `/healthz` and
  `/station/v1` remain available on the station-facing listener. Rejected
  privileged requests return 404 so the surface is not advertised.
- Production release `20260729-m6-broker-loopback-boundary-1` activated with
  zero broker restarts or warnings. Loopback retained the expected 200/401/400
  management behavior, while the `10.2.0.254:5090` listener changed
  `/api/stations`, `/api/receive-sessions`, and `/receive/v1` to 404.
  `/station/v1` remained reachable for WebSocket upgrade. The authenticated
  Admin APIs still returned 200, `odu-campus` remained online on agent `0.3.4`,
  both ODU radios and PSOC2 were healthy at 2-of-2 free, and no operators,
  browser sessions, or remote tunnels remained.
- Candidate `m6-credential-split-1` removes the all-powerful gateway bearer.
  Runtime authority is limited to station inventory, receive-session
  inventory/admission/cleanup, and `/receive/v1`; station-administration
  authority is limited to credential inventory, enrollment-code creation, and
  enable/disable/revoke actions. Either credential is rejected with 401 from
  the other authority's endpoints, and broker startup fails if the verifiers
  are missing or identical.
- The gateway uses separate root-configured credential files. Its runtime path
  is used only by catalog and projection services, while Admin station-security
  actions use the administration path. The deployment migration preserves the
  existing secret as the runtime credential, generates a new 256-bit
  administration credential, removes the legacy configuration keys, verifies
  the four-way authorization matrix, and rolls back binaries, configuration,
  and the newly generated secret if either service fails. Raw credentials are
  neither logged nor placed in process arguments. The candidate passes 57
  AetherRemote tests, 151 gateway tests, and 105 browser tests; both
  self-contained Linux publishes and the embedded root installer parse
  successfully.
- Production release `20260729-m6-credential-split-1` completed the atomic
  migration with zero broker or gateway restarts and no warning-level log
  entries. The legacy environment key was removed; the runtime and
  administration files are distinct, mode 0600, and owned by `flexweb`.
  Live authorization checks proved runtime inventory/session access returned
  200 while credential inventory and enrollment returned 401; the
  administration credential showed the inverse behavior, with credential
  inventory returning 200 and an intentionally invalid enrollment request
  reaching validation as 400 without creating a code. A real ODU-6400 browser
  admission reached `RADIO: LIVE`, appeared as one operator, one browser
  session, and one remote tunnel, then cleaned up to 2-of-2 free with zero
  operators, sessions, or tunnels. `odu-campus` remained online on agent 0.3.4.
- Candidate `m6-capability-grants-1` makes station capabilities an admission
  authority instead of informational metadata. The broker binds the immutable
  capability list to the current authenticated station connection and rejects
  receive admission with `409 station_capability` before allocating a session
  or sending an open command unless `receive-projection-v1` is present. The
  denied station remains online and its receive-session inventory stays empty.
- Agent `0.3.5` advertises only grants explicitly configured under
  `Agent:Capabilities`. A missing list fails startup; `[]` is an intentional
  deny-all grant; duplicate, malformed, and unknown entries such as a future
  transmit capability fail closed. The station installer migrates the current
  implicit receive grant to an explicit list while preserving the live
  configuration owner and mode, and restores the original configuration with
  the previous binaries if startup or broker reconnection fails. The live
  `odu-campus` configuration is mode 0640, owned by `root:aetherremote`, and
  currently requires this migration because the property is absent. The staged
  tree passes 59 AetherRemote tests, 151 gateway tests, and 105 browser tests;
  broker/agent Linux publishes and all embedded deployment helpers validate.
- Production release `20260729-m6-capability-grants-1` migrated the station to
  explicit `Agent:Capabilities = ["receive-projection-v1"]` while preserving
  mode 0640 and `root:aetherremote` ownership. Agent `0.3.5` reconnected and the
  broker/Admin inventory reported only that grant; Admin rendered it as
  `Receive projection`. A real ODU-6400 admission passed the broker capability
  gate and reached `RADIO: LIVE`, appearing as one operator, one browser
  session, and one remote receive tunnel at 1-of-2 free. Closing the browser
  deterministically returned the station to zero tunnels and ODU-6400 to
  2-of-2 free with zero operators or sessions. Web, broker, station engine, and
  agent services remained active with zero process restarts; the WAN soak was
  inactive and no warning-level journal entries were emitted. Denial without
  the grant remains covered by the integration test that proves
  `409 station_capability`, no session allocation, and no command delivery.
- Candidate `m6-short-lived-station-link-1` removes the long-lived station
  credential from persistent WebSocket authentication. The agent uses the
  device credential only for `POST /station/v1/token`, requesting its explicit
  capability list. The broker returns a random 256-bit, no-store link token
  valid for 60 seconds; only the hash is retained in memory. The token is bound
  to one station and one exact capability set, is invalidated by a newer token
  or station revocation, and is consumed on the first `/station/v1` upgrade.
  Reuse, expiry, station mismatch, direct use of the device credential on the
  WebSocket, and hello/token capability mismatch all fail closed.
- The token endpoint bounds requests to 4 KiB and requires the same forwarded
  HTTPS policy as the station WebSocket. Outstanding tokens are capped and
  expired entries are pruned. Agent `0.3.6` validates the returned token,
  expiry, and capability echo before connecting, derives the HTTPS token URL
  from the configured WSS endpoint, and requests a fresh token for every
  reconnect. Disabling or revoking a station invalidates every outstanding
  token immediately, and token responses are explicitly `Cache-Control:
  no-store`. The staged tree passes 68 AetherRemote tests, 151 gateway tests,
  and 105 browser tests; self-contained broker and agent Linux publishes and
  deployment-script syntax checks pass.
- The full-stack rollout is station-first. Agent `0.3.6` can connect to the old
  broker only when `/station/v1/token` is genuinely absent, using a logged
  upgrade-only compatibility path. The gateway then activates the token-only
  broker and does not commit until its authenticated inventory shows a fresh
  online station with `receive-projection-v1`. Failure to establish that link
  triggers the existing gateway rollback, after which the upgraded agent can
  reconnect to the old broker. The live broker migration writes an explicit
  60-second token lifetime into the root-owned configuration.
- Production release `20260729-m6-short-lived-station-link-1` completed after
  the first full-stack invocation upgraded the station but stopped before the
  gateway phase with a local shell EOF error. Agent `0.3.6` kept the station
  online through its logged compatibility path until a gateway-only completion
  activated the token-only broker. The agent then obtained a 60-second token
  and established a fresh link with no further compatibility warning.
- Live boundary checks returned 200 and `Cache-Control: no-store` from token
  issuance, rejected the long-lived device credential at `/station/v1` with
  401, accepted the temporary token once with WebSocket 101, and rejected its
  replay with 401. A real ODU-6400 browser admission reached `RADIO: LIVE`,
  appeared as one remote receive tunnel at 1-of-2 free, and cleaned up to zero
  broker sessions and 2-of-2 free. Agent, station engine, broker, and FlexWeb
  remained active with zero process restarts; the WAN soak was inactive and no
  unexpected warning-level entries followed the secure reconnection.
- Live station credential disable/re-enable acceptance completed on 2026-07-30
  for `odu-campus` while an independent PSOC2 browser session remained
  connected. Disabling the station made both ODU radios unavailable as
  expected. Re-enabling it restored both radios after approximately 5–10
  seconds, while the PSOC2 stream continued without interruption. This proves
  the core fail-closed station isolation and recovery path. Audit outcomes,
  fresh-instance identity, and post-recovery capacity were not separately
  reported in this run and are not claimed here.
- Final live credential-lifecycle acceptance completed on 2026-07-30. Admin
  revoked `odu-campus` with a succeeded audit event attributed to Steven
  Griggs (KC4CAW); the old station credential remained unable to reconnect.
  A fresh one-time re-enrollment through `aetherremote-enroll` returned the
  station to `enabled` with purpose `reenroll` and recorded credential rotation
  at 2026-07-30 19:00:32 UTC. The agent reconnected in approximately five
  seconds with a fresh station instance, both ODU-6400 and ODU-6600M returned
  available, no stale receive sessions reappeared, and the independent PSOC2
  stream remained unaffected. The operator marked the complete revoke,
  rotation, and recovery workflow as a pass.

Deployment:

- Run AetherD as the station node on a small Linux system or VM on the same LAN
  as each radio.
- Provide a no-VPN station link: AetherD initiates a persistent TLS WebSocket
  connection to the central gateway over outbound TCP 443. The gateway routes
  authenticated browser sessions through that application-level link; remote
  radio sites require no inbound port forwarding.
- Support WireGuard or another administrator-managed private network as an
  optional transport profile, not a requirement for station enrollment.
- Keep SmartSDR TCP/VITA-49, radio discovery, DSP, stream draining, and
  fail-closed safety local to the station; send only versioned control/state,
  bounded spectrum/waterfall frames, meters, and compressed audio to the
  gateway.

Scope:

- Station enrollment, cryptographic device identity, key rotation, revocation,
  and a stable station/radio identifier independent of changing LAN addresses.
- One-time enrollment establishes a station credential separate from Entra
  user identity. The long-lived device credential obtains only short-lived,
  capability-limited station-link sessions and is never exposed to browsers.
- Heartbeats, offline/degraded state, reconnect with backoff, bounded queues,
  and deterministic cleanup after a tunnel or station failure.
- A TLS WebSocket station-link profile that works through ordinary NAT and
  HTTPS-aware firewalls, plus an optional hub-and-spoke WireGuard profile.
  Neither profile exposes radio TCP/UDP ports to browsers.
- Amend or extend the accepted AetherD RFC before implementation: its current
  remote-security decision ships WireGuard, while the no-VPN profile adds an
  outbound application-level station transport.
- Resolve the architecture difference between M1's distinct FLEX GUI client
  per browser page and the accepted AetherD RFC's per-client projection over
  shared radio state. The production design must preserve radio-authoritative
  admission and the operator isolation accepted for this web client.
- An optional routed-WireGuard pilot for the current receive-only `FlexRx`
  adapter may prove connectivity, but it is not the production boundary and
  must not become a public raw-radio proxy.

Acceptance criteria:

- A station behind NAT, with no inbound firewall rule and only outbound HTTPS
  access, enrolls and presents its radios in the authenticated central radio
  selector.
- Losing the WAN or station node marks its radios unavailable, releases or
  safely retains resources according to the approved session design, and never
  disturbs radios at another station.
- Reconnection restores a fresh radio-authoritative snapshot; stale client
  state is never replayed over newer radio state.
- A constrained-WAN soak meets the published control, spectrum, waterfall,
  meter, and audio latency/bandwidth budgets with zero crossed operator state.
- Security review confirms that Entra user identity, station device identity,
  AetherD capability grants, and any future TX lease are separate,
  least-privilege credentials.

## M7 — Transmit safety

Status: **Complete — production browser MOX/PTT and every required loss path accepted**

Goal: enable transmit only after engine-side arbitration can prove deliberate
operator intent and force-unkey on every loss path.

Final production release: `20260803-m7-browser-tx-capability-refresh-1`.
The accepted source was merged by PR #37 as merge commit
`e7754f43863b1d12da59884de6510ebace9f1277`.

Milestone state:

- **Safety foundation and loss-path HIL: Complete.** The physical-radio lease,
  exact-owner gate, signed station-local command path, independent unkey-only
  supervisor, production/HIL binary separation, and every required owner and
  liveness loss path have accepted evidence.
- **Production browser TX integration: Complete for MOX/PTT.** An authorized
  browser can acquire the exact physical-radio lease, deliberately key and unkey
  through the station-local safety boundary, maintain purpose-bound heartbeats,
  and receive success only after radio-authoritative confirmation.
- **Additional transmit surfaces remain deliberately unavailable.** Browser
  TUNE, microphone transmit audio, and CW expose no executable production path.
  Enabling any of those surfaces requires a separate milestone increment with
  explicit operator action, exact ownership, loss-path safety, and production
  HIL acceptance.
- **Production and mainline are aligned.** The merged main tree is byte-identical
  to the accepted deployed tree; no post-merge redeployment was required.

The detailed evidence below is chronological. Present-tense statements in older
phase entries describe the state at that checkpoint and are superseded by the
final closure evidence immediately above the acceptance criteria.

- Replaced the per-browser prototype lease with one process-wide authority keyed
  by the normalized physical radio ID. Separate browser sessions using the same
  radio can no longer obtain independent leases, while different radios remain
  independent.
- Leases now use a random opaque ID, are bounded to 1–15 seconds, require the
  exact radio/session/browser owner for renewal and release, expire under a
  250 ms watchdog, and are released on browser disconnect or session disposal.
  The opaque ID is returned only to the holder; welcome snapshots and broadcasts
  expose a redacted holder/expiry status.
- Added a station-local FLEX TX occupancy registry. The first production pass
  incorrectly treated `local_ptt=1` as active transmit; a live receive-only
  PSOC2 session proved that field identifies local-PTT ownership even while the
  radio is unkeyed. The corrected classifier subscribes to `sub tx all` and
  uses the authoritative `interlock` state plus `tx_client_handle`. Client
  roster and `local_ptt` data now enrich owner labels only. READY/RECEIVE are
  idle; transmitting, transition, fault, conflicting, missing-owner, and stale
  states fail closed as Aether-owned, external, ambiguous, or unknown.
- AetherSDR-owned and external TX are deliberately separate. Lease loss may
  eventually force-unkey only a keying action proven to belong to the matching
  AetherSDR lease/client. It must never unkey SmartSDR, Maestro, hardware PTT,
  or another external FLEX client.
- The browser MOX/PTT/TUNE/CW/audio path remains unreachable and production
  configuration remains receive-only. The station-local command ownership and
  loss-path safety foundation is accepted, but its production browser integration
  has not yet been implemented or enabled.
- The private station TX command-gate increment is staged in source. The gate is
  browser-inaccessible and not registered in production DI. It requires the
  exact physical-radio lease, radio session, browser client, AetherSDR FLEX
  handle, fresh idle interlock, and one exclusive Local PTT authority matching
  that handle before creating a key-pending intent. SmartSDR owning Local PTT
  blocks the request even while the radio is idle.
- The gate does not claim TX until the radio reports `TRANSMITTING` with the
  exact `tx_client_handle`. Lease loss starts an ownership-safe unkey only while
  that exact handle remains proven. External, ambiguous, stale, or replaced
  ownership never receives a global unkey command. Unkey retries are limited to
  three and evaluated by a private 100 ms watchdog.
- Command transport distinguishes explicit radio rejection from an unknown
  socket outcome. Unknown key/unkey results retain the guarded intent for
  interlock reconciliation instead of assuming the command failed. Admin now
  exposes separate `TX OCCUPANCY` and `PTT AUTHORITY` diagnostics.
- The real FLEX `xmit 1`/`xmit 0` adapter is protected by the compile-time
  `EnableTxHil=true` switch. A normal self-contained production publish contains
  zero keying command strings; an explicit HIL publish contains exactly the two
  expected commands. Neither build registers the gate with the browser.
- The staged tree passes 190 server tests, 106 browser tests, and 70
  AetherRemote tests. Coverage includes cross-session lease exclusivity,
  opaque-ID ownership and redaction, expiry signaling, Local PTT freshness,
  SmartSDR Local PTT denial while idle, radio-confirmed exact-handle ownership,
  unknown command outcomes, bounded unkey retries, SmartSDR external TX
  protection, ownerless hardware PTT, ambiguous/stale observations, reporter
  disconnect behavior, and Admin TX/PTT diagnostics.
- Production acceptance on 2026-07-29 activated
  `20260729-m7-interlock-occupancy-1` and verified live coexistence on PSOC2.
  SmartSDR-Win on `STEVENS-SURFACE` remained connected as an external GUI
  client and held Local PTT ownership while a separate AetherSDR browser GUI
  reached `RADIO: LIVE`. The radio-authoritative interlock remained idle, so
  Admin correctly displayed `TX OCCUPANCY: Idle` and an empty occupant list;
  the receive-only MOX control remained disabled. Closing only the AetherSDR
  browser session returned PSOC2 to 1 of 2 free with zero web operators,
  sessions, and broker receive tunnels, proving the SmartSDR client was not
  displaced or released. All four services remained active with zero restart
  counters, no post-deployment warnings, and public health continued to report
  `transmitEnabled=false`.
- Production acceptance on 2026-07-29 activated
  `20260729-m7-station-tx-gate-foundation-1`. The deployed production
  executable contained zero UTF-16 `xmit 1`, `xmit 0`, or
  `FlexStationTxCommandTransport` strings, proving the HIL-only adapter was not
  included. Public, broker, and station health all continued to report
  `transmitEnabled=false`; the four services remained active with zero restart
  counters and no post-deployment warnings.
- A live PSOC2 coexistence test connected one receive-only AetherSDR browser
  alongside SmartSDR-Win on `STEVENS-SURFACE`. Admin reported
  `TX OCCUPANCY: Idle` with no TX occupants and `PTT AUTHORITY: External` for
  the SmartSDR handle, while MOX remained disabled. The AetherSDR session
  streamed normally with zero queue drops and zero missing audio packets.
  Closing only the AetherSDR tab returned PSOC2 to 1 of 2 free, with zero web
  operators, sessions, and broker tunnels; the remaining occupied GUI slot
  confirmed SmartSDR stayed connected and was not displaced.
- Added a standalone `AetherSDR.TxHil` console harness outside the web gateway.
  The web project explicitly excludes `tx-hil/**` and `tx-hil-tests/**`. A clean
  production publish contains zero `xmit 1`, `xmit 0`, exact CWX-ID command,
  HIL-class, or TX-audio-stream creation strings. The standalone HIL DLL alone
  contains exactly one `xmit 1`, one `xmit 0`, and one exact
  `cwx send "KC4CAW" 1`, with no DAX or remote TX-audio stream creation.
- The first HIL workflow is hard-bound to PSOC2 radio ID
  `FLEX:1121-1104-6700-2912` and chassis serial
  `1121-1104-6700-2912`. It requires an explicitly supplied clear frequency
  from 14.225000 through 14.350000 MHz and is fixed at USB, ANT1, 1 W, and
  100 ms for the initial interlock pulse. It creates and verifies its own
  GUI-owned pan, waterfall, and TX slice, then switches only that owned slice to
  CW for automatic station identification.
- Before Local PTT transfer, the harness captures RF power, DAX routing,
  microphone selection, VOX state, and CWX WPM/QSK/break-in delay. It applies
  only transmit fields that actually differ from the silent target and restores
  only changed fields after fresh idle. This avoids firmware rejection of
  unchanged `mic_selection=PC` writes. Partial FLEX slice statuses are merged
  rather than replaced, and partial resource creation cleans its own slice,
  waterfall, and pan on failure.
- CWX identification is constrained to `KC4CAW` at 20 WPM. The radio's existing
  QSK and break-in delay are preserved because PSOC2 reports those fields but
  rejects `cwx qsk_enabled` writes on this command path. The harness parses the
  radio's insertion-start reply, computes the final callsign index, requires a
  fresh `cwx sent=` value to reach that index, requires exact-handle Aether-owned
  TX to have been observed, and then requires fresh idle. Abnormal cleanup sends
  `cwx clear` and may send `xmit 0` only while the exact HIL client is proven to
  own TX; external or ambiguous ownership is never globally unkeyed.
- Actual pulse execution requires a five-minute, mode-0600 arm manifest created
  only after the compact operator assertion
  `KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF`. The random token is never stored
  in plaintext and a valid manifest is deleted before the state-changing radio
  connection. Expired, replayed, malformed, wrong-radio, out-of-range-frequency,
  non-ANT1, non-USB, over-1-W, or over-100-ms requests fail closed.
- Live no-RF PSOC2 verification passed. CWX settings round-tripped 30→20→30 WPM
  while QSK stayed enabled and break-in delay stayed 5 ms; the verifier had no
  `cwx send` or `xmit 1` path. The full preflight then created and verified a
  14.250 MHz ANT1 slice, USB→CW mode transition, 1 W, exclusive HIL Local PTT,
  and idle interlock, before restoring 100 W and removing the test resources.
  A post-cleanup inventory showed no leaked 14.250 MHz slice or display object.
- The staged tree passes 35 HIL tests, 210 server tests, 106 browser tests,
  and 70 AetherRemote tests. Coverage includes independent CWX sent/config
  freshness, queue-index arithmetic, exact-owner drain and idle confirmation,
  four purpose-bound one-time manifest paths, unknown command outcomes,
  ownership-safe emergency unkey, external-owner protection, partial slice-
  status merging, heartbeat identity, exact connected-to-disconnected engine
  transitions, bounded emergency retries, startup reconciliation, and
  transmit/CWX restoration.
- Live HIL acceptance completed on 2026-07-30 at 14.250 MHz. A fresh five-minute
  arm manifest verified PSOC2 serial `1121-1104-6700-2912`, zero external GUI
  clients, ANT1, USB, 1 W, 100 ms, idle interlock, and fresh CWX configuration.
  The radio confirmed `Keyed` for exact client handle `0x49dfd8b9`, then
  confirmed `Idle` after ownership-safe unkey. The measured keyed-to-idle
  interval was 186.7982 ms, including radio status/command latency around the
  fixed 100 ms hold.
- The harness then switched only its owned slice to CW and sent `KC4CAW` at
  20 WPM. FLEX returned insertion index 0; `cwx sent=` reached final index 5,
  exact-handle Aether-owned TX was observed, and the radio returned idle after
  queue drain. Post-test inspection confirmed RF power restored to 100 W,
  `dax=1`, `mic_selection=PC`, `vox_enable=0`, CWX restored to 30 WPM with QSK
  enabled and 5 ms break-in delay, no leaked 14.250 MHz HIL slice, and the
  one-time arm manifest consumed. No external client was unkeyed or displaced.
  The controlling operator also visually observed PSOC2's transmit indication
  through the remote camera, corroborating the radio telemetry that the test
  physically keyed the transmitter.
- Added a private independent station TX safety supervisor with no key method
  and an unkey-only transport interface. Its arm record binds an engine
  instance, TX lease, browser/session identity, exact protected FLEX client
  handle, and bounded heartbeat deadline. It arms only from fresh idle with
  exclusive Local PTT authority for that exact handle. Heartbeat expiry or an
  explicit owner-loss signal may issue unkey only while fresh occupancy has one
  TX occupant whose handle exactly matches the protected arm. SmartSDR,
  Maestro, hardware PTT, stale status, ambiguous ownership, or a replaced
  handle never receives global unkey.
- The supervisor retains guarded intent across an unavailable emergency
  transport and unknown/rejected unkey outcomes, retries at most three times,
  requires radio-confirmed idle before clearing, and starts disarmed so a newly
  restarted watchdog never invents ownership of an existing transmission.
  Thirteen focused supervisor tests plus the existing fifteen gate tests pass
  28/28. A standalone simulation command runs eight fault scenarios—idle
  expiry, protected-TX expiry, browser loss, external-owner protection, unknown
  outcome, transport outage, startup reconciliation, and bounded retry
  exhaustion—with no radio connection.
- A live non-GUI observer preflight passed on PSOC2. Separate engine handle
  `0x364debb0` and observer handle `0x3ef379d5` were established; the observer
  was not GUI-registered, independently saw the engine's Local PTT authority,
  armed and accepted an exact heartbeat while idle, then disarmed with zero
  unkey commands and no key capability.
- The forced-unkey operation now uses a distinct version-2 arm-manifest purpose
  `independent-heartbeat-expiry`; it cannot be launched with a normal
  `operator-unkey-pulse` token, and the inverse is also rejected. The live
  two-connection no-RF preflight staged engine handle `0x49764071`, observer
  handle `0x638b175e`, one owned 14.250 MHz ANT1 slice, the silent 1 W route,
  exact engine Local PTT, and a 750 ms future heartbeat expiry. It completed
  with `rfEmitted=false`, zero observer unkeys, restored 100 W and the original
  CWX/transmit settings, and left no test slice or display resource.
- Added a third purpose-bound HIL operation,
  `independent-browser-session-loss`. Its token cannot launch the normal pulse
  or heartbeat-expiry operation, and neither of those tokens can launch it.
  The live no-RF preflight used engine handle `0x41cfa0f3` and non-GUI observer
  handle `0x0d0fc5b3`, staged the owned 14.250 MHz ANT1 slice and silent 1 W
  route, released exactly one synthetic controlling-session TX lease, delivered
  the explicit `browser-session-lost` supervisor signal, and disarmed from idle
  with zero unkey commands and `rfEmitted=false`. Post-preflight inspection
  confirmed 100 W, the original TX/CWX settings, and no leaked test slice.
- Live independent browser-session-loss acceptance completed on 2026-07-30 at
  14.257 MHz. The purpose-bound manifest used engine handle `0x2f7b3e2b` and
  non-GUI observer handle `0x3337cca0`. The engine sent exactly one key and zero
  unkeys. Releasing the exact controlling session removed one TX lease and
  delivered `browser-session-lost`; the observer independently proved the exact
  engine handle still owned TX and sent exactly one unkey with no key
  capability. The safety unkey request followed session loss by 35.877 ms,
  FLEX confirmed idle 26.9665 ms later, and total keyed-to-idle time was
  69.7106 ms.
- The operation then sent CW `KC4CAW` at 20 WPM. FLEX reported insertion index
  12 and final sent index 17, exact-handle TX was observed, the queue drained,
  and the radio returned idle. Post-test inspection confirmed 100 W,
  `dax=1`, `mic_selection=PC`, `vox_enable=0`, CWX 30 WPM/QSK-on/5 ms, no
  leaked 14.257 MHz resources, and the session-loss manifest consumed. The
  clean production publish contains zero key, unkey, CW-ID, session-loss HIL,
  emergency-unkey, or TX-audio-stream command paths; the HIL DLL alone retains
  exactly one key, one unkey, and one CW-ID command.
- Added an exact-identity station-engine connection monitor. It has no radio
  command transport and may signal the unkey-only supervisor only after it has
  observed the active arm's exact engine instance, lease, and FLEX handle in a
  connected state followed by a disconnected state. A mismatched engine,
  lease, or handle; startup while disconnected; and repeated disconnect
  observations never invent ownership or send duplicate immediate unkeys.
  Seven monitor cases plus the gate and supervisor suites pass 35/35.
- Added a fourth purpose-bound HIL operation,
  `independent-engine-connection-loss`. This first increment injects loss of
  the station engine's TX command channel after keying; it deliberately retains
  the engine FLEX status session only for independent evidence, CW
  identification, restoration, and resource cleanup. It is therefore not yet
  the later full TCP/process-kill test. The disconnected gate cannot issue
  unkey or any post-loss command, while the independent non-GUI observer
  remains the sole emergency-unkey path.
- The live no-RF engine command-channel-loss preflight passed at 14.257 MHz
  with engine handle `0x6417899a` and observer handle `0x07222ab7`. It staged
  the owned ANT1 slice and silent 1 W route, independently verified Local PTT,
  observed the exact command channel connected, injected its loss while idle,
  and disarmed with zero engine keys, zero engine unkeys, zero post-loss engine
  attempts, zero observer unkeys, and `rfEmitted=false`. Post-preflight
  inspection confirmed idle, 100 W, the original TX/CWX settings, and no leaked
  14.257 MHz test resource.
- Live engine command-channel-loss acceptance completed on 2026-07-30 at
  14.262 MHz. Engine handle `0x7ec86b98` sent exactly one key and zero unkeys;
  non-GUI observer handle `0x5ca70331` sent the only unkey after the exact
  connected-to-disconnected transition. No post-loss engine command was
  attempted. The observer requested unkey 30.3111 ms after injected connection
  loss, FLEX confirmed idle 26.8505 ms later, and total keyed-to-idle time was
  60.6095 ms. The disconnected engine gate reconciled fail-closed to
  `flex_client_lost`/Faulted.
- The operation then transmitted CW `KC4CAW` at 20 WPM, with insertion index 18
  and final sent index 23; exact-handle TX was observed, the queue drained, and
  idle was confirmed. The controlling operator independently tuned PSOC1 and
  audibly copied the callsign, proving the latest command-channel-loss test over
  the complete RF path. Final inspection confirmed 100 W, DAX on, PC mic route,
  VOX off, CWX 30 WPM/QSK-on/5 ms, no leaked 14.262 MHz resources, and the
  purpose-bound manifest consumed.
- Added a fifth purpose-bound operation,
  `independent-engine-process-loss`, which launches the engine as a separate
  one-time child process. The five-minute operator manifest creates a second
  mode-0600 child plan valid for only 30 seconds; it stores only a token hash,
  binds the authorizing parent PID/start time and exact radio topology, and is
  deleted before the child connects. The parent controls the child only through
  prefixed JSON evidence and a one-time `key` stdin instruction. Failure is
  injected with `Process.Kill(entireProcessTree: true)`; the child has no
  graceful unkey or cleanup opportunity.
- The live true-process no-RF preflight passed at 14.262 MHz. Child PID 47030,
  FLEX handle `0x5eb82d3b`, and non-GUI observer handle `0x17d206b8` established
  the exact idle topology. The child was killed with exit code 137, its roster
  entry disappeared, its test-frequency resources were removed by the radio
  before cleanup, and the supervisor disarmed from idle with zero observer
  commands and `rfEmitted=false`. Process kill-to-exit was 83.4178 ms; roster
  removal followed 1058.4511 ms later; the safety action followed roster loss
  by 1.8628 ms. Final inspection confirmed idle, 100 W, original TX/CWX
  settings, and no 14.262 MHz resource.
- The staged tree now passes 42 HIL tests, 210 server tests, 106 browser tests,
  and 70 AetherRemote tests. A clean production publish contains zero key,
  unkey, CW-ID, process-loss child, or TX-audio-stream commands; the standalone
  HIL DLL alone contains exactly one key, one unkey, and one CW-ID command.
- Live true engine-process/TCP-loss acceptance completed on 2026-07-30 at
  14.262 MHz. Child PID 48741 and FLEX handle `0x379a01d5` sent exactly one key
  and zero unkeys, then the parent killed the entire child process tree. The
  child exited with code 137, no graceful child cleanup ran, its FLEX roster
  entry and test resources disappeared, and non-GUI observer handle
  `0x40efbbdc` issued the sole unkey with no key capability. Radio-confirmed
  keyed-to-idle time was 3782.7442 ms.
- The process-loss operation then sent CW `KC4CAW` at 20 WPM, observed exact
  owned TX, drained the queue, and returned idle. Final inspection confirmed
  zero TX occupants, 100 W, DAX on, PC microphone selection, VOX off, CWX
  restored to 30 WPM/QSK-on/5 ms, no leaked process-test resources, and both
  the outer manifest and child plan consumed. Analysis showed 3629.5864 ms of
  the 3782.7442 ms keyed-to-idle interval was spent waiting for FLEX roster
  removal after the child had already exited.
- The process-loss trigger was optimized so verified OS-process exit signals the
  exact-identity connection monitor immediately; FLEX roster removal remains a
  required later postcondition instead of blocking emergency reconciliation.
  A second live no-RF process-kill preflight passed with child PID 49188, child
  handle `0x5c4f74b2`, and observer handle `0x1e97e788`. Kill-to-exit was
  79.6503 ms, process-exit-to-safety-action 1.7401 ms, and
  safety-action-to-idle 3.6919 ms, while roster removal completed 1568.3875 ms
  after process exit. It issued zero radio commands, reported
  `rfEmitted=false`, removed child resources before cleanup, and restored the
  radio cleanly. A subsequent idle inspection exposed that a direct no-RF run
  may preserve an already-reduced station power setting, so the HIL now includes
  an idle-only `restore-idle-defaults` command and the process-loss preflight
  refuses to start unless PSOC2 is already at the known 100 W/DAX-on/PC-mic/
  VOX-off baseline. Recovery from 1 W to 100 W completed with zero RF and zero
  key/unkey commands. A repeated optimized no-RF preflight then passed with
  child PID 50521: exit-to-safety-action 1.8242 ms,
  safety-action-to-idle 3.8122 ms, roster cleanup 1872.6752 ms after exit, and
  final independent inspection confirmed idle and the full 100 W baseline.
- Optimized live RF re-acceptance completed on 2026-07-30 at 14.262 MHz. Child
  PID 51514 and FLEX handle `0x5d842625` sent one key and zero unkeys before
  the full process tree was killed with exit code 137. Non-GUI observer handle
  `0x5fb12ab5` issued the sole unkey with no key capability. Keyed-to-idle time
  improved from 3782.7442 ms to 1199.0572 ms. Process kill-to-exit was
  110.0109 ms; process-exit-to-safety-action completion was 1079.5545 ms;
  radio idle followed 0.914 ms later; roster removal followed process exit by
  1080.7058 ms. CW `KC4CAW` at 20 WPM completed with exact-owned TX observed,
  and final inspection confirmed idle, zero TX occupants, 100 W, DAX on, PC
  mic, VOX off, CWX 30 WPM/QSK-on/5 ms, no leaked resources, and both one-time
  files consumed. Additional HIL instrumentation now separates safety-signal,
  unkey-dispatch, command-completion, idle, and roster-loss timestamps.
- Replacement-engine startup reconciliation is now part of the same process
  boundary. The child accepts one additional exact instruction,
  `reconcile-idle-and-exit`, which is valid only while the radio is freshly
  idle, the gate has no active intent, Local PTT belongs exclusively to that
  child, and both command counters remain zero. The expanded live no-RF
  preflight passed at 14.262 MHz: dead child PID 53214/FLEX handle
  `0x2f5f8bea` was replaced by PID 53234/handle `0x182650ae`; process, engine,
  session, browser, lease, and FLEX identities were all fresh, the old handle
  remained absent, replacement key/unkey counts stayed 0/0, exit code was 0,
  resources were removed, the 100 W baseline was restored, and
  `rfEmitted=false`. The one-command operator script now requires this evidence
  during the next live process-loss acceptance.
- Live independent heartbeat-expiry acceptance completed on 2026-07-30 at
  14.250 MHz. The purpose-bound manifest armed a 750 ms heartbeat deadline for
  engine handle `0x43924c50` and non-GUI observer handle `0x06512903`. The
  engine sent exactly one key and zero unkeys. After heartbeat expiry, the
  observer independently proved the exact engine handle still owned TX and sent
  exactly one unkey; its boundary had no key capability. FLEX confirmed idle
  25.7973 ms after the safety unkey request and 817.6022 ms after key
  confirmation.
- The operation then sent CW `KC4CAW` at 20 WPM. FLEX reported insertion index
  6 and final sent index 11, exact-handle transmit was observed, the queue
  drained, and the radio returned idle. The controlling operator independently
  tuned PSOC1 to 14.250 MHz and audibly copied `KC4CAW`, proving the complete
  over-the-air RF path from PSOC2 through the antenna system to a separate
  receiver, not merely the FLEX command and status path. Post-test inspection
  confirmed 100 W,
  `dax=1`, `mic_selection=PC`, `vox_enable=0`, CWX 30 WPM/QSK-on/5 ms, no
  leaked 14.250 MHz resources, and the safety-expiry manifest consumed.
  Production remains receive-only and its clean publish contains zero FLEX
  key/unkey, CWX-ID, HIL operation, or TX-audio-stream creation strings.
- Authentication-loss safety is now staged behind the separate purpose
  `independent-authentication-loss`. An exact-identity monitor must first
  observe the active engine, lease, session, browser client, and protected FLEX
  handle as authenticated; only the matching authenticated-to-unauthenticated
  transition may release the controlling lease and signal the independent
  unkey-only supervisor. Startup unauthenticated, mismatched identity, repeated
  loss reports, and external SmartSDR ownership remain non-actionable.
- The staged tree passes 222 server tests, 45 TX-HIL tests, 70 AetherRemote
  tests, and 106 browser tests. The simulated independent-watchdog matrix now
  passes all nine scenarios, including authentication loss, without creating a
  radio connection. Clean production publish inspection found zero key, unkey,
  CW-ID, HIL-operation, or authentication-loss-purpose strings; the standalone
  HIL artifact contains exactly one key, one unkey, and one CW-ID command
  literal.
- Live no-RF authentication-loss preflight passed on 2026-07-30 at 14.310 MHz
  after the operator confirmed the frequency clear and PSOC2 free of external
  GUI clients. Engine handle `0x09970815` and non-GUI observer handle
  `0x259e6128` established the exact identity boundary. The preflight released
  one controlling-session lease after the authenticated-to-unauthenticated
  transition, delivered the `authentication-lost` signal, issued zero key and
  zero unkey commands while idle, left the interlock idle, restored and removed
  the owned pan/waterfall/slice resources, and reported `rfEmitted=false`.
- Live authentication-loss RF acceptance passed on 2026-07-30 at 14.310 MHz.
  Engine handle `0x030ba266` sent exactly one key and zero unkeys. Non-GUI
  observer handle `0x4dd88b7c`, which had no key capability, sent the only
  unkey after the exact authenticated-to-unauthenticated transition released
  one controlling-session lease and delivered `authentication-lost`. The
  observer requested unkey 42.5287 ms after authentication loss; FLEX confirmed
  idle 28.0645 ms later, for a total keyed-to-idle interval of 77.8632 ms. The
  engine gate reconciled to Idle with reason `lease-lost`.
- The same operation sent CW `KC4CAW` at 20 WPM. FLEX reported insertion index
  48 and final sent index 53, exact-handle transmit was observed, the queue
  drained, and the radio returned idle. The purpose-bound operation completed
  successfully and returned control without a cleanup error.
- Gateway-process-loss safety is now staged behind the separate purpose
  `independent-gateway-process-loss`. The station engine and independent
  non-GUI observer remain connected while a separate HIL-only gateway-authority
  child process is observed alive and then force-killed. That child creates no
  radio connection and has no key or unkey capability. Only the same exact
  gateway process instance, engine, lease, session, browser client, and FLEX
  handle may transition connected-to-lost and signal the independent unkey-only
  supervisor. Starting disconnected, replacing the gateway process, mismatched
  identity, repeated loss reports, and external SmartSDR ownership remain
  non-actionable.
- The staged gateway increment passes 233 server tests, 48 TX-HIL tests, 70
  AetherRemote tests, and 106 browser tests. The simulated independent-watchdog
  matrix passes all ten scenarios, including gateway process loss, without a
  radio connection. A real HIL child-process launch confirmed a distinct PID
  with no radio, key, or unkey capability. Clean production publish inspection
  found zero key, unkey, CW-ID, gateway-child, or gateway-loss-purpose strings;
  the standalone HIL artifact retains exactly one key, one unkey, and one CW-ID
  command literal.

- Live no-RF gateway-process-loss preflight passed on 2026-07-30 at 14.325 MHz.
  Engine handle `0x631cb085` and non-GUI observer handle `0x2d48ba07` established
  the exact station ownership boundary. Gateway child PID 76884 was force-killed
  with exit code 137. It created no radio connection and had no key or unkey
  capability. One controlling lease was released, zero unkey commands were
  issued while idle, the interlock remained idle, and `rfEmitted=false`.
- Live gateway-process-loss RF acceptance passed on 2026-07-30 at 14.325 MHz.
  Gateway child PID 76960, instance `gateway-76960-639210501076492349`, was
  force-killed with exit code 137 after engine handle `0x6f31c692` became the
  radio-confirmed TX owner. The engine sent exactly one key and zero unkeys.
  Non-GUI observer handle `0x4f9dcab9`, with no key capability, sent the only
  unkey. Unkey was requested 38.6124 ms after process loss; FLEX confirmed idle
  27.2152 ms later, for a total keyed-to-idle interval of 149.0774 ms. The gate
  reconciled to Idle with reason `lease-lost`.
- CW `KC4CAW` completed at 20 WPM with insertion index 54 and final sent index
  59. Exact-handle transmit was observed, the queue drained, and the radio
  returned idle. All M7 loss paths are now proven in HIL while normal production
  publishes remain receive-only with no reachable keying command.
- Phase 2A registered the accepted gate, supervisor, and exact-identity loss
  monitors per production radio session behind unavailable command transports.
  Phase 2B now records monotonic browser, gateway, station FLEX heartbeat, and
  lease observation sequences plus timestamps in administrative diagnostics.
  Every parsed message on an admitted browser WebSocket and every successful
  station FLEX ping refreshes only its exact current identity. Browser freshness
  reflects the principal admitted for that socket and is not an independent
  mid-socket Entra token refresh. Mismatched browser IDs and FLEX handles are
  ignored. An exact authenticated-to-unauthenticated browser activity transition
  releases only that browser's lease and reaches the authentication-loss monitor.
  Admin renders the resulting disabled/disarmed state and per-boundary freshness
  as `TX LIFECYCLE`. The gate remains Disabled, the supervisor remains Disarmed,
  and production still contains no reachable key or unkey transport.
- Phase 2C adds a one-second in-process watchdog over those exact observations.
  A tracked lease requires browser freshness within six seconds and station FLEX
  plus gateway freshness within ten seconds. Explicit engine/gateway disconnect
  releases the exact tracked lease immediately; stale browser, engine, or gateway
  observations release it on watchdog evaluation. Frozen-clock tests prove that
  each boundary revokes the tracked lease independently, later fresh observations
  cannot restore it, and another browser's lease in the same session is not
  released. Health now reports `txLifecycleWatchdogRegistered=true`; Admin shows
  watchdog count/time, fresh/stale flags, authority reason, and the last lifecycle
  observation. This remains an in-process authority revoker, not the future
  independent emergency-unkey process, and production remains receive-only.
  The complete validation gate passes 252 server tests, 48 TX-HIL isolation
  tests, 70 AetherRemote tests, and 107 browser tests (477 total), with a
  zero-warning solution build. Clean production publish inspection contains no
  forbidden TX/HIL command surface.
- Phase 2D adds the first independent process artifact without connecting it to
  the web gateway or a radio. The new `AetherSDR.TxWatchdog` host accepts only a
  4096-character-bounded local stdio protocol for status, exact radio/session/
  browser/gateway/engine/connection/lease/FLEX-handle registration, heartbeat,
  and disconnect observations. It remains Disarmed,
  reports no radio command transport or arming surface, carries no lease
  mutation or emergency action, and persists nothing. Exact identity plus a strictly
  increasing sequence prevents replacement and stale observations. Deterministic
  process tests force-kill a registered host and prove that a replacement has a
  different process instance, no restored identity, sequence zero, and the empty
  Disarmed startup state. The guarded production package includes the independent
  self-contained binary but does not launch it as a service yet. Health reports
  `txIndependentWatchdogHostPackaged=true`, state `packaged-disarmed`, connected
  false, and both command transport and arming unavailable.
- The 2026-07-31 Phase 2D validation-only gate passed 252 server tests, 16
  independent-watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote tests,
  and 107 browser tests (493 total), with a zero-warning solution build. Both
  self-contained production binaries contained no forbidden TX/HIL command
  strings. The published watchdog status probe returned a new empty Disarmed
  instance with no identity, no connection, sequence zero, and no radio command
  transport. No production service, Git commit, or Git remote was changed.
- Release `20260731-m7-independent-watchdog-phase2d` deployed successfully on
  2026-07-31. Internal and public health reported the independent host packaged,
  state `packaged-disarmed`, disconnected, with no command transport and no arming
  availability. The deployed binary independently returned a new empty Disarmed
  instance with no bound lease or identity and sequence zero; no persistent
  watchdog process was running. Browser Bridge acceptance kept PSOC2 live and
  RX-only, with MOX/TUNE hidden and disabled, no PTT, PC MIC hidden, and
  SPLIT/CWX/DVK/FDX disabled. The 2D -> 3D -> 2D display transition remained
  receive-only. Admin showed `DISABLED · DISARMED · NO LEASE` with fresh browser,
  engine, and gateway observations, no production TX transports, and both radio
  and Admin consoles had zero entries.
- Phase 2E supervises one command-incapable watchdog child per isolated radio
  session inside the gateway service's existing least-privileged cgroup. The
  private transport remains redirected stdio; no listener, FLEX connection,
  shared authority file, radio command transport, or arming operation was added.
  Startup accepts only a new empty `Disarmed` process. Complete exact browser,
  gateway, engine, connection, lease, and FLEX-handle authority registers that
  process epoch; subsequent exact observations heartbeat it. Lease release or
  incomplete/changed authority disconnects and replaces the child with a fresh
  empty epoch. Session disposal terminates the child and removes it from aggregate
  health.
- Child exit, missing binary, malformed/oversized response, request-ID mismatch,
  timeout, rejected request, identity mismatch, missing registration confirmation,
  or non-advancing heartbeat fails closed. Loss is published before the bounded
  restart delay and releases only that lifecycle's tracked physical-radio lease.
  Replacement readiness is diagnostic only: it starts with a different host
  instance, sequence zero, no bound lease, and cannot restore the released lease
  or affect another session's lease. Every accepted child response must remain
  exactly `Disarmed` with reason `command-incapable-skeleton`, no command
  transport, and no arming availability.
- Health now reports real supervision registration, supervised Disarmed state,
  session/running/connected/registered-identity counts, and restart count. With
  production browser leases disabled, the deployment gate requires zero
  registered watchdog identities. Admin `TX LIFECYCLE` shows the child PID, host
  epoch, IPC sequence, restart count, and degraded reason without adding a
  control surface. The release activator requires both production executables
  and marks both executable before activation.
- The 2026-07-31 Phase 2E validation gate passed 263 server tests, 25
  independent-watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote tests,
  and 107 browser tests (513 total), with a zero-warning solution build. Real
  process tests covered startup, exact registration/heartbeat, disconnect reset,
  forced child loss, immediate loss before restart delay, missing-binary
  degradation, identity mismatch, and clean session shutdown. Both self-contained
  production binaries contained no forbidden TX/HIL command surface, and the
  published watchdog status probe returned an empty Disarmed instance with no
  identity, connection, lease, command transport, or arming surface.
- Release `20260731-m7-independent-watchdog-supervision-phase2e` deployed
  successfully on 2026-07-31. Public health reported one connected supervised
  child, zero registered identities, state `supervised-disarmed`, no command
  transport, no arming availability, and production transmit plus browser TX
  leases disabled. Browser Bridge acceptance kept PSOC2 live and RX-only with
  MOX/TUNE hidden and disabled, no PTT, PC MIC hidden, SPLIT/CWX/DVK/FDX disabled,
  and a successful 2D -> 3D -> 2D display transition. The command-incapable child
  PID 95105 / host `watchdog-bbca3586...` was terminated deliberately; supervision
  created PID 96311 / host `watchdog-1bc15a44...`, advanced restart count to one,
  and returned sequence zero with no identity or lease. The gate stayed Disabled,
  the safety supervisor stayed Disarmed, no lease or authority was restored,
  Admin showed the replacement PID and restart count, and both browser consoles
  remained empty. Production remains receive-only.
- Phase 2F adds a strict browser TX protocol version 1 without adding a radio
  command boundary. Lease acquire, renew, release, and deliberate intent requests
  require JavaScript-safe positive request IDs, monotonic JavaScript-safe
  per-WebSocket sequences, exact unique properties, explicit 1-15 second duration
  where applicable, and the exact 32-character lowercase opaque lease ID. The
  connection retains at most 64 intent IDs and rejects non-object roots, stale
  sequences, replayed IDs, and unknown or duplicate fields. Reconnect starts at
  sequence one and the browser never recovers or replays a prior lease secret.
- Validation-only intent supports exact `mox.set`, `ptt.set`, `tune.set`,
  `microphone.set`, and bounded printable `cw.send` payloads. The server ignores
  browser identity assertions and freshly re-derives authentication, role,
  current connection, exact lease, idle occupancy, lifecycle session/FLEX
  handle, and a registered connected lease-bound Disarmed watchdog epoch. A
  fully valid request returns `validated=true`, `ok=false`, and
  `transport-unavailable`; no command-gate or radio-transport method is invoked.
- The browser controller automatically renews the holder's lease, releases on
  deliberate page exit where possible, bounds unanswered TX requests to 16,
  discards the secret on every disconnect, ignores mismatched/stale responses,
  and fails closed if a request cannot be sent. It also discards the secret after
  a rejected renewal, unsupported lease-event version, or missing exact renewal
  response before the current server expiry. Renewal cannot extend a lease after
  idle occupancy or exact supervised lifecycle authority is lost; the server
  releases only that lease as `renewal-authority-lost`. A separately labeled
  validation-only panel is hidden while the default
  `BrowserTxLeaseEnabled=false` setting remains active. Even when validation is
  configured, the actual MOX, TUNE, and CWX controls stay hidden/disabled because
  executable keying, TUNE, CW, and microphone capabilities remain false. PC MIC
  remains a local meter and sends no samples.
- Admin `TX LIFECYCLE` now includes the current or last lease holder name, expiry
  or revocation reason, and latest monotonic browser intent action/outcome/reason.
  The opaque lease ID is not projected. Health explicitly reports browser TX
  intent protocol version 1, validation registration, and absent browser intent
  command transport. RX-only defaults remain unchanged.
- The 2026-07-31 Phase 2F validation-only gate passed 298 server tests, 25
  independent-watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote tests,
  and 123 browser tests (564 total), with a zero-warning solution build. Strict protocol
  and integration coverage includes duplicate/unknown fields, non-object roots,
  JavaScript-safe integer bounds, invalid versions and lease IDs, stale sequence,
  bounded replay and pending requests, wrong/expired lease, replaced connection,
  authentication loss, non-idle occupancy, renewal authority loss, missing
  lifecycle authority, post-barrier lease revocation, exact validation followed
  by `transport-unavailable`, reconnect secret discard, unconfirmed-renewal
  expiry, unsupported event versions, immediate release-secret discard, malformed
  response identifiers, per-browser redacted lease events, and default-hidden
  executable controls. Both self-contained production binaries contained no
  forbidden TX/HIL command surface, and the published watchdog status probe
  remained empty and Disarmed. Validation log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260731-m7-browser-tx-intent-validation-phase2f-validation-final-flexweb-validation.txt`.
  No server, Git commit, or Git remote was changed.
- Phase 2G adds a sealed station-local command boundary protocol version 1 while
  keeping production command-incapable. Its deterministic ECDSA P-256/SHA-256
  payload binds the command UUID, monotonic sequence, bounded issue/expiry time,
  station, radio, session, browser client, opaque lease, gateway instance,
  engine instance, protected FLEX handle, action, and enabled value. Invalid
  signatures, malformed identities, wrong station/radio/session/browser/lease/
  gateway/engine/handle, expired or future envelopes, and stale sequence fail
  before an adapter can be called.
- Adapter admission additionally requires current authentication and lifecycle
  freshness, a live lease through the envelope expiry, fresh radio-authoritative
  idle occupancy, exclusive Local PTT authority for the protected handle, and a
  freshly Armed safety-supervisor record with the exact same ownership tuple.
  The browser, AetherRemote, and independent watchdog have no entry point to the
  adapter interface. Bounded audit records retain outcome and reason with only a
  short lease fingerprint; raw lease IDs and signatures are not retained.
- Every production session constructs the boundary disabled with no signature
  verifier, no command adapter, no arming capability, and no set-transmit
  capability. Health and Admin diagnostics expose those exact false capability
  bits. The final Phase 2G proof suite passed 28 focused boundary tests. The
  guarded validation passed 330 FlexWeb server tests, 25 independent-watchdog
  tests, 48 TX-HIL isolation tests, 70 AetherRemote tests, and 123 browser tests
  (596 total), with a zero-warning solution build and production binary
  inspection confirming no forbidden TX/HIL command surface. Validation log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-boundary-phase2g-validation-r2-flexweb-validation.txt`.
  No production keying, unkeying, TUNE, CW, microphone-audio, or RF test path
  was added or run.
- Release `20260801-m7-station-command-boundary-phase2g` deployed successfully
  on 2026-08-01 with the accepted Phase 2F release retained for rollback. Public
  health reported protocol version 1, a registered but disabled station command
  boundary, and false signature-verification, adapter, arming, set-transmit,
  watchdog-command, and production-command capabilities. Browser Bridge kept
  PSOC2 `RADIO: LIVE` and `RX-ONLY`, preserved the 2D -> 3D -> 2D display
  transition, kept the validation panel hidden, kept MOX/CWX hidden and disabled,
  exposed no PTT, and left TUNE disabled. Admin rendered
  `command boundary v1 disabled`, `signature absent`, `adapter absent`,
  `arming absent`, `set-transmit absent`, `audit 0`, and
  `DISABLED · DISARMED · NO LEASE`; the Admin console was empty. Radio debugger
  attachment produced one fixed six-entry WebSocket retry burst while the page
  stayed live, with no additional errors during a second ten-second steady-state
  interval. One managed gateway PID owned the active release and public health
  remained fail-closed.
- Phase 2H adds one station-scoped `StationTxCommandTrust` configuration object
  for immutable public-key verification without adding command ingress or a
  radio adapter. Verification defaults false and the trust ring defaults empty.
  Up to four exact key IDs support bounded rotation; every configured anchor is
  loaded at startup even while verification is disabled so malformed staged
  configuration cannot remain latent until activation.
- Trust anchors must be exact UTF-8 `PUBLIC KEY` PEM files containing ECDSA
  P-256 SubjectPublicKeyInfo. Absolute paths may contain no relative segments.
  The file and its immediate containing directory must be regular, non-symlink,
  and not writable by group or other users on Unix. Duplicate IDs or paths,
  missing or oversized files, private keys, unsupported curves, multiple PEM
  blocks, trailing data, invalid UTF-8, unknown configuration properties, and
  unsafe file/directory permissions fail startup. Malformed key IDs are not
  echoed into errors. Diagnostics expose only key IDs, short public-key fingerprints,
  readiness, and count; they do not expose paths or key bytes.
- A singleton registry owns and disposes the imported verification keys. Each
  production session receives only its verifier interface. Even with a ready
  verifier, focused lifecycle proof keeps the command boundary disabled, the
  adapter absent, arming and set-transmit unavailable, audit count zero, the
  command gate Disabled, and the safety supervisor Disarmed. The Phase 2H
  focused suites pass 32 trust-store cases and 81 combined command-boundary,
  trust, lifecycle, and session-wiring cases. No signer, envelope-submit method,
  browser or AetherRemote route, watchdog command path, FLEX command, or RF
  operation was added.
- The final 2026-08-01 Phase 2H guarded validation passed 364 FlexWeb server
  tests, 25 independent-watchdog tests, 48 TX-HIL isolation tests, 70
  AetherRemote tests, and 124 browser tests (631 total), with a zero-warning
  solution build. Both self-contained production binaries contained no
  forbidden TX/HIL command surface, and the published watchdog status probe
  remained empty and Disarmed with no command transport or arming capability.
  Validation log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-trust-phase2h-validation-flexweb-validation.txt`.
- Release `20260801-m7-station-command-trust-phase2h-r2` deployed successfully
  on 2026-08-01 with release `20260801-020917-flexweb-validation` retained for
  automatic rollback. Internal and public health reported trust verification
  disabled, zero trusted keys, signature verification unavailable, the station
  command boundary registered but disabled, and adapter, arming, set-transmit,
  watchdog-command, and production command transports unavailable. One
  supervised watchdog process was connected, remained Disarmed, and had zero
  registered identities. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-trust-phase2h-r2-flexweb-validation.txt`.
- Browser Bridge acceptance kept PSOC2 `LIVE` and `RX-ONLY`, passed the 2D -> 3D
  -> 2D renderer transition, rendered no validation-only panel, kept MOX and CWX
  hidden and disabled, exposed no PTT, and left both TUNE surfaces disabled. The
  radio UI stated `No browser transmit path is connected.` Admin reported
  `DISABLED · DISARMED · NO LEASE` and `command boundary v1 disabled signature
  absent adapter absent arming absent set-transmit absent audit 0`, followed by
  `TX transports absent`; its console was empty. Radio debugger capture retained
  four stale-session 502/404 entries and six WebSocket retry entries associated
  with deployment/reconnect, but the fixed total of ten did not increase during
  a further ten-second steady-state interval while the page remained live and
  RX-only. Gateway PID `111161` owned the accepted release, watchdog PID `111294`
  remained Disarmed, and the post-acceptance health probe remained fail-closed.
- Phase 2I adds one separate station-scoped `StationTxCommandSigning`
  configuration object for private-key readiness without adding command
  submission. Signing defaults false and key ID/path default empty. A configured
  key is loaded even while signing remains disabled. The file must be one exact
  UTF-8 unencrypted PKCS#8 ECDSA P-256 `PRIVATE KEY` PEM at an absolute
  canonical path. It must be regular and non-symlink; on Unix it must be mode
  0400 or 0600 and its immediate regular directory cannot be writable by group
  or other users. Public-only or encrypted keys, other curves, extra blocks,
  trailing data, invalid UTF-8, unknown properties, oversized files, path
  indirection, and unsafe permissions fail startup.
- The singleton signing authority owns and disposes the imported private key.
  Its internal request accepts only the exact station/radio/session/browser/
  lease/gateway/engine/FLEX tuple, `SetTransmit`, and the Boolean value. The
  authority owns the protocol version, key ID, random canonical command UUID,
  strictly increasing process-local sequence, millisecond-canonical issue time,
  fixed five-second expiry, and base64url ECDSA P-256/SHA-256 signature. Its
  public surface exposes diagnostics and disposal only; it is not injected into
  a session or lifecycle and no browser, HTTP, WebSocket, AetherRemote,
  watchdog, timer, adapter, or envelope-submit path exists. Health explicitly
  reports signing disabled, no key configured, signing unavailable, and no
  submission registration while all prior command capabilities remain false.
- The Phase 2I signing-authority suite passes 32 cases, and the combined signing,
  trust, command-boundary, lifecycle, and session-wiring proof passes 128 cases.
  Shared CIFS worktrees can strip the generated watchdog apphost execute bit;
  the affected process-boundary test now creates a private mode-0700 temporary
  wrapper that invokes the same reviewed watchdog assembly through the current
  `dotnet` host. Production watchdog launch behavior is unchanged. The guarded
  packaging path similarly normalizes only the two known published Linux entry
  points to mode 0755 before binary inspection.
- The final 2026-08-01 Phase 2I guarded validation passed 396 FlexWeb server
  tests, 25 independent-watchdog tests, 48 TX-HIL isolation tests, 70
  AetherRemote tests, and 124 browser tests (663 total), with a zero-warning
  solution build. Both self-contained production binaries contained no
  forbidden TX/HIL command surface, and the published watchdog status probe
  remained empty and Disarmed with no command transport or arming capability.
  Validation log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-signing-phase2i-validation-r2-flexweb-validation.txt`.
  No server, Git commit, Git remote, radio command, or RF operation was changed
  by this validation-only run.
- Release `20260801-m7-station-command-signing-phase2i` deployed successfully on
  2026-08-01 with release
  `20260801-m7-station-command-trust-phase2h-r2` retained for automatic rollback.
  Internal and public health reported signing disabled, no signing key
  configured, signing unavailable, no envelope-submission registration, trust
  verification disabled, zero trusted keys, the command boundary registered but
  disabled, and adapter, arming, set-transmit, watchdog-command, and production
  command transports unavailable. Gateway PID `112615` owned the active release;
  watchdog PID `113152` was connected and Disarmed with zero registered
  identities. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-signing-phase2i-flexweb-validation.txt`.
- Browser Bridge acceptance authenticated Steven Griggs (KC4CAW), showed all
  four radios online, and connected PSOC2 with one available Multi-Flex slot.
  The live console rendered spectrum and waterfall and reported `AETHER-WEB`,
  `FLEX-6700`, `RX-ONLY`, and `RADIO: LIVE`. The harmless browser-local 2D -> 3D
  -> 2D transition passed while the receiver stayed live. Deep DOM inspection
  found MOX, TUNE, and CWX hidden and disabled; TX and P/CW applets hidden;
  split, DVK, and FDX disabled; PC MIC hidden with its local-monitor-only and
  never-sent-to-radio warning; and no visible enabled PTT/MOX/TUNE/CW control or
  validation-only authority panel.
- Admin showed one healthy browser/radio session with current spectrum and audio,
  idle TX occupancy, exact AetherSDR Local PTT authority, and
  `DISABLED · DISARMED · NO LEASE`. Its lifecycle line retained command boundary
  v1 disabled, signature absent, adapter absent, arming absent, set-transmit
  absent, audit 0, authority `no-active-lease`, and `TX transports absent`; its
  console contained zero errors or warnings. Attaching the radio debugger itself
  produced one fixed five-entry WebSocket failure burst and temporarily showed
  Reconnecting/Offline. The count did not increase during a ten-second
  observation, and detaching immediately restored live 2D spectrum/waterfall
  without replacing the server-side radio session. No frequency, TX control,
  microphone permission, radio command, or RF operation was used.
- Phase 2J adds one station-scoped internal
  `StationTxCommandEnvelopeCoordinator` with submission disabled by default.
  Its public surface exposes diagnostics only. The internal request contains one
  already-validated operator intent plus one server-owned command-authority
  snapshot; callers cannot supply a command envelope, protocol/key/command ID,
  envelope sequence, timestamps, signature, or adapter. Only canonical,
  positive-sequence MOX/PTT Boolean intent observed within five seconds and no
  more than one second in the future is eligible. TUNE, microphone, and CW do
  not map to `SetTransmit` in this phase.
- Before signing, the coordinator requires its enable bit, signer and trust
  verifier readiness, an enabled caller-owned boundary with its own verifier,
  a registered adapter, fresh arming, and SetTransmit capability. It derives all
  signed identities from the authority snapshot, consumes each intent ID once,
  requires strictly increasing intent sequence per session/browser owner, and
  bounds replay state to 256 live IDs and 128 live owners. Cancellation,
  signing failure, boundary/adapter rejection, and unknown outcomes never retry
  a consumed intent. The generated canonical 64-byte P-256/P1363 signature is
  self-verified against the station trust ring before the caller boundary
  independently revalidates protocol, signature, exact authority, safety, and
  replay state.
- At the Phase 2J checkpoint, production resolved the coordinator for startup
  diagnostics only. No caller-owned boundary was attached, and no browser,
  HTTP, WebSocket, AetherRemote, watchdog, timer, adapter, arming, FLEX command,
  or RF path could invoke it. Health distinguished internal registration from
  external reachability: coordinator registered, submission disabled, boundary
  unattached, submission unavailable, and external envelope submission
  unregistered while adapter, arming, and set-transmit remained false. The
  focused coordinator suite passed 39 cases and the combined coordinator,
  signer, trust, command-boundary, lifecycle, and session-wiring proof passed
  168 cases.
- The final 2026-08-01 Phase 2J guarded validation passed 435 FlexWeb server
  tests, 25 independent-watchdog tests, 48 TX-HIL isolation tests, 70
  AetherRemote tests, and 124 browser tests (702 total), with a zero-warning
  solution build. Both self-contained production binaries contained no
  forbidden TX/HIL command surface, and the published watchdog probe remained
  empty and Disarmed with no command transport or arming capability. Validation
  log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-envelope-phase2j-validation-flexweb-validation.txt`.
  No server, Git commit, Git remote, radio command, or RF operation was changed
  by this validation-only run.
- Release `20260801-m7-station-command-envelope-phase2j` deployed successfully
  to the staging FlexWeb host on 2026-08-01 with
  `20260801-m7-station-command-signing-phase2i` retained for rollback. Internal,
  public, and post-deploy health reported coordinator registered, submission
  disabled, coordinator signer/verifier unavailable under default configuration,
  boundary unattached, boundary verification unavailable, submission
  unavailable, and external envelope submission unregistered. The command
  boundary remained disabled and adapter, arming, SetTransmit, watchdog-command,
  and production command transports remained unavailable. Gateway PID `113705`
  owned the active release; the deployed watchdog artifact remained
  command-incapable and Disarmed. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-envelope-phase2j-flexweb-validation.txt`.
  Git was not committed or pushed, and no radio command or RF operation was
  used.
- Browser Bridge acceptance on 2026-08-01 authenticated the operator, kept the
  PSOC2 console live with spectrum and waterfall, and reported `AETHER-WEB`,
  `FLEX-6700`, `RX-ONLY`, and `RADIO: LIVE`. The browser-local 2D -> 3D -> 2D
  renderer transition passed without changing frequency or invoking a radio
  control. Deep DOM inspection found MOX and CWX hidden and disabled, both TUNE
  surfaces disabled, the TX applet unreachable, the validation-only authority
  panel hidden with its controls disabled, and DVK/FDX disabled. PC MIC remained
  a local input meter only; no microphone permission or transmit audio path was
  used.
- Phase 2J health reported coordinator registered, submission disabled,
  coordinator signing/verifier unavailable under default configuration,
  boundary unattached, boundary verification unavailable, submission
  unavailable, and external envelope submission unregistered. Admin showed one
  healthy browser/radio session with current streaming, no external GUI owner,
  idle occupancy, exact AetherSDR Local PTT authority, and
  `DISABLED · DISARMED · NO LEASE`. Its command line retained boundary v1
  disabled, signature absent, adapter absent, arming absent, set-transmit absent,
  audit 0, no active lease, and `TX transports absent`; the Admin console buffer
  contained zero errors or warnings.
- Attaching the radio debugger during the deep audit created one overlong
  browser-network diagnostic sample and a sticky validation banner. Current
  Admin diagnostics continued to report normal fresh traffic. Reloading the same
  receive-only page after detaching the debugger cleared the test-induced banner
  and restored a clean live 2D view without replacing radio authority. No TX
  control, radio command, or RF operation was used.
- Phase 2K adds one internal `StationTxCommandSessionComposition` to each radio
  session. `RadioSessionRegistry` passes the station-scoped coordinator through
  an internal submitter interface into `StationTxProductionLifecycle`, where it
  is attached to that session's existing disabled command boundary. Neither
  `RadioCoordinator` nor `RadioWebSocketEndpoint` receives the coordinator,
  submitter, composition, or submission method. At this phase the only non-test
  declaration of `SubmitValidatedBrowserTxIntentAsync` was the internal lifecycle
  method and production had no caller; Phase 2Q later removes that direct seam.
- The composition request accepts only the current WebSocket connection ID, the
  already-parsed MOX/PTT Boolean intent, its positive JavaScript-safe sequence,
  and the server observation time. It derives the gateway station identity,
  canonical radio, session, stable browser-page identity, exact active
  connection-owned lease and expiry, gateway/engine instances, FLEX handle,
  authentication/freshness flags, occupancy, and safety snapshot from
  server-owned lifecycle state. A caller cannot supply or override an authority
  field. A replaced connection, absent/mismatched/expired lease, stale authority,
  missing handle, unsupported action, missing Boolean, invalid sequence,
  cancellation, or resolver failure stops before coordinator submission.
- Production diagnostics now distinguish station-scoped coordinator registration
  from per-session composition and external ingress. Health reports session
  composition registered and browser ingress unregistered. Admin reports
  coordinator/boundary attachment, authority availability, submission
  availability, bounded attempt/forward counts, last outcome, and fail-closed
  reason without exposing lease IDs, signatures, key paths, or key material.
  Default production still has submission disabled, the boundary disabled,
  signer/verifier unavailable, adapter absent, arming absent, SetTransmit absent,
  and no FLEX command or RF path.
- The Phase 2K focused composition suite passes 18 cases. The production session
  registry plus focused composition proof passes 19 cases. The combined browser
  intent, session composition, envelope coordinator, signing, trust,
  command-boundary, lifecycle, and session-registry proof passes 196 cases. The
  focused Admin diagnostics suite passes 11 cases.
- The final 2026-08-01 Phase 2K guarded validation passed 454 FlexWeb server
  tests, 25 independent-watchdog tests, 48 TX-HIL isolation tests, 70
  AetherRemote tests, and 124 browser tests (721 total), with a zero-warning
  solution build. Both self-contained production binaries contained no
  forbidden TX/HIL command surface, and the published watchdog probe remained
  empty and Disarmed with no command transport or arming capability. Validation
  log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-session-phase2k-validation-flexweb-validation.txt`.
  No server, Git commit, Git remote, radio command, or RF operation was changed
  by this validation-only run.
- Release `20260801-m7-station-command-session-phase2k` deployed successfully to
  the staging FlexWeb host on 2026-08-01 with
  `20260801-m7-station-command-envelope-phase2j` retained for rollback. Internal,
  public, and post-acceptance health reported session composition registered,
  browser ingress unregistered, coordinator registered, submission disabled,
  boundary/adapter/arming/SetTransmit unavailable, and external envelope
  submission unregistered. Gateway PID `117283` owned the active release;
  watchdog PID `117770` remained supervised and Disarmed with zero registered
  identities. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-session-phase2k-flexweb-validation.txt`.
- Browser Bridge acceptance kept PSOC2 live and receive-only. Health showed the
  Phase 2K composition registered with browser ingress false. Deep DOM inspection
  kept MOX, TUNE, and CWX hidden and disabled, lease acquisition and validation
  controls disabled, and the production command capabilities false. The
  browser-local 2D -> 3D -> 2D selection passed while the footer remained
  `RX-ONLY` and `RADIO: LIVE`; the browser window was occluded during the 3D
  capture, so its canvas repaint was throttled, but the selected state and live
  radio footer remained authoritative. The final visible page text reported
  clean 2D selection and `RADIO: LIVE`.
- Admin showed one healthy FlexRx session with current spectrum/audio activity,
  idle TX occupancy, exact AetherSDR Local PTT ownership, and
  `DISABLED · DISARMED · NO LEASE`. Its Phase 2K line reported coordinator and
  boundary attached, authority absent without a lease, submission unavailable,
  attempts 0, forwarded 0, last outcome `none`, and reason
  `submission-disabled`; the command boundary remained disabled with signature,
  adapter, arming, and SetTransmit absent, audit 0, and all TX transports absent.
  A fresh Admin console observation contained zero entries. No TX control,
  microphone permission, radio command, or RF operation was used.
- Phase 2L adds one typed `StationTxCommandAdapterComposition` to every
  production lifecycle and passes it into the existing disabled signed command
  boundary. The composition implements the internal adapter contract but owns no
  executor, radio transport, arming operation, browser route, or retry loop.
  Normal session construction supplies no `IStationTxCommandAdapterExecutor`, so
  the existing adapter-registered, arming, and SetTransmit capability bits
  remain false.
- A future executor can receive only an already validated command. Immediately
  before delegation, the composition independently re-resolves lifecycle-owned
  authority and requires the exact station/radio/session/browser/lease/gateway/
  engine/FLEX-handle tuple, bounded command and lease lifetime, current
  authentication and observations, fresh idle occupancy, exclusive Local PTT
  authority, and a matching freshly Armed safety identity. Mismatch, missing
  executor, capability loss, rejection, unknown outcome, cancellation, or
  exception never creates a retry.
- Production reachability inspection found the executor interface only in the
  new composition and the lifecycle's optional internal constructor parameter.
  `RadioSessionRegistry`, `RadioCoordinator`, and `RadioWebSocketEndpoint` do not
  accept or expose it. The only normal-source FLEX transmit command
  implementation remains inside the compile-time HIL block; no production
  executor implementation was added.
- The Phase 2L focused adapter-composition suite passes 34 cases. The focused
  lifecycle, registry, and adapter proof passes 36 cases, including an exact
  active lease with a deliberately ready test executor that remains blocked by
  the Disarmed safety identity and disabled boundary. The Admin diagnostics
  suite passes 11 cases.
- The final 2026-08-01 Phase 2L guarded validation passed 489 FlexWeb server
  tests, 25 independent-watchdog tests, 48 TX-HIL isolation tests, 70
  AetherRemote tests, and 124 browser tests (756 total), with a zero-warning
  solution build. Both self-contained production binaries contained no
  forbidden TX/HIL command surface, and the published watchdog probe remained
  empty and Disarmed with no command transport or arming capability. Validation
  log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-adapter-phase2l-validation-flexweb-validation.txt`.
  No server, Git commit, Git remote, radio command, or RF operation was changed
  by this validation-only run.
- Release `20260801-m7-station-command-adapter-phase2l` deployed successfully to
  the staging FlexWeb host on 2026-08-01 with
  `20260801-m7-station-command-session-phase2k` retained for rollback. Internal,
  public, and post-acceptance health reported adapter composition registered,
  executor attachment and registration false, adapter-composition browser
  ingress false, boundary disabled, envelope submission unregistered, and
  adapter/arming/SetTransmit/production command transports unavailable. Gateway
  PID `118380` owned the active release; watchdog PID `118886` remained
  supervised and Disarmed with zero registered identities. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-adapter-phase2l-flexweb-validation.txt`.
- Browser Bridge acceptance kept PSOC2 live and receive-only with current
  spectrum and waterfall, `FLEX-6700`, `RX-ONLY`, and `RADIO: LIVE`. Deep DOM
  inspection found MOX, TUNE, and CWX hidden and disabled; lease acquisition and
  validation-only intent controls disabled; DVK and FDX disabled; and PC MIC
  still a local input meter rather than a transmit-audio path. Health reported
  adapter composition registered while executor, browser ingress, actual
  adapter, arming, SetTransmit, envelope submission, and command transport all
  remained false.
- The browser-local 2D -> 3D -> 2D renderer transition passed without changing
  frequency or invoking a radio control. Attaching the radio debugger for deep
  inspection temporarily caused the known reconnect state; detaching and
  reloading the same receive-only page restored a clean live session. Admin then
  showed `DISABLED · DISARMED · NO LEASE`, boundary v1 disabled, adapter
  composition executor absent and unregistered, authority absent, adapter/
  arming/SetTransmit absent, attempts 0, forwarded 0, last outcome `none`, reason
  `executor-unattached`, unchanged command composition, and all TX transports
  absent. A fresh Admin console observation contained zero entries. No TX
  control, microphone permission, radio command, or RF operation was used.
- Phase 2M adds one internal `StationTxCommandGateExecutor` to each production
  lifecycle. It implements the Phase 2L executor contract and maps an already
  validated SetTransmit true command only to the existing gate's exact-owner
  key request, while false maps only to the exact-owner unkey request. It owns no
  browser route, FLEX router, safety arm, timer, or retry loop.
- The gate now exposes a fail-closed capability snapshot. Production reports the
  gate executor and adapter registered, but the gate remains constructed with
  `allowTransmit:false`, its command transport remains unavailable, the safety
  supervisor remains Disarmed, and arming and SetTransmit remain false. Neither
  `RadioSessionRegistry`, `RadioCoordinator`, nor `RadioWebSocketEndpoint`
  accepts the executor type, and the envelope submission method remains internal
  and uncalled.
- Key and unkey retain different radio-authoritative occupancy requirements in
  both the signed boundary and adapter composition. Key requires fresh idle
  occupancy plus exclusive Local PTT for the exact AetherSDR handle. Unkey can
  proceed only when the radio is already idle or fresh occupancy proves that the
  exact handle is the single AetherSDR TX owner. External SmartSDR, Maestro,
  hardware PTT, ambiguous, stale, or replaced ownership cannot reach the gate.
  Known gate rejection stays rejected; unknown key/unkey command outcomes stay
  unknown for bounded gate reconciliation, with no executor retry.
- The Phase 2M focused command-pipeline proof passes 94 cases, including direct
  disabled-gate behavior, exact key/unkey mapping, one-attempt unknown outcomes,
  full adapter-composition-to-gate delegation, boundary and adapter unkey
  ownership checks, production lifecycle registration, and real session
  registry isolation. The combined command-stack proof passes 276 cases and the
  Admin diagnostics suite passes 11 cases.
- Full server validation exposed a pre-existing flush-barrier race: a gateway
  observation could release a lease and enqueue its lease-change observation
  behind an already queued test/diagnostic barrier. The lifecycle barrier now
  moves to the queue tail until causally generated observations drain. The
  gateway-disconnect regression passes alone and across 20 consecutive stress
  runs without weakening immediate lease-release assertions.
- The final 2026-08-01 Phase 2M guarded validation passed 508 FlexWeb server
  tests, 25 independent-watchdog tests, 48 TX-HIL isolation tests, 70
  AetherRemote tests, and 124 browser tests (775 total), with a zero-warning
  solution build. Both self-contained production binaries contained no
  forbidden TX/HIL command surface, and the published watchdog probe remained
  empty and Disarmed with no command transport or arming capability. Validation
  log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-gate-phase2m-validation-flexweb-validation.txt`.
  No server, Git commit, Git remote, radio command, or RF operation was changed
  by this validation-only run.
- Release `20260801-m7-station-command-gate-phase2m` deployed successfully to the
  staging FlexWeb host on 2026-08-01 with
  `20260801-m7-station-command-adapter-phase2l` retained for rollback. Internal,
  public, and post-acceptance health reported the gate executor and command
  adapter registered while gate transmit enablement, command transport,
  SetTransmit, browser ingress, boundary execution, envelope submission, and
  safety arming remained false. Gateway PID `119676` owned the active release;
  watchdog PID `120175` remained supervised and Disarmed with zero registered
  identities. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-gate-phase2m-flexweb-validation.txt`.
- Browser Bridge acceptance kept PSOC2 live and receive-only with current
  spectrum and waterfall, `FLEX-6700`, `RX-ONLY`, and `RADIO: LIVE`. Deep DOM
  inspection found MOX, TUNE, and CWX hidden and disabled; lease acquisition and
  validation-only intent controls disabled; and the Phase 2M health fields at
  executor/adapter registered but gate disabled, transport absent, arming false,
  SetTransmit false, browser ingress false, boundary disabled, and submission
  unregistered. The harmless browser-local 2D -> 3D -> 2D renderer transition
  passed without changing a radio control.
- Admin showed `DISABLED · DISARMED · NO LEASE`, command boundary v1 disabled,
  executor attached and registered, adapter registered, authority absent,
  arming/SetTransmit absent, attempts 0, forwarded 0, last outcome `none`, reason
  `executor-arming-unavailable`, unchanged disabled session composition, and all
  TX transports absent. Attaching the debugger temporarily made the background
  browser heartbeat stale; detaching restored a fresh `RADIO: LIVE` session. A
  fresh Admin console observation contained zero entries. No TX control,
  microphone permission, radio command, or RF operation was used.
- Phase 2N adds one internal `StationTxSafetyArmComposition` around each
  production lifecycle's existing unkey-only safety supervisor. Typed requests
  can carry only the current connection identity plus a bounded heartbeat
  timeout or abort reason. The composition re-resolves exact lifecycle-owned
  station/radio/session/browser/lease/gateway/engine/FLEX-handle authority,
  validates fresh occupancy and the supervisor identity, asks an optional
  internal arm authority to authorize the exact operation, and forwards at most
  one supervisor call with no retry.
- Arm requires fresh idle occupancy, exclusive Local PTT for the protected
  handle, current authentication/lease/observations, and a Disarmed supervisor.
  Idle heartbeat also requires Local PTT to remain exact; active heartbeat and
  abort require fresh proof of the exact single AetherSDR TX owner. An idle
  abort can clear only the matching arm without a radio command. External,
  ambiguous, stale, expired, disconnected, replaced, cancelled, or faulted
  authority stops before the supervisor.
- Normal production attaches no `IStationTxSafetyArmAuthority` and exposes no
  lifecycle, registry, coordinator, WebSocket, HTTP, AetherRemote, watchdog,
  reconnect, or timer caller. Health and Admin report the safety-arm composition
  registered while arm-authority attachment/registration, arm, heartbeat, abort,
  boundary execution, submission, command transport, and SetTransmit remain
  false. The supervisor and independent watchdog remain Disarmed and
  command-incapable.
- The Phase 2N focused composition suite passes 26 cases. The production
  lifecycle/registry integration proof passes 30 cases, the combined command and
  safety stack passes 350 cases, and the Admin diagnostics suite passes 11 cases.
  The complete FlexWeb server suite passes 534 cases and all 124 browser tests
  pass.
- The final 2026-08-01 Phase 2N guarded validation passed 534 FlexWeb server
  tests, 25 independent-watchdog tests, 48 TX-HIL isolation tests, 70
  AetherRemote tests, and 124 browser tests (801 total), with a zero-warning
  solution build. Both self-contained production binaries contained no
  forbidden TX/HIL command surface, and the published watchdog probe remained
  empty and Disarmed with no command transport or arming capability. Validation
  log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-safety-arm-phase2n-validation-flexweb-validation.txt`.
  No server, Git commit, Git remote, radio command, or RF operation was changed
  by this validation-only run.
- Release `20260801-m7-station-command-safety-arm-phase2n` deployed successfully
  to the staging FlexWeb host on 2026-08-01 with
  `20260801-m7-station-command-gate-phase2m` retained for rollback. Internal,
  public, and post-acceptance health reported the safety-arm composition
  registered while arm-authority attachment/registration, arm, heartbeat,
  abort, browser ingress, boundary execution, envelope submission, command
  transport, safety arming, and SetTransmit remained false. Gateway PID `120825`
  owned the active release; watchdog PID `121336` remained supervised and
  Disarmed with zero registered identities. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-safety-arm-phase2n-flexweb-validation.txt`.
- Browser Bridge acceptance kept PSOC2 live and receive-only with current
  spectrum and waterfall, `FLEX-6700`, `RX-ONLY`, and `RADIO: LIVE`. Deep DOM
  inspection found MOX, TUNE, and CWX hidden and disabled; lease acquisition and
  validation-only intent controls disabled; and Phase 2N health at composition
  registered but arm authority absent, arm/heartbeat/abort unavailable, browser
  ingress false, boundary disabled, submission unregistered, command transport
  absent, safety arming false, and SetTransmit false. PC MIC remained a local
  input meter. The harmless browser-local 2D -> 3D -> 2D renderer transition
  passed without changing a radio control.
- Admin showed `DISABLED · DISARMED · NO LEASE`, safety-arm composition authority
  absent and unregistered, session authority absent, arm/heartbeat/abort
  unavailable, attempts 0, forwarded 0, last `none/none`, reason
  `arm-authority-unattached`, unchanged disabled command and adapter
  compositions, and all TX transports absent. Attaching the debugger temporarily
  made the background browser heartbeat stale; detaching restored a fresh 2D
  `RX-ONLY / RADIO: LIVE` session. A fresh Admin console observation contained
  zero entries. No TX control, microphone permission, radio command, or RF
  operation was used.
- Phase 2O adds one lifecycle-owned `StationTxSafetyArmAuthority` and attaches it
  to the Phase 2N composition. Each authorization independently reads the signed
  boundary, adapter composition, gate executor, command gate, safety supervisor,
  and a newly resolved current lifecycle authority. The supplied and current
  station/radio/session/browser/lease-expiry/gateway/engine/FLEX-handle tuples
  must match exactly.
- Arm requires the complete normal command path, an idle gate, fresh idle
  occupancy, exclusive Local PTT, and a Disarmed supervisor. Heartbeat requires
  that path to remain ready, the exact current arm and deadline, and either idle
  with exact Local PTT or the exact single AetherSDR TX owner. Abort remains
  independent of normal command-path availability but still requires the exact
  active safety identity and ownership-safe idle or exact AetherSDR TX state.
  Dependency faults, stale/replaced authority, cancellation, and rejection
  perform no retry.
- Production now reports the arm authority attached and registered, but the
  signed boundary remains disabled, the gate retains `allowTransmit:false`, both
  command transports remain absent, no operation caller exists, and arm,
  heartbeat, abort, SetTransmit, boundary execution, and submission remain
  unavailable. The supervisor and independent watchdog remain Disarmed.
- The Phase 2O focused authority suite passes 35 cases. The combined authority,
  command, and safety stack passes 353 cases; the focused authority/composition/
  lifecycle/registry proof passes 100 cases; the Admin diagnostics suite passes
  11 cases; the complete FlexWeb server suite passes 569 cases; and all 124
  browser tests pass.
- The final 2026-08-01 Phase 2O guarded validation passed 569 FlexWeb server
  tests, 25 independent-watchdog tests, 48 TX-HIL isolation tests, 70
  AetherRemote tests, and 124 browser tests (836 total), with a zero-warning
  solution build. Both self-contained production binaries contained no
  forbidden TX/HIL command surface, and the published watchdog probe remained
  empty and Disarmed with no command transport or arming capability. Validation
  log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-safety-arm-authority-phase2o-validation-flexweb-validation.txt`.
  No server, Git commit, Git remote, radio command, or RF operation was changed
  by this validation-only run.
- Release `20260801-m7-station-command-safety-arm-authority-phase2o` deployed
  successfully to the staging FlexWeb host on 2026-08-01 with
  `20260801-m7-station-command-safety-arm-phase2n` retained for rollback.
  Internal, public, and post-acceptance health reported the safety-arm authority
  attached and registered while its boundary, command transport, SetTransmit,
  browser ingress, arm, heartbeat, and abort capabilities remained false. The
  signed boundary, envelope submission, command transport, and supervisor arming
  remained unavailable. Gateway PID `122055` owned the active release; watchdog
  PID `122616` remained supervised and Disarmed with zero registered identities.
  Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-safety-arm-authority-phase2o-flexweb-validation.txt`.
- Browser Bridge acceptance kept PSOC2 live and receive-only with current
  spectrum and waterfall, `FLEX-6700`, `RX-ONLY`, and `RADIO: LIVE`. Deep health
  inspection confirmed authority attachment/registration true while boundary,
  command transport, SetTransmit, browser ingress, arm, heartbeat, abort,
  submission, and supervisor arming remained false. MOX, TUNE, and CWX remained
  hidden and disabled; lease acquisition and validation-only intent controls
  remained disabled. The harmless browser-local 2D -> 3D -> 2D renderer
  transition passed without changing a radio control.
- Admin showed `DISABLED · DISARMED · NO LEASE`; safety-arm authority boundary
  disabled, signature absent, adapter/executor/gate registered, transmit
  disabled, transport and SetTransmit absent, session authority absent, gate
  `Disabled`, safety `Disarmed`, arm/heartbeat/abort unavailable, attempts,
  accepts, and rejects all zero, and last `none/none`. The attached composition
  also showed zero attempts and forwards. All TX transports remained absent.
  Attaching the debugger temporarily made the background browser heartbeat
  stale; detaching restored a fresh 2D `RX-ONLY / RADIO: LIVE` session. A fresh
  Admin console observation contained zero entries. No TX control, microphone
  permission, radio command, or RF operation was used.
- Phase 2P adds one lifecycle-owned `StationTxCommandTransactionComposition`
  above the existing safety-arm and command-session compositions. A key
  transaction resolves exact lifecycle authority, forwards one arm, revalidates
  the stable station/radio/session/browser/lease-expiry/gateway/engine/FLEX-
  handle tuple and Armed safety identity, then forwards one signed command.
  Known key rejection performs one ownership-safe cleanup; unknown outcome,
  cancellation, or exception retains the arm for reconciliation and never
  retries.
- An unkey transaction requires the exact active transaction, forwards one
  heartbeat, one false command, and clears the arm only after confirmed
  acceptance. Known rejection retains the arm. Unknown command or cleanup
  outcome retains it for reconciliation. Explicit heartbeat and abort operations
  remain internal, exact-connection-bound, and serialized with command
  submission.
- Production constructs the transaction composition with both participants
  attached but adds no lifecycle, registry, coordinator, WebSocket, HTTP,
  AetherRemote, watchdog, reconnect, timer, or browser caller. Submission,
  boundary, gate, and transports remain disabled; key, heartbeat, unkey, and
  abort remain unavailable; no transaction is active; and reconciliation is not
  required.
- The Phase 2P focused transaction suite passes 33 cases. The focused
  transaction/lifecycle/registry proof passes 72 cases, the combined command and
  safety stack passes 434 cases, and the Admin diagnostics suite passes 11
  cases. The complete FlexWeb server suite passes 602 cases and all 124 browser
  tests pass.
- The final 2026-08-01 Phase 2P guarded validation passed 602 FlexWeb server
  tests, 25 independent-watchdog tests, 48 TX-HIL isolation tests, 70
  AetherRemote tests, and 124 browser tests (869 total), with a zero-warning
  solution build. Both self-contained production binaries contained no
  forbidden TX/HIL command surface, and the published watchdog probe remained
  empty and Disarmed with no command transport or arming capability. Validation
  log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-transaction-phase2p-validation-flexweb-validation.txt`.
  No server, Git commit, Git remote, radio command, or RF operation was changed
  by this validation-only run.
- Release `20260801-m7-station-command-transaction-phase2p` deployed
  successfully to the staging FlexWeb host on 2026-08-01 with
  `20260801-m7-station-command-safety-arm-authority-phase2o` retained for
  rollback. Internal, public, and post-acceptance health reported the transaction
  composition and both participants registered while key, heartbeat, unkey,
  abort, active transaction, reconciliation, browser ingress, boundary,
  submission, command transport, safety arming, and SetTransmit remained false.
  The independent watchdog remained supervised and Disarmed with zero registered
  identities. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-transaction-phase2p-flexweb-validation.txt`.
- Browser Bridge acceptance kept PSOC2 live and receive-only with current
  spectrum and waterfall, `FLEX-6700`, `RX-ONLY`, and `RADIO: LIVE`. Deep health
  inspection confirmed transaction registration and both participant attachments
  true while key, heartbeat, unkey, abort, active state, reconciliation, browser
  ingress, boundary execution, submission, command transport, SetTransmit, and
  supervisor arming remained false. MOX, TUNE, and CWX remained hidden and
  disabled; lease acquisition and validation-only intent controls remained
  disabled; PC MIC remained a local input meter. The harmless browser-local 2D
  -> 3D -> 2D renderer transition passed without changing a radio control.
- Admin showed `DISABLED · DISARMED · NO LEASE`; transaction safety and command
  participants attached; authority absent; key/heartbeat/unkey/abort unavailable;
  active `no`; reconciliation `no`; state `idle`; attempts, arm, command,
  heartbeat, cleanup, accepted, rejected, and unknown all zero; last `none/none`;
  reason `submission-disabled`; and all TX transports absent. Attaching the
  debugger temporarily affected the background heartbeat; detaching restored a
  fresh 2D `RX-ONLY / RADIO: LIVE` session. A fresh Admin console observation
  contained zero entries. No TX control, microphone permission, radio command,
  or RF operation was used.
- Phase 2Q removes `SubmitValidatedBrowserTxIntentAsync` from production. The
  lifecycle now exposes only typed internal transaction submit, heartbeat, and
  abort operations, each delegating directly to the Phase 2P transaction
  composition. No lifecycle method returns a command-session result, and
  registry, coordinator, WebSocket, HTTP, AetherRemote, watchdog, reconnect,
  timer, and browser types receive none of the transaction request/result types.
- Production remains callerless and fail-closed. A valid key request stops at
  the unavailable command-path prerequisite with zero arm, command, heartbeat,
  or cleanup forwards. Inactive unkey, heartbeat, and abort requests stop before
  participants. Pre-cancelled requests are not counted. Health distinguishes the
  registered lifecycle transaction boundary from absent direct session
  submission and absent lifecycle browser ingress.
- The radio page now has one fixed Canvas 2D spectrum implementation. Both mode
  selectors, the browser mode preference, alternate renderer state, trace-
  history capture, stacked drawing routine, and associated CSS/tests are
  removed. Fill, peak hold, and waterfall visibility remain device-local display
  preferences. No radio command is involved in this UI simplification.
- The focused Phase 2Q lifecycle/session/transaction proof passes 56 cases. The
  focused app-shell/slice/waterfall proof passes 29 cases. The selected combined
  command/safety/lifecycle stack passes 399 cases. The complete FlexWeb server
  suite passes 607 cases and all 123 browser tests pass.
- The final 2026-08-01 Phase 2Q guarded validation passed 607 FlexWeb server
  tests, 25 independent-watchdog tests, 48 TX-HIL isolation tests, 70
  AetherRemote tests, and 123 browser tests (873 total), with a zero-warning
  solution build. Both self-contained production binaries contained no
  forbidden TX/HIL command surface; published index, application, renderer,
  control, and stylesheet assets contained none of the removed alternate-
  renderer selector, preference, state, history, or drawing markers; and the
  watchdog probe remained empty and Disarmed with no command transport or
  arming capability. The final deployment reran the same 873-test matrix after
  adding one-time cleanup of the obsolete renderer preference and enforced that
  the published application deletes but never reads or writes that key.
  Validation log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-transaction-lifecycle-phase2q-validation-flexweb-validation.txt`.
- Release `20260801-m7-station-command-transaction-lifecycle-phase2q-final`
  deployed successfully to the staging FlexWeb host on 2026-08-01 with the
  preceding Phase 2Q release retained for rollback. Internal and public health
  reported the lifecycle transaction boundary registered, direct session
  submission absent, lifecycle and transaction browser ingress absent, and key,
  heartbeat, unkey, abort, active transaction, reconciliation, boundary,
  submission, command transport, safety arming, and SetTransmit all false. The
  independent watchdog remained supervised and Disarmed with zero registered
  identities. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-station-command-transaction-lifecycle-phase2q-final-flexweb-validation.txt`.
- Browser Bridge acceptance kept the selected FLEX radio live and receive-only
  with current spectrum and waterfall, `FLEX-6700`, `RX-ONLY`, and
  `RADIO: LIVE`. Deep DOM inspection found zero mode-selector nodes and zero 3D
  buttons. The obsolete renderer preference was absent from local storage after
  startup, the spectrum accessibility label described the single live spectrum
  path, and opening and closing the Display panel exposed only fill, peak,
  waterfall, WNB, bandwidth, and radio-backed display controls. MOX, TUNE, and
  CWX remained hidden and disabled; lease acquisition and validation-only intent
  controls remained disabled.
- Admin showed `DISABLED · DISARMED · NO LEASE` and the new `transaction
  lifecycle boundary` line: safety and command participants attached, authority
  absent, key/heartbeat/unkey/abort unavailable, active `no`, reconciliation
  `no`, state `idle`, every attempt/forward/accepted/rejected/unknown counter
  zero, last `none/none`, reason `submission-disabled`, and all TX transports
  absent. A fresh Admin console observation contained zero entries. Detaching the
  debugger restored a fresh receive-only radio session. No TX control,
  microphone permission, radio command, or RF operation was used.
- Phase 2R adds a lifecycle-owned `BrowserTxTransactionIngress` that requires an
  exact server validation result paired with the parsed browser request. It
  requires the validation-only outcome and current intent-validation capability,
  bounds validation evidence to two seconds with one second of future clock skew,
  accepts only Boolean `mox.set` and `ptt.set`, derives the five-second heartbeat
  bound server-side, rejects unsupported or mismatched requests before the
  transaction boundary, forwards at most once, and preserves accepted,
  rejected, and unknown outcomes without retry.
- Production constructs the Phase 2R ingress execution-disabled. It is visible
  only through health and Admin diagnostics; `RadioWebSocketEndpoint` remains on
  the existing validation-only `EvaluateBrowserTxIntent` path. Registry,
  coordinator, HTTP, AetherRemote, watchdog, reconnect, and timer types receive
  none of the ingress request/result types. Key, unkey, transaction, boundary,
  submission, transport, SetTransmit, and safety-arming availability remain
  false.
- The focused Phase 2R ingress and lifecycle proof passes 36 cases. The complete
  FlexWeb server suite passes 638 cases and all 123 browser tests pass.
- The final 2026-08-01 Phase 2R guarded gate passed 638 FlexWeb server tests, 25
  independent-watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote tests,
  and 123 browser tests (904 total), with a zero-warning solution build. The
  production web and watchdog artifacts contained no forbidden TX/HIL command
  surface, and the watchdog probe remained empty and Disarmed with no command
  transport or arming capability. Validation log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-browser-tx-transaction-ingress-phase2r-validation-flexweb-validation.txt`.
- Staging release
  `20260801-m7-browser-tx-transaction-ingress-phase2r-final` was activated with
  `20260801-m7-browser-tx-transaction-ingress-phase2r` retained for rollback.
  Internal and public health reported the ingress registered, execution disabled,
  transaction boundary attached, key/unkey unavailable, and WebSocket, HTTP,
  AetherRemote, watchdog, reconnect, and timer callers absent. Transaction,
  boundary, submission, transport, SetTransmit, and safety-arming capabilities
  remained false.
- Browser Bridge acceptance recovered `FLEX-6700` as `RX-ONLY / RADIO: LIVE` in
  the fixed 2D display. MOX, TUNE, and CWX remained hidden and disabled; lease
  acquisition and validation-only intent controls remained disabled. Admin
  showed `browser transaction ingress execution disabled`, boundary attached,
  key/unkey unavailable, attempts/forwarded/accepted/rejected/unknown all zero,
  last `none`, reason `execution-disabled`, and all TX transports absent. A fresh
  Admin console capture contained zero entries. No TX control, microphone
  permission, radio command, or RF operation was used.
- Phase 2S adds one server-owned `StationTxProductionReadinessPolicy`. It
  evaluates the existing transmit, browser-lease, coordinator, submission,
  signing, verification, boundary, adapter, gate, command-transport,
  SetTransmit, emergency-unkey, safety-arm authority, and independent-watchdog
  prerequisites. The result contains one readiness decision, the first blocking
  reason, and the complete ordered missing-prerequisite list; it owns no lease,
  browser authority, transaction, retry, or radio operation.
- The production lifecycle now exposes one internal typed ingress operation that
  accepts only the Phase 2R request and returns only the Phase 2R result. No
  registry, coordinator, WebSocket, HTTP, AetherRemote, watchdog, reconnect, or
  timer type receives the operation or its request/result types. The ingress
  remains execution-disabled, and the WebSocket remains validation-only.
- Phase 2S focused readiness and lifecycle tests pass 29 cases; the combined
  Admin controls and diagnostics proof passes 25 cases. The complete FlexWeb
  server suite passes 661 cases and all 123 browser tests pass with a
  zero-warning solution build.
- The final 2026-08-01 Phase 2S guarded gate passed 661 FlexWeb server tests, 25
  independent-watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote tests,
  and 123 browser tests (927 total). The production web and watchdog artifacts
  contained no forbidden TX/HIL command surface, and the watchdog probe remained
  empty and Disarmed with no command transport or arming capability. Validation
  log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-production-readiness-phase2s-validation-flexweb-validation.txt`.
- Staging release `20260801-m7-production-readiness-phase2s` was activated with
  `20260801-m7-browser-tx-transaction-ingress-phase2r-final` retained for
  rollback. Internal and public health reported the readiness policy registered,
  readiness false, reason `transmit-disabled`, the complete ordered missing list,
  typed lifecycle ingress registered, and WebSocket caller absent. Production
  transmit, lease, ingress execution, transaction, boundary, submission,
  command transport, SetTransmit, watchdog unkey/arming, and supervisor arming
  remained false. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260801-m7-production-readiness-phase2s-flexweb-validation.txt`.
- Browser Bridge acceptance recovered `FLEX-6700` as `RX-ONLY / RADIO: LIVE`.
  MOX, TUNE, and CWX remained hidden and disabled; lease and validation controls
  remained disabled. Admin rendered `production readiness blocked reason
  transmit-disabled` with all 12 public-session prerequisites, the Phase 2R
  ingress execution-disabled with every counter zero, and all TX transports
  absent. A fresh Admin console capture contained zero entries. No TX control,
  microphone permission, radio command, or RF operation was used.
- Phase 2T adds the first reviewed production-primary FLEX key/unkey transport
  after explicit maintainer approval. Its owned configuration defaults disabled,
  requires an exact bounded radio allowlist when enabled, and accepts only local
  `FlexRx` sessions. Remote and simulation sessions are permanently ineligible.
  The command gate remains constructed with `allowTransmit:false`, browser
  ingress remains execution-disabled and callerless, and the emergency-unkey
  transport remains absent.
- The internal command transport contract now carries the exact expected FLEX
  client handle. The gate passes its guarded-intent handle, and the router checks
  that handle under the same lock used to capture the control session. A
  replaced session is rejected before a command write. The adapter sends at most
  once, propagates cancellation, preserves accepted, known-rejected, and unknown
  socket/timeout outcomes, and bounds untrusted radio response text.
- Phase 2T health and Admin diagnostics distinguish transport registration from
  availability. The global transport is registered but configured disabled;
  each session reports bounded eligibility, channel/handle state, counters, and
  last outcome without exposing allowlist values or command text. WebSocket,
  HTTP, AetherRemote, watchdog, reconnect, and timer types receive no production
  transport surface.
- The focused production transport, gate, and reachability proof passes 58
  cases. The complete FlexWeb server suite passes 682 cases and all 48 TX-HIL
  isolation tests pass. The combined Admin controls and diagnostics proof passes
  25 cases.
- The final 2026-08-02 Phase 2T guarded gate passed 682 FlexWeb server tests, 25
  independent-watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote tests,
  and 123 browser tests (948 total), with a zero-warning solution build. A clean
  production publish contained exactly one reviewed `xmit 1` and one reviewed
  `xmit 0` in the web artifact, zero of either in the watchdog artifact, and no
  HIL process, CWX-send, or TX-audio command surface. The published transport
  configuration remained disabled with an empty allowlist, and the watchdog
  probe remained empty, Disarmed, and command-incapable. Validation log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-production-command-transport-phase2t-validation-final-flexweb-validation.txt`.
- Immutable staging release
  `20260802-m7-production-command-transport-phase2t` was activated with
  `20260801-m7-production-readiness-phase2s` retained for rollback. Internal and
  public health reported the production command transport registered, configured
  disabled, allowlist count zero, timeout 2000 ms, availability false,
  SetTransmit false, reason `transport-disabled`, and WebSocket caller absent.
  Production transmit, browser lease, ingress execution, transaction key/unkey,
  signed boundary, command submission, emergency unkey, watchdog command/arming,
  and supervisor arming all remained false. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-production-command-transport-phase2t-flexweb-validation.txt`.
- Browser Bridge acceptance recovered `FLEX-6700` as `RX-ONLY / RADIO: LIVE`.
  MOX, TUNE, and CWX remained hidden and disabled; lease and validation controls
  remained disabled. Admin showed the transport config disabled, local FLEX
  eligibility yes, radio blocked by the empty allowlist, live command channel
  attached, current handle observed, transport and SetTransmit unavailable,
  attempts/forwarded/key/unkey/accepted/rejected/unknown all zero, last
  `none/none`, reason `transport-disabled`, and emergency unkey absent. Readiness
  remained blocked at `transmit-disabled`; the Phase 2R ingress remained
  execution-disabled. A fresh Admin console capture contained zero entries. No
  TX control, microphone permission, radio command, or RF operation was used.
- Phase 2U adds a separate production emergency-unkey transport and a separate
  independent-watchdog unkey transport after explicit maintainer approval. Both
  own only `xmit 0`, default disabled, require exact bounded radio allowlists,
  and add no key or arbitrary-command method. Remote and simulation sessions are
  ineligible.
- The in-process emergency interface now carries the exact protected FLEX
  handle. The safety supervisor passes its arm-record handle, and the shared
  router rejects a replaced connection under the control-session lock before a
  command write. The adapter forwards at most once and preserves accepted,
  known-rejected, and unknown outcomes without retry.
- The watchdog process accepts only a strict optional IPv4 endpoint argument
  set. Its purpose-built TCP client waits for a valid FLEX session handle and can
  encode only `C1|xmit 0`. Protocol version 1 still exposes only status,
  register, heartbeat, and disconnect; no arm or unkey request exists. A process
  with the adapter configured still starts empty and `Disarmed`, reports arming
  unavailable, and cannot invoke the transport in Phase 2U.
- Phase 2U health and Admin diagnostics distinguish registration,
  configuration, channel/handle observation, live availability, arming, bounded
  counters, and caller absence for both unkey transports. The command gate stays
  transmit-disabled, browser transaction ingress stays execution-disabled and
  callerless, and production defaults remain RX-only.
- The focused independent-watchdog proof passes 44 cases. The focused emergency
  transport, watchdog-client, safety, lifecycle, and registry proof passes 68
  cases. The complete FlexWeb server suite passes 693 cases and the combined
  Admin controls and diagnostics proof passes 25 cases.
- The final 2026-08-02 Phase 2U guarded gate passed 693 FlexWeb server tests, 44
  independent-watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote tests,
  and 123 browser tests (978 total), with a zero-warning solution build. The web
  artifact contained one reviewed key string, one runtime-deduplicated unkey
  string, and both reviewed primary/emergency transport type markers. The
  watchdog artifact contained one unkey string, zero key strings, and no HIL,
  CWX, or TX-audio surface. The local watchdog probe began empty, Disarmed,
  transport-disabled, and unarmed. Validation log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-unkey-only-emergency-watchdog-phase2u-validation-flexweb-validation.txt`.
- Immutable staging release
  `20260802-m7-unkey-only-emergency-watchdog-phase2u` was activated with
  `20260802-m7-production-command-transport-phase2t` retained for rollback.
  Internal and public health reported both unkey transports registered,
  configured disabled, allowlist counts zero, timeout 2000 ms, availability and
  unkey false, WebSocket callers absent, and watchdog arming false. Production
  transmit, browser lease, ingress execution, transaction key/unkey, signed
  boundary, command submission, and supervisor arming remained false.
  Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-unkey-only-emergency-watchdog-phase2u-flexweb-validation.txt`.
- Browser Bridge acceptance connected a live PSOC2 session in RX-only mode.
  TUNE and OPERATE remained disabled; emergency and watchdog unkey transports
  remained disabled with zero attempts; both safety supervisors remained
  Disarmed; public health was fail-closed; and fresh radio/Admin console captures
  contained no entries. No TX control, microphone permission, radio command, or
  RF operation was used.
- Phase 2V adds independent-watchdog protocol version 2 after explicit
  maintainer approval. It permits only status, register, arm, heartbeat, disarm,
  and disconnect; no key, unkey, lease, reset, retry, or arbitrary-command
  request exists. A separate `ArmingEnabled:false` default is invalid without the
  reviewed unkey transport.
- The one-shot watchdog controller binds one exact authority tuple and protected
  FLEX handle to a 250-5000 ms heartbeat deadline. Ordinary registration
  heartbeats cannot arm or renew it. Disconnect preserves an active arm. Deadline
  expiry performs at most one ownership-checked unkey; known rejection or
  unknown outcome remains `ReconciliationRequired` without retry.
- The standalone FLEX observer sends only fixed client/TX subscriptions before
  unkey. Fresh idle completes without a command. A different or unconfirmed TX
  owner rejects before the single fixed `xmit 0` write. After dispatch, both the
  matching command response and fresh radio-confirmed idle are required; missing
  idle confirmation is an unknown outcome requiring reconciliation. The
  lifecycle-owned
  safety transaction participant is the only production caller of arm,
  safety-heartbeat, and disarm; browser, HTTP, AetherRemote, reconnect, timer,
  and ordinary lifecycle heartbeat surfaces receive none of those operations.
- The final 2026-08-02 Phase 2V guarded gate passed 704 FlexWeb server tests,
  57 independent-watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote
  tests, and 123 browser tests (1,002 total), with a zero-warning solution build.
  The web artifact contained exactly one reviewed key string, one runtime-
  deduplicated unkey string, and both reviewed primary/emergency transport type
  markers. The watchdog artifact contained one unkey string, zero key strings,
  and no HIL, CWX, or TX-audio surface. Its protocol-v2 status probe began empty,
  Disarmed, transport-disabled, arming-disabled, unarmed, and with zero unkey
  outcomes. Validation log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-watchdog-arming-timeout-unkey-phase2v-validation-flexweb-validation.txt`.
- Immutable staging release
  `20260802-m7-watchdog-arming-timeout-unkey-phase2v-final` was activated with
  `20260802-m7-watchdog-arming-timeout-unkey-phase2v` retained for rollback.
  Internal and public health reported watchdog protocol version 2, arming
  registered but configured disabled, unkey transport configured disabled,
  armed-process count zero, reconciliation-required count zero, unkey-attempt
  count zero, arming unavailable, and WebSocket caller absent. Production
  transmit, browser lease, signing, submission, command gate, ingress execution,
  transaction key/unkey, and both emergency transports remained unavailable.
  Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-watchdog-arming-timeout-unkey-phase2v-final-flexweb-validation.txt`.
- Browser Bridge acceptance recovered the live PSOC2 session in RX-only mode.
  TUNE and OPERATE remained disabled. Public health and Admin showed protocol v2,
  the independent process connected but Disarmed and unarmed, no active lease,
  arming and unkey transport unavailable, and unkey attempt/accepted/rejected/
  unknown counts all zero. Primary and emergency production transports also
  remained disabled with zero counters; radio-authoritative occupancy was idle;
  browser transaction ingress remained execution-disabled and callerless. The
  radio and Admin console captures completed without surfacing an entry. No
  browser TX control, microphone permission, radio command, or live RF/HIL
  operation was used.
- Phase 2W adds a read-only production activation composition over the existing
  deterministic readiness policy. It evaluates current infrastructure on every
  snapshot and exposes only attachment, availability, reason, and nested
  readiness diagnostics. Reflection tests prove the composition has no execute,
  activate, enable, submit, lease, arm, key, unkey, or configuration-mutation
  method. The lifecycle, Admin, and health projections report the composition
  attached but activation unavailable at `transmit-disabled`; no activation
  caller is registered.
- The 2026-08-02 Phase 2W guarded gate passed 709 FlexWeb server tests, 57
  independent-watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote tests,
  and 123 browser tests (1,007 total), with a zero-warning solution build. The
  production web artifact retained exactly one reviewed key string and one
  runtime-deduplicated unkey string with both reviewed transport markers. The
  watchdog artifact retained one unkey string, zero key strings, and no HIL,
  CWX, or TX-audio surface. Validation log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-production-tx-activation-composition-phase2w-validation-flexweb-validation.txt`.
- Immutable staging release
  `20260802-m7-production-tx-activation-composition-phase2w` was activated with
  `20260802-m7-watchdog-arming-timeout-unkey-phase2v-final` retained for
  rollback. Internal and public health reported the activation composition
  registered, activation unavailable, reason `transmit-disabled`, and activation
  caller absent. Production transmit, browser lease, signing, submission,
  boundary, command gate, transaction execution, primary/emergency transports,
  and watchdog arming remained disabled or unavailable. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-production-tx-activation-composition-phase2w-flexweb-validation.txt`.
- Browser Bridge acceptance recovered a live PSOC2 RX-only session. TUNE,
  OPERATE, ACQUIRE LEASE, and VALIDATE ONLY remained disabled. The fixed 2D
  spectrum/waterfall canvases were present with no WebGL path. Admin showed
  `DISABLED · DISARMED · NO LEASE`, production readiness blocked at
  `transmit-disabled`, and the activation composition evaluation attached but
  unavailable. The first console capture observed transient session-load errors
  during deployment cutover; fresh post-stabilization radio and Admin captures
  contained zero errors. No browser TX control, microphone permission, radio
  command, or live RF/HIL operation was used.
- Phase 2X adds the feature-owned `StationTxProductionActivation` configuration
  object with one disabled-by-default `Enabled` request switch. Startup projects
  all currently configurable static TX prerequisites into one deterministic
  interlock. An explicit request fails before application startup when local
  `FlexRx` mode, transmit/browser lease opt-ins, command trust and signing keys,
  envelope submission, allowlisted primary/emergency transports, or supervised
  watchdog unkey and arming are incomplete. The unrequested default remains
  valid, exposes its complete static missing-prerequisite list, and adds no
  activation, browser, lease, command, gate, transport, watchdog operation, or
  radio-authority method.
- The 2026-08-02 Phase 2X automated and guarded validation gates passed a
  zero-warning solution build plus 715 FlexWeb server tests, 57 independent-
  watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote tests, and 123
  browser tests (1,013 total). Production artifact inspection retained exactly
  one reviewed key string and one runtime-deduplicated unkey string with both
  reviewed transport markers; the watchdog retained one unkey string and zero
  key strings, with no additional TX/HIL, CWX, or TX-audio surface. Validation
  log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-production-tx-activation-config-interlock-phase2x-validation-flexweb-validation.txt`.
- Immutable staging release
  `20260802-m7-production-tx-activation-config-interlock-phase2x` was activated
  with `20260802-m7-production-tx-activation-composition-phase2w` retained for
  rollback. Internal and public health reported activation configuration
  registered, request absent, configuration valid, reason
  `activation-not-requested`, interlock attached, activation unavailable, and
  activation caller absent. Dynamic readiness remained blocked at
  `transmit-disabled`; browser ingress, boundary, gate, primary/emergency
  transports, and watchdog arming remained disabled or unavailable. Deployment
  log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-production-tx-activation-config-interlock-phase2x-flexweb-validation.txt`.
- Browser Bridge acceptance recovered the live RX-only session after deployment.
  TUNE, OPERATE, ACQUIRE LEASE, and VALIDATE ONLY remained disabled. All three
  rendered canvases used the fixed 2D path with no WebGL context. Admin showed
  `DISABLED · DISARMED · NO LEASE`, the configuration interlock attached,
  activation request absent, configuration valid, activation unavailable at
  `activation-not-requested`, and the expected 14 static staged prerequisites.
  The initial cutover capture contained only stale-session 502/404 responses;
  fresh post-stabilization radio and Admin captures contained zero entries. No
  browser TX control, microphone permission, radio command, or live RF/HIL
  operation was used.
- Phase 2Y adds `StationTxProductionActivationPlanner` and one immutable
  four-switch activation plan over the validated Phase 2X request. A valid
  request describes command-boundary enablement, command-gate transmit,
  browser-transaction-ingress execution, and browser keying-capability
  projection as one all-or-nothing unit. The planner exposes only a fresh
  snapshot; absent or invalid requests keep every switch false. Phase 2Y always
  reports the plan unapplied and passes it to no boundary, gate, ingress,
  coordinator, transport, watchdog, browser, or radio operation.
- The 2026-08-02 Phase 2Y automated and guarded validation gates passed a
  zero-warning solution build plus 723 FlexWeb server tests, 57 independent-
  watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote tests, and 123
  browser tests (1,021 total). Production artifact inspection retained exactly
  one reviewed key string and one runtime-deduplicated unkey string with both
  reviewed transport markers; the watchdog retained one unkey string and zero
  key strings, with no additional TX/HIL, CWX, or TX-audio surface. Validation
  log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-production-tx-activation-plan-phase2y-validation-flexweb-validation.txt`.
- Immutable staging release
  `20260802-m7-production-tx-activation-plan-phase2y` was activated with
  `20260802-m7-production-tx-activation-config-interlock-phase2x` retained for
  rollback. Internal and public health reported the planner registered and
  attached, plan unavailable and unapplied, all four planned switches false,
  reason `activation-not-requested`, and no plan or activation caller. Dynamic
  readiness remained blocked, and browser ingress, boundary, gate,
  primary/emergency transports, and watchdog arming remained disabled or
  unavailable. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-production-tx-activation-plan-phase2y-flexweb-validation.txt`.
- Phase 2Y Browser Bridge acceptance opened fresh radio and Admin tabs after
  the extension was reconnected. The live RX session recovered; TUNE, OPERATE,
  ACQUIRE LEASE, and VALIDATE ONLY remained disabled. All three canvases used
  the fixed 2D path with no WebGL context. Admin showed
  `DISABLED · DISARMED · NO LEASE`, the activation plan attached but unavailable
  and unapplied, every planned switch off, and reason
  `activation-not-requested`. Fresh radio and Admin console captures contained
  zero entries. No browser TX control, microphone permission, radio command, or
  live RF/HIL operation was used.
- Phase 2Z adds one immutable per-session activation binding that applies the
  Phase 2Y command-boundary, command-gate transmit, browser-ingress execution,
  and browser keying-capability switches only as a complete set. Binding requires
  the reviewed master request, local `FlexRx`, transmit and browser-lease
  opt-ins, and the complete Phase 2X static prerequisite set. Remote, simulation,
  absent, incomplete, or partial activation remains fully unbound. Browser TX
  protocol version 2 adds a strict purpose-bound `tx.heartbeat`; successful
  MOX/PTT key and unkey requests now delegate through the existing signed
  transaction, gate, local safety arm, production transport, and independent
  watchdog. TUNE, microphone transmit, and CW remain unavailable.
- Phase 2Z no-RF integration testing used deterministic in-process command,
  emergency-unkey, signing/verifying, and independent-watchdog fakes while
  exercising the real browser coordinator, lifecycle, transaction composition,
  command boundary, gate, radio-occupancy reconciliation, and safety layers. One
  deliberate key produced one recorded `true` transport call and fresh simulated
  Aether-owned TX confirmation; one browser safety heartbeat renewed the exact
  active transaction; monotonic renewal of the same lease remained valid; one
  deliberate unkey produced one recorded `false` call, fresh idle confirmation,
  gate-intent cleanup, transaction cleanup, and both safety layers Disarmed.
  The proof emitted no FLEX command, opened no radio socket, and produced no RF.
  It also exposed and fixed the dormant connection-ID/lease-ID mismatch,
  active-lease-expiry extension mismatch, missing heartbeat authority refresh,
  missing radio-confirmation barrier before browser success or safety cleanup,
  and cancellation during post-command radio confirmation now enters explicit
  reconciliation instead of leaving an unclassified active transaction.
- The 2026-08-02 Phase 2Z automated, validation-only, and final guarded
  deployment gates passed a zero-warning solution build plus 737 FlexWeb server
  tests, 57 independent-watchdog tests, 48 TX-HIL isolation tests, 70
  AetherRemote tests, and 127 browser tests (1,039 total). The browser suite also
  proves that live keying capability disables the older validation-only selector
  so an executable intent cannot be presented as a dry run. Production artifact
  inspection retained exactly one reviewed key string and one runtime-
  deduplicated unkey string with both reviewed transport markers; the watchdog
  retained one unkey string and zero key strings, with no additional TX-HIL,
  CWX, TX-audio, or process-child surface. Validation-only log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-production-tx-activation-binding-phase2z-validation-flexweb-validation.txt`.
- Immutable default-config staging release
  `20260802-m7-production-tx-activation-binding-phase2z-hardened` was activated
  with `20260802-m7-production-tx-activation-binding-phase2z-final` retained for
  rollback.
  Internal and public health reported browser TX protocol v2, activation binding
  registered and attached but unapplied, session ineligible, all four bound
  switches false, activation request absent, browser WebSocket transaction caller
  absent, primary/emergency transports disabled, watchdog arming unavailable,
  and zero watchdog unkey attempts. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-production-tx-activation-binding-phase2z-hardened-flexweb-validation.txt`.
- Phase 2Z Browser Bridge acceptance opened the deployed radio and Admin pages,
  used only the read-only Admin Refresh action, and recovered a live RX session.
  ACQUIRE LEASE and VALIDATE ONLY remained disabled; MOX, TUNE, and CWX remained
  hidden and disabled. All three canvases had 2D contexts and no WebGL context.
  Admin showed `DISABLED · DISARMED · NO LEASE`, browser transaction ingress
  execution disabled, the activation binding attached but unapplied, all four
  bound switches off, reason `activation-not-requested`, zero transaction
  attempts, zero primary key/unkey calls, and zero independent-watchdog unkey
  attempts. The first post-cutover radio capture contained only one stale-session
  502/404 pair; fresh stabilized radio and Admin captures each contained zero
  console entries. No lease was acquired, no browser TX intent or heartbeat was
  sent, no microphone permission was requested, and no live RF/HIL operation was
  run.
- Phase 3A adds a non-starting production TX activation preflight to the normal
  web executable. `--validate-production-tx-activation` requires one exact
  `--production-tx-radio-id`, loads the same deployment configuration as the
  service, simulates the master activation request without changing it, validates
  key files and permissions, matches the signing fingerprint to the trusted
  public key under the same key ID, checks all three exact radio allowlists,
  validates watchdog settings and the reviewed executable, emits redacted JSON,
  and exits before the host, dependency injection, hosted services, watchdog,
  HTTP listener, lease manager, or radio transport can start. The owner-only
  deployment wrapper rejects symbolic links and environment files other than
  exact mode 0400/0600. A ready report is only permission to proceed to a
  separate operator-controlled activation; it does not enable TX.
- The 2026-08-02 Phase 3A automated gate passed a zero-warning solution build
  plus 741 FlexWeb server tests, 57 independent-watchdog tests, 48 TX-HIL
  isolation tests, 70 AetherRemote tests, and 127 browser tests (1,043 total).
  The four focused preflight cases prove that a fully staged package reports
  ready while the actual master switch remains off; mismatched signing and trust
  keys, a wrong primary radio allowlist, and a non-executable watchdog each fail
  closed. Direct CLI and owner/mode-guarded wrapper runs against RX-only config
  exited 2, listed only redacted missing-prerequisite codes, and explicitly
  reported web host, radio connection, and watchdog process all not started.
- Immutable RX-only staging release
  `20260802-m7-production-tx-activation-preflight-phase3a-final` was activated
  with `20260802-m7-production-tx-activation-preflight-phase3a` retained for
  rollback. The final release packages the owner-only preflight wrapper under
  `current/tools`; the guarded deployment rejects a missing, non-executable, or
  syntactically invalid local or deployed wrapper. It reran all 1,043 tests,
  production binary inspection, and the Disarmed watchdog probe. Internal and
  public health remained fail-closed with activation not requested, all four
  binding switches off, transports disabled, watchdog arming unavailable, and
  zero TX activity. Deployment log:
  `/home/devspace/.local/state/aethersdr-web/deploy-logs/20260802-m7-production-tx-activation-preflight-phase3a-final-flexweb-validation.txt`.
- The packaged deployed wrapper was invoked directly against the real owner-only
  service environment. It exited 2, confirmed the reviewed watchdog executable
  was ready, reported the current master activation request absent, and listed
  only redacted missing codes for the still-disabled transmit, browser lease,
  trust/signing, submission, transport/allowlist, and watchdog-arm prerequisites.
  It explicitly reported web host, radio connection, and watchdog process all
  not started.
- Phase 3A Browser Bridge acceptance used the exact final release radio and Admin
  pages without acquiring a lease or invoking a TX control. The live RX session
  recovered with three 2D canvases and no WebGL context; TUNE, OPERATE, ACQUIRE
  LEASE, and VALIDATE ONLY remained disabled, while MOX and CWX remained hidden.
  Admin showed `DISABLED · DISARMED · NO LEASE`, browser transaction ingress
  execution disabled, the activation binding attached but unapplied, all four
  bound switches off, reason `activation-not-requested`, zero transaction
  attempts, zero primary key/unkey calls, and zero independent-watchdog unkey
  attempts. The first radio capture contained only a transient network-change
  and stale-session recovery pair; fresh stabilized radio and Admin captures
  each contained zero console entries. No microphone permission, browser TX
  intent, safety heartbeat, radio command, or live RF/HIL operation was used.
- The 2026-08-02 operator-authorized production browser MOX check used the
  reviewed PSOC2 FLEX-6700 station at 14.317874 MHz after the operator confirmed
  the frequency clear, RF path safe, local/camera observation available, and
  remote emergency-off ready. The exact browser lease, session, engine, and FLEX
  handle were fresh; one browser key command was accepted and radio-confirmed.
  The immediate second click occurred before the control transitioned and sent
  no browser unkey. The independent watchdog then issued one accepted deadline
  unkey and the radio returned to authoritative idle, but the web process left
  the local command transaction and safety arm stale. Primary command counts
  were key 1/unkey 0; watchdog unkey count was 1 accepted. The lease was released,
  the complete pre-TX environment backup was restored, the service was restarted,
  activation returned to `activation-not-requested`, browser keying became
  unavailable, and no further RF test was run.
- Phase 3B adds serialized lifecycle-only reconciliation for that exact failure.
  An active transaction records the watchdog host and accepted-unkey baseline.
  Cleanup requires a later accepted `deadline-unkey-accepted` count from the same
  host, the exact watchdog identity tuple, and fresh radio idle. While holding
  the transaction operation lock, a command-incapable participant verifies that
  the gate intent and local arm belong to the same transaction, consumes idle in
  the gate, resets the local supervisor from idle, rechecks fresh idle, and
  clears the transaction only after both gate and safety snapshots are empty.
  Stale evidence, watchdog replacement, identity mismatch, or non-idle/stale
  radio state remains reconciliation-required and invokes no cleanup participant.
- The 2026-08-02 Phase 3B automated gate passed targeted formatter verification,
  a zero-warning solution build, 745 FlexWeb server tests, 57 independent-
  watchdog tests, 48 TX-HIL isolation tests, 70 AetherRemote tests, and 127
  browser tests (1,047 total). The regression proof reproduces accepted browser
  key, accepted watchdog deadline unkey, radio idle, and lease release; it clears
  gate, local safety, and transaction state without a primary or emergency
  command. Negative cases prove pre-existing watchdog counts and non-idle radio
  evidence remain fail-closed. Production TX remained disabled throughout this
  automated validation, and the live MOX scenario has not yet been repeated
  against the fix.
- The 2026-08-02 live no-RF station check built the merged TX-HIL tool with zero
  warnings, observed the reviewed radio freshly idle with zero TX occupants and
  no external GUI client, and found the HIL GUI profile recalling 1 W. The
  hard-bound `restore-idle-defaults` operation twice radio-confirmed 100 W, DAX
  on, PC microphone selection, VOX off, idle, zero occupants, `rfEmitted:false`,
  and no key or unkey command. The ten-scenario simulated safety fault matrix
  passed with no radio connection and an unkey-only boundary. The live non-GUI
  observer preflight also passed: exact engine Local PTT ownership was observed
  while idle, the observer was not a GUI client, zero unkey commands were sent,
  and no key command was available. The full state-changing no-RF frequency
  preflight remains pending an operator-supplied clear-frequency/camera/remote-
  off confirmation; no token was fabricated and no live RF operation was run.
- Final live M7 acceptance on 2026-08-02 proved normal production-browser key
  and unkey plus browser/session loss, authentication loss, gateway-process loss,
  engine command-channel loss, and engine process/TCP loss. Every keyed loss path
  returned the reviewed radio to authoritative idle through the ownership-safe
  command or independent-watchdog path without disturbing external transmitters.
  Final no-RF restoration confirmed the radio idle defaults and zero leaked
  browser authority.
- A delayed emergency transport exposed a duplicate-unkey retry window because
  confirmation timeout measurement began before the awaited transport completed.
  The supervisor now starts that window after transport completion. Regression
  coverage proves a slow accepted unkey cannot immediately trigger a duplicate
  retry.
- Post-deployment Browser Bridge acceptance exposed one final fail-closed UI
  defect: the browser retained its initial pre-radio `radio-disconnected` TX
  capability after a receive session became healthy. Connection snapshots now
  carry a fresh per-browser TX capability, while the client preserves monotonic
  request state and discards local authority immediately when the server revokes
  it. The accepted page reached `LIVE`, advertised `LEASE AVAILABLE`, and enabled
  only lease acquisition; no lease, TX intent, or RF command was used during the
  corrective acceptance.
- The final guarded production gate passed a zero-warning solution build plus
  747 FlexWeb server tests, 57 independent-watchdog tests, 48 TX-HIL isolation
  tests, 70 AetherRemote tests, and 128 browser tests. Artifact inspection kept
  the exact reviewed key/unkey and watchdog surfaces with no additional TUNE,
  CWX, microphone-TX, or HIL path. GitHub CI passed after PR #37 merged, and
  production returned to zero web operators, zero browser GUI clients, zero
  remote receive sessions, and an empty Disarmed watchdog state.

Acceptance criteria:

- A single-holder TX lease is enforced below the browser boundary and keyed by
  physical radio, not browser session.
- An authorized operator can deliberately key and unkey a radio from the
  production browser through the station-local TX gate.
- Browser microphone audio, TUNE, and CW require explicit operator action,
  transmit capability, the exact physical-radio lease, and matching
  browser/session/engine/FLEX ownership.
- Lease loss, authentication loss, browser loss, gateway loss, and engine
  restart immediately remove browser TX authority and force an ownership-safe
  unkey of AetherSDR-owned TX without interrupting external SmartSDR, Maestro,
  or hardware-PTT transmission.
- Production TX defaults to disabled and requires explicit reviewed
  configuration to enable; receive-only deployments retain no reachable keying
  command.
- Browser-driven production HIL proves key, microphone audio or bounded test
  modulation, TUNE and CW where enabled, unkey, final radio-authoritative idle,
  resource cleanup, restart/reconnect behavior, and external-client protection.

## M8 — Standalone self-hosted production release

Status: **Active — standalone installation, onboarding, and lifecycle planning**

Goal: let an individual radio owner install, secure, operate, update, back up,
and recover AetherSDR without requiring an existing enterprise network, Entra ID
tenant, reverse proxy, or hand-edited application configuration.

Product direction:

- The primary supported deployment is a native standalone installation on
  Ubuntu Server 24.04 with hardened systemd services. Container packaging is
  outside M8 scope.
- One guided installer supports a personal single-site station, a public gateway
  with remote radios, or an existing gateway adding another remote station.
- Setup collects one canonical public URL and uses it for authentication
  callbacks, browser and station WebSockets, generated proxy configuration,
  AetherRemote enrollment, hosted downloads, health links, and diagnostics.
- Production TX remains disabled by default. Server-wide activation and explicit
  per-radio eligibility are both required; receive-only radios retain no
  reachable keying path.
- Human identities, station device credentials, release-signing trust, command
  signing keys, and radio TX leases remain separate least-privilege authorities.
- Installation and update operations provide a dry run, preserve live state,
  install immutable releases, verify health, and roll back automatically on any
  failed activation or migration.

Implementation slices:

### M8A — Portable configuration and first-run foundation

Status: implemented through the browser preflight, restart-recovery, lifecycle
shutdown, and clean-host acceptance boundary. Production administrator creation
remains M8D; installer, proxy, service, package, and firewall mutation remains
M8C.

- Define versioned configuration, state, secret, release, backup, and log paths
  that do not depend on the W4CAR topology.
- Add an installation-state model and a resumable first-run setup center.
- Print a short-lived, local-only first-administrator setup token; an unfinished
  Internet-facing installation cannot be claimed without that token.
- Collect deployment topology, canonical public URL, data location, update
  channel, backup location, and whether TX support will be installed. Installing
  TX support does not enable TX.
- Provide a non-mutating preflight that reports planned users, packages, ports,
  files, services, proxy changes, firewall expectations, and migrations.

### M8B — Signed GitHub releases and transactional updates

Status: active. The local-only signed-manifest verifier, normal-runtime public-key
trust composition, immutable local offline-directory bundle reader, read-only
offline bundle CLI `check`, read-only local release `status`, read-only offline
install preflight, verified installation-plan composition, private verified staging,
atomic inactive-release publication, activation-transaction plan composition,
activation-readiness evidence evaluation, authoritative runtime evidence
collection, exact-plan TX-lease admission closure/drain composition, exact-plan
configuration-backup planning and atomic execution, exact-plan staged-copy
migration planning, disabled-by-default locally pinned migration-runner trust and
exact selection, callerless probe-only runner invocation, exact staged-copy migration
execution, exact-plan migration evidence, exact service-control transaction
planning, disabled-by-default exact local two-phase service-control execution and
evidence, disabled-by-default exact atomic current-pointer switching and evidence,
exact post-switch health-verification planning, disabled-by-default exact health
execution, exact-plan health evidence, exact rollback transaction planning,
disabled-by-default exact rollback execution and evidence, exact operator-approval
authority, and disabled-by-default read-only GitHub release checking through the same
signed-bundle verifier are implemented; publishing artifacts, persistent download,
extraction, operational backup orchestration, transaction orchestration, host-restart
and remote-node service-control transports, authenticated approval issuance, and
Admin/browser callers remain unimplemented.

The first increment defines a strict version-1 JSON manifest for one release
identity, semantic version, Stable/Beta/exact-Pinned channel, supported Linux
architecture, the four required package roles, SHA-256 and length metadata,
configuration and protocol compatibility, minimum previous version, restart and
migration declarations, bounded release-note metadata, and a versioned TX-support
capability declaration. One canonical payload plus algorithm and key identifier is
verified with injected ECDSA P-256/SHA-256 public-key material. Unknown or duplicate
JSON fields, unsupported schema/algorithm/key/architecture, invalid signatures,
unsafe or duplicate package declarations, missing or extra local packages,
checksum/length drift, incompatible transitions, contradictory restart/migration
metadata, and any TX-support authority grant fail closed. The typed redacted
report omits signature, checksum, path, and key material and does not reflect
unverified release metadata.

This increment operates only on immutable copied local bytes. It adds no network,
GitHub, polling, download, extraction, installation, active-release mutation,
symlink switch, service control, migration, backup/restore, radio, watchdog,
command, browser, or TX caller. Deterministic signing material exists only in the
focused test assembly; no production private key or production trust anchor is
committed.

The second increment adds a strict disabled-by-default `ReleaseManifestTrust`
configuration, a bounded immutable public-key registry, and one local verification
service composed with the first increment. Normal-runtime startup rejects unknown
configuration, unsupported algorithms, duplicate identifiers or paths, relative
or non-canonical paths, missing/empty/oversized keys, symlinks, unsafe Unix write
permissions, invalid or multiple PEM blocks, private keys, invalid UTF-8, and
non-P-256 public keys. Enabling verification requires at least one reviewed key.
The registry exposes only redacted readiness diagnostics and the service has no
network, download, extraction, installation, activation, service-control,
migration, backup, radio, watchdog, command, lease, browser, or TX method. Health
reports local verification readiness and explicitly reports those update mutation
surfaces as unregistered.

The third increment adds a typed reader for one pre-existing local offline bundle
directory containing exactly `release-manifest.json` and four package files. It
requires one canonical absolute root, manually traverses no more than sixteen
regular directories, rejects symlinks, reparse points, unsafe paths, empty
subdirectories, missing or extra files, and empty or oversized inputs, and requires
all Unix bundle directories and files to have no write bits. The manifest is
copied under its one-megabyte bound; package files are streamed into immutable
length and SHA-256 snapshots and rechecked for metadata drift before the existing
trust-backed verifier decides acceptance. The composed service has no configured
path, startup scan, polling, archive extraction, download, installation, staging,
activation, service-control, migration, backup, radio, watchdog, command, lease,
Admin, browser, or TX caller.

The fourth increment adds the first read-only release CLI workflow:
`--check-offline-release-bundle`. Its owned parser requires the canonical bundle
path, exact installed semantic version, Stable/Beta/Pinned channel, a pinned
identity only for Pinned, and positive canonical configuration-schema and protocol
versions. It derives Linux architecture from the running process, is mutually
exclusive with setup commands and production-TX preflight, and returns before any
web host, setup-only host, authentication, hosted service, radio, watchdog, or
routing composition. It constructs the same production trust registry, rejects
unavailable trust before filesystem I/O, emits one redacted versioned JSON report,
and returns `0` only for a fully verified bundle or `2` for a verification/read
failure. It adds no network, extraction, staging, installation, activation,
rollback, migration, service-control, Admin, browser, radio, watchdog, command,
lease, or TX method.

The fifth increment adds `--release-status`. It loads but never creates setup
state, requires persisted installation paths to equal the currently resolved
layout, and then reads only direct children of the configured release directory
plus its sibling `current` symbolic link. Missing release storage or a missing
pointer is a successful empty/inactive status. Unsafe setup state, files or links
inside the release inventory, non-canonical identities, group/other-writable Unix
directories, more than 64 releases, non-link or non-canonical `current` entries,
and targets outside or absent from the inventory fail closed. The versioned report
omits every path, returns `0` for a trustworthy snapshot or `2` for an unsafe or
unreadable layout, and deliberately reports no known rollback candidate. It adds
no network, extraction, staging, installation, activation, rollback, migration,
service-control, Admin, browser, radio, watchdog, command, lease, or TX method.

The sixth increment adds `--preflight-offline-release-install`. Its owned parser
requires a canonical immutable bundle path plus exact active release identity,
installed semantic version, configuration-schema version, and protocol version.
Completed setup supplies the channel, Pinned identity, installation paths, and
TX-support installation policy; Linux architecture is derived from the process.
Preflight requires completed setup and a validated `current` pointer matching the
supplied identity, delegates the bundle to the existing production-trust-backed
verifier, rejects an equal or already-inventoried target and any TX-support policy
mismatch, then rereads setup, inventory, and `current` to detect concurrent drift.
The path-redacted report returns `0` only for a stable eligible plan or `2` for any
rejection. It adds no network, download, extraction, write, staging, installation,
activation, rollback, migration execution, service-control, Admin, browser, radio,
watchdog, command, lease, or TX method.

The seventh increment retains an internal defensive manifest snapshot only after
full signature, compatibility, restart/migration, TX-support, package inventory,
length, and SHA-256 verification. Successful preflight carries that snapshot
internally without changing its public redacted report. A pure
`VerifiedReleaseInstallationPlanComposer` then binds the stable preflight to
resolved installation paths, checks exact identity/version/architecture/channel/
TX-support agreement, validates the four-role package plan, and derives canonical
direct target and package destinations plus signed restart, migration, release-
notes, length, and digest metadata. Public diagnostics and results expose no paths,
package names, or digests. The composer performs no filesystem I/O and registers
no network, extraction, file write, staging execution, installation, activation,
rollback, migration execution, service-control, Admin, browser, radio, watchdog,
command, lease, or TX caller.

The eighth increment adds `VerifiedReleaseStagingService`, the first mutation
boundary. It is registered for diagnostics but exposes no public execution method
and has no CLI, Admin, browser, hosted-service, timer, startup, radio, watchdog,
command, lease, or TX caller. Its internal operation accepts only the verified
plan, rereads completed setup/inventory/`current`, and requires the same revision,
channel/Pinned selection, TX-support policy, active release, and absent target.

The service creates one unique owner-private transaction directory under the
deployment sibling `.release-staging`, re-enumerates exactly the immutable manifest
and four package files, streams each into a new destination while checking retained
length and SHA-256 values, flushes them, revalidates source layout, freezes the
staged tree owner-only/non-writable, rehashes it, and rereads release status. Any
copy, integrity, layout, cancellation, target, or status failure removes the
partial tree when cleanup remains safe. Success returns the staging path only in
an internal artifact. It does not create the target release directory, switch
`current`, extract archives, install, activate, roll back, execute migrations,
control services, or touch radio/TX state.

The ninth increment adds `VerifiedReleasePublicationService`. It exposes only
public diagnostics and has no CLI, Admin, browser, hosted-service, startup, timer,
or operational caller. Its internal method requires the exact successful staging
token, rereads completed setup/inventory/`current`, and rehashes the frozen tree.
It then temporarily opens only the verified root directory owner-writable, uses
one no-overwrite cross-parent `Directory.Move` into the absent direct release
target, immediately refreezes the root, rehashes the published tree, and requires
status to show exactly one inventory addition with the active release unchanged.
Cancellation stops before but never interrupts outcome reconciliation after the
atomic rename. Ambiguous post-rename state is retained and reported for
reconciliation rather than deleted. The boundary never copies files, switches
`current`, activates, rolls back, executes migrations, controls services, or
touches Admin/browser, AetherRemote runtime, radio, watchdog, command, lease, or
TX state.

The tenth increment adds `VerifiedReleaseActivationPlanComposer`, a pure planning
boundary that accepts only a successful immutable inactive publication with an
exact internal token, consumed staging source, unchanged `current`, no activation,
and no reconciliation requirement. It validates setup/identity/version/
architecture/channel/TX-policy agreement, exact publication byte totals, canonical
release and relative link paths, the four unique package roles, and coherent signed
schema-migration metadata. The internal plan preserves previous/target paths,
`current` link values, signed migration/restart/release-note metadata, and service
roles while the public result remains path-, package-, and digest-redacted.

Every successful activation plan requires future operator approval, closure of new
TX-lease admission, radio-authoritative idle, disarmed watchdogs, configuration
backup, staged-copy migration when declared, atomic `current` switching, service
health verification, and automatic rollback. The composer does not perform or
assert any of those steps and registers no filesystem write, pointer mutation,
activation, backup, migration execution, service control, health probe, CLI/Admin/
browser, AetherRemote runtime, radio, watchdog, command, lease, or TX caller.

The eleventh increment adds `VerifiedReleaseActivationReadinessEvaluator`. It is
registered for diagnostics but exposes no public evaluation method and has no
evidence collector or operational caller. Its internal evaluation accepts only the
successful activation plan plus one bounded snapshot no more than five seconds old.
It rechecks plan/public-summary agreement and requires release status to retain the
same completed setup revision, channel/Pinned policy, TX-support installation
selection, previous active identity, and published inactive target inventory.

Readiness requires explicit closure of TX-lease admission, zero active leases, and
unique bounded session evidence. Every active session must be connected, report
fresh radio-authoritative idle with no occupants, have an idle/no-intent gate,
disarmed inactive safety state, no active or reconciliation-required command
transaction, and a disarmed reconciliation-free independent watchdog. The global
watchdog aggregate must agree and may not be degraded, armed, or awaiting
reconciliation; TX-support installations additionally require exact session,
running, connected, and registered watchdog counts.

The same evidence snapshot must prove a prepared configuration backup, resolved
signed migration requirement, required service/host restart control, post-switch
health verification, automatic rollback, and explicit operator approval. Success
returns an internal defensive readiness token while the public report contains only
counts and booleans. It exposes no paths, package names, digests, radio/session/
lease identities, occupants, or process data and adds no filesystem write, lease
mutation, radio/watchdog command, pointer switch, activation, backup, migration,
service, health-probe, rollback, CLI/Admin/browser, hosted-service, timer,
AetherRemote, command, lease, or TX caller.

The twelfth increment adds `VerifiedReleaseActivationEvidenceCollector`, a
callerless internal collector over the existing release-status reader, TX lease
manager, radio-session registry, and independent-watchdog registry. It accepts only
the exact successful activation plan, reads release status before and after the
runtime snapshots, rejects any drift, and requires the whole collection to remain
inside the evaluator's five-second freshness window. Session diagnostics are
projected into the same bounded internal safety evidence used by the evaluator.

A new internal TX-lease observation snapshot reads the manager under its existing
lock without expiring leases or publishing change events. Expired stored leases
therefore remain visible and fail closed. The collector defensively copies release
inventory, lease, and session collections before retaining one internal token. Its
public report exposes only counts and booleans and omits paths, inventory,
radio/session/lease identities, occupants, watchdog process data, package names,
and digests.

The collector marks configuration backup, required migration execution, required
service/host control, health verification, rollback, and operator approval
unavailable because no authoritative sources exist yet. A signed no-migration or
no-restart plan may satisfy only that corresponding no-op prerequisite. It exposes
no public collection method and adds no filesystem write, pointer mutation,
activation, lease mutation, radio/watchdog command, backup, migration, service
control, health probe, rollback, CLI/Admin/browser, hosted-service, timer,
AetherRemote, command, lease, or TX caller.

The thirteenth increment adds
`VerifiedReleaseActivationLeaseQuiescenceBoundary`, a callerless internal boundary
that composes one exact verified activation plan into an opaque lease-admission
closure token. Composition is non-mutating. Closure is serialized under the same
`TxLeaseManager` lock as acquisition and renewal, so an active transaction rejects
both new leases and renewals without racing either path. Independently composed or
equivalent-but-distinct plan tokens cannot reuse or take over the active closure.

Existing leases remain owner-controlled and are never force-released. Validation
and release remain available, while ordinary expiry continues through the existing
watchdog/event safety lifecycle. Drain evaluation is observation-only and leaves
expired stored leases visible until that normal expiry boundary resolves them.
Zero stored leases proves only lease drain; it never infers radio-authoritative
idle, watchdog safety, or activation authority. Normal lease behavior is unchanged
when no activation closure exists.

The evidence collector now reads exact-plan closure state and the stored lease set
from the same serialized observation. Public health diagnostics separately report
composition, closure authority, active state, acquisition and renewal suppression,
drain evaluation, absence of force-release and lease mutation, absence of radio-idle
inference, operational callers, and activation authority. The closure operation has
no public method and no CLI, Admin, browser, HTTP, WebSocket, hosted-service, timer,
AetherRemote, command, radio, watchdog, TX, or activation caller.

The fourteenth increment adds
`VerifiedReleaseActivationConfigurationBackupPlanner`, a callerless pure boundary
that composes the exact internal activation plan with the resolved installation
layout. It rejects missing, ineligible, or public-summary-mismatched plans; requires
the resolved release root to match the activation plan; and rejects non-canonical,
filesystem-root, nested, or otherwise overlapping configuration, state, secret,
release, backup, and log roots. Non-release installation roots must also remain
outside the activation deployment root.

The internal plan maps exactly the dedicated configuration, state, and secret roots
into distinct private staging children beneath one bounded activation-backup
identity. It also records a separate manifest and final publication path, requires
future atomic publication, forbids overwrite, defensively copies the source plan,
and retains the exact activation-plan object rather than trusting equivalent public
metadata. Public reports remain path-redacted.

Composition does not inspect whether a source or backup exists, read configuration
or secret content, create a directory, write a manifest or backup, alter permissions,
rename or overwrite a tree, provide configuration-backup evidence, mutate `current`,
or authorize activation. Health diagnostics separately report source/path/identity/
manifest/atomic planning and the absence of reads, writes, mutation, overwrite,
execution, evidence, operational callers, and activation authority. There is no
public composition or execution method and no CLI, Admin, browser, HTTP, WebSocket,
hosted-service, timer, AetherRemote, service-control, radio, watchdog, command,
lease, TX, or activation caller.

The fifteenth increment adds
`VerifiedReleaseActivationConfigurationBackupService`, a callerless Linux-only
executor for that exact internal plan. It rechecks the planning report, canonical
layout, exact activation-plan object, completed setup revision and policy, inactive
target inventory, and unchanged previous `current` identity before reading source
content. The dedicated backup root must already be one owner-private non-link
directory.

Configuration, state, and secret traversal rejects links, reparse points, shared
write permissions, and every shared secret permission. It is bounded to 512
directories, 4,096 files, 128 MiB per file, and 1 GiB total. Files are copied into
create-new mode-0600 private staging while SHA-256 is computed and writes are
flushed. The service then re-enumerates and rehashes every source; any path,
metadata, mode, length, digest, or release-status drift removes staging and fails
closed.

A path-redacted manifest records exact setup/release identities and bounded relative
entry metadata. Files and directories are frozen mode 0400/0500, the complete tree
is revalidated, and one absent final identity is atomically renamed into place.
Existing staging or final identities are never reused, deleted, or overwritten.
Ambiguous publication or failed post-publication validation retains the tree and
requires reconciliation. Success retains exact-plan in-memory evidence consumed by
the read-only evidence collector; equivalent-but-distinct plans cannot reuse it.
The public surface exposes only diagnostics and state, and no CLI, Admin, browser,
HTTP, WebSocket, hosted-service, timer, AetherRemote, migration, service, health,
rollback, current-pointer, activation, radio, watchdog, command, lease, or TX caller
is added.

The sixteenth increment adds
`VerifiedReleaseActivationMigrationPlanComposer`, a callerless pure boundary that
requires the exact activation-plan object and exact immutable configuration-backup
artifact. It revalidates both public summaries, the exact retained object binding,
backup counts and manifest digest, non-overwriting publication evidence, and the
canonical setup-revision backup layout. Equivalent-but-distinct plans cannot reuse
one another's backup.

A signed no-migration declaration resolves as an exact no-op without paths or a
runner. A required declaration must preserve its increasing configuration-schema
transition, target schema, bounded migration identity, and signed gateway restart.
Only the three immutable backup children are mapped into distinct staging and final
migration trees beneath one separate migration root; source backup, migration
paths, and deployment state cannot overlap. A migration manifest, runner, and
non-overwriting atomic publication remain future requirements.

Composition reads and writes no filesystem state, selects no runner, creates no
staged copy, executes no migration, and provides no required-migration evidence.
Public reports redact paths, migration identity, and backup digest. Health
separately reports exact plan/backup binding, no-op and required planning, schema,
identity, staged-copy, manifest, and atomic planning while runner selection, reads,
writes, mutation, execution, evidence, operational callers, current-pointer
mutation, and activation authority remain absent. There is no CLI, Admin, browser,
HTTP, WebSocket, hosted-service, timer, AetherRemote, service-control, health-probe,
rollback, radio, watchdog, command, lease, or TX caller.

The seventeenth increment adds `ReleaseMigrationRunnerTrust`, one strict
feature-owned configuration object that defaults to disabled with no trusted
artifacts. `ReleaseMigrationRunnerTrustRegistry` accepts at most eight runner
artifacts, sixteen exact signed migrations per runner, and sixty-four mappings in
total. Every runner requires a unique bounded identity, protocol version `1`, an
absolute canonical path, one canonical lowercase SHA-256 pin, and at least one
unique signed migration identity with an increasing positive schema transition.

Startup rejects unknown configuration, missing or empty enabled trust, duplicate
runner identities, paths, digests, or migration identities, unsupported protocols,
unsafe schema transitions, and missing, empty, oversized, linked, mutable, changed,
or digest-mismatched files. Linux artifacts must be owner-readable and
owner-executable with no write bit; their immediate trust directory must be regular,
non-link, and not group/other writable. The registry reads each artifact only for
startup validation, retains defensive immutable metadata, and exposes only counts
and readiness booleans publicly.

`VerifiedReleaseActivationMigrationRunnerSelector` accepts only the exact successful
migration-plan report and retained internal plan. A signed no-op resolves without
trust. A required migration must match exactly one trusted signed identity and
from/to schema pair; selection retains an internal token bound to that exact plan
and startup-validated runner metadata. The selector reopens no artifact, reads no
backup content, invokes no runner, writes no staged copy, executes no migration,
and produces no required-migration readiness evidence. No CLI, Admin, browser,
HTTP, WebSocket, hosted-service, timer, AetherRemote, service-control, health-probe,
rollback, current-pointer, activation, radio, watchdog, command, lease, or TX caller
is added.

The eighteenth increment adds
`VerifiedReleaseActivationMigrationRunnerInvocationService`, a callerless probe-only
process boundary. It accepts only the exact successful runner-selection report and
retained internal selection. Signed no-migration plans remain a no-process no-op;
required plans must preserve the exact selected runner, mapping, protocol, migration
identity, and schema transition.

Immediately before process start the service revalidates the runner's canonical
path, containing directory, link status, regular-file shape, immutable Linux mode,
length, timestamp, and pinned SHA-256. The reviewed artifact is launched directly
without a shell or arguments, with stdin/stdout/stderr redirected, the environment
cleared, and only fixed locale and protocol variables restored. One JSON request is
bounded to 4 KiB, stdout to 16 KiB, stderr to 8 KiB, and the process to five seconds;
oversized output or timeout terminates the complete process tree.

The probe request contains setup/release, runner, migration, and schema identities
only and explicitly states that migration execution was not requested and no source
paths were provided. The strict response rejects unknown fields and must echo the
exact protocol, nonce, runner, migration, and schemas while reporting no migration
execution, filesystem mutation, or source-path receipt. Nonzero exit, stderr,
malformed or mismatched output, rejection, timeout, and artifact drift fail closed.
Probe success creates no migration readiness evidence and no production caller,
file mutation, current-pointer change, activation authority, service control,
rollback, radio, watchdog, command, lease, or TX surface.

The nineteenth increment adds
`VerifiedReleaseActivationMigrationExecutionService`, a callerless Linux-only
single-use staged-copy executor. It accepts only the successful probe report and
retained exact selection token. Signed no-migration plans become ready without
process or filesystem work. Required plans double-read release status, revalidate
the exact immutable backup manifest and every bounded regular non-link entry, then
copy configuration, state, and secrets into a fresh private staging identity.

The selected runner is rehashed immediately before direct execution and receives
only staging paths. It receives no live configuration, immutable backup-source,
deployment, release-pointer, or credential content. The strict bounded protocol
requires staged-copy mutation while explicitly denying backup-source receipt,
`current` mutation, activation, service control, radio commands, and TX commands.
Timeout, output overflow, stderr, rejection, malformed output, tree drift, links,
unsafe permissions, or digest mismatch fail closed and clean staging while the
outcome is known.

Success independently inventories and hashes the migrated tree, writes and durably
flushes a host-owned manifest, freezes files and directories, atomically publishes
the exact identity, and validates it again. Existing identities are never
overwritten; an ambiguous publication is frozen and marked for reconciliation.
The retained evidence is reference-bound to the exact activation plan, and the
activation evidence collector now reads required-migration readiness only from that
exact observation. Production resolves diagnostics and zeroed state only. No route,
CLI, Admin/browser, hosted service, timer, AetherRemote, `current` mutation,
activation authority, service control, health probe, rollback, radio, watchdog,
command, lease, or TX caller is added.

The twentieth increment adds
`VerifiedReleaseActivationServiceControlPlanComposer`, a callerless pure boundary
that accepts only the successful activation-plan report and its retained exact plan.
It revalidates the report, exact object binding, service count, host-restart shape,
and required-migration gateway restart before mapping the signed declaration.

The only service identities in the internal plan are the repository-owned gateway,
broker, AetherRemote agent, and station-engine units. Non-host restarts produce a
deterministic pre-switch stop sequence and reverse post-switch start sequence. A
signed host restart requires all four service declarations and supersedes those
sequences with one post-switch host-restart marker. A release that signs no restart
resolves as an exact no-op. Public reports expose counts and booleans only and redact
unit identities and the host marker.

The planner performs no process launch, shell, `systemctl`, D-Bus, systemd command,
host restart, service-control evidence, `current` mutation, health probe, rollback,
activation, radio, watchdog, command, lease, or TX action. Production resolves only
diagnostics and adds no CLI, Admin, browser, HTTP, WebSocket, hosted-service, timer,
AetherRemote, or operational caller.

The twenty-first increment adds
`VerifiedReleaseActivationHealthVerificationPlanComposer`, a callerless pure
boundary that accepts only the successful service-control report and retained exact
plan. It revalidates the exact activation-plan binding, complete four-package role
coverage, signed restart declaration, deterministic service actions, and all still-
mandatory activation obligations before retaining a separate health-plan token.

Every health plan covers the station engine, broker, AetherRemote agent, and gateway
in fixed dependency order and requires the corresponding repository-owned unit to
be active. Station engine, broker, and gateway receive loopback-only `GET /healthz`
contracts expecting HTTP 200 under bounded 45/30/45-second deadlines. Gateway health
also requires the runtime canonical host binding. The agent receives one fresh
broker-link observation contract under a bounded 60-second deadline. Host-restart
plans identify the same complete set as post-boot verification; ordinary and no-
restart plans identify post-switch verification.

Public reports expose counts and booleans only and redact unit names, ports, paths,
endpoint authorities, and contract internals. The planner performs no network
request, socket use, `HttpClient` call, process launch, `systemctl` command, journal
read, health evidence, `current` mutation, rollback, activation, radio, watchdog,
command, lease, or TX action. Production resolves diagnostics only and adds no CLI,
Admin, browser, HTTP, WebSocket, hosted-service, timer, AetherRemote, service-control,
or operational caller.

The twenty-second increment adds
`VerifiedReleaseActivationHealthVerificationService`, one strict disabled-by-default
and callerless execution boundary. The internal one-shot method accepts only the
exact successful health-plan token, requires the target release active before and
after execution, double-reads completed setup and release status, and binds the
persisted topology plus canonical gateway authority. Independently composed
equivalent plans cannot share evidence.

Personal and local-station gateways verify their locally owned gateway, broker, and
station engine; the topology-declared absent agent is a no-op. Hybrid gateways verify
those same local services and require one bounded canonical exact remote station
identity for the agent's fresh broker-link contract. Personal and local-only
configurations reject a station identity. Remote-station gateways fail closed because
no reviewed remote station-engine health transport is registered; remote-station
nodes cannot run this gateway boundary.

Broker and station engine use direct no-shell
`/usr/bin/systemctl is-active --quiet`; the gateway user service uses
`/usr/bin/systemctl --user is-active --quiet`. The environment is cleared, output and
runtime are bounded, and only fixed locale plus a canonical matching user D-Bus
binding may be restored. The three local services receive fixed loopback HTTP/1.1
`GET /healthz` requests with no proxy, redirects, cookies, or decompression and with
bounded JSON. The hybrid agent contract reads the existing runtime broker snapshot
and requires one fresh matching station ID plus positive heartbeat and inventory
sequences. No journal, runtime credential, administration credential, or enrollment
secret is read.

Success retains one exact-plan in-memory health observation consumed only by the
read-only evidence collector. Readiness is phase-aware: absent health evidence keeps
the previous installed release active, while exact post-switch health evidence
requires the target active. Production registers diagnostics and zeroed state only,
with execution disabled and no CLI, Admin, browser, HTTP, WebSocket, hosted-service,
timer, AetherRemote command, service-control, rollback, pointer, activation, radio,
watchdog, command, lease, or TX caller.

The twenty-third increment adds
`VerifiedReleaseActivationServiceControlExecutionService`, one strict disabled-by-
default and callerless two-phase execution boundary. The pre-switch phase accepts
only the exact service-control token, requires the signed installed release active,
double-reads completed setup and release status, and performs the deterministic stop
list. The post-switch phase requires the same retained pre-switch token plus the
signed target release active and performs the deterministic start list. This boundary
never changes `current` between phases.

Personal and local-station gateways control only locally owned gateway, broker, and
station-engine units; the topology-declared absent agent action is an explicit no-op.
Hybrid or remote-gateway plans requiring a remote-node action fail before any process
because no reviewed remote service-control transport exists. Host-restart plans also
fail closed and perform no host action.

The runtime invokes absolute `/usr/bin/systemctl` directly with exact fixed units,
exact stop/start verbs, and explicit user scope for the gateway. There is no shell,
the environment is cleared, output and execution time are bounded, and timeout kills
the process tree. No automatic action retry exists. A partial or unknown outcome,
cancellation after launch, or status/setup drift enters reconciliation-required state
and blocks all later phases.

Successful post-switch completion retains exact in-memory service-control evidence.
The read-only collector observes but never invokes it, and health verification now
requires the same exact service-control plan to be complete before any probe. Public
reports remain unit-, topology-, path-, and action-redacted. Production registers
disabled diagnostics and zeroed state only and adds no CLI, Admin, browser, HTTP,
WebSocket, hosted-service, timer, AetherRemote command, health-probe orchestration,
rollback, pointer, activation, radio, watchdog, command, lease, keying, or TX caller.

The twenty-fourth increment adds
`VerifiedReleaseActivationCurrentPointerSwitchService`, one strict disabled-by-
default and callerless pointer boundary. It accepts only the exact service-control
plan and its retained pre-switch token, requires the installed release active, and
double-reads completed setup, release inventory, and the exact installed `current`
link before mutation. Host-restart plans remain ineligible.

The complete target tree is traversed with bounded counts and must contain exactly the
signed manifest plus four planned packages. All entries must be link-free,
non-writable regular files or real directories; unexpected, missing, mutable, empty,
unsafe, linked, or manifest/package length or SHA-256-drifted content fails closed. One unpredictable
same-directory temporary symlink carries only the exact planned relative target, and
native Linux `rename(2)` atomically replaces `current`.

The consumed temporary entry, exact target link, immutable tree, target-active status,
and unchanged setup are revalidated after the rename. Unknown outcomes, cancellation
after the atomic attempt, post-switch drift, or failed temporary cleanup retain
reconciliation-required state and block retry. Success retains exact in-memory switch
evidence bound by reference to the activation, service-control, and stop-phase tokens.

Post-switch service starts now require the exact successful switch report, and health
execution requires the exact retained switch observation before any process, HTTP
request, or broker snapshot. Production registers disabled diagnostics and zeroed
state only. No operational/CLI/Admin/browser/HTTP/WebSocket/hosted-service/timer/
AetherRemote caller, service start, host restart, remote service control, health
probe, rollback, activation authority, radio/watchdog/command/lease/keying/TX action,
or live RF operation is added.

The twenty-fifth increment adds
`VerifiedReleaseActivationRollbackPlanComposer`, one pure callerless rollback-plan
boundary. It accepts only the exact activation, immutable original-backup, migration,
service-control, and health-plan tokens. All five objects must remain connected by
reference through one activation transaction; equivalent summaries or independently
composed objects fail closed.

Rollback is planned as restoration from the original immutable backup. Required
migration never introduces a reverse-runner path. The three exact configuration,
state, and secret backup roots map back to their original live roots, each with a
same-parent restore-staging identity and a separate displaced-live-tree identity. The
future sequence is deterministic target-service stops, all three restores, atomic
`current` return to the installed release, deterministic installed-service starts,
and complete installed-release health verification.

Host-restart transactions are rejected because no reviewed host-restart rollback
transport exists. The planner performs no source read, write, directory mutation,
process, `systemctl` command, network request, health probe, pointer mutation,
rollback execution, evidence production, activation, radio/watchdog/command/lease/
keying/TX action, or live RF operation. Production resolves diagnostics only and has
no operational/CLI/Admin/browser/HTTP/WebSocket/hosted-service/timer/AetherRemote
caller.

The twenty-sixth increment adds
`VerifiedReleaseActivationRollbackExecutionService`, one separate strict rollback
boundary that remains disabled by default and has no production caller. Its internal
entry points accept only the exact rollback plan, the retained successful forward
pointer-switch evidence, and either an eligible exact failed post-switch service-start
report or failed health-verification report. Successful, pre-switch, free-standing,
equivalent, or independently composed transactions cannot trigger rollback.

The immutable activation-backup manifest advances to schema 2 and retains original
Unix modes. Revalidation checks the exact manifest digest, activation identities,
source counts and bytes, unique safe paths, immutable copied-tree modes, every file
length and SHA-256 digest, and safe original configuration/state/secret modes. All
three restore trees are copied, flushed, mode-restored, and rehashed before the first
service action.

Personal, local-station, and hybrid gateway topologies reuse the reviewed local unit
ownership rules. Exact target units stop in deterministic order; each live root is
atomically displaced and replaced by its same-parent staged original tree; an exact
temporary link and native atomic rename return `current` to the installed release;
installed units start in deterministic order; and the complete unit, loopback HTTP,
canonical-host, plus optional exact-station broker-link health contracts verify the
installed release. Reverse migration is never invoked, and displaced failed trees are
removed only after full health success.

Any process, directory, pointer, status, setup, topology, health, cleanup,
cancellation, or unknown mutation outcome retains exact-plan reconciliation state and
blocks retry. Success retains path-, unit-, content-, and station-redacted rollback
evidence separately from forward activation `RollbackReady`. Production exposes
disabled diagnostics and zeroed state only and adds no operational/CLI/Admin/browser/
HTTP/WebSocket/hosted-service/timer/AetherRemote caller, activation authority, host
restart, remote service-control transport, radio/watchdog/command/lease/keying/TX
action, or live RF operation.

The twenty-seventh increment adds
`VerifiedReleaseActivationOperatorApprovalAuthority`, one disabled-by-default,
callerless exact-plan approval boundary. Its strict configuration permits a bounded
30-through-600-second approval lifetime and defaults to 300 seconds. An internal
approval attempt requires the exact retained activation plan, current authentication,
administrator authorization, and fresh reauthentication. Equivalent plans, stale or
malformed authentication evidence, non-administrators, duplicate active approvals,
and malformed approval identities fail closed.

Only one reference-bound approval may remain active. It carries internal subject and
random approval identities, expires automatically, and can be revoked once. Public
reports and health diagnostics disclose only bounded release identities, booleans,
counts, outcomes, and timestamps. The activation-evidence collector now observes the
exact fresh approval, while default production remains unapproved because no issuer or
operational caller exists.

Approval is readiness evidence and never activation authority. The boundary performs
no file write, pointer mutation, backup, migration, service control, health probe,
rollback, activation, radio/watchdog command, lease mutation, keying, TX action, or
live RF operation. Production registers no CLI/Admin/browser/HTTP/WebSocket/hosted-
service/timer/AetherRemote/command/lease/TX caller. Authenticated Admin issuance and
transaction orchestration remain later separately reviewed M8B work.

Automated checkpoint on 2026-08-03 for the first increment: Release solution build
completed with zero warnings and zero errors; the focused signed-manifest verifier
suite passed 42/42; web tests passed 928/928; independent-watchdog tests passed
57/57; TX-HIL isolation tests passed 48/48; AetherRemote tests passed 70/70; and
browser tests passed 135/135. The complete checkpoint covered 1,103 .NET tests and
1,238 tests overall. No live radio or RF operation was performed or required.

Automated checkpoint on 2026-08-03 for the production trust composition: the
deployment script passed shell syntax validation; Release solution build completed
with zero warnings and zero errors; the focused release-trust suite passed 35/35;
web tests passed 963/963; independent-watchdog tests passed 57/57; TX-HIL isolation
tests passed 48/48; AetherRemote tests passed 70/70; and browser tests passed
135/135. The complete checkpoint covered 1,138 .NET tests and 1,273 tests overall.
No live radio or RF operation was performed or required.

Automated checkpoint on 2026-08-03 for the immutable local offline bundle reader:
the deployment script passed shell syntax validation; Release solution build
completed with zero warnings and zero errors; the focused offline-bundle suite
passed 31/31; web tests passed 994/994; independent-watchdog tests passed 57/57;
TX-HIL isolation tests passed 48/48; AetherRemote tests passed 70/70; and browser
tests passed 135/135. The complete checkpoint covered 1,169 .NET tests and 1,304
tests overall. No live radio or RF operation was performed or required.

Automated checkpoint on 2026-08-03 for the read-only offline bundle CLI check: the
deployment script passed shell syntax validation; Release solution build completed
with zero warnings and zero errors; the focused CLI suite passed 38/38; web tests
passed 1,032/1,032; independent-watchdog tests passed 57/57; TX-HIL isolation tests
passed 48/48; AetherRemote tests passed 70/70; and browser tests passed 135/135.
The complete checkpoint covered 1,207 .NET tests and 1,342 tests overall. A direct
built-DLL invocation with default-disabled trust returned one redacted JSON report,
exit code `2`, and did not reflect or access the supplied missing bundle path. No
live radio or RF operation was performed or required.

Automated checkpoint on 2026-08-03 for the read-only release status CLI: the
deployment script passed shell syntax validation; Release solution build completed
with zero warnings and zero errors; the focused status suite passed 25/25; web
tests passed 1,057/1,057; independent-watchdog tests passed 57/57; TX-HIL isolation
tests passed 48/48; AetherRemote tests passed 70/70; and browser tests passed
135/135. The complete checkpoint covered 1,232 .NET tests and 1,367 tests overall.
A direct built-DLL invocation with missing setup state returned one path-redacted
JSON report and exit code `2`. No live radio or RF operation was performed or
required.

Automated checkpoint on 2026-08-03 for the read-only offline install preflight:
the deployment script passed shell syntax validation; Release solution build
completed with zero warnings and zero errors; the focused preflight suite passed
38/38; web tests passed 1,095/1,095; independent-watchdog tests passed 57/57;
TX-HIL isolation tests passed 48/48; AetherRemote tests passed 70/70; and browser
tests passed 135/135. The complete checkpoint covered 1,270 .NET tests and 1,405
tests overall. A direct built-DLL invocation with missing setup state returned one
path-redacted JSON report, exit code `2`, and did not reflect or access the supplied
missing bundle path. No live radio or RF operation was performed or required.

Automated checkpoint on 2026-08-03 for verified installation-plan composition:
the deployment script passed shell syntax validation; Release solution build
completed with zero warnings and zero errors; the focused composition suite passed
26/26; web tests passed 1,121/1,121; independent-watchdog tests passed 57/57;
TX-HIL isolation tests passed 48/48; AetherRemote tests passed 70/70; and browser
tests passed 135/135. The complete checkpoint covered 1,296 .NET tests and 1,431
tests overall. No live radio or RF operation was performed or required.

Automated checkpoint on 2026-08-03 for private verified release staging: the
deployment script passed shell syntax validation; Release solution build completed
with zero warnings and zero errors; the focused staging suite passed 27/27; web
tests passed 1,149/1,149; independent-watchdog tests passed 57/57; TX-HIL isolation
tests passed 48/48; AetherRemote tests passed 70/70; and browser tests passed
135/135. The complete checkpoint covered 1,324 .NET tests and 1,459 tests overall.
A live development health probe confirmed staging read/write/freeze/cleanup
registration while network, extraction, publication, installation, activation,
rollback, migration, service, CLI/Admin/browser, radio, watchdog, command, lease,
and TX callers remained absent. No live radio or RF operation was performed.

Automated checkpoint on 2026-08-03 for atomic inactive-release publication: the
deployment script passed shell syntax validation; Release solution build completed
with zero warnings and zero errors; the focused publication suite passed 38/38;
web tests passed 1,187/1,187; independent-watchdog tests passed 57/57; TX-HIL
isolation tests passed 48/48; AetherRemote tests passed 70/70; and browser tests
passed 135/135. The complete checkpoint covered 1,362 .NET tests and 1,497 tests
overall. A live development health probe confirmed frozen-tree validation, the
root permission transition, atomic directory publication, and published-tree
validation while file copy, `current` mutation, activation, rollback, migration,
service, CLI/Admin/browser, radio, watchdog, command, lease, and TX callers
remained absent. No live radio or RF operation was performed or required.

Automated checkpoint on 2026-08-03 for verified activation-transaction plan
composition: the deployment script passed shell syntax validation; Release
solution build completed with zero warnings and zero errors; the focused activation
plan suite passed 47/47; web tests passed 1,234/1,234; independent-watchdog tests
passed 57/57; TX-HIL isolation tests passed 48/48; AetherRemote tests passed 70/70;
and browser tests passed 135/135. The complete checkpoint covered 1,409 .NET tests
and 1,544 tests overall. A live development health probe confirmed publication
input, path, TX-quiescence, backup, migration, restart, health-verification, and
rollback planning while file write, `current` mutation, activation, backup,
migration execution, service control, health probes, CLI/Admin/browser, radio,
watchdog, command, lease, and TX callers remained absent. No live radio or RF
operation was performed or required.

Automated checkpoint on 2026-08-03 for verified activation-readiness evaluation:
the deployment script passed shell syntax validation; Release solution build
completed with zero warnings and zero errors; the focused readiness suite passed
47/47; web tests passed 1,281/1,281; independent-watchdog tests passed 57/57;
TX-HIL isolation tests passed 48/48; AetherRemote tests passed 70/70; and browser
tests passed 135/135. The complete checkpoint covered 1,456 .NET tests and 1,591
tests overall. A live development health probe confirmed plan/status, lease-
admission, session-safety, radio-idle, watchdog, backup, migration, service,
health, rollback, and operator-approval evaluation while file write, `current`
mutation, activation, lease/radio/watchdog mutation, backup/migration/service/
health/rollback execution, CLI/Admin/browser, hosted-service, timer, AetherRemote,
command, lease, and TX callers remained absent. No live radio or RF operation was
performed or required.

Automated checkpoint on 2026-08-03 for authoritative activation-evidence
collection: the deployment script passed shell syntax validation; Release solution
build completed with zero warnings and zero errors; the focused evidence-collection
suite passed 38/38; web tests passed 1,319/1,319; independent-watchdog tests passed
57/57; TX-HIL isolation tests passed 48/48; AetherRemote tests passed 70/70; and
browser tests passed 135/135. The complete checkpoint covered 1,494 .NET tests and
1,629 tests overall. A live development health probe confirmed double-read release
status, observation-only lease snapshots, session/radio occupancy projection,
watchdog aggregation, bounded collection, and fail-closed missing prerequisites
while file write, `current` mutation, activation, lease/radio/watchdog mutation,
backup/migration/service/health/rollback execution, CLI/Admin/browser, hosted-
service, timer, AetherRemote, command, lease, and TX callers remained absent. No
live radio or RF operation was performed or required.

Automated checkpoint on 2026-08-03 for exact-plan TX-lease admission closure and
drain composition: Release solution build completed with zero warnings and zero
errors; the focused lease-quiescence, lease-manager, and activation-evidence suite
passed 57/57; web tests passed 1,327/1,327; independent-watchdog tests passed
57/57; TX-HIL isolation tests passed 48/48; AetherRemote tests passed 70/70; and
browser tests passed 135/135. The complete checkpoint covered 1,502 .NET tests and
1,637 tests overall. A live development health probe confirmed exact-plan
composition, admission authority, acquisition and renewal suppression,
observation-only drain evaluation, and fail-closed inactive state while
force-release, lease-mutation authority, radio-idle inference, activation
authority, CLI/Admin/browser/HTTP/WebSocket, hosted-service, timer, AetherRemote,
command, radio, watchdog, and TX callers remained absent. Transmit remained
disabled. No live radio or RF operation was performed or required.

Automated checkpoint on 2026-08-03 for exact-plan configuration-backup planning:
the deployment script passed shell syntax validation; Release solution build
completed with zero warnings and zero errors; the focused activation-plan and
configuration-backup planning suite passed 58/58; web tests passed 1,338/1,338;
independent-watchdog tests passed 57/57; TX-HIL isolation tests passed 48/48;
AetherRemote tests passed 70/70; and browser tests passed 135/135. The complete
checkpoint covered 1,513 .NET tests and 1,648 tests overall. A live development
health probe with simulation-only radio settings and all writable paths redirected
to a temporary directory confirmed exact-plan binding, dedicated configuration,
state, and secret source planning, release-root agreement, backup-root separation,
manifest planning, and future atomic-publication requirements while source reads,
file or directory mutation, overwrite, backup execution, readiness evidence,
current-pointer mutation, operational callers, activation authority, and transmit
remained absent. No live radio or RF operation was performed or required.

Automated checkpoint on 2026-08-04 for atomic exact-plan configuration backup:
the deployment script passed shell syntax validation; Release solution build
completed with zero warnings and zero errors; the focused configuration-backup,
activation-evidence, lease-quiescence, and readiness suite passed 116/116; web
tests passed 1,350/1,350; independent-watchdog tests passed 57/57; TX-HIL isolation
tests passed 48/48; AetherRemote tests passed 70/70; and browser tests passed
135/135. The complete checkpoint covered 1,525 .NET tests and 1,660 tests overall.
A live development health probe with simulation-only radio settings and all
writable paths redirected to a temporary directory confirmed exact-plan input,
release-status double reads, bounded no-link traversal, source digest validation,
private staging, durable manifest writes, immutable freeze, atomic publication,
published-tree validation, cleanup, and exact-plan evidence registration while
backup readiness and reconciliation remained false before execution. Existing-
backup overwrite, current-pointer mutation, migration, service control, health-
probe and rollback callers, CLI/Admin/browser/HTTP/WebSocket, hosted-service,
timer, AetherRemote, radio, watchdog, command, lease, TX, activation authority,
and transmit remained absent. No live radio or RF operation was performed or
required.

Automated checkpoint on 2026-08-04 for exact-plan staged-copy migration planning:
the deployment script passed shell syntax validation; Release solution build
completed with zero warnings and zero errors; the focused activation-plan,
configuration-backup, and migration-planning suite passed 79/79; web tests passed
1,359/1,359; independent-watchdog tests passed 57/57; TX-HIL isolation tests passed
48/48; AetherRemote tests passed 70/70; and browser tests passed 135/135. The
complete checkpoint covered 1,534 .NET tests and 1,669 tests overall. A live
development health probe with simulation-only radio settings and all writable paths
redirected to a temporary directory confirmed exact activation-plan and immutable-
backup binding, no-op and required migration planning, schema and identity
validation, staged-copy path planning, manifest planning, and future atomic
publication requirements while runner selection, source reads, file or directory
mutation, migration execution, readiness evidence, current-pointer mutation,
operational callers, activation authority, and transmit remained absent. No live
radio or RF operation was performed or required.

Automated checkpoint on 2026-08-04 for locally pinned migration-runner trust and
exact selection: the guarded production-TX deployment gate completed successfully;
Release solution build completed with zero warnings and zero errors; the focused
configuration-backup, migration-plan, runner-trust, and exact-selection suite passed
47/47; web tests passed 1,374/1,374; independent-watchdog tests passed 57/57;
TX-HIL isolation tests passed 48/48; AetherRemote tests passed 70/70; and automated
browser tests passed 135/135. The complete checkpoint covered 1,549 .NET tests and
1,684 tests overall. Deployed public and internal health confirmed strict trust
configuration, bounded artifact/mapping validation, canonical path and link checks,
permission and size checks, digest pinning, exact identity/schema/protocol/digest
selection binding, disabled selection with zero trusted artifacts, and a registered
selector while runner invocation, migration execution and evidence, current-pointer
mutation, activation authority, operational callers, and migration-runner TX surface
remained absent. The independent watchdog started empty and Disarmed; production TX
remained unavailable because command transport and safety-supervisor prerequisites
were absent. The separate interactive Browser Bridge acceptance could not run because
the extension was disconnected and therefore remains pending. No live radio or RF
operation was performed or required.

Automated checkpoint on 2026-08-04 for exact probe-only migration-runner invocation:
the guarded production-TX deployment gate completed successfully; Release solution
build completed with zero warnings and zero errors; the focused configuration-backup,
migration-plan, runner-trust, exact-selection, and probe-invocation suite passed
62/62; web tests passed 1,389/1,389; independent-watchdog tests passed 57/57;
TX-HIL isolation tests passed 48/48; AetherRemote tests passed 70/70; and automated
browser tests passed 135/135. The complete checkpoint covered 1,564 .NET tests and
1,699 tests overall. Focused process-boundary tests additionally proved exact artifact
rehashing, direct no-shell/no-argument launch, cleared environment, bounded JSON
stdin/stdout/stderr, strict unknown-field and identity/schema/nonce validation,
nonzero/stderr/rejection/malformed/mismatch failure, hard timeout, output-bound
process-tree termination, no source paths, and no filesystem mutation or migration
execution. Deployed public and internal health confirmed probe registration while
shell invocation, source-path input, source reads, writes, directory mutation,
migration execution and evidence, current-pointer mutation, activation authority,
operational callers, and TX surface remained absent. Runner trust remained disabled
with zero trusted artifacts; the independent watchdog started empty and Disarmed;
production TX remained unavailable because command-transport and safety-supervisor
prerequisites were absent. Browser Bridge was connected, but the FlexWeb acceptance
tab redirected to Microsoft sign-in because no authenticated FlexWeb browser session
was available; interactive acceptance therefore remains pending and no credentials
were entered. No live radio or RF operation was performed or required.

Automated checkpoint on 2026-08-04 for exact staged-copy migration execution and
evidence: the guarded production-TX deployment gate completed successfully; the
deployment script passed shell syntax validation; Release solution build completed
with zero warnings and zero errors; the focused backup, migration planning, runner
trust/selection/invocation, execution, and evidence suite passed 110/110; web tests
passed 1,399/1,399; independent-watchdog tests passed 57/57; TX-HIL isolation tests
passed 48/48; AetherRemote tests passed 70/70; and browser tests passed 135/135.
The complete checkpoint covered 1,574 .NET tests and 1,709 tests overall.

The Linux process-boundary suite created a real immutable exact-plan backup, copied
only backup content into a new private staging identity, revalidated the pinned
runner after probe, and passed only staging paths to one direct no-shell execution.
It proved bounded JSON, cleared environment, strict source-path/current/activation/
service/radio/TX denials, immutable host-owned manifest publication, exact evidence,
no-op readiness, runner-drift rejection, malformed/rejected/nonzero/timeout cleanup,
existing-publication refusal, and reconciliation after an ambiguous atomic rename.
The original immutable backup remained unchanged.

Deployed health confirmed the executor and migration evidence boundary registered
with zero active plan, directories, files, bytes, manifest, readiness, or
reconciliation. Operational, CLI, Admin, browser, HTTP, WebSocket, hosted-service,
timer, AetherRemote, service-control, health-probe, rollback, radio, watchdog,
command, lease, TX, `current` mutation, and activation-authority callers remained
false. Runner trust remained disabled with zero artifacts; the independent watchdog
started empty and Disarmed; production TX remained unavailable because command-
transport and safety-supervisor prerequisites were absent.

Interactive Browser Bridge acceptance used the updated fixed-2D playbook. The
authenticated radio desk reported four radios online and ready; PSOC2/HF/XVTR
connected; the footer reported `AETHER-WEB`, `FLEX-6700`, `RX-ONLY`, and
`RADIO: LIVE`; MOX and CWX were hidden and disabled; TUNE, SPLIT, DVK, FDX, and the
validation-only intent action were disabled; the TX panel stated that it had no
radio command or microphone-audio transport; PC MIC remained off; the local FILL
display control toggled and was restored; and console errors and warnings were both
zero. No lease was acquired and no live radio command or RF operation was performed.

Automated checkpoint on 2026-08-04 for exact service-control transaction planning:
the deployment script passed shell syntax validation; Release solution build
completed with zero warnings and zero errors; the focused activation-plan,
service-control-plan, readiness, and evidence suite passed 145/145; web tests passed
1,412/1,412; independent-watchdog tests passed 57/57; TX-HIL isolation tests passed
48/48; AetherRemote tests passed 70/70; and browser tests passed 135/135. The
complete checkpoint covered 1,587 .NET tests and 1,722 tests overall.

Focused tests proved exact activation-plan binding, fixed repository-owned service
mapping, deterministic gateway/broker/agent/engine stop ordering and reverse start
ordering, signed host-restart supersession, no-op readiness, required-migration
gateway restart, contradictory-declaration rejection, distinct exact tokens, and
public unit/action-identity redaction. Production has no composition caller and
resolves only the planner diagnostics.

The first guarded production-TX deployment attempt rolled back safely because the
previous Browser Bridge acceptance radio tab was still open and therefore retained
a supervised watchdog process, which correctly violated the empty-session profile.
After that test tab was closed and its session released, the unchanged gate passed.
Deployed health confirmed service-control planning registered while process launch,
`systemctl`, host-restart execution, service-control evidence, operational/CLI/Admin/
browser/HTTP/WebSocket/hosted-service/timer/AetherRemote callers, health probes,
rollback, radio/watchdog/command/lease/TX callers, `current` mutation, and activation
authority remained false. Runner trust remained disabled with zero artifacts; the
independent watchdog started empty and Disarmed; production TX remained unavailable.

Interactive Browser Bridge acceptance then passed against the deployed release. The
authenticated fixed-2D radio desk connected PSOC2/HF/XVTR and reported the explicit
RX-only/live footer. The validation-only panel stated that it had no radio command
or microphone-audio transport; MOX, TUNE, and CWX were hidden and disabled; intent
selection and validation, SPLIT, DVK, and FDX were disabled; PC MIC remained off;
the harmless FILL display state toggled and was restored; and console errors and
warnings were both zero. The tab was closed afterward to release the session. No TX
lease, keying action, transmit-control command, or RF operation was performed.

Automated checkpoint on 2026-08-04 for exact post-switch health-verification
planning: independent post-merge `main` CI run `30915908898` passed; the deployment
script passed shell syntax validation; Release solution build completed with zero
warnings and zero errors; the focused activation-plan, service-control-plan, health-
verification-plan, readiness, and evidence suite passed 158/158; web tests passed
1,425/1,425; independent-watchdog tests passed 57/57; TX-HIL isolation tests passed
48/48; AetherRemote tests passed 70/70; and browser tests passed 135/135. The
complete checkpoint covered 1,600 .NET tests and 1,735 tests overall.

Focused tests proved exact service-control and activation-plan object binding,
complete four-service coverage independent of restart subset, deterministic station-
engine/broker/agent/gateway health ordering, fixed loopback `/healthz` contracts,
HTTP 200 expectations, canonical gateway host binding, fresh agent broker-link
planning, bounded 30/45/60-second deadlines, post-host-restart phase selection,
internal-action tamper rejection, distinct exact tokens, and public unit/port/path
redaction. Production has no composition or probe caller and resolves only planner
diagnostics.

The guarded production-TX deployment gate passed against release
`/home/flexweb/aethersdr/releases/20260804-140609-flexweb-validation`, retaining
`/home/flexweb/aethersdr/releases/20260804-132134-flexweb-validation` for rollback.
Deployed health confirmed exact health planning registered while network requests,
sockets, `HttpClient`, process launch, `systemctl`, journal reads, health evidence,
operational/CLI/Admin/browser/HTTP/WebSocket/hosted-service/timer/AetherRemote/
service-control callers, rollback, radio/watchdog/command/lease/TX callers, `current`
mutation, and activation authority remained false. Runner trust remained disabled
with zero artifacts; the independent watchdog started empty and Disarmed;
production TX remained unavailable.

Interactive Browser Bridge acceptance passed against the deployed release. The
authenticated fixed-2D radio desk connected PSOC2/HF/XVTR and reported the explicit
RX-only/live footer. The validation-only panel stated that it had no radio command
or microphone-audio transport; MOX, TUNE, and CWX were hidden and disabled; intent
selection and validation, SPLIT, DVK, and FDX were disabled; PC MIC remained off;
the harmless FILL display state toggled and was restored; and console errors and
warnings were both zero. The tab was closed afterward to release the session. No TX
lease, keying action, transmit-control command, or RF operation was performed.

Automated checkpoint on 2026-08-04 for exact post-switch health execution and
evidence: independent post-merge `main` CI run 30923596292 passed; the deployment
script passed shell syntax validation; Release solution build completed with zero
warnings and zero errors; the focused activation-plan, service-control, health-plan,
health-execution, readiness, and evidence suite passed 179/179; web tests passed
1,446/1,446; independent-watchdog tests passed 57/57; TX-HIL isolation tests passed
48/48; AetherRemote tests passed 70/70; and browser tests passed 135/135. The complete
checkpoint covered 1,621 .NET tests and 1,756 tests overall.

Focused execution tests proved strict unknown-configuration rejection, disabled
empty defaults, exact active-target, setup, topology, and canonical-host double
binding; personal/local agent no-op semantics; hybrid exact-station enforcement;
remote-engine topology rejection; direct no-shell system and user `systemctl`
argument vectors under a cleared environment; fixed loopback HTTP request shape;
bounded retries and deadlines; exact fresh station identity; unit/HTTP/link failure;
release/setup drift rejection; one-shot evidence; public redaction; and reference-
bound observation. Phase-aware readiness requires the previous release before exact
health evidence and the target release after exact health evidence.

The guarded production-TX deployment gate completed successfully with active release
`20260804-161356-flexweb-validation`. Deployed health confirmed topology binding,
the executor registered with execution disabled and unavailable, no configured
station identity, zero targets/checks/readiness, exact evidence support, and no shell,
journal, credential read, service control, pointer mutation, rollback, activation
authority, operational/CLI/Admin/browser/HTTP/WebSocket/hosted-service/timer/
AetherRemote-command/radio/watchdog/command/lease/TX caller. The independent watchdog
started empty and Disarmed, command transport remained unavailable, and no live RF
operation occurred.

Authenticated Browser Bridge acceptance passed against the deployed release. The
fixed-2D radio desk connected PSOC2/HF/XVTR and reported the explicit RX-only/live
footer. Current validation-only copy stated that the panel had no radio command or
microphone-audio transport; MOX, TUNE, and CWX were hidden and disabled; intent,
validation, SPLIT, DVK, and FDX were disabled; PC MIC remained off; FILL toggled and
was restored; and console errors and warnings were both zero. The tab was closed to
release the session. No lease, keying action, transmit-control command, or RF
operation was performed.

Automated checkpoint on 2026-08-04 for exact two-phase service-control execution and
evidence: independent post-merge `main` CI run 30930631062 passed; the deployment
script passed shell syntax validation; formatting and whitespace validation passed;
Release solution build completed with zero warnings and zero errors; the focused
activation-plan, service-control-plan, service-control-execution, health-plan,
health-execution, readiness, and evidence suite passed 154/154; web tests passed
1,468/1,468; independent-watchdog tests passed 57/57; TX-HIL isolation tests passed
48/48; AetherRemote tests passed 70/70; and browser tests passed 135/135. The complete
checkpoint covered 1,643 .NET tests and 1,778 tests overall.

Focused execution tests proved strict unknown-configuration rejection, disabled
empty defaults, exact retained-plan and phase ordering, installed-before/target-after
status double reads, setup/topology binding, local user/system unit ownership,
personal/local agent no-op semantics, remote-node and host-restart rejection before
process launch, exact direct no-shell `systemctl` stop/start argument vectors under a
cleared environment, bounded output, hard timeout with process-tree termination, no
automatic action retry, cancellation-after-launch reconciliation, partial and unknown
outcome reconciliation, one-shot exact evidence, public redaction, and reference-
bound observation. Health execution now requires the same completed service-control
plan token before any probe, and the read-only evidence collector observes but never
invokes service control.

The first guarded production-TX deployment attempt rolled back safely because an
unrelated actively streaming operator session kept the independent watchdog transport
available. Admin diagnostics confirmed that session was connected, Disarmed, had no
lease and zero unkey attempts; it was left untouched. After the operator session ended
naturally, the unchanged gate completed successfully with active release
`20260804-173906-flexweb-validation` and retained
`20260804-161356-flexweb-validation` for rollback. Deployed health confirmed the
service-control executor registered with execution disabled and unavailable, zero
phase/action/evidence state, exact topology and status binding, no operational/CLI/
Admin/browser/HTTP/WebSocket/hosted-service/timer/AetherRemote-command/health-probe
caller, no pointer mutation, host restart, rollback, activation authority, radio,
watchdog mutation, command, lease, keying, or TX caller. The independent watchdog
started empty and Disarmed; no live RF operation occurred.

Authenticated Browser Bridge acceptance then passed against the newly deployed release
using `ODU-6400 · FLEX-6400 · Remote`, leaving the unrelated PSOC2 operator path
untouched. The selected radio identity was exact, the footer reported `FLEX-6400`,
`RX-ONLY`, and `RADIO: LIVE`, and the TX surface remained locked and validation-only.
MOX, TUNE, and CWX were hidden and disabled; intent, validation, SPLIT, DVK, and FDX
were disabled; PC MIC remained off; FILL toggled and was restored; and console errors
and warnings were both zero. The ODU-6400 tab was closed afterward to release the
remote receive session. No TX lease, keying action, transmit-control command, or RF
operation was performed.

Automated checkpoint on 2026-08-04 for exact atomic current-pointer switching and
evidence: independent post-merge `main` CI run 30937773249 passed; the deployment
script passed shell syntax validation; changed C# files passed format verification;
Release solution build completed with zero warnings and zero errors; the focused
activation-plan, service-control-plan, service-control-execution, pointer-switch,
health-plan, health-execution, readiness, and evidence suite passed 222/222; web tests
passed 1,489/1,489; independent-watchdog tests passed 57/57; TX-HIL isolation tests
passed 48/48; AetherRemote tests passed 70/70; and browser tests passed 135/135. The
complete checkpoint covered 1,664 .NET tests and 1,799 tests overall.

Focused pointer tests proved strict unknown-configuration rejection, disabled empty
defaults, exact pre-switch reference binding, equivalent-plan rejection, complete
five-file immutable-tree validation, unexpected-entry and symbolic-link rejection,
mutable-target rejection, same-length manifest and package digest-drift rejection,
exact installed-link binding, setup drift cleanup, failed cleanup reconciliation,
ambiguous rename reconciliation with no retry, post-switch status drift,
cancellation-after-atomic-attempt reconciliation, one-shot evidence, public
redaction, and a real Linux native `rename(2)` replacement of an existing directory
symlink. Post-switch service starts require the exact successful switch
report, and health execution requires the exact retained switch observation before
any process, HTTP request, or broker read.

The guarded production-TX deployment gate completed successfully with active release
`20260804-185857-flexweb-validation`; release
`20260804-184310-flexweb-validation` was retained for rollback. Deployed health
confirmed the pointer boundary registered with execution disabled and unavailable,
all exact-plan/status/setup/tree/link/atomic/evidence capabilities registered, zero
switch/evidence/reconciliation state, and no operational/CLI/Admin/browser/HTTP/
WebSocket/hosted-service/timer/AetherRemote-command/service-start/host-restart/
remote-control/health-probe/rollback/activation/radio/watchdog/command/lease/TX
caller. The service-control and health boundaries advertised the exact pointer-token
dependency. The independent watchdog started empty and Disarmed; no live RF operation
occurred.

Authenticated Browser Bridge acceptance then passed against the exact final release
using `ODU-6400 · FLEX-6400 · Remote`. The selected identity was exact; the footer
reported `FLEX-6400`, `RX-ONLY`, and `RADIO: LIVE`; and the TX panel remained locked
and validation-only with explicit no-command/no-microphone-transport copy. MOX, TUNE,
and CWX were hidden and disabled; intent, validation, SPLIT, DVK, FDX, and both tuner
TUNE controls were disabled; PC MIC remained off; FILL toggled and was restored; and
console errors and warnings were both zero. The tab was closed afterward to release
the remote receive session. No TX lease, keying action, transmit-control command, or
RF operation was performed.

Automated checkpoint on 2026-08-04 for exact rollback transaction planning:
independent post-merge `main` CI run 30944485006 passed; the deployment script passed
shell syntax validation; changed C# files passed format verification; Release solution
build completed with zero warnings and zero errors; the focused rollback-plan suite
passed 13/13; and the combined activation, backup, migration, service-control,
pointer-switch, health, rollback, readiness, and evidence boundary passed 277/277.
Web tests passed 1,502/1,502; independent-watchdog tests passed 57/57; TX-HIL isolation
tests passed 48/48; AetherRemote tests passed 70/70; and browser tests passed 135/135.
The complete checkpoint covered 1,677 .NET tests and 1,812 tests overall.

Focused rollback tests proved diagnostics-only public surface, exact reference binding
across activation, immutable original backup, migration, service-control, and health
plans, equivalent-token rejection, no-migration and required-migration composition,
original-backup restoration without a reverse migration runner, exact three-source
live-root mapping, same-parent restore staging, distinct displaced-live identities,
host-restart rejection, backup metadata and unsafe-layout rejection, and path-, unit-,
and health-contract-redacted public reports. The planner produced no read, write,
process, systemd, network, probe, pointer, rollback, evidence, activation, radio,
watchdog, command, lease, keying, or TX action.

The guarded production-TX deployment gate completed successfully with active release
`20260804-200028-flexweb-validation`; release
`20260804-185857-flexweb-validation` was retained for rollback. Deployed health
confirmed the rollback planner registered with all exact-input, immutable-backup,
three-source restore, deterministic service, atomic-pointer, and installed-health
planning capabilities while reverse migration, host restart, source reads, writes,
directory mutation, process/systemd/network/probe activity, rollback evidence,
rollback execution, pointer mutation, activation authority, and every operational
caller remained absent. The independent watchdog started empty and Disarmed; no live
RF operation occurred.

Authenticated Browser Bridge acceptance passed against that exact deployed release
using `ODU-6400 · FLEX-6400 · Remote`. The footer reported `FLEX-6400`, `RX-ONLY`, and
`RADIO: LIVE`; the TX panel remained locked and validation-only with explicit
no-command/no-microphone-transport copy; MOX, TUNE, and CWX were hidden and disabled;
intent, validation, SPLIT, DVK, FDX, and both tuner TUNE controls were disabled; PC MIC
remained off; FILL toggled and was restored; and console errors and warnings were both
zero. The tab was closed afterward to release the remote receive session. No TX lease,
keying action, transmit-control command, or RF operation was performed.

Automated checkpoint on 2026-08-05 for exact rollback execution and evidence:
independent post-merge `main` CI run 30962859507 passed; the deployment script passed
shell syntax validation; changed C# files passed format verification; Release solution
build completed with zero warnings and zero errors; and the complete activation,
backup, migration, service-control, pointer-switch, health, rollback, readiness, and
evidence boundary passed 330/330. Web tests passed 1,517/1,517; independent-watchdog
tests passed 57/57; TX-HIL isolation tests passed 48/48; AetherRemote tests passed
70/70; and browser tests passed 135/135. The complete checkpoint covered 1,692 .NET
tests and 1,827 tests overall.

Focused rollback execution tests proved disabled and zero-state defaults, strict
station-identity configuration, exact successful forward-pointer evidence, exact
failed service/health-plan reference binding, successful- and equivalent-report
rejection before observation or mutation, immutable backup digest/mode drift rejection,
real three-root restore with original Unix modes, deterministic topology-owned stop and
start actions, exact installed pointer restoration, installed health verification,
redacted public state, duplicate-execution rejection, ambiguous directory and pointer
reconciliation, retained displaced trees after installed-health failure, preservation
of pre-existing restore-staging evidence, and reconciliation when post-staging cleanup
cannot complete. Backup manifest schema 2 and staged-copy migration share one strict
immutable source contract.

The guarded production-TX deployment gate completed successfully with active release
`20260805-011152-flexweb-validation`; release
`20260805-010048-flexweb-validation` was retained for rollback. Deployed health
confirmed the rollback executor registered with exact plan, forward-pointer evidence,
failed post-switch trigger, schema-2 immutable-backup revalidation, original-mode
restore, three-source staging, deterministic service, atomic directory/pointer,
installed-health, cleanup, evidence, and reconciliation capabilities while execution
remained disabled and unavailable, every state field remained zero or false, and
reverse migration, automatic retry, host restart, remote service control, activation
authority, and every operational/CLI/Admin/browser/HTTP/WebSocket/hosted-service/
timer/AetherRemote/radio/watchdog/command/lease/TX caller remained absent. The
independent watchdog started empty and Disarmed; no live RF operation occurred.

The active validation release `20260805-011152-flexweb-validation` predates the final
restore-staging cleanup correction, so no exact-build browser acceptance is claimed for
the post-fix tree. The normal ODU-6400 RX-only browser checklist is waived as a
pre-staging requirement because it is an indirect gateway regression check rather than
rollback-specific acceptance. It remains an optional post-PR, pre-merge regression
check and would require an explicit deployment/restart confirmation before use.

Automated checkpoint on 2026-08-05 for exact operator-approval authority and evidence:
the deployment script passed shell syntax and its complete validation-only production
gate; changed C# files passed format verification; Release solution build completed
with zero warnings and zero errors; the focused authority suite passed 18/18; the
combined authority and evidence suite passed 62/62; and the complete activation,
backup, migration, service-control, pointer-switch, health, rollback, approval,
readiness, and evidence boundary passed 351/351. Web tests passed 1,538/1,538;
independent-watchdog tests passed 57/57; TX-HIL isolation tests passed 48/48;
AetherRemote tests passed 70/70; and browser tests passed 135/135. The complete
checkpoint covered 1,713 .NET tests and 1,848 tests overall.

Focused approval tests proved disabled defaults, diagnostics-only public surface,
strict bounded configuration, exact retained-plan reference binding, current
authentication plus administrator authorization and fresh reauthentication, redacted
subject and approval identities, unauthenticated/non-administrator/stale/malformed/
missing-plan/equivalent-plan rejection, one active approval, automatic expiry and
replacement, exact revocation, and malformed identity-source rejection. Evidence
collection accepts only a valid fresh exact observation, rejects future-issued or
otherwise malformed approval evidence, and drops an approval that expires during its
bounded collection window.

The validation-only published gateway retained authority disabled, zero active approval
and attempt state, and no activation authority or CLI/Admin/browser/HTTP/WebSocket/
hosted-service/timer/AetherRemote/radio/watchdog/command/lease/TX caller. The local
independent watchdog started empty and Disarmed. No deployment, server update, radio
connection, TX lease, keying action, transmit-control command, or live RF operation was
performed.

The twenty-eighth increment adds the first GitHub-hosted release source and read-only
CLI caller. `ReleaseGitHubSource` defaults disabled and strictly binds one canonical
public GitHub owner/repository, a 1-through-100 release-list bound, and a 5-through-120
second request timeout. Enabling it does not bypass separately configured local
manifest-verification trust.

The source reads one bounded GitHub release page, rejects malformed metadata and unsafe
asset API URLs, ignores drafts, and selects the highest canonical
`aethersdr-<semantic-version>` release whose GitHub prerelease state agrees with the
exact Stable or Beta channel; Pinned accepts only its exact release identity. Equal
semantic precedence across distinct tags fails as ambiguous.

For the process-derived `linux-x64` or `linux-arm64` architecture, the selected release
must contain exactly one architecture manifest plus gateway, broker, AetherRemote agent,
and station-engine assets using the milestone's fixed names. Each asset must be fully
uploaded, non-empty, within its existing package or manifest byte bound, and remain
bound to the configured repository. Optional GitHub SHA-256 metadata is checked in
addition to the signed manifest. Automatic redirects are disabled; at most four HTTPS
redirects may be followed, and every destination must remain on the reviewed GitHub API
or release-asset host set.

The five assets are streamed once into one random owner-private temporary directory
using create-new files, bounded buffers, declared-length checks, incremental SHA-256,
and durable flushes. The tree is frozen and passed to the existing immutable offline
signed-bundle verifier. The signed identity must equal the selected GitHub tag. The
temporary tree is removed after success and failure; ambiguous cleanup fails closed.

`--check-github-release` reuses the exact installed version, channel/Pinned identity,
configuration-schema, protocol, and process-architecture inputs already used by offline
checking. It emits one redacted report and exits before any web/setup host, authentication,
hosted service, radio, watchdog, or routing composition. There is no persistent download,
archive extraction, staging, installation, pointer mutation, service control, activation,
rollback, Admin/browser, AetherRemote runtime, radio/watchdog command, lease mutation,
keying, TX action, or live RF operation.

Automated checkpoint on 2026-08-05 for GitHub release checking: changed C# files passed
scoped format verification; the deployment script passed shell syntax and its complete
validation-only production gate; `git diff --check` passed; and the Release solution
build completed with zero warnings and zero errors. The focused GitHub source/CLI suite
passed 23/23 and the existing offline check suite passed 38/38. Web tests passed
1,561/1,561; independent-watchdog tests passed 57/57; TX-HIL isolation tests passed
48/48; AetherRemote tests passed 70/70; and browser tests passed 135/135. The complete
checkpoint covered 1,736 .NET tests and 1,871 tests overall.

Focused tests proved disabled source and disabled trust rejection before network access,
strict owner/repository/count/timeout configuration, diagnostics-only source surface,
Stable/Beta/exact-Pinned selection, draft exclusion, highest semantic-version selection,
exact five-asset architecture contracts, missing/duplicate/foreign-repository rejection,
metadata length and optional digest drift rejection, bounded reviewed-host redirects,
signed-tag identity agreement, redacted JSON output, cancellation, and temporary-tree
cleanup after both success and failure.

The final validation-only release was
`20260805-080102-flexweb-validation`. Published health retained the GitHub source disabled
with the reviewed repository contract, metadata/asset/temp verification boundaries
registered, and persistent download, extraction, staging, installation, service control,
activation, rollback, Admin/browser, radio, watchdog, command, lease, and TX callers
false. Production web/watchdog artifact inspection retained the reviewed TX string shape,
and the independent watchdog started empty and Disarmed. No deployment, server update,
radio connection, TX lease, keying action, transmit-control command, or live RF operation
was performed.

- Publish architecture-specific gateway, broker, AetherRemote agent, and station-
  engine packages for `linux-x64` and `linux-arm64` through GitHub Releases.
- Publish a signed release manifest containing checksums, configuration schema,
  protocol compatibility, minimum previous version, release channel, restart
  requirements, and release notes.
- Embed only the release-signing public key in the installer. Reject any package
  whose signature, checksum, architecture, or compatibility declaration fails.
- Provide Admin and CLI workflows for `check`, `download`, `install`, `status`,
  and `rollback`. Support Stable, Beta, and exact-version Pinned channels.
- Default TX-capable stations to automatic download with administrator-approved
  installation. Never silently restart a station or change TX software.
- Before activation, stop new TX leases, require radio-authoritative idle and
  Disarmed watchdogs, back up state, migrate a staged copy, install a new
  immutable release, switch atomically, and verify every service and health
  contract. Restore the previous release and configuration automatically on
  failure.
- Support offline installation of a downloaded signed release bundle through the
  same verification, migration, health, and rollback path.

### M8C — Guided standalone installer, reverse proxy, and TLS

- Provide one supported native installer that creates dedicated service users,
  directories, permissions, hardened systemd units, release directories, and
  required firewall guidance.
- Ask whether the operator already has a reverse proxy. For an existing proxy,
  generate and validate forwarding, TLS termination, forwarded headers, request
  limits, authentication callback, health endpoint, and both browser and station
  WebSocket upgrades.
- Provide reviewed templates for Caddy and Nginx plus explicit requirements for
  other proxies.
- When no proxy exists, offer to install and configure Caddy for HTTPS,
  certificate renewal, WebSocket forwarding, security headers, and health
  routing. LAN-only deployments require an explicit internal-certificate plan
  and never silently fall back to insecure public HTTP.
- Re-running the installer must be idempotent and must not regenerate credentials,
  overwrite operator policy, or lose the current rollback release.

### M8D — Production local authentication and role administration

- Add production local accounts for operators who do not use Entra ID. The
  development authentication handler remains development-only.
- Support local accounts, Microsoft Entra ID, and generic OpenID Connect, with an
  optional combined local and external-provider deployment.
- Local authentication has no default password and requires secure password
  hashing, rate limiting, lockout, passkeys or TOTP MFA, recovery codes, session
  revocation, administrator reset, and durable audit records.
- Preserve the explicit Observe, Control, Transmit, and Admin roles. Role changes
  and authentication-provider changes require administrator reauthentication and
  revoke affected active authority where appropriate.

### M8E — Radio onboarding and per-radio TX policy

- Discover local and remote radios, let administrators assign stable labels, and
  show exact source, health, client capacity, and station ownership before use.
- Add per-radio Admin policy states: Receive only, TX eligible, Temporarily
  disabled, and Unavailable because prerequisites failed.
- TX eligibility never bypasses the server master switch, trusted signing keys,
  exact radio allowlists, command transport, ownership gate, independent
  watchdog, or activation preflight.
- Enabling a radio requires administrator reauthentication, exact-radio preflight,
  a human-readable prerequisite report, and an audit record. It applies only to
  safely created or restarted sessions.
- Disabling a radio immediately removes browser TX capability, rejects new
  leases, releases idle authority, and ownership-safely unkeys only TX proven to
  belong to AetherSDR. It must not interrupt SmartSDR, Maestro, or hardware PTT.
- Keep room for later per-radio maximum power, antenna, and frequency constraints
  implemented beneath the browser rather than as UI-only limits.

### M8F — AetherRemote bootstrap and guided station enrollment

- Host the exact compatible signed AetherRemote packages and installer from the
  operator's own gateway.
- Publish non-secret discovery metadata under
  `/.well-known/aethersdr`, including gateway version, compatible agent range,
  broker WebSocket URL, enrollment endpoint, release manifest, and download
  locations.
- The remote installer asks for or accepts the operator's AetherSDR URL, detects
  architecture, downloads the matching signed package, installs dedicated users
  and systemd services, validates DNS/TLS/WebSockets, and discovers local FLEX
  radios before enrollment.
- Enrollment codes remain separate from URLs, command lines, downloaded scripts,
  and shell history. The installer prompts locally for the short-lived code and
  stores the resulting station credential with restrictive permissions.
- Admin guides station naming, code creation, installation command, connection
  wait, identity confirmation, discovered-radio approval, naming, and receive-
  only or TX-eligible policy selection.
- The gateway tracks agent and station-engine versions and may request only a
  specific signed release update. It never exposes an arbitrary remote shell or
  arbitrary-command channel. The station stages, verifies, restarts, reconnects,
  and rolls back locally if its health check fails.

### M8G — Backup, restore, diagnostics, and operations

- Provide one supported encrypted backup workflow covering local users and MFA,
  authentication configuration, Data Protection keys, radio policies, station
  credentials, signing/trust configuration, audit state, managed proxy state,
  and current/rollback release identities.
- Support restore on the same host and migration to a replacement VM, with an
  explicit list of secrets that require separate handling when not included.
- Add security and dependency scanning, rate-limit review, structured logs,
  metrics, alerts, service health, storage health, certificate expiry, backup
  age, release compatibility, and operational runbooks.
- Provide a downloadable diagnostic bundle that redacts passwords, private keys,
  station credentials, tokens, enrollment codes, and other sensitive identifiers.
- The setup center and Admin diagnostics test public URL, TLS, proxy headers,
  browser and station WebSockets, authentication callback, radio discovery,
  AetherRemote compatibility, TX prerequisites, backup readiness, update
  readiness, and rollback readiness with actionable operator-facing errors.
- Publish versioned release notes and a supported browser, device, architecture,
  reverse-proxy, and station-topology matrix.

### M8H — Standalone release acceptance

- Rehearse clean installation, update, failed-update rollback, backup, restore,
  and uninstall on supported Ubuntu Server 24.04 architectures.
- Run the complete automated suite, multi-client hardware soak, mobile/VPN
  recovery suite, security review, and operator acceptance checklist against the
  packaged release rather than a source checkout.
- Retain the previous immutable release and all credentials, policies, and audit
  state through both successful and automatically rolled-back updates.

Acceptance criteria:

- A personal operator can install a LAN or public AetherSDR gateway on a clean
  Ubuntu Server 24.04 machine without Entra ID or a pre-existing reverse proxy,
  create a protected local administrator, and connect one local radio in
  receive-only mode.
- An operator with an existing reverse proxy and Entra ID or generic OIDC can
  validate and use that infrastructure without the installer replacing it.
- A clean remote Linux station can download AetherRemote from its own gateway,
  ask for the gateway URL, enroll with a one-time code, discover its radios, and
  appear in Admin without hand-editing the W4CAR broker URL or source files.
- Two radios can have different TX policies. Enabling one cannot expose TX for
  the other, and disabling an eligible radio removes browser authority and safely
  handles only an AetherSDR-owned active transmission.
- A signed GitHub release can be checked, downloaded, installed, verified, and
  rolled back from Admin or CLI without losing users, station credentials,
  radio policy, certificates, signing/trust state, audit history, or the prior
  release.
- A compatible remote station can update from the gateway's signed package,
  reconnect successfully, and roll back locally when post-update health fails.
- A supported backup can restore the installation onto a replacement VM with
  documented handling for every secret and external dependency.
- The final release candidate passes the full automated suite, clean-machine
  installation matrix, update/rollback rehearsal, multi-client hardware soak,
  mobile/VPN recovery suite, and operator acceptance checklist.
