# Independent TX Watchdog

`AetherSDR.TxWatchdog` is the independent process boundary for the future
station-local transmit safety supervisor. Phase 2D introduced the standalone
host; Phase 2E supervises one host per active web radio session. Phase 2U adds a
disabled-by-default, unkey-only FLEX transport primitive. It has no key method,
no arbitrary-command method, no arming operation, no lease operation, and no
persistence.

The executable currently proves only these boundaries:

- it runs as a process separate from the web gateway;
- it accepts a bounded newline-delimited JSON protocol over standard input and
  returns one JSON response per line on standard output;
- it tracks one exact radio/session/browser/gateway/engine/connection/lease/
  FLEX-handle identity with a strictly increasing observation sequence;
- it starts empty and `Disarmed` on every process start;
- disconnect requires an exact re-registration before another heartbeat;
- malformed, oversized, unknown, stale, and mismatched messages are rejected;
- process restart never restores or infers prior identity or authority;
- the optional radio adapter can encode only `xmit 0`, and the protocol has no
  request that can invoke it in Phase 2U.

Phase 2U still does **not** move emergency reconciliation or radio authority into
the process. The production gateway launches the host as a private supervised
child inside the same least-privileged service cgroup and communicates only
through redirected standard input/output. An exact local `FlexRx` endpoint is
passed only when the watchdog transport setting is enabled and the physical
radio is allowlisted. Even then, the host remains Disarmed and the protocol has
no arm or unkey request. The lease ID is only an exact identity binding; the host
has no lease acquire, renew, release, or restore operation.

Complete authority registers one process epoch. Exact observations heartbeat
that epoch. Authority loss or disconnect replaces it with a new empty Disarmed
process. Child exit, malformed response, timeout, request mismatch, or identity
mismatch is reported immediately to the in-process lifecycle so only its tracked
lease is revoked. Restart is asynchronous and never replays the old identity.
A replacement ready response is diagnostic only.

## Protocol

Run the local stdio host with the transport disabled:

```bash
AetherSDR.TxWatchdog --stdio
```

The reviewed but still callerless unkey adapter can be configured only with the
strict argument shape below:

```bash
AetherSDR.TxWatchdog --stdio --unkey-enabled \
  --radio-id REVIEWED-RADIO-ID \
  --radio-host 192.0.2.10 \
  --radio-port 4992 \
  --command-timeout-ms 2000
```

The host accepts only a unicast IPv4 endpoint, a bounded timeout, and an exact
radio ID. This configuration exposes no key or general command interface.

Each request is one JSON line, at most 4096 characters. Protocol version 1
supports only `status`, `register`, `heartbeat`, and `disconnect`. Registration,
heartbeat, and disconnect carry the same exact identity and a positive,
strictly increasing sequence. Status carries no identity or sequence.

Example status request:

```json
{"protocolVersion":1,"requestId":"status-1","type":"status"}
```

A new process responds with a new host instance ID and an empty Disarmed
snapshot. Gateway response parsing requires the exact state `Disarmed` and a
matching request ID. A disabled adapter reports
`unkey-transport-disabled-disarmed` with
`radioCommandTransportAvailable=false`; a configured adapter reports
`unkey-transport-ready-disarmed` with that field true. In both cases
`armingAvailable` is false, registration/connection/lease binding begin false,
and `lastSequence` is zero. After registration the response may report
`leaseBound=true`, but it never echoes the opaque lease ID or the full authority
identity.

## Validation

From the repository root:

```bash
dotnet test \
  prototypes/tx-watchdog/AetherSDR.TxWatchdog.Tests/AetherSDR.TxWatchdog.Tests.csproj \
  -c Release
```

The full FlexWeb validation gate also publishes a self-contained Linux artifact,
scans both the web and watchdog binaries for exact reviewed command counts,
executes a status request against the published watchdog, and verifies supervised
Disarmed process counts after deployment. The watchdog artifact must contain
exactly one `xmit 0`, zero `xmit 1`, and no HIL/CWX/TX-audio surfaces. With
production browser TX leases disabled, no child may report a registered identity.
