# PSOC2 TX hardware-in-the-loop harness

`AetherSDR.TxHil` is a standalone console tool for controlled M7 transmit-safety
validation. It is not a web endpoint, is not registered with the production
service, and is explicitly excluded from the production web project.

The first on-air workflows are hard-bound to:

- Radio: `FLEX:1121-1104-6700-2912`
- Chassis serial: `1121-1104-6700-2912`
- Host: `10.2.0.12:4992`
- Frequency: explicitly supplied from 14.225000 through 14.350000 MHz
- Initial pulse mode: USB
- Identification mode: CW
- RF power: exactly 1 W
- TX antenna: exactly ANT1
- Initial TX route: DAX enabled, microphone selection PC, VOX disabled
- TX audio streams created by this harness: none
- Automatic station identification: `KC4CAW` at 20 WPM through FLEX CWX

The frequency is never selected automatically. The controlling operator must
listen immediately before arming and explicitly confirm the selected frequency
is clear.

## Running the tool

The project filesystem may be mounted `noexec`, so invoke the managed DLL:

```bash
cd /mnt/devspace-projects/aethersdr-web
HIL=prototypes/web-client/tx-hil/bin/Release/net10.0/AetherSDR.TxHil.dll
```

## No-RF verification commands

### Safety inspection

Creates a temporary FLEX GUI registration and reads identity, GUI roster,
interlock ownership, transmit settings, CWX settings, and slice inventory. The
radio may assign Local PTT to this temporary GUI when it is the only GUI client.
It sends no `xmit 1`, no `cwx send`, and creates no TX audio stream.

```bash
dotnet "$HIL" inspect
```

### Restore known idle station defaults

This idle-only recovery command is hard-bound to PSOC2. It refuses to act unless
the radio is freshly idle with zero TX occupants and no external GUI client. It
restores RF power to 100 W, DAX on, microphone selection PC, and VOX off. It has
no key or unkey path and emits no RF.

```bash
dotnet "$HIL" restore-idle-defaults
```

The process-loss preflight now also refuses to start unless that exact 100 W
baseline is already present.

### External-owner denial

Run while SmartSDR owns Local PTT. The real station gate is connected to a
transport that throws if reached. Passing output contains:

- `denial: external_local_ptt_owner`
- `commandCount: 0`

```bash
dotnet "$HIL" verify-external-block
```

### CWX configuration round trip

Clears the CWX queue, changes WPM from its current value to 20, radio-confirms
the change, clears again, and restores the original WPM. It preserves the
radio's existing QSK and break-in-delay settings because PSOC2 firmware reports
those values but rejects `cwx qsk_enabled` writes on this command path.

This command has no `cwx send` or `xmit 1` path:

```bash
dotnet "$HIL" verify-cwx-config
```

### Full single-client radio preflight

Exercises the complete state-changing setup and cleanup path without keying:

1. Verify exact serial, empty external GUI roster, and fresh idle interlock.
2. Create an owned pan, waterfall, and slice at the chosen frequency.
3. Verify USB, ANT1, exact GUI ownership, and TX-slice assignment.
4. Apply and verify the silent route and exactly 1 W.
5. Acquire and verify exclusive Local PTT while still idle.
6. Switch only the HIL-owned slice from USB to CW.
7. Restore the exact transmit settings and remove the owned slice, waterfall,
   and pan.

```bash
dotnet "$HIL" verify-preflight \
  --frequency-hz 14250000 \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

### Simulated independent-watchdog fault matrix

Runs eight deterministic fault scenarios without opening a radio connection:

- heartbeat expiry while idle
- heartbeat expiry while the exact protected handle owns TX
- browser/session loss
- external SmartSDR owner protection
- unknown unkey command outcome
- temporary emergency-transport outage
- startup reconciliation
- bounded retry exhaustion

```bash
dotnet "$HIL" verify-safety-faults
```

Passing output explicitly reports `unkeyOnly=true` and
`radioConnectionCreated=false`.

### Live non-GUI observer preflight

Creates one non-GUI FLEX observer and one temporary engine GUI. The observer
independently sees the engine's exact Local PTT handle, arms the safety
supervisor, accepts an exact heartbeat, and disarms while the radio remains
idle. The observer has an unkey-only adapter and no key method.

```bash
dotnet "$HIL" verify-safety-observer
```

A passing run reports:

- `observerGuiRegistered=false`
- `engineLocalPttObserved=true`
- `unkeyCommands=0`
- `keyCommandAvailable=false`

### Full two-connection heartbeat-expiry preflight

Stages the exact two-connection forced-unkey topology without transmitting:

1. Connect the independent non-GUI observer.
2. Connect the temporary engine GUI.
3. Create and verify the engine-owned 14.250 MHz ANT1 slice.
4. Set and confirm the silent 1 W route.
5. Transfer Local PTT to the engine handle.
6. Have the observer independently verify that exact handle.
7. Arm and heartbeat the unkey-only safety supervisor.
8. Disarm from idle with zero unkey commands.
9. Restore 100 W and remove all temporary resources.

```bash
dotnet "$HIL" verify-safety-expiry-preflight \
  --frequency-hz 14250000 \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

