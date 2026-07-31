# AetherSDR-Web — Project Context for AI Agents

This is the canonical project guide for any AI assistant working on
AetherSDR-Web, including Claude Code, OpenAI Codex, Cursor, GitHub Copilot,
Gemini Code Assist, Aider, or another agent.

Read this file before writing code, changing deployment behavior, running
hardware-in-the-loop tests, or recommending a merge.

## Project identity

AetherSDR-Web is a standalone web-based FLEX radio application suite. It is a
separate program from the native C++/Qt AetherSDR desktop application.

The native AetherSDR repository may be used as a behavioral reference for
publicly observable radio behavior, open-source protocol knowledge, and prior
test evidence. It is not a source dependency, build dependency, deployment
dependency, or destination for AetherSDR-Web code.

Do not:

- move AetherSDR-Web code back into the native AetherSDR tree;
- introduce links to files in a neighboring native checkout;
- add a runtime dependency on native AetherSDR;
- copy native implementation code merely because it already solves a similar
  problem;
- assume an AetherD/native-engine architecture is automatically the final web
  architecture.

Any behavior adopted from the native project must remain clean-room and be
supported by an allowed source: public documentation, published open-source
code, packet captures, radio observations, or independently documented tests.

## Repository layout

```text
AetherSDR-Web.slnx
AGENTS.md
CONSTITUTION.md
.specify/memory/constitution.md
assets/
AetherRemote/
prototypes/web-client/
prototypes/tx-watchdog/
scripts/
docs/
local-config/              # ignored; never commit
```

Primary components:

- `prototypes/web-client/` — ASP.NET Core station engine/web gateway, browser
  client, server tests, browser tests, deployment helpers, and the isolated TX
  hardware-in-the-loop harness.
- `prototypes/tx-watchdog/` — command-incapable independent TX watchdog
  protocol, process host, deterministic process-boundary tests, and supervised
  local-IPC integration. It has no production radio transport or arming surface.
- `AetherRemote/src/AetherRemote.Protocol/` — versioned station/broker protocol
  contracts and validation.
- `AetherRemote/src/AetherRemote.Agent/` — station-local remote connectivity and
  receive-session agent.
- `AetherRemote/src/AetherRemote.Broker/` — central remote-station broker and
  enrollment boundary.
- `AetherRemote/tests/AetherRemote.Tests/` — protocol, broker, agent, network,
  enrollment, and integration tests.
- `assets/` — repository-owned assets required by the web app. The project must
  not reference assets from the native checkout.
- `docs/evidence/` — retained test evidence suitable for the repository. Do not
  place credentials, tokens, private keys, or live enrollment data here.
- `local-config/` — ignored local/deployed configuration snapshots. Keep files
  mode-restricted and never commit them.

The `prototypes/web-client` path is intentionally retained while existing HIL,
deployment, and operational paths depend on it. A future rename must update all
scripts, service files, documentation, and absolute HIL paths in one reviewed
change.

## Constitution

Read `.specify/memory/constitution.md` before writing or reviewing code.
`CONSTITUTION.md` is its byte-identical root mirror.

The inherited constitution contains 14 principles. The domain and defensive
principles—radio authority, clean-room work, transmit-on-intent, boundary
validation, evidence, claim coordination, sandboxing, operator precedence, and
atomic persistence—are binding here.

The constitution also contains native AetherSDR-specific implementation and
technology language. Do not silently rewrite or ignore that text. When a
native-only rule does not map cleanly to this standalone .NET/web repository,
follow the cross-cutting safety intent and raise the mismatch for a formal
constitution amendment rather than inventing an exception.

Commit messages should cite the most load-bearing principle as
`Principle <Roman numeral>.` when the change is principle-relevant.

## Project goals

AetherSDR-Web should provide a secure, responsive browser interface to FLEX
radios while preserving radio-authoritative state, independent client sessions,
and external SmartSDR/Maestro/hardware-PTT ownership.

The system should support:

