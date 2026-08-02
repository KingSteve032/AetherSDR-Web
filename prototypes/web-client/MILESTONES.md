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

Status: **Active — browser intent validation implemented; executable production TX outstanding**

Goal: enable transmit only after engine-side arbitration can prove deliberate
operator intent and force-unkey on every loss path.

Foundation deployed and corrected on 2026-07-29. Corrective candidate:
`20260729-m7-interlock-occupancy-1`.

Milestone state:

- **Safety foundation and loss-path HIL: Complete.** The single-radio lease,
  exact-owner gate, independent unkey-only supervisor, production/HIL binary
  separation, and every required owner/liveness loss path have accepted evidence.
- **Production browser TX integration: Validation-only Phase 2F implemented.**
  The browser can manage the exact ownership lease and submit deliberate TX
  intents through a strict replay-resistant protocol when separately configured,
  but every fully valid request stops at `transport-unavailable`. The production
  server still exposes no reachable MOX/PTT, TUNE, CW, microphone-audio, key, or
  unkey radio path and continues to report `transmitEnabled=false`.
- **M7 remains Active** until an authorized operator can deliberately transmit
  from the production browser through the accepted station-local safety boundary
  and the complete browser-driven workflow passes production HIL acceptance.

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

## M8 — Production release

Status: **Planned — blocked on completion of M7 production browser TX integration**

Goal: make the gateway supportable as a maintained station service.

Scope:

- Repeatable install, upgrade, rollback, backup, and restore procedures.
- Security review, dependency scanning, rate limits, structured logs, metrics,
  alerting, and operational runbooks.
- Versioned release notes and a supported-browser/device matrix.

Acceptance criteria:

- A clean Ubuntu Server 24.04 VM can be installed and rolled back from the
  documented procedure.
- A release candidate passes the full automated suite, multi-client hardware
  soak, mobile/VPN recovery suite, and operator acceptance checklist.