Passing output reports `rfEmitted=false`, a 750 ms future heartbeat-expiry
setting, and zero unkey commands.

### Full two-connection browser-session-loss preflight

Stages the browser/session-loss forced-unkey topology without transmitting. It
creates the engine and non-GUI observer connections, stages the owned
14.250 MHz ANT1 slice and silent 1 W route, acquires the exact synthetic
controlling-session lease, releases that session while the radio remains idle,
and sends the explicit `browser-session-lost` supervisor signal. The supervisor
must disarm with zero unkey commands because the interlock is already idle.

```bash
dotnet "$HIL" verify-safety-session-loss-preflight \
  --frequency-hz 14250000 \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

Passing output reports one released lease, the exact session/browser identity,
`unkeyCommands=0`, and `rfEmitted=false`.

### Full two-connection engine command-channel-loss preflight

Stages the next forced-unkey topology without transmitting. The engine and
non-GUI observer establish the exact protected handle, create the owned slice,
apply the silent 1 W route, and arm the supervisor. A connection monitor first
observes the exact engine instance, lease, and FLEX handle connected, then the
HIL wrapper injects loss of only the engine TX command channel while the radio
remains idle. The monitor must disarm the supervisor without any radio command.

```bash
dotnet "$HIL" verify-safety-engine-loss-preflight \
  --frequency-hz 14257000 \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

Passing output requires `connectedObserved=true`, `lossSignaled=true`, zero
engine keys/unkeys/post-loss attempts, zero observer unkeys, and
`rfEmitted=false`. This preflight and RF operation inject loss of the TX command
boundary while retaining the engine status session for evidence,
identification, restoration, and cleanup.

### True child-process/TCP-loss preflight

Stages the later process-loss topology without transmitting. A temporary GUI
inspection session captures the radio-authoritative restoration state and
exits. The parent then opens only the independent non-GUI observer and launches
the engine as a separate child process using a second one-time mode-0600 plan.
The child stages the owned slice, 1 W route, and Local PTT, reports its exact
PID/lease/FLEX handle, and waits without keying. The parent arms the observer,
kills the child with `Process.Kill(entireProcessTree: true)`, proves the exact
handle disappears from the FLEX roster, and requires idle disarm with zero
radio commands.

```bash
dotnet "$HIL" verify-safety-process-loss-preflight \
  --frequency-hz 14262000 \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

Passing output requires `rfEmitted=false`, child exit code 137 on Linux,
`gracefulCleanupRan=false`, zero child key/unkey commands, zero observer
unkeys, and removal of the child's test-frequency resources before the cleanup
session restores the original transmit settings.

## Normal operator-unkey pulse

### Prepare

The normal pulse uses a purpose-bound manifest named
`operator-unkey-pulse`. The file is mode 0600, valid for five minutes, and
stores only the SHA-256 hash of its random one-time token.

```bash
ARM="/run/user/$UID/aethersdr-tx-hil.json"

dotnet "$HIL" prepare \
  --arm-file "$ARM" \
  --frequency-hz 14250000 \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

### Run

```bash
dotnet "$HIL" pulse \
  --arm-file "$ARM" \
  --token "<ONE-TIME-TOKEN>"
```

The engine keys only after exact lease, Local PTT, client-handle, and idle
checks. It holds the initial USB pulse for 100 ms, performs an ownership-safe
operator unkey, requires fresh radio-confirmed idle, switches only its owned
slice to CW, sends `KC4CAW`, confirms queue drain and idle, then restores the
radio.

