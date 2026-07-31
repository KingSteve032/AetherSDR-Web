# Independent TX Watchdog Skeleton

`AetherSDR.TxWatchdog` is the Phase 2D process-boundary skeleton for the future
station-local transmit safety supervisor. It is intentionally command-incapable.
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

This increment does **not** move emergency reconciliation into the process yet.
The production web gateway does not launch or connect to the host, and the host
cannot act on a radio. The lease ID is only an exact identity binding; the host
has no lease acquire, renew, release, or restore operation. The guarded deployment package includes the executable so
its independent artifact can be inspected before any later transport or service
registration is reviewed.

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

A new process responds with a new host instance ID and an empty disarmed
snapshot. `radioCommandTransportAvailable`, `armingAvailable`, `registered`,
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
and executes a status request against the published watchdog before deployment.