- authenticated browser access;
- independent per-browser/per-radio GUI sessions;
- spectrum, waterfall, meters, audio, slices, and receive controls;
- bounded browser and network backpressure;
- station discovery and remote station projection;
- explicit administrative observability without hidden control paths;
- transmit only after the complete station-local safety boundary is proven.

Production remains receive-only unless transmit is deliberately enabled through
an approved build/configuration path and all production TX acceptance criteria
are complete.

## Architecture and trust boundaries

### Browser

The browser is untrusted for authorization, ownership, and TX safety.

A disabled button is a usability affordance, not a security boundary. Every
browser message must be validated and re-authorized on the server. Never trust a
browser-supplied role, radio identity, lease identity, session identity, FLEX
handle, capability, or ownership assertion.

Browser changes must preserve:

- same-origin WebSocket policy;
- bounded message/frame sizes;
- enumerated intent and property names;
- ordered state versions and snapshot recovery;
- bounded queues where disposable latest-value data may be dropped safely;
- no cross-session leakage of slices, pans, audio, credentials, or control
  state.

### Web gateway/station engine

The ASP.NET Core boundary owns authentication integration, authorization,
session isolation, message validation, browser projections, and station-local
radio coordination implemented by this repository.

Radio state is authoritative. Browser/client state must reconcile to fresh radio
status rather than reassert remembered state.

Do not manufacture a capability because a browser requested it. Do not infer TX
ownership from login role, Local PTT alone, a stale roster, or a previous
session.

### AetherRemote

AetherRemote is part of this repository and must evolve with the web app under a
versioned, validated protocol boundary.

- Protocol types belong in `AetherRemote.Protocol`.
- Station-only transport and FLEX discovery belong in `AetherRemote.Agent`.
- Broker enrollment, liveness, routing, and remote projection belong in
  `AetherRemote.Broker`.
- Do not duplicate protocol DTOs separately in the agent, broker, or web app.
- Reject unknown, oversized, expired, mismatched, or malformed messages at the
  receiving boundary.
- Remote connectivity must not create a bypass around station-local capability,
  session, lease, or TX-safety checks.

### FLEX radio

The FLEX radio is authoritative for live radio state, interlock state, client
roster, stream identities, and command responses.

Discovery data is advisory and can be stale. A live GUI-client response and
fresh status determine actual admission and ownership.

External SmartSDR, Maestro, and hardware PTT are independent actors. Never evict,
release, take over, or globally unkey an external owner as a side effect of a web
session transition.

## Transmit safety

Transmit code is high risk. Principle VI applies absolutely: no RF without
unambiguous operator intent.

### Production boundary

Normal production builds and publishes must remain receive-only unless an
explicitly approved production TX milestone changes that rule.

Currently:

- the real `xmit 1`/`xmit 0` adapter is isolated behind `EnableTxHil=true`;
- the production browser does not receive a reachable keying path merely because
  HIL source exists;
- production publish verification must continue to prove that HIL-only key,
  unkey, CW-ID, process-loss child, and TX-audio creation paths are absent;
- do not register HIL transports, gates, or operations in production dependency
  injection or browser routing.

Any change that weakens this separation requires explicit maintainer approval,
security review, production binary inspection, automated tests, and real-radio
acceptance.

### Ownership-safe TX rules

A key request must require all of the following, freshly and exactly:

- deliberate operator action;
- authenticated transmit capability;
- the single physical-radio TX lease;
- matching radio, web session, browser client, engine instance, and FLEX handle;
- fresh idle interlock state;
- exclusive Local PTT authority matching that exact AetherSDR handle;
- no external, stale, ambiguous, or conflicting owner.

Actual TX ownership and force-unkey decisions use radio-authoritative interlock
state plus `tx_client_handle`. `local_ptt` alone does not prove RF is keyed.

Unknown command outcomes are not success or failure. Preserve guarded intent and
reconcile against fresh radio state.

A force-unkey may be sent only while fresh evidence proves the exact protected
AetherSDR handle is the sole TX owner. Never unkey SmartSDR, Maestro, hardware
PTT, an ownerless external transmission, an ambiguous owner, or a replaced
handle.