## Independent heartbeat-expiry forced-unkey test

This is a separate armed operation. Its token cannot launch the normal pulse,
and a normal-pulse token cannot launch this test.

### Prepare

```bash
SAFETY_ARM="/run/user/$UID/aethersdr-tx-safety-expiry.json"

dotnet "$HIL" prepare-safety-expiry \
  --arm-file "$SAFETY_ARM" \
  --frequency-hz 14250000 \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

The prepare output must show:

- purpose `independent-heartbeat-expiry`
- heartbeat expiry `750` ms
- engine explicit unkey `false`
- independent observer unkey-only `true`

### Run

```bash
dotnet "$HIL" safety-expiry \
  --arm-file "$SAFETY_ARM" \
  --token "<ONE-TIME-TOKEN>"
```

The forced-unkey sequence is:

1. Open the non-GUI observer before the engine GUI.
2. Revalidate exact PSOC2 identity, empty external GUI roster, fresh idle,
   restorable settings, and fresh CWX configuration.
3. Create the engine-owned USB/ANT1 slice and verify the silent 1 W route.
4. Transfer Local PTT to the engine and have the observer independently verify
   that exact handle.
5. Arm the independent supervisor with engine, lease, session, browser, and
   exact FLEX-handle identity.
6. Have the engine issue exactly one `xmit 1`.
7. Require both engine and observer streams to confirm the exact engine handle
   owns TX.
8. Send one final heartbeat with a 750 ms deadline, then deliberately stop
   heartbeats.
9. Require the independent observer—not the engine—to issue exactly one
   `xmit 0` after the deadline.
10. Require both status streams to confirm idle.
11. Verify the command split: engine key count 1, engine unkey count 0,
    observer unkey count 1.
12. Send the CW `KC4CAW` identification, confirm queue drain and idle, restore
    settings, and remove temporary resources.

The observer may unkey only when fresh radio state contains exactly one TX
occupant and its handle matches the purpose-bound protected engine handle.
SmartSDR, Maestro, hardware PTT, ambiguous ownership, stale status, or a
replaced handle never receives a global unkey.

## Independent browser-session-loss forced-unkey test

This is a third purpose-bound operation named
`independent-browser-session-loss`. Its token cannot launch the normal pulse or
the heartbeat-expiry operation, and neither of those tokens can launch this
test.

### Prepare

```bash
SESSION_ARM="/run/user/$UID/aethersdr-tx-session-loss.json"

dotnet "$HIL" prepare-safety-session-loss \
  --arm-file "$SESSION_ARM" \
  --frequency-hz 14250000 \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

The prepare output must show:

- purpose `independent-browser-session-loss`
- controlling-session lease release enabled
- explicit supervisor abort reason `browser-session-lost`
- engine explicit unkey `false`
- independent observer unkey-only `true`

### Run

```bash
dotnet "$HIL" safety-session-loss \
  --arm-file "$SESSION_ARM" \
  --token "<ONE-TIME-TOKEN>"
```

The sequence is:

1. Establish the independent non-GUI observer and engine GUI.
2. Stage the exact USB/ANT1/14.250 MHz silent 1 W route and transfer Local PTT.
3. Arm the observer with the exact engine, lease, session, browser, and FLEX
   client handle.
4. Have the engine send exactly one `xmit 1` and prove exact-handle TX on both
   independent status streams.
5. Release exactly one lease by the controlling session ID and confirm the
   physical radio has no remaining current TX lease.
6. Send the explicit `browser-session-lost` signal to the independent
   supervisor.
7. Require the observer—not the engine—to send exactly one `xmit 0`.
8. Require both status streams to confirm idle and verify engine unkey count
   remains zero.
9. Send CW `KC4CAW`, confirm queue drain and idle, restore settings, and remove
   temporary resources.

## Independent authentication-loss forced-unkey test

This fourth purpose-bound operation is named
`independent-authentication-loss`. Its token cannot launch the normal pulse,
heartbeat-expiry, browser-session-loss, engine-loss, or process-loss
operations, and none of those tokens can launch this test.

The authentication monitor has no radio command transport. It acts only after
it first observes the exact active safety arm—engine instance, lease, session,
browser client, and FLEX handle—as authenticated. Starting unauthenticated,
reporting a mismatched identity, or repeating the same loss report cannot
invent ownership or issue a duplicate immediate unkey.

