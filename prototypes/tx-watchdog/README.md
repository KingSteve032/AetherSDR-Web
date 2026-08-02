# Independent TX Watchdog

`AetherSDR.TxWatchdog` is the independent process boundary for the station-local
transmit safety supervisor. Phase 2D introduced the standalone host; Phase 2E
supervises one host per active web radio session; Phase 2U added a reviewed,
disabled-by-default, unkey-only FLEX transport; and Phase 2V adds a separately
disabled arm/heartbeat/disarm state machine.

The executable has no key method and no arbitrary-command method. Its only radio
command literal is `xmit 0`.

## Safety boundary

The process:

- runs separately from the web gateway;
- accepts bounded newline-delimited JSON over standard input and returns one
  response per request;
- tracks one exact radio/session/browser/gateway/engine/connection/lease/FLEX-
  handle identity with a strictly increasing sequence;
- starts empty and `Disarmed` on every process start;
- cannot infer or restore a prior arm after restart;
- requires separate unkey-transport and arming switches;
- accepts an arm only after exact authority registration;
- uses a 250-5000 ms server-owned heartbeat timeout;
- preserves an active arm across controlling-connection loss until its deadline;
- performs at most one timeout unkey attempt and never retries automatically;
- clears an arm only after command acceptance and fresh radio-confirmed idle;
- enters `ReconciliationRequired` after a known rejection, missing idle
  confirmation, or another unknown unkey outcome;
- rejects stale, mismatched, malformed, oversized, or unknown messages.

Before the TCP adapter sends `xmit 0`, it opens its own FLEX control connection,
subscribes using the fixed `sub client all` and `sub tx all` commands, and
requires fresh interlock state naming the exact protected FLEX handle as the
current TX owner. Idle state completes without sending a command. A different,
ambiguous, missing, or unconfirmed owner sends no command. After `xmit 0`, both
the matching command response and a fresh `READY` or `RECEIVE` interlock status
are required; command acceptance without idle confirmation is an unknown outcome.

The watchdog does not acquire, renew, release, or restore a browser TX lease.
The lease ID in its identity is only an exact safety binding.

## Configuration

Transport and arming disabled:

```bash
AetherSDR.TxWatchdog --stdio
```

Reviewed unkey transport present but arming disabled:

```bash
AetherSDR.TxWatchdog --stdio --unkey-enabled \
  --radio-id REVIEWED-RADIO-ID \
  --radio-host 192.0.2.10 \
  --radio-port 4992 \
  --command-timeout-ms 2000
```

Reviewed unkey transport and arming enabled:

```bash
AetherSDR.TxWatchdog --stdio --unkey-enabled --arming-enabled \
  --radio-id REVIEWED-RADIO-ID \
  --radio-host 192.0.2.10 \
  --radio-port 4992 \
  --command-timeout-ms 2000
```

`--arming-enabled` is invalid without `--unkey-enabled`. The endpoint must be a
unicast IPv4 FLEX endpoint, the timeout is bounded, and the radio ID is supplied
only after the gateway's exact allowlist and local-FLEX checks succeed.

## Protocol

Protocol version 2 supports only:

- `status`
- `register`
- `arm`
- `heartbeat`
- `disarm`
- `disconnect`

There is no `key`, `unkey`, arbitrary command, lease, retry, or reset request.
All authority-bearing requests carry the same exact identity and a positive,
strictly increasing sequence. `arm` requires
`heartbeatTimeoutMilliseconds`. An armed `heartbeat` also requires a fresh
bounded timeout. Ordinary registration heartbeats while Disarmed carry no
safety timeout and cannot arm or renew the deadline.

Example status request:

```json
{"protocolVersion":2,"requestId":"status-1","type":"status"}
```

Example arm request shape:

```json
{
  "protocolVersion": 2,
  "requestId": "arm-2",
  "type": "arm",
  "sequence": 2,
  "identity": {
    "radioId": "REVIEWED-RADIO-ID",
    "sessionId": "session-id",
    "browserClientId": "browser-id",
    "gatewayInstanceId": "gateway-id",
    "engineInstanceId": "engine-id",
    "connectionClientId": "connection-id",
    "leaseId": "opaque-lease-id",
    "stationClientHandle": 305419896
  },
  "heartbeatTimeoutMilliseconds": 1000
}
```

Responses expose bounded state only: arm/deadline timestamps, transport and
arming availability, one-shot unkey counters, and the last bounded outcome and
reason. The opaque lease and full authority identity are never serialized in a
snapshot.

With production defaults, a new process reports `Disarmed`,
`unkey-transport-disabled-disarmed`, transport unavailable, arming unavailable,
not armed, and zero unkey attempts.

## Validation

From the repository root:

```bash
dotnet test \
  prototypes/tx-watchdog/AetherSDR.TxWatchdog.Tests/AetherSDR.TxWatchdog.Tests.csproj \
  -c Release
```

The full FlexWeb validation gate publishes the watchdog and web gateway, inspects
both managed/native artifact layers, and runs a protocol-v2 status probe. The
watchdog artifact must contain exactly one `xmit 0`, zero `xmit 1`, and no
HIL/CWX/TX-audio surfaces. Default deployment health must report arming disabled,
zero armed processes, zero reconciliation-required processes, zero unkey
attempts, and no browser caller.