No reconnect, startup, profile restore, state reconciliation, timer, retry,
status echo, or model update is operator intent to transmit.

### Independent safety supervisor

The independent safety supervisor must have no key capability. Its radio command
transport is unkey-only and purpose-bound to the exact armed engine identity,
lease, session/browser owner, and protected FLEX handle.

A newly started supervisor begins disarmed. It cannot infer ownership of an
already-active transmission.

Connection-loss and heartbeat-loss signals must bind to an identity that was
observed connected first. Startup while disconnected, stale reports, mismatched
identity, and repeated loss reports must not invent ownership or trigger
duplicate unkeys.

### Hardware-in-the-loop work

Live HIL can emit RF. Never run a live RF HIL operation autonomously.

The operator must run the purpose-built wrapper and personally provide all
required confirmations, including frequency, antenna/load conditions, station
baseline, external-client state, and on-air authorization.

Agents may autonomously run:

- unit tests;
- builds;
- static production publish inspection;
- read-only radio inspection when explicitly requested and safe;
- documented no-RF preflight operations that cannot key and restore the known
  idle baseline.

Agents must not:

- bypass one-time manifests or tokens;
- extend expiration windows merely for convenience;
- remove exact radio/serial/frequency/power binding;
- loosen mode-0600 file requirements;
- invoke the live process-loss wrapper on the operator's behalf;
- claim an over-the-air result without operator evidence.

HIL reports are evidence only when the manifest/plan lifecycle, command counts,
process identity, FLEX identity, ownership, final idle state, resource cleanup,
and station baseline are all verified.

## Session and state isolation

Every browser page is a distinct FLEX GUI client, even when multiple pages use
the same authenticated identity.

Each browser/radio aggregate owns its own:

- session ID;
- browser client ID;
- FLEX GUI client identity;
- radio coordinator/connection;
- slices and panadapters;
- stream IDs;
- browser queues;
- audio state.

A reconnect from the same page may recover its own aggregate. Another page must
never receive or reuse that session's private identity or radio resources.

Operator presence may aggregate authenticated identities per radio, but it must
not merge their slice, panadapter, audio, capability, lease, or control state.

Version gaps require a fresh snapshot. Do not guess deltas or replay stale local
state over the radio.

## SmartSDR/FLEX protocol guidance

Protocol behavior must be supported by published open-source FlexLib, public
protocol material, packet captures, or observed radio evidence. Do not guess.

Important observed rules include:

- TCP messages use `V`, `H`, `C`, `R`, `S`, and `M` prefixes.
- Status object names may contain spaces.
- Command names and status names are not always identical.
- `client set local_ptt=1` is the relevant Local PTT command on tested firmware.
- The radio does not report MOX as a simple `mox=` transmit-status field; use the
  interlock state machine.
- VITA-49 streams must be filtered by packet class and owned stream identity.
- `client_handle` and stream ownership are required for Multi-Flex isolation.
- UDP discovery and advertised client capacity may be stale.

When protocol behavior is uncertain, collect logs or packet evidence before
changing the implementation. Record firmware/protocol versions alongside
non-obvious compatibility decisions.

## Coding standards

### C# and .NET

- Target the framework versions declared by the project files; do not silently
  downgrade or introduce an additional target framework.
- Keep nullable reference types enabled and resolve warnings rather than
  suppressing them broadly.
- `TreatWarningsAsErrors` is intentional.
- Prefer immutable records for validated protocol messages and snapshots.
- Keep services single-purpose and explicit about ownership/lifetime.
- Use dependency injection for boundaries, clocks, transports, and durable
  stores where tests require deterministic behavior.
- Use `TimeProvider` for testable time-based behavior rather than scattering
  direct wall-clock calls.
- Pass `CancellationToken` through asynchronous boundaries.
- Do not use `.Result`, `.Wait()`, or sync-over-async in request, WebSocket, or
  service paths.
- Bound channels, queues, buffers, payload sizes, retry counts, and timeouts.
- Dispose sockets, streams, timers, cancellation sources, and child processes
  deterministically.