### No-RF preflight

```bash
dotnet "$HIL" verify-safety-auth-loss-preflight \
  --frequency-hz <operator-confirmed-clear-frequency-hz> \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

The preflight creates and removes the owned radio resources, establishes the
exact authenticated authority, releases its lease, injects authentication loss
while the radio is idle, and must finish with zero key/unkey commands and
`rfEmitted=false`.

### Prepare

```bash
AUTH_LOSS_ARM="/run/user/$UID/aethersdr-tx-auth-loss.json"

dotnet "$HIL" prepare-safety-auth-loss \
  --arm-file "$AUTH_LOSS_ARM" \
  --frequency-hz <operator-confirmed-clear-frequency-hz> \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

The prepare output must show:

- purpose `independent-authentication-loss`
- exact authenticated authority observed
- controlling-session lease release enabled
- explicit supervisor abort reason `authentication-lost`
- engine explicit unkey `false`
- independent observer unkey-only `true`

### Run

```bash
dotnet "$HIL" safety-auth-loss \
  --arm-file "$AUTH_LOSS_ARM" \
  --token "<ONE-TIME-TOKEN>"
```

The sequence is:

1. Establish the independent non-GUI observer and engine GUI.
2. Stage the exact operator-selected USB/ANT1 silent 1 W route and transfer
   Local PTT to the engine handle.
3. Arm the observer with the exact engine, lease, session, browser, and FLEX
   client handle.
4. Have the authentication monitor record that exact authority as
   authenticated before any key request.
5. Have the engine send exactly one `xmit 1` and prove exact-handle TX on both
   independent status streams.
6. Release exactly one lease for the authenticated session and inject the exact
   authenticated-to-unauthenticated transition.
7. Require the observer—not the browser/gateway engine path—to send exactly one
   `xmit 0`; repeated loss reports may not duplicate the immediate command.
8. Require both status streams to confirm idle and verify the engine unkey count
   remains zero.
9. Send CW `KC4CAW`, confirm queue drain and idle, restore settings, and remove
   temporary resources.

## Independent web-gateway process-loss forced-unkey test

This purpose-bound operation is named `independent-gateway-process-loss`.
Its token cannot launch the normal pulse, heartbeat-expiry,
browser-session-loss, authentication-loss, engine-loss, or engine-process-loss
operations.

The station engine and independent non-GUI safety observer remain connected to
the radio. A separate HIL-only gateway-authority child process is observed as
the exact control authority and then force-killed with its entire process tree.
That child creates no radio connection and has no key or unkey capability. Only
the exact observed gateway process transition may release the controlling
session lease and signal the station-local unkey-only supervisor.

### No-RF preflight

```bash
dotnet "$HIL" verify-safety-gateway-loss-preflight \
  --frequency-hz <clear-frequency-hz> \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

The preflight must prove the child process was force-killed, exactly one lease
was released, the idle supervisor issued zero unkeys, the interlock remained
idle, and `rfEmitted=false`.

### Prepare and run

```bash
GATEWAY_ARM="/run/user/$UID/aethersdr-tx-gateway-loss.json"

dotnet "$HIL" prepare-safety-gateway-loss \
  --arm-file "$GATEWAY_ARM" \
  --frequency-hz <clear-frequency-hz> \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF

dotnet "$HIL" safety-gateway-loss \
  --arm-file "$GATEWAY_ARM" \
  --token "<ONE-TIME-TOKEN>"
```

The live sequence requires one engine key, zero engine unkeys, a forced gateway
child exit, one released controlling lease, exactly one observer unkey, fresh
radio idle, CW `KC4CAW` identification, complete setting restoration, and no
leaked resources. A replacement or never-observed gateway process cannot claim
the prior connection, and external SmartSDR, Maestro, or hardware-PTT ownership
is never globally unkeyed.

## Independent engine TX command-channel-loss forced-unkey test

This fifth purpose-bound operation is named
`independent-engine-connection-loss`. Its token cannot launch any other HIL
operation, and the other purpose tokens cannot launch it.

This test injects failure of the engine's TX command transport, not yet a full
engine process or radio TCP-session kill. The engine status session is retained
only so the harness can record radio-authoritative evidence, send the required
CW identification after idle, restore settings, and remove owned resources.
Once failure is injected, the gate reports its transport disconnected and the
engine cannot issue unkey or another TX command.

### Prepare

```bash
ENGINE_LOSS_ARM="/run/user/$UID/aethersdr-tx-engine-loss.json"

