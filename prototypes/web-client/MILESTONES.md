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
| M6 — Remote station connectivity | End-to-end pilot staged | Secure access to radios on other networks |
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

Status: **End-to-end receive pilot operational; production hardening remains**

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

Status: **Active**

Goal: enable transmit only after engine-side arbitration can prove deliberate
operator intent and force-unkey on every loss path.

Foundation deployed and corrected on 2026-07-29. Corrective candidate:
`20260729-m7-interlock-occupancy-1`.


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
  configuration remains receive-only while station-local command ownership and
  hardware unkey tests are built.
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

Acceptance criteria:

- A single-holder TX lease is enforced below the browser boundary and keyed by
  physical radio, not browser session.
- PTT, TUNE, CW, and microphone transmission require an explicit operator
  action and the appropriate capability.
- Lease loss, authentication loss, browser loss, gateway loss, and engine
  restart all force an immediate unkey of AetherSDR-owned TX in
  hardware-in-the-loop tests without interrupting external SmartSDR, Maestro,
  or hardware-PTT transmission.
- Receive-only deployments continue to have no reachable keying command.

## M8 — Production release

Status: **Planned**

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
