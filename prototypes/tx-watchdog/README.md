# Independent TX Watchdog

`AetherSDR.TxWatchdog` is the command-incapable process boundary for the future
station-local transmit safety supervisor. Phase 2D introduced the standalone
host; Phase 2E supervises one host per active web radio session. It remains
intentionally command-incapable.
It has no FLEX connection, no radio command transport, no arming operation, no
lease operation, and no persistence.

The executable currently proves only these boundaries:

- it runs as a process separate from the web gateway;
- it accepts a bounded newline-delimited JSON protocol over standard input and
  returns one JSON response per line on standard output;
- it tracks one exact radio/session/browser/gateway/engine/connection/lease/
  FLEX-handle identity with a strictly increasing observation sequence;
- it starts empty and `Disarmed` on every process start;
- disconnect requires an exact re-registration before another heartbeat;
- malformed, oversized, unknown, stale, and mismatched messages are rejected;
- process restart never restores or infers prior identity or authority.

Phase 2E does **not** move emergency reconciliation or radio authority into the
process. The production gateway launches the host as a private supervised child
inside the same least-privileged service cgroup and communicates only through
redirected standard input/output. The host still cannot act on a radio. The
lease ID is only an exact identity binding; the host has no lease acquire,
renew, release, or restore operation.

Complete authority registers one process epoch. Exact observations heartbeat
that epoch. Authority loss or disconnect replaces it with a new empty Disarmed
process. Child exit, malformed response, timeout, request mismatch, or identity
mismatch is reported immediately to the in-process lifecycle so only its tracked
lease is revoked. Restart is asynchronous and never replays the old identity.
A replacement ready response is diagnostic only.

## Protocol

Run the local stdio host:

```bash
AetherSDR.TxWatchdog --stdio
```

Each request is one JSON line, at most 4096 characters. Protocol version 1
supports only `status`, `register`, `heartbeat`, and `disconnect`. Registration,
heartbeat, and disconnect carry the same exact identity and a positive,
strictly increasing sequence. Status carries no identity or sequence.

Example status request:

```json
{"protocolVersion":1,"requestId":"status-1","type":"status"}
```

A new process responds with a new host instance ID and an empty Disarmed
snapshot. Gateway response parsing requires the exact state `Disarmed`, reason
`command-incapable-skeleton`, and a matching request ID.
`radioCommandTransportAvailable`, `armingAvailable`, `registered`,
`connected`, and `leaseBound` are all false, and `lastSequence` is zero. After
registration the response may report `leaseBound=true`, but it never echoes the
opaque lease ID or the full authority identity.

## Validation

From the repository root:

```bash
dotnet test \
  prototypes/tx-watchdog/AetherSDR.TxWatchdog.Tests/AetherSDR.TxWatchdog.Tests.csproj \
  -c Release
```

The full FlexWeb validation gate also publishes a self-contained Linux artifact,
scans both the web and watchdog binaries for forbidden TX/HIL command strings,
executes a status request against the published watchdog, and verifies supervised
Disarmed process counts after deployment. With production browser TX leases
disabled, no child may report a registered identity.