dotnet "$HIL" prepare-safety-engine-loss \
  --arm-file "$ENGINE_LOSS_ARM" \
  --frequency-hz 14257000 \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

The prepare output must show:

- purpose `independent-engine-connection-loss`
- injected boundary `station-engine-tx-command-channel`
- exact connected-to-disconnected transition required
- engine explicit unkey `false`
- independent observer unkey-only `true`
- full process kill `false`

### Run

```bash
dotnet "$HIL" safety-engine-loss \
  --arm-file "$ENGINE_LOSS_ARM" \
  --token "<ONE-TIME-TOKEN>"
```

The sequence is:

1. Establish the independent non-GUI observer and the engine GUI/status path.
2. Stage the exact operator-selected USB/ANT1 silent 1 W route and transfer
   Local PTT to the engine handle.
3. Arm the independent observer with the exact engine instance, lease, session,
   browser, and FLEX handle.
4. Have the connection monitor record the exact TX command channel connected.
5. Have the engine send exactly one `xmit 1` and require both status streams to
   prove the exact engine handle owns TX.
6. Inject command-channel loss and record the matching connected-to-disconnected
   transition. The engine gate becomes unavailable and may send no unkey or
   other post-loss command.
7. Require the independent observer to issue exactly one `xmit 0` for the exact
   protected handle and require radio-confirmed idle.
8. Reconcile the engine gate to `flex_client_lost`/Faulted without any command.
9. Verify one engine key, zero engine unkeys, zero post-loss engine attempts,
   and one observer unkey.
10. Send CW `KC4CAW`, confirm queue drain and idle, restore settings, and remove
    temporary resources.

## Independent engine process/TCP-loss forced-unkey test

This sixth purpose-bound operation is named
`independent-engine-process-loss`. Its token cannot launch any earlier HIL
operation, and earlier purpose tokens cannot launch it.

The parent consumes the five-minute operator manifest, captures the original
radio settings through a temporary GUI session, then creates a second child
plan valid for 30 seconds. The child plan is mode 0600, stores only a SHA-256
token hash, binds the exact parent PID/start time and PSOC2 topology, and is
deleted before the child connects. The engine child has a five-second backup
lease watchdog if the parent disappears unexpectedly.

### Prepare

```bash
PROCESS_LOSS_ARM="/run/user/$UID/aethersdr-tx-process-loss.json"

dotnet "$HIL" prepare-safety-process-loss \
  --arm-file "$PROCESS_LOSS_ARM" \
  --frequency-hz 14262000 \
  --on-air-confirm KC4CAW-PSOC2-ANT1-CLEAR-CAMERA-REMOTE-OFF
```

The prepare output must show:

- purpose `independent-engine-process-loss`
- injected boundary `engine-process-and-flex-tcp`
- child plan lifetime 30 seconds
- process kill of the entire child tree
- graceful child cleanup expected `false`
- engine explicit unkey `false`
- independent observer unkey-only `true`

### Run

```bash
dotnet "$HIL" safety-process-loss \
  --arm-file "$PROCESS_LOSS_ARM" \
  --token "<ONE-TIME-TOKEN>"
```

The sequence is:

1. Capture the restorable PSOC2 transmit/CWX state through a temporary GUI and
   disconnect it.
2. Start the non-GUI observer with an empty GUI roster and fresh idle.
3. Create and consume the 30-second child plan, then launch the engine child as
   a separate OS process.
4. Have the child stage the exact USB/ANT1/operator-frequency silent 1 W route,
   acquire Local PTT and its five-second backup lease, and report its exact
   PID, engine/session/browser identity, lease, FLEX handle, and zero command
   counts.
5. Arm the independent observer and record the exact child connection present.
6. Send the child one `key` instruction, require one child key and zero child
   unkeys, and prove exact-handle TX through the observer.
7. Kill the child and its process tree. No child `finally`, graceful unkey, or
   radio cleanup path may run.