- Do not catch broad exceptions merely to continue in an unknown safety state.
- Log identifiers needed for diagnosis without logging secrets, raw tokens, or
  credentials.
- Validate configuration at startup and fail closed on security-sensitive
  omissions.

### Browser JavaScript

- Keep protocol parsing and state-version handling deterministic and testable.
- Treat all network data as untrusted.
- Do not use `innerHTML` with untrusted content.
- Do not place authorization decisions in browser-only code.
- Bound frame queues and favor latest-frame semantics only for disposable
  spectrum/waterfall data.
- Preserve ordered control/state messages.
- Audio changes must preserve bounded jitter behavior and avoid unbounded
  buffering.
- UI changes must remain usable on desktop and mobile and must not expose
  disabled-but-reachable control paths.

### Shell and deployment

- Use `set -euo pipefail` in new Bash scripts.
- Quote variables and paths.
- Validate source and destination paths before destructive operations.
- Destructive cleanup must be guarded, explicit, and performed only after a
  successful copy/build/test verification.
- Never overwrite deployed credentials or live appsettings during install or
  upgrade.
- Preserve rollback material intentionally; generated publish trees do not
  belong in Git.
- System services should run under the least-privileged dedicated account and
  use restrictive file permissions.

## Configuration, credentials, and persistence

Never commit:

- OIDC client secrets;
- station enrollment codes;
- station credentials;
- runtime or administration credential hashes tied to production;
- ASP.NET Data Protection keys;
- private keys or certificates;
- live `local-config/` contents;
- published appsettings copied from deployed hosts;
- HIL manifests, child plans, or one-time tokens.

Safe examples must use placeholders and default to disabled or fail-closed
behavior.

Persist security-sensitive state atomically. Write the complete new state to a
restricted temporary file, flush where appropriate, set final permissions, and
atomically replace the previous file. Never delete the only valid credential or
registry before the replacement is durable.

Configuration ownership must be clear. Avoid multiple components independently
persisting the same authoritative value.

Radio-persistable settings remain radio-owned. The web client may persist only
web/client-specific preferences and operational configuration the radio does not
own.

## Generated files and repository hygiene

Do not commit generated or runtime output, including:

- `bin/`, `obj/`, `bin-hil/`, `obj-hil/`;
- `.publish/`, `.deploy/`, `.artifacts/`;
- `TestResults/`, coverage output, logs, dumps, and temporary files;
- deployment tarballs and split archives;
- Data Protection key rings;
- copied .NET runtime files;
- local credentials and live configuration.

After builds/tests, generated output may remain locally but must stay ignored.
Cleanup scripts must distinguish source from generated `.cs` files under `obj`
and `obj-hil`.

Do not use a broad repository cleanup command in a dirty checkout unless the
operator explicitly approves the exact removal set.

## Build and test

From the repository root:

```bash
dotnet build AetherSDR-Web.slnx -c Release

dotnet test prototypes/web-client/tests/AetherSDR.Web.Tests.csproj -c Release
dotnet test prototypes/web-client/tx-hil-tests/AetherSDR.TxHil.Tests.csproj -c Release
dotnet test AetherRemote/tests/AetherRemote.Tests/AetherRemote.Tests.csproj -c Release
node --test prototypes/web-client/tests-ui/*.test.mjs
```

Relevant test classes include:

- authentication/origin/policy validation;
- radio session isolation and soak behavior;
- FLEX status/VITA parsing;
- radio admission and client roster behavior;
- TX lease, occupancy, gate, supervisor, connection-loss, heartbeat-loss, and
  process-loss behavior;
- HIL manifest/plan validation and command-surface isolation;
- AetherRemote protocol, enrollment, liveness, credential, network-boundary,
  agent, broker, and integration behavior.

Run the narrowest relevant test first, then the complete affected project, then
the combined solution when preparing a checkpoint or merge recommendation.

A test claim must include the actual command and result. A test not run is a
hypothesis, not evidence.

### Production publish inspection

