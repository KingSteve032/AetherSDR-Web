# AetherSDR-Web

AetherSDR-Web is a standalone web-based FLEX radio application suite. It is not
part of the native C++/Qt AetherSDR desktop program.

The native project can be used as a behavioral reference while documenting FLEX
radio behavior, but this repository builds, tests, deploys, and runs on its own.

## Layout

- `prototypes/web-client/` — station engine, web gateway, browser client, tests,
  deployment helpers, and the isolated TX hardware-in-the-loop harness.
- `AetherRemote/` — remote station protocol, agent, broker, tests, and deployment
  helpers.
- `assets/` — logo, meter-face data, and band-plan data required by the web app.

The `prototypes/web-client` path is intentionally retained for now so existing
HIL scripts and deployment documentation continue to use the same absolute path:

```text
/mnt/devspace-projects/aethersdr-web/prototypes/web-client
```

A later source-layout change can rename that folder after all scripts and service
files are updated together.

## Build and test

```bash
dotnet build AetherSDR-Web.slnx -c Release

dotnet test prototypes/web-client/tests/AetherSDR.Web.Tests.csproj -c Release
dotnet test prototypes/web-client/tx-hil-tests/AetherSDR.TxHil.Tests.csproj -c Release
dotnet test AetherRemote/tests/AetherRemote.Tests/AetherRemote.Tests.csproj -c Release
node --test prototypes/web-client/tests-ui/*.test.mjs
```

## Transmit safety

Production is receive-only unless transmit is explicitly enabled through the
intended station configuration and build path. TX state is radio-authoritative,
operator intent is mandatory, and stale or ambiguous ownership fails closed.