8. Signal process loss immediately from the verified OS-process exit. If TCP
   closure already made the radio idle, the supervisor disarms with zero
   commands; otherwise it may issue exactly one unkey only while the dead
   child's exact handle remains the sole TX occupant.
9. Require radio-confirmed idle, later FLEX roster removal, and disappearance of
   the dead child's test resources.
10. Restore the known 100 W station baseline, create a fresh 30-second child
    plan, and launch a replacement engine process.
11. Require a new PID, engine instance, session, browser identity, lease, and
    FLEX handle. The old handle must remain absent and the radio must stay idle.
12. Send only `reconcile-idle-and-exit`; require zero key commands, zero unkey
    commands, no active TX intent, a clean exit code 0, resource removal, and
    restoration of the 100 W/DAX-on/PC-mic/VOX-off baseline.
13. Open a new GUI cleanup/identification session, transmit CW `KC4CAW`, confirm
    queue drain and idle, restore the original settings, and remove only the new
    cleanup resources.

The result reports which unkey mechanism occurred:
`radio-auto-unkey-on-engine-tcp-close` or `independent-observer-unkey`. Both are
accepted only after exact process, roster, handle, and radio-idle evidence.

## Operator checklist

All items are mandatory immediately before any prepare command:

- KC4CAW is the controlling operator.
- PSOC2 ANT1 is connected to the intended antenna system.
- No amplifier or external RF switching path can key.
- The exact frequency is within the operator's privileges.
- The operator listened on that exact frequency and confirmed it clear.
- SmartSDR, Maestro, and every other FLEX GUI client are disconnected.
- No MIC, ACC, RCA, tuner, amplifier, or external PTT source is active.
- The camera remains live.
- Remote power-off is immediately available.
- The radio interlock is idle.

Immediately before consuming a token, confirm the frequency is still clear and
that the five-minute manifest has not expired.

## Abort conditions

Do not continue when any of these occurs:

- Serial mismatch or external GUI client present.
- External, ambiguous, unknown, stale, or replaced TX ownership.
- Interlock not freshly idle.
- Transmit-route or CWX configuration fields unavailable or stale.
- RF power not confirmed at 1 W.
- Owned slice, frequency, mode, ANT1, or TX assignment not confirmed.
- Observer does not see the exact engine Local PTT handle.
- Heartbeat identity or manifest purpose does not match exactly.
- CWX reply index is invalid or fresh `sent=` never reaches the calculated end.
- Exact engine TX ownership is never observed by both paths.
- Token, manifest mode, expiry, or one-time-consumption failure.
- Camera or remote power control becomes unavailable.
- Any unkey, CWX clear, idle-confirmation, or restoration failure.

If fresh idle cannot be proven, the harness does not restore the previous
100 W setting or deliberately remove resources. Treat the critical console
message as an immediate remote-power-off incident.

## Current evidence

- HIL tests: 42/42
- Web server tests: 210/210
- Browser tests: 106/106
- AetherRemote tests: 70/70
- Gate, independent-supervisor, and engine-connection-monitor focused tests:
  35/35
- Process-loss child-plan and manifest safety tests: one-time hash-only child
  plan, exact parent identity, 30-second expiry, fixed RF bounds, and purpose
  isolation and idle-default recovery are included in the 42/42 HIL suite
- Executable simulated safety matrix: 8/8, no radio connection
- Live SmartSDR denial: `external_local_ptt_owner`, command count zero
- Live CWX no-RF round trip: 30 → 20 → 30 WPM; QSK remained on and delay
  remained 5 ms
- Live single-client no-RF preflight: 14.250 MHz, USB → CW, ANT1, 1 W,
  exclusive Local PTT, full 100 W restoration, no leaked test slice
- Live non-GUI observer preflight: engine handle `0x364debb0`, observer handle
  `0x3ef379d5`, observer non-GUI, exact engine Local PTT observed, zero unkeys
- Live two-connection safety-expiry no-RF preflight: engine handle `0x49764071`,
  observer handle `0x638b175e`, ANT1/14.250 MHz/1 W staged, 750 ms heartbeat
  configured, zero unkeys, `rfEmitted=false`, full restoration
- Live browser-session-loss no-RF preflight: engine handle `0x41cfa0f3`,
  observer handle `0x0d0fc5b3`, exact synthetic session lease released once,
  explicit `browser-session-lost` signal delivered, zero unkeys,
  `rfEmitted=false`, 100 W restored, and no leaked test slice