TX-sensitive changes require clean production and HIL publishes to be inspected
separately. Confirm the normal production artifact has no reachable HIL key,
unkey, CW-ID, process-child, manifest, or TX-audio command path. Confirm the HIL
artifact contains only the expected purpose-bound command surface.

Do not infer this from source conditionals alone; inspect the produced artifact
as documented by the TX-HIL workflow.

## Documentation and evidence

`prototypes/web-client/MILESTONES.md` tracks acceptance evidence, not merely
implementation status.

When updating milestones:

- state exactly what was tested;
- include date, radio/frequency when relevant, command counts, ownership proof,
  final state, and cleanup result;
- distinguish automated tests, no-RF preflight, live radio status verification,
  and over-the-air operator confirmation;
- do not replace older evidence without a reason;
- do not claim repeated success when only one qualifying run exists.

Architecture changes belong in `DESIGN.md` or another reviewed design document.
Wire-contract details belong in `PROTOCOL.md`. Operational procedures belong in
README/deployment documentation, not only in chat history.

## Autonomous agent boundaries

Agents may autonomously:

- fix bugs with a clear root cause;
- improve validation and fail-closed behavior;
- add or update tests demonstrating an existing requirement;
- fix build, test, path, packaging, and platform issues;
- update documentation to match verified behavior;
- remove generated files from a migration target after validation;
- refactor locally when behavior and safety invariants remain demonstrably
  unchanged.

Agents must not autonomously:

- enable production transmit;
- weaken TX ownership, lease, intent, observer, or unkey rules;
- add a new keying surface or TX mode;
- run live RF HIL;
- change authentication/authorization architecture or default exposure;
- broaden remote network reachability;
- replace the station/broker trust model;
- introduce a new architecture, framework, runtime dependency, or protocol
  version without maintainer direction;
- change user-facing visual design or interaction behavior based only on agent
  preference;
- alter production defaults that affect all operators without explicit approval;
- delete old source, deployed configuration, credentials, rollback artifacts, or
  test evidence without a guarded and reviewed procedure.

When uncertain, preserve the safer existing behavior, provide evidence, and
surface the decision to the maintainer.

## Issue and PR claim protocol

When actively reviewing, implementing, commenting on, or recommending merge for
an issue/PR, claim it visibly before producing overlapping work.

1. Check current assignees.
2. If unassigned or assigned only to the repository's triage bot, assign
   yourself.
3. If another human or agent already holds the active claim, coordinate instead
   of duplicating work.
4. Stay assigned while working.
5. If interrupted, leave a short status note and release the claim.
6. Read-only listing or summarization does not require assignment.

Typical GitHub CLI commands:

```bash
gh issue view NNNN --json assignees
gh pr view NNNN --json assignees

gh issue edit NNNN --add-assignee @me
gh pr edit NNNN --add-assignee @me

gh issue edit NNNN --remove-assignee @me
gh pr edit NNNN --remove-assignee @me
```

Claims are mortal. Do not leave an invisible or unrecoverable lock on work.

## Git and commits

Before editing:

- inspect `git status`;
- identify whether files are tracked, untracked, generated, or locally deployed;
- avoid overwriting unrelated dirty work;
- verify the current branch/base before preparing a patch.

Before committing:

- inspect the exact staged diff;
- ensure no generated output, credential, key, token, live appsettings, or
  deployment payload is staged;
- run the relevant tests;
- cite the applicable constitution principle when relevant;
- sign every commit.

If commit signing is not configured, stop before committing and help the
operator configure SSH signing or their existing approved signing method. Verify
the resulting signature after the first commit.

Never use `git add .` blindly in a newly migrated or dirty repository.

## Evidence and operator precedence

The operator outranks every agent. Direct maintainer instructions override agent
consensus, prior-agent notes, and self-assessed completion.

Claims require evidence:

- a build must have actually run;
- a test must have actually run;
- a radio state must have been freshly observed;
- a live RF result must come from the operator-run workflow;
- a cleanup must list and verify what was removed;
- a production safety claim must inspect the production artifact.

When evidence is missing, say so. Do not convert confidence into a factual
claim.
