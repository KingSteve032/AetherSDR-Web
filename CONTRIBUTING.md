# Contributing to AetherSDR-Web

Thank you for helping improve AetherSDR-Web. By participating, you agree to
follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

1. Read [AGENTS.md](AGENTS.md) and the
   [project constitution](CONSTITUTION.md). They define the architecture,
   safety boundaries, evidence requirements, and contribution workflow.
2. Search existing issues and pull requests. Claim active issue or pull-request
   work as described in `AGENTS.md` so contributors do not create overlapping
   changes.
3. Report vulnerabilities privately according to [SECURITY.md](SECURITY.md).
   Do not open a public issue for an undisclosed security problem.
4. Keep every contribution clean-room. Use public documentation, published
   open-source code, packet captures, radio observations, or independently
   documented tests. Never use proprietary decompiler or disassembler output.

## Safety and architecture

The FLEX radio is authoritative for live and radio-persisted state. Client
commands are requests; status received from the radio is truth.

Production transmit remains fail-closed. Do not add or expose a keying path,
weaken ownership or intent checks, enable production transmit, or run live-radio
or RF tests without explicit maintainer direction and operator-controlled
procedures. Offline tests and deterministic fakes are the default.

Validate untrusted input where it enters the system. Preserve authentication,
authorization, session isolation, same-origin WebSocket enforcement, bounded
queues and messages, atomic security-sensitive persistence, and external
operator ownership.

## Build and test

Use the .NET SDK selected by `global.json` and Node.js for the browser tests:

```bash
dotnet restore AetherSDR-Web.slnx
dotnet build AetherSDR-Web.slnx -c Release --no-restore
dotnet format AetherSDR-Web.slnx --verify-no-changes --no-restore

dotnet test prototypes/web-client/tests/AetherSDR.Web.Tests.csproj -c Release --no-build
dotnet test prototypes/web-client/tx-hil-tests/AetherSDR.TxHil.Tests.csproj -c Release --no-build
dotnet test prototypes/tx-watchdog/AetherSDR.TxWatchdog.Tests/AetherSDR.TxWatchdog.Tests.csproj -c Release --no-build
dotnet test AetherRemote/tests/AetherRemote.Tests/AetherRemote.Tests.csproj -c Release --no-build
dotnet test tools/release/AetherSDR.ReleaseBuilder.Tests/AetherSDR.ReleaseBuilder.Tests.csproj -c Release --no-build
node --test prototypes/web-client/tests-ui/*.test.mjs
```

Run the tests relevant to your change at minimum. Before requesting merge, run
the complete offline suite when practical and state exactly what ran. Never
describe a build or test as passing unless you ran it.

## Pull requests

Keep a pull request focused and include:

- the problem, root cause, and resulting behavior;
- tests and their actual results;
- security, deployment, protocol, and compatibility impact;
- documentation updates for architecture, wire contracts, or operations;
- the most load-bearing constitutional citation, such as `Principle VII.`,
  when the change is principle-relevant;
- confirmation that no credentials, keys, tokens, live configuration,
  deployment payloads, or generated build outputs are included.

Sign every commit. If commit signing is not configured, stop before committing
and configure an approved signing method. Do not bypass branch protection or
review requirements.