- Live engine command-channel-loss no-RF preflight: engine handle
  `0x6417899a`, observer handle `0x07222ab7`, exact engine instance/lease/handle
  connected then injected unavailable, zero engine keys, zero engine unkeys,
  zero post-loss attempts, zero observer unkeys, `rfEmitted=false`, 100 W and
  original TX/CWX settings restored, and no leaked 14.257 MHz resource
- Live engine command-channel-loss RF acceptance on 2026-07-30 at 14.262 MHz:
  engine handle `0x7ec86b98`, observer handle `0x5ca70331`, one engine key,
  zero engine unkeys, zero post-loss attempts, and one observer unkey
- Command-channel-loss timing: observer unkey requested 30.3111 ms after loss,
  radio idle 26.8505 ms later, total keyed-to-idle interval 60.6095 ms; the
  disconnected gate reconciled to `flex_client_lost`/Faulted
- CW identification after command-channel loss: `KC4CAW` at 20 WPM, insertion
  index 18, final sent index 23, exact-handle TX observed, queue drained, and
  idle confirmed. The controlling operator independently received the callsign
  on PSOC1, proving the over-the-air path for this test
- Live true engine-process/TCP-loss no-RF preflight: child PID 47030, child FLEX
  handle `0x5eb82d3b`, observer handle `0x17d206b8`, process-tree kill exit code
  137, no graceful cleanup, zero child commands, zero observer unkeys,
  `rfEmitted=false`, and child resources absent before cleanup
- Original process preflight timing: kill-to-exit 83.4178 ms,
  exit-to-roster-removal 1058.4511 ms, and roster-loss-to-safety-action
  1.8628 ms. Final inspection showed idle, 100 W, PC/DAX/VOX route restored,
  CWX 30 WPM/QSK-on/5 ms, and no 14.262 MHz resource
- Optimized true-process no-RF preflight: child PID 49188, child FLEX handle
  `0x5c4f74b2`, observer handle `0x1e97e788`, process-tree kill exit code 137,
  zero child commands, zero observer unkeys, and `rfEmitted=false`. Verified
  process exit now triggers safety before FLEX roster cleanup: kill-to-exit
  79.6503 ms, exit-to-safety-action 1.7401 ms, safety-action-to-idle 3.6919 ms,
  while roster removal completed 1568.3875 ms after process exit. Child
  resources were absent before cleanup and final restoration remained clean
- Production process-loss artifact proof: clean production publish has zero
  key, unkey, CW-ID, process-loss operation, child command/plan, or TX-audio
  creation strings. The standalone `AetherSDR.TxHil.dll` contains exactly one
  key, one unkey, and one CW-ID literal; its HIL-only referenced
  `AetherSDR.Web.dll` contains the command-gate key/unkey adapter and is not part
  of the production publish
- Live true engine-process/TCP-loss RF acceptance on 2026-07-30 at 14.262 MHz:
  child PID 48741, child FLEX handle `0x379a01d5`, observer handle
  `0x40efbbdc`, one child key, zero child unkeys, process-tree kill exit code
  137, no graceful child cleanup, child resources absent, and one observer
  unkey with no observer key capability
- Process-loss mechanism: `independent-observer-unkey`; the original
  radio-confirmed keyed-to-idle interval was 3782.7442 ms. Analysis showed
  3629.5864 ms was spent waiting for FLEX roster removal after the process had
  already exited. The operation now signals safety immediately from verified
  process exit and keeps roster removal as a required later postcondition
- Optimized live RF re-acceptance on 2026-07-30 at 14.262 MHz: child PID 51514,
  child FLEX handle `0x5d842625`, observer handle `0x5fb12ab5`, one child key,
  zero child unkeys, one observer unkey, process-tree exit code 137, and no
  graceful cleanup. Keyed-to-idle improved to 1199.0572 ms. Kill-to-exit was
  110.0109 ms; process-exit-to-safety-action completion was 1079.5545 ms;
  idle followed 0.914 ms later; roster loss followed process exit by
  1080.7058 ms. CW `KC4CAW` completed at 20 WPM, exact-owned TX was observed,
  final restoration returned to 100 W/DAX-on/PC-mic/VOX-off/CWX defaults, and
  both one-time files were consumed. The HIL result now separately timestamps
  safety signal, unkey dispatch, command completion, idle, and roster loss
- Replacement-engine startup reconciliation no-RF acceptance passed at
  14.262 MHz. Dead child PID 53214/handle `0x2f5f8bea` was replaced by PID
  53234/handle `0x182650ae`; engine instance, session, browser identity, lease,
  PID, and FLEX handle were all fresh. The old handle remained absent, the
  replacement reported zero keys and zero unkeys, reconciled from fresh idle,
  exited normally with code 0, removed its resources, and restored the exact
  100 W/DAX-on/PC-mic/VOX-off baseline. `rfEmitted=false`.
- CW identification after process loss: `KC4CAW` at 20 WPM, exact-owned TX
  observed, queue drained, and idle confirmed
- Process-loss final restoration: zero TX occupants, 100 W, DAX on, PC mic
  route, VOX off, CWX 30 WPM/QSK-on/5 ms, no leaked resources, outer manifest
  consumed, and child plan consumed
- Live browser-session-loss RF acceptance on 2026-07-30 at 14.257 MHz:
  engine handle `0x2f7b3e2b`, non-GUI observer handle `0x3337cca0`, one engine
  key, zero engine unkeys, exactly one released controlling-session lease, and
  one observer unkey after the explicit `browser-session-lost` signal
- Session-loss timing: observer unkey requested 35.877 ms after session loss,
  radio idle 26.9665 ms later, total keyed-to-idle interval 69.7106 ms
- CW identification after session-loss unkey: `KC4CAW` at 20 WPM, insertion
  index 12, final sent index 17, exact-handle TX observed, queue drained, and
  idle confirmed
- Session-loss final restoration: 100 W, DAX on, PC mic route, VOX off, CWX
  30 WPM/QSK-on/5 ms, no leaked 14.257 MHz resources, and manifest consumed
- Production web publish: exactly one reviewed dormant `xmit 1`, one runtime-
  deduplicated dormant `xmit 0`, and both primary/emergency transport type
  markers; zero `cwx send "KC4CAW" 1`, HIL operation classes, process-child
  surfaces, and TX-audio-stream creation strings. The production watchdog
  publish contains zero `xmit 1` and exactly one dormant unkey-only `xmit 0`.
- Standalone HIL executable: exactly one `xmit 1`, one `xmit 0`, and one exact
  `cwx send "KC4CAW" 1`; no TX-audio-stream creation command
- Live normal RF acceptance on 2026-07-30 at 14.250 MHz: exact client handle
  `0x49dfd8b9` reached radio-confirmed `Keyed`, then radio-confirmed `Idle`; the
  measured keyed-to-idle interval was 186.7982 ms around the fixed 100 ms hold
- The controlling operator also visually observed PSOC2's transmit indication
  through the remote camera, corroborating the FLEX telemetry that RF keying
  occurred
- CW identification acceptance: `KC4CAW` at 20 WPM, insertion index 0, final
  `cwx sent=` index 5, exact-handle TX ownership observed, queue drained, and
  radio-confirmed idle
- Post-test restoration: 100 W, DAX on, PC mic route, VOX off, CWX 30 WPM,
  QSK on, 5 ms delay, no leaked 14.250 MHz HIL slice, and arm manifest consumed

- Live independent heartbeat-expiry RF acceptance on 2026-07-30 at 14.250 MHz:
  engine handle `0x43924c50`, non-GUI observer handle `0x06512903`, one engine
  key, zero engine unkeys, one observer unkey after the 750 ms heartbeat expiry,
  and no observer key capability
- Timing: safety unkey requested 791.8049 ms after key confirmation, radio idle
  25.7973 ms later, total keyed-to-idle interval 817.6022 ms
- CW identification after forced unkey: `KC4CAW` at 20 WPM, insertion index 6,
  final sent index 11, exact-handle TX observed, queue drained, and idle
  confirmed
- Independent over-the-air confirmation: the controlling operator tuned PSOC1
  to 14.250 MHz and audibly copied `KC4CAW` from PSOC2, proving the RF and
  antenna path in addition to the FLEX command/status telemetry
- Final restoration: 100 W, DAX on, PC mic route, VOX off, CWX 30 WPM/QSK-on/
  5 ms, no leaked 14.250 MHz test resources, and the purpose-bound manifest
  consumed
