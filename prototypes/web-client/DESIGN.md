# AetherSDR Web Boundary Design

## Goals

- Preserve the AetherSDR interaction model: slice cards, panadapter, waterfall,
  meters, receive controls, and a clear TX surface.
- Let authenticated people use independent radio sessions concurrently,
  subject to the radio's live GUI-client admission.
- Treat every browser page as a distinct FLEX GUI client, including two pages
  signed in with the same Entra identity.
- Keep every transmit decision below the browser boundary and fail closed.
- Reuse AetherD's canonical models and binary frames once protocol v1 exists.

## Deployment shape

```text
Browser users
    |
    | HTTPS + secure cookie + same-origin WebSocket
    v
AetherSDR Web Gateway
    |-- OIDC login (Microsoft Entra ID or AD FS)
    |-- app-role authorization
    |-- per-browser/per-radio GUI session registry
    |-- radio-wide operator presence and browser backpressure
    |-- AD identity -> short-lived AetherD credential exchange
    |
    | private WebSocket / WireGuard interface
    v
AetherD at the shack
    |-- canonical radio models
    |-- per-client capability grants
    |-- single-holder TX lease and force-unkey watchdog
    |-- binary spectrum/waterfall + Opus audio
    v
FlexRadio on the station LAN
```

The browser gateway is not a second radio engine. It translates authenticated
web sessions into AetherD client sessions and renders the resulting projection.
SmartSDR TCP/VITA-49 remains entirely inside the Flex backend.

For GUI and receive-path development, the prototype includes an isolated
`FlexRx` adapter that reads FFT, waterfall, meter, and audio packets directly
from a selected radio and republishes them in the experimental browser frames.
Supported receive-only controls are mapped back to SmartSDR commands. It is not
the final gateway architecture. Production transmit remains subject to the M7
station-local ownership, command, emergency-unkey, and independent-watchdog
boundaries.

## Standalone setup foundation

M8A introduces a versioned setup model before adding a network setup surface.
The setup model keeps these concerns explicit and separately testable:

- one canonical public AetherSDR URL, normalized to an HTTPS authority with no
  user information, path, query, or fragment;
- an installation topology profile that distinguishes personal, local-gateway,
  remote-gateway, hybrid-gateway, and remote-station-node roles;
- one `InstallationPaths` configuration object covering configuration, state,
  secrets, immutable releases, backups, and logs;
- a resumable, revisioned setup document whose completed-step marker never
  advances unless the data required by that step validates;
- an independent first-run lock that can be re-issued without discarding setup
  progress and becomes complete only after the first administrator exists; and
- a short-lived random bootstrap token revealed only to the local process. Only
  its SHA-256 digest and expiry are persisted, and successful claim clears all
  token material atomically.

The supported production defaults are `/etc/aethersdr`,
`/var/lib/aethersdr`, `/var/lib/aethersdr/secrets`,
`/opt/aethersdr/releases`, `/var/backups/aethersdr`, and
`/var/log/aethersdr`. Development uses one ignored `.aethersdr` tree under the
content root. Every configured override must be absolute and every directory
role must remain distinct.

Setup state lives under the resolved state directory at
`setup/installation.json`. Writes use a complete temporary document, durable
flush, and atomic replacement. On Unix, the setup directory and state file are
required to remain mode `0700` and `0600`. Unknown fields, unsupported schema
versions, stale revisions, non-canonical URLs, invalid topology values, and
inconsistent lock state fail closed.

This first foundation slice does not add an anonymous setup endpoint, local
account provider, installer mutation, Docker support, or any executable TX
surface. Runtime setup-only startup, claim-session protection, preflight, and
administrator creation are separate reviewed increments built on this state
boundary.

The next console-only increment adds two process-local commands over that state:
a redacted setup-status report and short-lived bootstrap-token issuance. Both
commands terminate before authentication, radio discovery, station sessions,
command transport, or TX supervision are configured. Status output reports only
progress and non-secret presence flags. Token issuance writes only the digest and
expiry, prints the raw token once, and is refused when standard output is
redirected so service logs and shell pipelines cannot silently retain it. No
HTTP, WebSocket, AetherRemote, browser, timer, or startup path can issue or reveal
a bootstrap token.

The setup-workflow increment adds one typed mutation boundary for topology,
canonical public URL, paths, update channel, backup confirmation, and the
explicit TX-support installation choice. Every mutation requires a claimed lock,
the exact expected revision, and the immediately preceding validated step.
Revisiting an earlier step preserves later data only when the resulting complete
state still validates. The preflight reader requires all choices, reads an
existing state without creating or modifying it, and reports planned service
users, repository-backed packages and service units, loopback/public ports,
files, proxy work, firewall expectations, migrations, and TX-support warnings.
It applies no package, service, proxy, firewall, migration, radio, or TX change.

The local setup-CLI increment exposes that boundary without adding a network
surface. Claim reads the bootstrap token from an interactive terminal with echo
disabled; there is no token-valued command-line option and redirected input is
rejected. Each configuration command loads the current state, submits that exact
revision to the workflow, and prints only the existing redacted status projection.
The paths command records the same resolved path layout used to locate the setup
store, and preflight serializes the read-only report without advancing setup
state. Every setup command still returns before authentication, hosted services,
radio discovery, station sessions, command transport, or TX supervision are
constructed.

The first-administrator handoff adds a dedicated typed transition to the terminal
setup state. It runs the same read-only preflight, sends the exact schema,
revision, creation identity, topology, and canonical URL to a trusted verifier,
and requires evidence for one durable, enabled subject holding the exact
`Aether.Admin` role. Unknown or duplicate roles, stale or mismatched setup
identity, invalid timestamps, verifier failure, and concurrent setup changes all
leave the lock claimed. A safe retry may verify an already-created administrator
against the new exact revision. The setup document stores no subject identity,
credential, provider secret, or role list; it advances only to `Administrator`
and records the completion timestamp after verification succeeds.

No administrator provider, dependency-injection registration, console command,
HTTP route, or normal-runtime caller is included in the handoff increment.
Production local-account creation remains M8D work, while installer mutation
remains separate reviewed work.

The setup claim-session increment provides the bearer boundary required before a
future browser setup center can expose any setup mutation. The service consumes
the short-lived bootstrap token through the existing claim operation and then
returns one 256-bit process-local bearer. Only its SHA-256 digest is retained in
memory; neither token nor digest is persisted. A new successful bootstrap claim
replaces the prior session, process restart loses it, and expiry is absolute and
never slides. The bearer is bound to the exact setup schema, creation identity,
claim timestamp, and revision. After one successful setup mutation, the caller
must rotate it across exactly one revision; skipped, concurrent, completed,
expired, restarted, replaced, or malformed sessions all return the same
unauthorized result. The setup document remains unchanged by validation,
rotation, and revocation.

This increment registers no service and adds no HTTP route, cookie, browser
asset, setup-only listener, account provider, installer mutation, radio caller,
or TX caller.

The setup-only startup-planning increment combines the exact disabled legacy
configuration, unfinished setup eligibility, and completed normal-runtime
readiness into one typed decision without changing `Program.cs`. Setup-only and
normal runtime are mutually exclusive. Setup-only planning requires an existing,
valid, incomplete setup document, rejects completed setup and any selected
topology that does not run the web gateway here, and returns only the existing
redacted status projection. It never creates setup state, issues a bootstrap
token, constructs a claim session, or authorizes a network endpoint. Exact normal
runtime continues to delegate to the revision-, URL-, path-, topology-, role-,
and TX-support-bound readiness gate.

The planner registers no service and adds no configuration section, HTTP route,
cookie, browser asset, listener, account provider, installer mutation, radio
caller, or TX caller.

The setup HTTP-security increment defines the exact browser boundary that a later
setup-only host must apply. Each request is classified as an initial page read,
bootstrap claim, session read, or session mutation and is rejected for insecure
scheme, non-canonical authority or origin, cross-site fetch metadata, query
strings, unexpected or unbounded bodies, non-JSON mutation content, missing
session state, or malformed and mismatched CSRF evidence. Bootstrap claims are
limited to 4 KiB and five requests per minute; session mutations are limited to
16 KiB and thirty requests per minute. All fixed-window limit contracts use no
queue.

The same contract publishes a `__Host-` session cookie that is Secure, HttpOnly,
SameSite=Strict, path `/`, domainless, and bounded by the claim-session maximum
lifetime; a separate readable `__Host-` CSRF cookie has the same origin and
lifetime restrictions. CSRF values are independently generated with 256 bits of
entropy, encoded as canonical base64url, compared in fixed time, and redacted
from diagnostic rendering. Setup responses are no-store and carry a restrictive
CSP, no-referrer, nosniff, same-origin opener/resource policies, and a permissions
policy that disables browser device capabilities.

The policy is instantiated only by the explicit setup-only program composition.
The setup-only HTTP adapter translates it into response-header middleware, four
zero-queue fixed-window rate-limit policies, strict host-only cookie writes, and
eleven JSON-only routes. Security evaluation happens before any bounded request
body is read or deserialized. Unknown JSON members are rejected, query strings
remain forbidden, and bootstrap, session, and CSRF values are never serialized in
responses or accepted through URLs.

The setup-center application increment composes the redacted status projection,
HTTP-security policy, bootstrap claim, process-local claim session, ordered setup
workflow, and non-mutating preflight behind one endpoint-agnostic façade. Security
classification runs before state or token operations. Initial page reads return
only redacted status, the published security contract, and a fresh double-submit
CSRF value. Bootstrap claim consumes the local token only after the request passes
the canonical HTTPS/origin/fetch/body boundary, then returns one process-local
session issue plus a newly rotated CSRF value.

Session reads and preflight require the exact active bearer and setup revision.
Each repository-defined mutation type validates the same session-and-CSRF boundary,
applies one exact workflow step, requires one persisted revision advance, and then
rotates both session and CSRF authority. Once persistence succeeds, bearer rotation
is completed independently of caller cancellation so a canceled request cannot
leave a successfully advanced setup document paired with intentionally stale
browser authority. Replaced, revoked, stale, concurrent, completed, malformed, or
wrong-revision authority remains fail closed. The façade refuses completed setup
and any topology that does not run the gateway here, even if the process started
while an earlier setup state was eligible.

The setup-only program-composition increment now selects the unified host startup
plan before any normal authentication, radio, remote-station, watchdog, command,
or TX settings are read. `InstallationSetupOnly` is an explicit owned
configuration object. Disabled mode requires an empty access URL. Enabled mode
requires one exact canonical HTTPS access URL and remains mutually exclusive with
normal installation runtime. The public-URL workflow step must match that same
origin exactly.

An eligible setup-only plan registers only resolved installation paths, time, the
setup store, the HTTP-security policy, rate limiting, the setup-center application,
and a redacted composition report. The program builds the isolated host, maps
`GET /setup` plus claim, session, preflight, topology, public-URL, paths,
update-channel, backup, TX-support-choice, and revoke operations under
`/setup/api/`, and returns before normal service configuration. Composition
rejects any plan that is completed, not setup-only, missing paths or status, or
attempted after a normal authentication/radio/remote/watchdog service
registration. The default configuration remains disabled and the development
environment example preserves that default.

Session and preflight reads require the exact revision in
`X-Aether-Setup-Revision`. Mutations carry one exact expected revision in bounded
JSON, and claim or mutation success rotates both strict cookies. The HttpOnly
session bearer and readable CSRF token are written only as `__Host-` cookies;
response DTOs contain only redacted status and session metadata. Revocation first
validates the exact revision, then clears both cookies. Cleartext, foreign-origin,
cross-site, malformed, oversized, stale, and unauthorized requests fail closed.

The setup browser-shell increment maps one human-facing document at
`GET /setup/center` plus fixed CSS and module-script assets under
`/setup/assets/`. The JSON adapter and its eleven routes remain unchanged. Page
navigation passes through the existing page-read security classification, issues
only the readable CSRF cookie, and renders encoded redacted status plus resolved
default path suggestions into `data-*` attributes. The raw bootstrap token,
session bearer, persisted bootstrap digest, and CSRF value never appear in HTML.

The module implements bootstrap claim, exact session resume, topology, canonical
URL, path, update-channel, backup, TX-support-choice, preflight, and revocation
workflows. It submits credentials only in bounded JSON bodies or strict cookies,
uses no local storage, session storage, IndexedDB, inline script, inline style, or
token-bearing URL, and clears the bootstrap input before awaiting the claim. Each
mutation consumes the rotated revision and cookies returned by the existing
adapter. Preflight is rendered with DOM text nodes rather than HTML injection.

The shell stops after non-mutating preflight review. It creates no administrator,
account provider, package, service, proxy, firewall rule, migration, radio path,
watchdog path, command path, or TX caller. Process-local session loss remains
fail closed and directs the operator to issue a new bootstrap token locally,
reload, and reclaim the preserved workflow rather than manufacturing replacement
browser authority.

The M8A lifecycle-acceptance increment binds the running setup-only host to the
exact setup schema, creation timestamp, and startup revision. A setup-only hosted
monitor permits only monotonically increasing revisions for that same identity.
It stops the host when trusted first-administrator handoff completes, the selected
topology no longer runs the gateway here, the state document disappears or is
malformed, the setup identity is replaced, or the revision rolls backward.
Completion therefore disposes all process-local claim authority and a completed
installation cannot re-enter setup-only startup. Only the exact completed
normal-runtime binding may start afterward.

Automated acceptance covers the full configuration path, preflight, process
restart, old-session rejection, local recovery-token issuance, preserved-step
reclaim, trusted administrator evidence, lifecycle shutdown decision, setup-only
restart rejection, and exact normal-runtime admission. The published artifact
also carries a read-only TLS smoke script and clean Ubuntu 24.04 VM runbook. The
smoke script sends only GET requests and never claims or mutates setup. Production
administrator creation remains M8D work; native installer, proxy, service, and
firewall mutation remains M8C work.

The runtime-readiness increment defines the fail-closed binding required before a
normal runtime may admit the web gateway or a remote station node. The binding
carries the exact completed setup revision, runtime role, topology, canonical
public URL, resolved path layout, and TX-support installation choice. Evaluation
reads existing setup state without creating or modifying it and rejects
incomplete setup, stale revisions, topology or role mismatches, URL or path drift,
and TX-support installation drift. Missing or malformed setup state remains an
error rather than an implicit development fallback.

The startup-gate increment wires that check into `Program.cs` before authentication
settings, hosted services, radio discovery, station sessions, command transport,
or TX supervision are configured. `InstallationRuntime:Enabled` defaults false;
its disabled state permits only the exact empty binding defaults and does not even
resolve installation paths. When enabled for this web process, the role must be
`Gateway`, the selected topology must run a gateway here, and the exact completed
revision, canonical URL, resolved paths, and TX-support installation choice must
match persisted setup. Production standalone path resolution remains Linux-only.
The gate registers no service, endpoint, account provider, radio caller, or TX
caller and mutates no setup state.

## Signed release verification boundary

The first M8B increment defines a versioned signed-release manifest and a
fail-closed verifier over local immutable inputs only. The JSON envelope contains
one typed payload plus signature metadata. The signature covers the complete
payload together with the declared algorithm and key identifier through one
canonical UTF-8 serialization. Parsing rejects unknown fields, duplicate JSON
properties, integer enum values, comments, trailing commas, excessive depth, and
manifests larger than the bounded one-megabyte limit.

The payload binds one canonical release identity and strict semantic version to
Stable, Beta, or exact Pinned channel semantics and one supported architecture:
`linux-x64` or `linux-arm64`. It requires exactly one package identity and safe
relative package path for each of gateway/web, broker, AetherRemote agent, and
station engine. Duplicate identities, paths, or roles; missing or unexpected
roles; absolute or traversal paths; oversized declarations; local package-set
drift; length mismatch; and SHA-256 mismatch all fail closed.

Compatibility is explicit and signed. The verifier requires the local
configuration schema and protocol version to fall inside declared ranges, the
installed semantic version to satisfy the minimum previous-version transition,
and the target version to be newer. Configuration-schema changes require one
exact from/to migration declaration and a gateway restart declaration; declaring
no migration is valid only when the local and target schemas already match. A
host restart declaration must include every packaged service, preventing
contradictory restart metadata.

TX-support capability is descriptive only. Its versioned declaration must state
that verification enables no transmit function, grants no eligibility, creates
no browser TX authority, and arms no watchdog. A package may therefore be marked
TX-support-capable without changing any production TX gate, lease, ownership,
command, or watchdog state.

The verifier accepts a caller-supplied immutable public-key set and currently
supports only ECDSA P-256 with SHA-256 and fixed-width signatures. It reads no key
file, embeds no production trust anchor, and contains no signer. Its typed report
omits signature bytes, checksums, paths, and key identifiers; unverified manifest
metadata is not reflected before signature success.

This slice adds no network or GitHub client, polling loop, downloader, archive
extraction, installer, release-directory mutation, symlink switch, service
control, migration runner, backup/restore writer, CLI, Admin route, browser
control, radio caller, watchdog caller, or TX caller. Published bundles,
activation, rollback, and post-activation health checks remain separate reviewed
M8B increments.

The second M8B increment adds the production public-key trust composition without
adding an update caller. `ReleaseManifestTrust` is one strict configuration object
with a disabled default and a bounded key list. Normal-runtime startup rejects
unknown fields, unsupported algorithms, duplicate identifiers or files,
non-canonical paths, missing or oversized files, symlinks, writable-by-group or
writable-by-other Unix files/directories, multiple PEM blocks, private-key PEM,
invalid UTF-8, malformed key data, and non-P-256 keys. Setup-only startup still
returns before this normal-runtime configuration is read.

The registry copies each exact reviewed public key into one immutable verifier key
and exposes only redacted readiness diagnostics: enablement, availability, key
count, canonical key identifiers, algorithms, and short public-key fingerprints.
It does not expose configured paths or key bytes and contains no private-key or
signing method. The local verification service composes that registry with the
existing typed manifest verifier. Disabled or unavailable trust fails with a typed
report before manifest verification begins.

`Program.cs` constructs both objects at normal-runtime startup so malformed
production trust configuration fails closed even though no check/download/install
caller exists yet. Health reports only release-verification readiness and explicit
`false` values for network download, installation, and activation registration.
No package is opened from a path, no manifest is fetched, and no release, service,
configuration, radio, watchdog, command, lease, or TX state can be changed by this
composition.

The third M8B increment adds one local offline-directory verification boundary.
It accepts one canonical absolute directory containing exactly
`release-manifest.json` and four package files. The reader manually traverses a
bounded directory tree, rejects reparse points and symbolic links, requires safe
relative package paths, and rejects missing, extra, empty, or oversized entries.
On Unix, the bundle root, subdirectories, manifest, and packages must have no
owner, group, or other write bit, so the input is already immutable before it is
opened.

The manifest is copied under the existing one-megabyte bound. Packages are not
copied into process memory: each regular file is read sequentially through a
bounded buffer and reduced to an immutable relative path, exact length, and
SHA-256 digest. Length and last-write metadata are rechecked after the read, and
the root is revalidated before verification. The resulting snapshot is submitted
to the existing production-trust-backed verifier, which remains authoritative for
signature, channel, architecture, compatibility, package inventory, length, and
digest acceptance.

Normal-runtime composition registers only this typed reader service and redacted
health diagnostics. There is still no configured bundle path, startup scan,
polling loop, archive or package extraction, downloader, Admin route, browser
control, installer, staging write, release activation, symlink mutation, service
control, migration runner, backup/restore writer, radio caller, watchdog caller,
command or lease caller, or TX authority.

The fourth M8B increment adds one read-only CLI adapter around that boundary. A
separate parser owns the complete release-check option set and strips it before
ASP.NET Core receives the remaining application arguments. The command is
mutually exclusive with installation-setup commands and production-TX preflight.
It executes immediately after configuration is built and returns before setup-only
planning, authentication, hosted services, radio discovery, station sessions,
watchdog supervision, or web routing.

The operator must provide one canonical absolute bundle directory, the exact
installed semantic version, Stable/Beta/Pinned channel, the pinned identity only
for Pinned, and positive canonical configuration-schema and protocol versions.
The adapter derives `linux-x64` or `linux-arm64` from the current process rather
than trusting a caller-selected architecture. Duplicate, missing, contradictory,
or non-canonical values fail at the CLI boundary before any bundle access.

Execution constructs the same strict production public-key registry and delegates
to the existing immutable directory reader. Disabled or unavailable trust rejects
before filesystem traversal. The only output is a versioned redacted JSON report;
exit `0` means the complete signed bundle verified and exit `2` means it did not.
The report omits the bundle path, trust paths, key material, signature, package
paths, and checksums. The CLI adds no network, extraction, staging, installation,
activation, rollback, migration, service-control, Admin, browser, radio, watchdog,
command, lease, or TX method.

The fifth M8B increment adds a separate read-only `--release-status` command. It
resolves the supported installation layout from the same strict configuration,
loads the existing setup document without creating it, and requires the persisted
path object to equal the currently resolved object before reading release storage.
An incomplete, missing, malformed, permission-invalid, or mismatched setup state
fails before release-directory access.

The status reader considers only direct children of the configured release
directory. It accepts at most 64 regular non-symlink directories with exact
canonical release identities and rejects files, aliases, reparse points, unsafe
Unix group/other write permissions, and excess inventory. The sibling `current`
entry may be absent. When present it must be one canonical symbolic-link target,
either absolute or relative to the deployment root, resolving directly to one
inventoried release without traversal or escape. The reader does not descend into
release contents and does not resolve any other link.

Exit `0` means a trustworthy snapshot was produced, including an empty or inactive
installation; exit `2` means the setup or release layout was unsafe or unreadable.
The report exposes only bounded setup progress, update-channel and pinned identity,
TX-support installation intent, sorted release identities, and the active identity.
It emits no paths and always reports that no rollback candidate is known because a
durable previous-release pointer has not been implemented. The command owns no
network, extraction, staging, installation, activation, rollback, migration,
service-control, Admin, browser, radio, watchdog, command, lease, or TX method.

The sixth M8B increment adds a separate read-only offline-install preflight. Its
owned parser requires one canonical immutable bundle path, the exact active release
identity, installed semantic version, configuration-schema version, and protocol
version. Completed setup remains authoritative for Stable/Beta/Pinned selection,
Pinned identity, installation paths, and whether TX-support packages belong on the
host. Process architecture remains authoritative for `linux-x64` or `linux-arm64`.

Preflight first requires a complete setup lock and a validated `current` pointer
whose identity exactly matches the caller's installed identity. It then submits
the bundle to the same production-trust-backed immutable reader. Signature,
inventory, hashes, architecture, channel, version transition, schema, and protocol
compatibility therefore remain one shared boundary. A verified target is rejected
when its identity equals the active release, already exists in the immutable
inventory, or its descriptive TX-support capability differs from completed setup
policy.

After bundle verification, preflight reads setup, inventory, and `current` again.
Any setup revision, policy, inventory, or pointer change invalidates the result.
Success means only that a separately reviewed future transaction may consider the
bundle; it grants no installation or activation authority. The versioned report
contains no paths, signatures, checksums, key IDs, or package names. The command
owns no network, download, extraction, write, staging, installation, activation,
rollback, migration execution, service-control, Admin, browser, radio, watchdog,
command, lease, or TX method.

The seventh M8B increment adds a typed verified-installation-plan composition
without adding an installer. The verifier reparses only its own immutable manifest
copy after complete success and converts the trusted payload into defensive scalar
and package snapshots. A failed signature, compatibility, package, length, or
digest check yields no snapshot. The public verifier, offline-bundle, and preflight
reports remain unchanged and redacted.

A successful stable preflight may then be combined with resolved installation
paths. The composer independently checks preflight/snapshot identity, version,
architecture, channel, package count, TX-support capability, canonical release
identities, exact four-role package inventory, bounded lengths, safe relative
paths, unique target paths, and one direct target directory under the release
root. Its internal plan preserves signed restart, migration, release-note, package
length, and digest metadata for a future reviewed transaction. The public result
contains no paths, package names, or digests. The composer performs no filesystem
read or write and has no download, extraction, staging execution, installation,
activation, rollback, migration execution, service-control, Admin, browser,
radio, watchdog, command, lease, or TX method.

The eighth M8B increment introduces a staging-only mutation service behind that
internal plan. `VerifiedReleaseStagingService` exposes diagnostics publicly but no
public execution method, route, CLI, hosted service, timer, or background loop.
Its internal operation accepts only the verified plan. It rereads release status
before any write and requires the exact completed setup revision, channel/Pinned
selection, TX-support installation policy, active release identity, and absent
target retained by the plan.

The writer requires regular non-symlink deployment and release roots, creates or
validates one owner-private `.release-staging` sibling, and creates a unique
owner-private transaction directory. It re-enumerates the exact immutable bundle
and permits only the retained manifest plus four package paths. Each source file
is streamed once into a new destination while checking length and SHA-256 against
the retained verified digests, flushing to storage, and revalidating source
metadata. It then rechecks the complete source layout, freezes the destination
files and directories owner-only/non-writable, rehashes the frozen tree, and
rereads release status. Failure, cancellation, target appearance, or status drift
removes the transaction tree when cleanup remains safe.

A successful result carries the private staging path only in an internal artifact.
The public report contains no path. The release inventory target is not created,
`current` is not read as authority beyond status revalidation and is never
mutated, archives are not extracted, and no install, activation, rollback,
migration execution, service control, Admin/browser, radio, watchdog, command,
lease, or TX caller exists.

The ninth M8B increment adds a separate verified release publication boundary.
`VerifiedReleasePublicationService` is registered for diagnostics but exposes no
public execution method or operational caller. Its internal operation accepts only
the exact successful staging report and staged artifact, checks their revision,
identities, counts, byte total, canonical private path, and inactive/unpublished
flags, rereads release status, and independently rehashes the complete immutable
staging tree before mutation.

Publication uses one no-overwrite cross-parent directory rename from the private
staging child into the absent direct release target. Linux requires the moved
root to be owner-writable while its parent link is changed, so the service changes
only that root from owner-read/execute to owner-read/write/execute immediately
before `Directory.Move`, then restores owner-read/execute at the published path.
Every file and descendant directory remains immutable throughout. The published
tree is re-enumerated and rehashed, and a second status read must show exactly the
one target identity added while completed setup policy and the active `current`
identity remain unchanged.

Cancellation is accepted only before the atomic rename. Once the rename may have
executed, the service finishes reconciliation without honoring cancellation. A
failed rename with the source still present restores the staging-root mode. An
ambiguous or invalid post-rename state is surfaced as reconciliation-required and
is not automatically deleted because an external actor may have changed `current`.
The public report contains no path, package name, or digest. Publication does not
copy files, mutate `current`, activate, roll back, execute migrations, control
services, or touch Admin/browser, AetherRemote runtime, radio, watchdog, command,
lease, or TX state.

The tenth M8B increment adds a pure activation-transaction plan behind successful
inactive publication. `VerifiedReleaseActivationPlanComposer` requires the exact
successful publication summary and internal published-release token, including a
consumed staging source, immutable published target, unchanged `current`, no prior
activation, and no reconciliation requirement. It independently checks setup and
identity agreement, canonical semantic version and Linux architecture, Stable/
Beta/exact-Pinned policy, TX-support installation consistency, manifest/package
byte totals, the exact four unique service roles, safe package destinations, and
coherent signed migration metadata.

The internal plan derives canonical previous/target release paths, the direct
`current` path, and canonical relative link values for both the previous and target
release. It preserves signed configuration-schema, migration, restart, release-
notes, package-role, and TX-support metadata. The public result contains no path,
package name, or digest and reports only bounded identities, counts, migration,
and restart summaries.

A successful plan marks operator approval, TX-lease admission closure, fresh
radio-authoritative idle, disarmed watchdogs, configuration backup, staged-copy
migration when declared, atomic `current` switching, service health verification,
and automatic rollback as mandatory future transaction steps. It does not claim
those steps have run. The composer performs no filesystem I/O and owns no current
mutation, activation, backup, migration execution, service control, health probe,
CLI/Admin/browser, AetherRemote runtime, radio, watchdog, command, lease, or TX
caller.

The eleventh M8B increment adds a pure readiness evidence boundary without adding
an activation orchestrator. `VerifiedReleaseActivationReadinessEvaluator` exposes
only diagnostics publicly; its internal evaluation method accepts the exact
successful activation-plan result plus one bounded evidence snapshot captured no
more than five seconds earlier. It rechecks the public/internal plan agreement and
requires release status to retain the same completed setup revision, update
channel/Pinned policy, TX-support installation choice, inactive target inventory,
and previous active `current` identity.

The evidence contract requires TX-lease admission closed and an exact empty lease
snapshot. Each active session must be connected and carry fresh idle occupancy for
its own radio with no occupants, an idle/no-intent gate, a disarmed inactive safety
supervisor, no active or reconciliation-required command transaction, and a
disarmed reconciliation-free independent watchdog. The bounded global watchdog
aggregate must be registered, non-degraded, unarmed, reconciliation-free, and—when
TX support is installed—have exact running, connected, and registered counts equal
to the session count.

Readiness further requires a prepared configuration backup, the signed migration
step resolved, required service/host restart control available, post-switch health
verification available, automatic rollback prepared, and explicit operator
approval. Success retains only an internal defensive copy of the activation plan
and session evidence. The public report reveals counts and booleans but no paths,
package names, digests, radio/session/lease identifiers, occupants, or watchdog
process details. No evidence collector, route, CLI, Admin/browser caller, hosted
service, timer, filesystem write, lease mutation, radio/watchdog command, pointer
mutation, activation, backup, migration, service control, health probe, rollback,
AetherRemote, command, lease, or TX execution path is added.

The twelfth M8B increment adds the first authoritative runtime evidence collector
without adding an activation caller. `VerifiedReleaseActivationEvidenceCollector`
accepts only the exact successful activation plan and is registered for diagnostics,
but its collection method remains internal. It reads release status before and
after one bounded runtime observation window and rejects any setup, inventory, or
`current` drift. The full collection must complete within the evaluator's
five-second freshness limit.

TX leases are read through a new internal lock-consistent observation snapshot.
Unlike the existing operational snapshot, it does not expire stored leases or emit
lease-change events, so observation cannot improve readiness by mutating state.
Radio-session diagnostics are projected into the existing bounded session-safety
evidence, and the independent-watchdog aggregate is captured from its authoritative
registry. Release inventory, lease, and session collections are defensively copied
before the internal token is retained.

The collector deliberately supplies false for every prerequisite without an
implemented authoritative source: configuration backup, required migration
execution, required service/host control, post-switch health verification,
rollback readiness, and operator approval. Only a signed no-migration or no-restart
plan may satisfy the corresponding no-op prerequisite. The public report exposes
counts and booleans but no paths, inventory, radio/session/lease identifiers,
occupants, watchdog process data, package names, or digests. There is still no
filesystem write, pointer mutation, activation, lease mutation, radio/watchdog
command, backup, migration, service control, health probe, rollback,
CLI/Admin/browser, hosted-service, timer, AetherRemote, command, lease, or TX caller.

The thirteenth M8B increment adds the first authoritative TX-lease admission
closure boundary without adding an activation orchestrator.
`VerifiedReleaseActivationLeaseQuiescenceBoundary` composes one opaque internal
transaction token from the exact verified activation-plan object. Public summaries
are revalidated against that internal object, and independently composed tokens—even
for equivalent plan metadata—are not interchangeable.

Closing admission is serialized by the existing `TxLeaseManager` lock used for
acquisition and renewal. The first exact token may establish the station-wide
closure; a different active token fails closed. While closure is active, new lease
acquisition and lease renewal are rejected at the manager boundary. Exact-owner
validation and release remain available, and no existing lease is force-released.
Normal acquisition and renewal behavior is unchanged when no closure transaction
exists.

The boundary observes closure state and the stored lease set under the same lock.
Observation does not expire leases, publish change events, command a radio, mutate
a watchdog, or infer radio state. Existing leases must release or be removed by the
ordinary bounded expiry/watchdog lifecycle before drain is satisfied. Even after
drain, radio-authoritative idle, session safety, watchdog state, backup, migration,
service, health, rollback, and operator approval remain independent evidence.

The evidence collector consumes this exact-plan closure observation atomically
with the lease snapshot. A closure owned by an equivalent-but-distinct activation
plan is reported open for the supplied plan. Public health diagnostics separate
plan composition, closure authority, active state, acquisition/renewal suppression,
drain evaluation, force-release absence, lease-mutation absence, radio-idle
non-inference, operational callers, and activation authority. The boundary exposes
no public close or evaluate method and has no CLI, Admin, browser, HTTP, WebSocket,
hosted-service, timer, AetherRemote, command, radio, watchdog, TX, or activation
caller.

The fourteenth M8B increment adds a pure exact-plan configuration-backup planning
boundary without adding a backup executor or activation orchestrator.
`VerifiedReleaseActivationConfigurationBackupPlanner` accepts only a successful
non-mutating activation-plan result that still retains the exact internal plan
object. Public-summary fields are revalidated against that object before any path
composition occurs.

The planner consumes the resolved installation layout and requires its release root
to equal the activation plan's verified release root. Configuration, state, secret,
release, backup, and log roots must already be canonical absolute non-root paths and
must not overlap one another. Configuration, state, secret, backup, and log roots
must also remain outside the activation deployment root. The plan maps exactly the
dedicated configuration, state, and secret roots into separate children of one
private staging identity under the backup root, with a separate final publication
path and manifest path. The internal source list is defensively copied and the
retained exact activation-plan reference is not replaceable by equivalent metadata.

This increment deliberately performs no source existence check, content read,
staging-directory creation, manifest or backup write, permission mutation,
publication rename, overwrite, readiness evidence, current-pointer mutation, or
activation. Atomic publication and an immutable manifest are requirements recorded
for the future executor, not claims that a backup exists. Public reports expose
only plan identities, counts, and booleans; all installation and backup paths remain
internal. Health diagnostics separate path/source/identity/manifest/atomic planning
from absent reads, writes, mutation, overwrite, execution, evidence, operational
callers, and activation authority. The planner exposes no public composition or
execution method and has no CLI, Admin, browser, HTTP, WebSocket, hosted-service,
timer, AetherRemote, service-control, radio, watchdog, command, lease, TX, or
activation caller.

The fifteenth M8B increment adds a private exact-plan configuration-backup
executor without adding an activation orchestrator or operational caller.
`VerifiedReleaseActivationConfigurationBackupService` accepts only the successful
planning report and its retained internal plan. It revalidates public summary,
canonical backup layout, exact activation-plan identity, completed setup, inactive
target inventory, and the unchanged previous `current` identity before any source
read.

Execution is Linux-only and requires the dedicated backup root to exist as an
owner-private non-link directory. Configuration, state, and secret roots are
traversed without following symbolic links or reparse points and are bounded to
512 directories, 4,096 files, 128 MiB per file, and 1 GiB total. Group/other write
permissions are rejected everywhere; secret directories and files reject all
shared permissions. The service snapshots source paths, lengths, timestamps, and
modes, copies each file once into create-new mode-0600 private staging while
computing SHA-256 and flushing to storage, then re-enumerates and rehashes the full
source set. Any layout, metadata, permission, length, or digest drift removes the
private staging tree and fails closed.

A bounded manifest records only source kind, safe relative path, entry type,
length, and digest plus the exact setup and release identities—never absolute paths
or file content. The manifest is flushed, every backup file is frozen mode 0400,
every directory is frozen mode 0500, and the complete tree is rehashed before an
absent final identity is atomically renamed into place. Existing staging and final
identities are never reused, removed, or overwritten. An ambiguous rename or any
post-publication validation/status failure retains the tree, marks reconciliation
required, and withholds readiness evidence.

Successful publication retains one in-memory evidence token bound by reference to
the exact activation-plan object. The evidence collector may observe that token but
cannot execute a backup; an equivalent independently composed plan cannot reuse it.
The public executor surface exposes only diagnostics and redacted state. No CLI,
Admin, browser, HTTP, WebSocket, hosted-service, timer, AetherRemote, migration,
service-control, health-probe, rollback, current-pointer, activation, radio,
watchdog, command, lease, or TX caller is added.

The sixteenth M8B increment adds a pure exact-plan migration composition boundary
without selecting a migration program or adding an executor.
`VerifiedReleaseActivationMigrationPlanComposer` accepts only the successful
activation-plan result and successful configuration-backup report while requiring
both retained internal objects. It revalidates every public summary field, exact
activation-plan reference, immutable backup counts and manifest digest, successful
non-overwriting atomic publication, and the canonical backup transaction layout.
Equivalent independently composed plans or backups are not interchangeable.

A signed `None` declaration composes one exact no-op plan with no migration paths or
runner requirement. A signed `Required` declaration must retain an increasing
from/to configuration-schema transition ending at the target schema, a bounded
ASCII migration identity, and the signed gateway restart. The composer maps only
the immutable `configuration`, `state`, and `secrets` backup children into a
separate migration staging tree, final result tree, and manifest path beneath the
same setup-revision backup root. Those paths are canonical, distinct, and outside
the immutable source backup and activation deployment tree.

The boundary records that a future required migration needs a staged copy, runner,
manifest, and non-overwriting atomic publication. It does not decide which target
release component may run the migration, inspect source existence, read or copy a
byte, create a directory, change permissions, execute migration logic, or provide
required-migration readiness evidence. Public reports expose schema numbers and
booleans but no migration identity, backup digest, or path. Health diagnostics keep
planning separate from absent runner selection, reads, writes, mutation, execution,
evidence, current-pointer authority, activation authority, and operational callers.
No CLI, Admin, browser, HTTP, WebSocket, hosted-service, timer, AetherRemote,
service-control, health-probe, rollback, radio, watchdog, command, lease, or TX
caller is added.

The seventeenth M8B increment adds a separate local runner-trust and exact-selection
boundary without adding migration execution. `ReleaseMigrationRunnerTrust` is one
strict feature-owned configuration object whose default disables selection and
contains no runners. `ReleaseMigrationRunnerTrustRegistry` bounds the trust set to
eight artifacts, sixteen migrations per artifact, and sixty-four exact declarations
overall. Runner identity, protocol version, canonical path, SHA-256 pin, signed
migration identity, and increasing from/to schema pair are all validated before the
registry becomes available.

Registry startup reads each configured artifact once. The artifact must be a regular
non-link file from 1 byte through 16 MiB in one regular non-link containing directory.
On Linux the directory may not be group/other writable, and the artifact must be
owner-readable and owner-executable with no user, group, or other write bit. Length,
last-write time, Unix mode, and path safety are checked before and after hashing; any
drift or digest mismatch fails startup. Duplicate runner identities, canonical
paths, digests, or signed migration identities are rejected. The registry retains
defensive immutable metadata and exposes only counts and booleans publicly—never a
path, runner identity, signed migration identity, or digest.

`VerifiedReleaseActivationMigrationRunnerSelector` is a pure internal exact-plan
boundary. It accepts only the successful migration-plan report and retained exact
plan, revalidates report flags and the exact backup/activation binding, and resolves
a signed no-migration declaration without trust. A required declaration must match
exactly one trusted migration identity plus from/to schema pair. Success retains one
internal token containing the exact migration-plan reference and startup-validated
runner metadata. It does not reopen the runner, read backup content, invoke a
process, write a staged copy, execute a migration, produce readiness evidence,
change `current`, or authorize activation. Required migration therefore remains not
ready until a separately reviewed executor revalidates and invokes the artifact.
No CLI, Admin, browser, HTTP, WebSocket, hosted-service, timer, AetherRemote,
service-control, health-probe, rollback, radio, watchdog, command, lease, or TX
caller is added.

The eighteenth M8B increment adds a separate probe-only process invocation boundary
without supplying migration paths or performing migration execution.
`VerifiedReleaseActivationMigrationRunnerInvocationService` accepts only the exact
successful runner-selection report and retained internal selection. It rechecks the
public/internal selection agreement, no-op versus required declaration, exact
runner/mapping/schema binding, and startup validation metadata before any process is
started.

For required migrations the runner artifact is reopened immediately before launch.
The service revalidates canonical path, containing directory, link status, regular-
file shape, immutable Linux mode, exact length and timestamp, and SHA-256 using a
fixed-time digest comparison. Drift prevents process creation. The reviewed artifact
is then started directly with `UseShellExecute=false`, no arguments, redirected
stdin/stdout/stderr, a cleared environment, and a fixed working directory. Only
locale and migration-runner protocol variables are restored.

Protocol version 1 is one probe request and one response. The bounded request
contains exact setup/release, runner, migration, and schema identities plus explicit
`MigrationExecutionRequested=false` and `MigrationSourcePathsProvided=false` flags.
No configuration, state, secret, backup, staging, publication, manifest, or
deployment path crosses the process boundary. Stdout is capped at 16 KiB, stderr at
8 KiB, stderr must be empty, and a five-second timeout or oversized channel kills
the process tree.

The strict response parser rejects unknown fields and requires exact protocol,
request nonce, runner identity, migration identity, and schema echoes plus explicit
false values for migration execution, filesystem mutation, and source-path receipt.
A successful probe provides no migration-readiness evidence and retains no process
output, request nonce, runner path, digest, or migration identity in public reports.
Production resolves only diagnostics and has no route, CLI, hosted service, timer,
or activation orchestrator caller. Current-pointer mutation, staged-copy creation,
migration execution, evidence, service control, rollback, radio, watchdog, command,
lease, and TX authority remain absent.

The nineteenth M8B increment introduces the separately bounded staged-copy mutation
and evidence service. `VerifiedReleaseActivationMigrationExecutionService` accepts
only the exact successful probe report and its retained internal selection token.
Required migration execution is Linux-only, single-use per service lifetime, and
starts with release-status agreement plus a complete digest-backed revalidation of
the immutable configuration backup. Every manifest directory and file is bounded,
canonical, non-link, permission-checked, length-checked, and hash-checked before it
is copied into a new mode-0700 staging identity.

The immutable backup is read-only input. The selected runner is revalidated again
immediately before launch and receives only the private staging root and its copied
configuration, state, and secret children. No live configuration path, immutable
backup path, deployment root, release pointer, or credential value crosses the
process boundary. The direct no-shell protocol requires the runner to affirm staged-
copy mutation while denying backup-source receipt, `current` mutation, activation,
service control, radio commands, and TX commands. Stderr, stdout, and runtime are
bounded, and failure kills the process tree and removes private staging when the
publication outcome is still known.

After a strict success response the host independently traverses the staged tree,
rejects links and host-manifest collisions, hashes every bounded file, writes and
durably flushes one host-owned manifest, freezes the entire tree, and validates it
before and after an atomic directory rename. Any ambiguous rename or post-publish
validation drift freezes the remaining tree and marks reconciliation required.
Existing staged or published identities are never overwritten.

A successful no-op or required execution retains one internal evidence object bound
by reference to the exact activation plan. The activation evidence collector can
mark migration readiness only from that exact observation; equivalent public fields
cannot manufacture readiness. Health remains path-, identity-, digest-, and content-
redacted. Production registers diagnostics and zeroed state only, with no execution
caller, route, CLI, Admin/browser entry, hosted service, timer, AetherRemote,
`current` mutation, activation authority, service control, health probe, rollback,
radio, watchdog, command, lease, or TX surface.

The twentieth M8B increment adds a separate exact service-control transaction-plan
boundary without adding service control. `VerifiedReleaseActivationServiceControlPlanComposer`
accepts only the successful activation-plan report and retained exact plan. It
revalidates the complete public/internal agreement, bounded restart-service count,
host-restart declaration, mandatory gateway restart for required migration, and all
future activation obligations before retaining a plan token.

The supported unit map is fixed in code to the repository-owned gateway, broker,
AetherRemote agent, and station-engine units. For ordinary service restarts, the
plan stops in dependency-facing order—gateway, broker, agent, engine—and starts in
the reverse order after a future pointer switch. A host restart is accepted only
when every service restart is signed and supersedes the individual actions with one
post-switch host-restart marker. A declaration with no service or host restart
resolves as an exact no-op.

Public reports expose restart and action counts plus phase booleans, never unit
identities or the internal host-action marker. Production resolves the planner's
diagnostics only. There is no process start, shell, `systemctl`, D-Bus, systemd,
host-restart execution, service-control evidence, current-pointer mutation, health
probe, rollback, activation authority, CLI, Admin, browser, HTTP, WebSocket, hosted
service, timer, AetherRemote, radio, watchdog, command, lease, or TX caller.

The twenty-first M8B increment adds a separate exact post-switch health-verification
plan without adding a health probe. `VerifiedReleaseActivationHealthVerificationPlanComposer`
accepts only the successful service-control report and retained internal plan. It
revalidates that plan's exact activation object, signed restart shape, action counts,
fixed unit identities, deterministic stop/start ordering, complete four-role package
coverage, and the still-unperformed activation obligations.

The internal health plan always covers station engine, broker, AetherRemote agent,
and gateway in dependency order. Every role requires its fixed unit to be active.
Station engine, broker, and gateway additionally require loopback-only `GET /healthz`
with HTTP 200 under bounded 45/30/45-second deadlines; gateway verification also
requires the runtime canonical host header. The agent requires one fresh broker-link
observation under a bounded 60-second deadline. A host-restart declaration marks the
same complete contract set as post-boot verification; all other plans remain post-
switch verification.

Public reports expose target and contract counts plus phase booleans, never unit
identities, ports, paths, endpoint authorities, or contract internals. Production
resolves diagnostics only. There is no socket, network request, `HttpClient`, process,
`systemctl`, journal read, health evidence, current-pointer mutation, rollback,
activation authority, CLI, Admin, browser, HTTP, WebSocket, hosted service, timer,
AetherRemote, service-control, radio, watchdog, command, lease, or TX caller.

The twenty-second M8B increment adds a separate disabled-by-default health execution
boundary. `VerifiedReleaseActivationHealthVerificationService` accepts only the
successful health-plan report and exact retained health and activation objects. It
requires the target identity to be the active `current` release before and after the
bounded sequence and double-reads both release status and completed setup. The
persisted topology and canonical public URL bind host ownership and the gateway Host
authority; arbitrary probe destinations are never accepted.

Topology is authoritative. Personal and local-station gateways run gateway, broker,
and station engine locally and do not run an agent, so the agent target resolves as a
signed topology no-op. Hybrid gateways run the same three local services and must
configure one exact remote station identity for a fresh broker-link observation.
Remote-station gateways are rejected because their station engine is remote and no
reviewed remote health-probe protocol is registered. Remote-station nodes cannot run
this gateway-owned boundary. The executor never sends a local process check for a
remote role.

The Linux runtime invokes absolute `/usr/bin/systemctl` directly. Broker and station
engine use `is-active --quiet <fixed-unit>`; the gateway user unit uses
`--user is-active --quiet <fixed-unit>`. There is no shell, output is redirected and
bounded, timeouts kill the process tree, and the environment is cleared. The only
optional user-service environment restored is a canonical `/run/user/<uid>` runtime
directory and its exact matching D-Bus address. HTTP contracts are fixed to local
loopback ports and `/healthz`, HTTP/1.1, no proxy, no cookies, no redirects, no
decompression, bounded headers and body, and strict JSON `status=ok`. The hybrid
agent contract consumes the existing read-only runtime broker snapshot and requires
one fresh uniquely matching configured station ID, heartbeat, and inventory
observation. It reads no runtime or administration credential and no journal.

A successful one-shot sequence retains an internal observation bound by reference to
the exact activation plan. The evidence collector observes but never calls this
executor. Readiness status is explicitly phase-aware: before health evidence,
`current` must remain the installed release; with exact post-switch health evidence,
`current` must be the target release. Public diagnostics and reports remain unit-,
endpoint-, host-, station-, path-, and credential-redacted. Production registers no
executor caller and keeps execution disabled, unavailable, and zero-state by default.
No service control, pointer mutation, rollback, activation authority, radio,
watchdog, command, lease, or TX action is added.

The twenty-third M8B increment adds a separate disabled-by-default two-phase service-
control execution boundary. `VerifiedReleaseActivationServiceControlExecutionService`
accepts only the exact successful service-control plan. Its pre-switch phase requires
the installed identity to remain active before and after deterministic stops. Its
post-switch phase requires the same retained pre-switch plan token and requires the
target identity active before and after deterministic starts. Completed setup,
topology, release inventory, channel, TX-support choice, and release-root binding are
double-read and must remain stable around each phase. Pointer mutation is deliberately
external to this boundary.

Topology determines whether an action is local, an explicit no-op, or unsupported.
Personal and local-station gateways control the local gateway user unit plus broker
and station-engine system units; their absent agent is a topology no-op. Hybrid or
remote-gateway plans that require a remote agent or engine action fail before any
process because no remote service-control protocol is registered. Host-restart plans
also fail closed and never invoke reboot or shutdown.

The Linux runtime uses only absolute `/usr/bin/systemctl` with exact fixed unit
identities and exact `stop`/`start` verbs. The gateway action includes `--user`; all
other supported local actions use system scope. `UseShellExecute` is false, stdout and
stderr are redirected and bounded, the environment is cleared, and a hard timeout
terminates the process tree. No action is automatically repeated. Once any process
starts, every ambiguous, failed, cancelled, or drifted phase enters reconciliation-
required state and blocks further execution.

A completed post-switch phase retains one in-memory observation bound by reference to
the exact service-control and activation plans. The evidence collector observes this
state but never invokes either phase. The health executor accepts only the same exact
completed service-control plan before probing the active target. Public diagnostics
redact units, topology, paths, and action internals. Production registers diagnostics
and zeroed state only with execution disabled and no operational caller. No `current`
mutation, host restart, remote command, rollback, activation authority, radio,
watchdog, lease, command, keying, or TX action is introduced.

The twenty-fourth M8B increment adds a separate disabled-by-default atomic pointer
boundary. `VerifiedReleaseActivationCurrentPointerSwitchService` accepts only the
exact service-control plan and its retained pre-switch evidence token. The installed
release must remain active while completed setup, channel, pinned identity, TX-support
choice, release root, release inventory, and exact installed `current` link are
double-read. A no-op service-control plan is eligible only through its exact ready
observation; restart plans require the exact stop-phase token. Host-restart plans are
not eligible.

Before mutation, the entire target release is traversed with bounded directory and
file counts. The tree must contain exactly one signed manifest plus the four planned
package files. Every entry must be a regular non-writable file or non-writable real
directory; symbolic links, reparse points, empty nested directories, unsafe relative
paths, unexpected or missing files, and manifest/package path, length, or SHA-256
drift fail closed. The
pointer operation accepts no arbitrary source path, destination path, or link value.

The Linux runtime creates one unpredictable `.current-switch-*` symlink in the same
deployment directory with the exact relative target value. Native `rename(2)` then
atomically replaces the existing `current` symlink. The temporary entry must be
consumed, the new link must be exact, the target release must be active, setup must
remain unchanged, and the complete immutable tree must still validate. The operation
retains no alternate mutable pointer and performs no service or host action.

Any unknown atomic outcome, cancellation after rename begins, post-switch observation
drift, or inability to remove a pre-switch temporary entry enters reference-bound
reconciliation state and blocks retry. A successful switch retains one in-memory
evidence object bound to the exact activation, service-control, and pre-switch tokens.
Post-switch service starts require that exact report, and health execution requires
the exact switch observation before any local process, loopback request, or broker
snapshot read. Equivalent public metadata or independently composed plans cannot
advance either boundary.

Production resolves only disabled diagnostics and zeroed state. There is no CLI,
Admin, browser, HTTP, WebSocket, hosted-service, timer, AetherRemote, or operational
pointer caller. The boundary does not start services, restart the host, control a
remote node, probe health, roll back, authorize activation, operate a radio, mutate a
lease or watchdog, send a command, key, or transmit.

The twenty-fifth M8B increment adds a pure exact rollback-plan boundary.
`VerifiedReleaseActivationRollbackPlanComposer` accepts the exact activation plan,
immutable original configuration backup, migration plan, service-control plan, and
health-verification plan. The retained backup, migration, service, and health objects
must all reference the same activation transaction; public summaries are insufficient
and equivalent independently composed objects cannot be combined.

The rollback strategy is restore-based, never reverse-migration-based. The original
immutable backup remains authoritative for configuration, state, and secrets even when
the signed target required migration. Each source is mapped to its exact original live
root plus one same-parent restore-staging identity and one distinct displaced-live
identity. These names permit a future executor to perform local atomic directory
replacement without staging beneath immutable backup or release storage.

The plan reuses the fixed deterministic service stop/start orders and complete bounded
health contracts. A future rollback sequence must stop target services, restore all
three live roots from the original backup, atomically return `current` from the target
link to the installed link, start installed services, and verify installed-release
health. Host-restart transactions fail closed because no reviewed reboot/rollback
transport exists. No arbitrary path, unit, health endpoint, or migration runner is
accepted.

This boundary performs no read, write, directory mutation, process, systemd command,
network request, health probe, pointer mutation, rollback execution, or activation.
It emits no readiness evidence, exposes only path- and unit-redacted diagnostics, and
has no CLI, Admin, browser, HTTP, WebSocket, hosted-service, timer, AetherRemote,
radio, watchdog, command, lease, or TX caller.

The twenty-sixth M8B increment adds the disabled-by-default exact rollback executor.
It requires the exact rollback-plan object, successful forward pointer-switch evidence,
and an eligible failed post-switch service-start or health-verification report. These
reference-bound prerequisites prevent an arbitrary rollback command and keep
pre-switch, successful, equivalent, or independently composed transactions ineligible.

The immutable backup manifest advances to schema 2 and retains each original Unix
mode. Revalidation checks the exact manifest digest, activation identities, source
counts and bytes, unique safe entries, immutable copied-tree modes, content hashes,
and safe original configuration/state/secret modes. All three restore trees are copied
and rehashed before any service or live-root mutation.

The executor supports only gateway-owned personal, local-station, and hybrid topology
contracts already reviewed by service control and health verification. It directly
stops local target units, atomically displaces and replaces each live root from its
same-parent staging tree, atomically returns `current` to the installed release, starts
local installed units, and verifies installed unit/loopback/canonical-host plus optional
exact remote-agent broker-link health. Reverse migration is never used.

A process outcome, directory rename, pointer rename, health result, setup/status drift,
cancellation, or cleanup ambiguity after mutation retains exact reconciliation state
and blocks automatic retry. Displaced failed trees are deleted only after complete
installed-release health. Successful rollback evidence remains separate from forward
activation `RollbackReady`; production resolves only disabled diagnostics and zeroed
state and registers no operational caller or activation/TX authority.

The twenty-seventh M8B increment adds a separate exact operator-approval authority.
`VerifiedReleaseActivationOperatorApprovalAuthority` is disabled by default, exposes
only diagnostics publicly, and retains at most one internal approval for the exact
activation-plan object. Approval requires current authentication, administrator
authorization, and reauthentication within a configured 30-to-600-second window. The
default is 300 seconds. Equivalent plan objects, stale or malformed authentication
evidence, duplicate active approvals, and malformed approval identities are rejected.

The retained approval has a random internal identity, exact administrator subject
binding, issue time, expiry time, and revocation state. Public reports and health
output disclose none of the approval or subject identities. Expired approvals are
unavailable and replaceable; revocation is reference-bound and idempotently rejects a
second attempt. The evidence collector observes only an exact fresh approval and
otherwise supplies `OperatorApproved=false`.

Approval evidence does not authorize or execute activation. Production registers no
issuer, CLI, Admin, browser, HTTP, WebSocket, hosted-service, timer, AetherRemote,
command, lease, radio, watchdog, or TX caller. The boundary owns no file, pointer,
backup, migration, service-control, health-probe, rollback, keying, or RF mutation.
An authenticated Admin issuer and the activation transaction orchestrator remain
separate later M8B boundaries.

The twenty-eighth M8B increment adds the first network-backed release source without
adding a persistent downloader or activation caller. `GitHubReleaseBundleSource` is
disabled by default and owns one strict public GitHub repository identity. It lists a
bounded release page, rejects malformed metadata, ignores drafts, and selects only a
canonical `aethersdr-<semantic-version>` tag whose GitHub prerelease state agrees with
the exact Stable/Beta selection, or whose identity exactly matches Pinned policy.

For the process-derived Linux architecture, the release must contain one exact
architecture-named manifest asset and one exact asset for each of gateway, broker,
AetherRemote agent, and station-engine roles. Asset API URLs remain bound to the
configured repository. Metadata, asset count, names, state, lengths, optional GitHub
SHA-256 digests, timeout, and redirect count are bounded. Automatic redirects are
disabled; each HTTPS redirect is validated against the small reviewed GitHub download
host set before it is followed.

The source writes the five responses once into one random owner-private temporary
bundle, flushes each file, freezes the complete tree, and delegates every signature,
identity, channel, architecture, compatibility, role, length, and package digest
decision to the existing local immutable signed-bundle verifier. The signed release
identity must equal the selected GitHub tag. The temporary tree is removed on success
and failure; cleanup ambiguity converts the result to failure.

`GitHubReleaseBundleCheckConsole` is the only caller and runs before any host, service,
radio, or watchdog composition. Public reports and health expose only bounded counts,
booleans, outcomes, and trusted release summaries—never repository URLs, redirect
URLs, temporary paths, package names, digests, signatures, or key material. Production
persists no download and adds no archive extraction, staging, installation, pointer
mutation, service control, activation, rollback, Admin/browser, AetherRemote runtime,
radio, watchdog, command, lease, keying, or TX caller.

The twenty-ninth M8B increment adds a separate release-production trust boundary. The
running web application remains verification-only. `AetherSDR.ReleaseBuilder` is a
standalone build-time executable granted internal access only to the canonical release
contract serializer and verifier. It accepts one canonical architecture asset
directory, exact release/channel/compatibility metadata, one owner-only PKCS#8 ECDSA
P-256 key file, and one new exact manifest output path. It reads no application
configuration, service state, radio state, credentials, or deployment target.

The signer requires exactly four regular architecture archives and rejects links,
missing or extra files, empty or oversized packages, metadata drift during hashing,
non-canonical semantic transitions, contradictory channel/version selection, unsafe key
permissions, non-PKCS#8 material, non-P-256 keys, existing output, and non-atomic writes.
It creates the same canonical signing bytes used by production verification, signs with
P1363 ECDSA/SHA-256, exports only the public verification material in memory, and submits
the complete generated manifest back through `SignedReleaseManifestVerifier`. Output is
created only after that self-verification succeeds. Private bytes and decoded key
characters are cleared; reports omit key paths, package paths, digests, and signatures.

`build-github-release-assets.sh` composes the build boundary for `linux-x64` and
`linux-arm64`. It uses deterministic .NET build settings, normalizes generated file
modes, packages sorted trees with fixed numeric ownership and one commit-derived mtime,
and disables gzip timestamps. The current gateway and station engine both use the same
reviewed web/watchdog tree, so their role archives are byte-identical by construction.
Both architecture trees are inspected for the approved production web/watchdog command
string counts, forbidden HIL/TX markers, and disabled configuration before signing. Ten
read-only assets are promoted into one new output directory only after both manifests
self-verify.

The manual `draft-release.yml` workflow may run only from `main`, requests the protected
`release-signing` environment, reruns the production validation-only gate, injects the
private key from one environment secret into a mode-0600 temporary file, builds the ten
assets, removes the key, and creates a GitHub draft at the exact workflow commit. It
never publishes an immutable release automatically. The workflow adds no runtime signer,
updater polling loop, persistent download, installation, activation, service control,
radio, watchdog, command, lease, keying, or RF authority.

The thirtieth M8B increment adds `GitHubReleaseBundleDownloadService` and its one
CLI-only adapter. The persistence boundary derives exactly one inventory root as the
direct `release-downloads` child of `InstallationPaths.StateDirectory`; it adds no new
operator-selected path. Linux requires the existing state root and the derived download
root to be regular canonical non-symlink directories with no group/other write bits. A
missing download root may be created owner-private, but a missing or unsafe state root
fails before any network request.

`GitHubReleaseBundleSource` now has an internal acquisition operation shared by both
callers. The read-only check caller always deletes the verified temporary acquisition.
The persistent caller configures the source temporary root to the download inventory,
so all five exact GitHub assets are downloaded, flushed, frozen, signed-verifier checked,
and identity-matched in a random same-parent directory. Disabled source or unavailable
public-key trust is rejected before creating the inventory root.

After verification, the persistent target name is the trusted release identity plus the
process architecture. An absent target is created only by same-parent atomic directory
rename. An existing target is accepted only after the same immutable offline verifier
proves exact identity, version, architecture, and channel agreement; otherwise it is
retained and the new temporary acquisition is removed. Clean rename failure removes the
source tree. A completed rename followed by an error is accepted only after exact target
reverification. Missing/both/unsafe post-rename states that cannot be proven return a
redacted reconciliation-required result rather than retrying or deleting evidence.

The persisted directory remains an offline signed bundle. The service does not open or
extract any package archive, copy package contents into release staging, create an
installation plan, mutate `current`, control services, activate, roll back, migrate,
issue approval, or expose an Admin/browser, hosted-service, timer, AetherRemote runtime,
radio, watchdog, command, lease, keying, TX, or live RF caller.

The thirty-first M8B increment adds a separate callerless archive-extraction trust
boundary. `VerifiedReleaseArchiveExtractionService` accepts only an internal successful
`VerifiedReleaseStagingReport` whose immutable source path, plan identity, package count,
byte count, setup revision, and no-publication flags all agree with its retained
`VerifiedStagedRelease`. A raw bundle path, downloaded path, archive path, public
execution method, CLI, route, hosted service, startup hook, timer, or retry loop cannot
reach extraction.

The source staging tree must remain the exact direct child of `.release-staging`, be
owner-private and non-writable, and contain only the verified manifest plus four signed
archives. Each compressed file is rechecked against the retained signed length and
SHA-256 digest before gzip decompression. Source layout, timestamps, lengths, and all five
digests are checked again after extraction so mutation during the operation fails closed.

The extractor uses `System.Formats.Tar` but owns the policy above it. It accepts only GNU
tar directories and regular files with bounded safe relative paths. Links, devices,
unsupported records, absolute paths, traversal, backslashes, controls, duplicate
file/directory names, excessive path depth or length, excessive entry/file/directory
counts, oversized files, excessive expanded bytes, malformed streams, and nonzero
trailing decompressed content are rejected. Valid bounded zero GNU-tar record padding is
drained explicitly. Archive-provided ownership and shared permission bits are ignored;
only the owner execute bit is projected into the private output.

One random extraction transaction tree is created under the direct deployment child
`.release-extraction-staging`. It contains the copied signed manifest and fixed role roots
`gateway-web`, `broker`, `aetherremote-agent`, and `station-engine`. Files are streamed
once, flushed, hashed, set to owner-read or owner-read/execute, then the complete tree is
frozen owner-only and rehashed against its internal extraction inventory. Setup,
installation policy, release inventory, active pointer, and target absence are
revalidated before and after extraction. Cancellation and failure remove only a fully
validated no-link private tree; unsafe cleanup remains explicit evidence.

The internal success artifact carries the extraction path and expanded-file digests for
a later reviewed publication/install transaction, while the public report exposes only
bounded counts, bytes, identities, booleans, and outcomes. No extracted tree is published
or installed; `current`, services, backups, migrations, approvals, Admin/browser,
AetherRemote runtime, radio, watchdog, command, lease, keying, TX, and RF state remain
untouched.

The thirty-second M8B increment adds a pure extracted-publication plan boundary without
adding an executor. `VerifiedReleaseExtractedPublicationPlanComposer` accepts only the
successful extraction report plus its retained `VerifiedExtractedRelease`. Summary setup,
identity, package/file/directory counts, bytes, manifest, immutable-tree, no-publication,
and no-cleanup fields must agree exactly with that token.

The source installation plan is revalidated for canonical release identities, semantic
version, architecture, channel/Pinned selection, TX-support agreement, exact four-role
archive metadata, and direct deployment/release/target paths. The extraction source must
be one canonical direct child of `.release-extraction-staging` named for the exact target
identity plus a lowercase 128-bit transaction suffix. The target remains the exact absent
direct release child from the verified installation plan.

Every extracted file is converted into an internal source/target mapping only after
requiring a safe unique relative path, bounded length, SHA-256 metadata, and exact parent
directory accounting. The copied manifest must be non-executable and match the retained
signed manifest length and digest. Every other file must stay below the fixed root for its
own gateway-web, broker, AetherRemote-agent, or station-engine role, and each required
role must contain at least one file. Total bytes and directory inventory must exactly
match the extraction token. Owner-executable intent is retained for a future publication
executor; archive ownership and shared permissions never reappear.

The public result contains no path, file name, digest, package identity, or executable
name. Composition performs no filesystem I/O and registers no archive execution, write,
rename/publication execution, current mutation, activation, rollback, migration, service,
CLI/Admin/browser, radio, watchdog, command, lease, or TX caller. The existing
archive-copy publication service is not invoked.

The thirty-third M8B increment adds the matching callerless atomic executor.
`VerifiedReleaseExtractedPublicationService` accepts only the exact internal composition
result and revalidates its summary, installation plan, canonical source/target paths,
archive-package target binding, manifest binding, role ownership, parent-directory
inventory, byte totals, digests, and executable intent before reading local status or
mutating a path. Normal runtime registers diagnostics only; no public execution method,
CLI, route, hosted service, timer, startup hook, Admin/browser adapter, or AetherRemote
caller exists.

The execution boundary requires Linux, completed setup, exact update-channel and
TX-support policy, the installed release still active through unchanged `current`, an
absent target, shared deployment/release roots with no group/other write authority, and
one exact owner-private writable `.release-extraction-staging` parent. The extracted
transaction itself must remain an exact owner-only immutable tree: every directory is
mode 0500, every data file is 0400, every retained executable is 0500, links and
non-regular entries are rejected, and all paths, counts, lengths, timestamps, and
SHA-256 values are checked before rename.

Publication performs no copy and reopens no archive. It temporarily makes only the
transaction root 0700, uses one `Directory.Move` into the direct inactive release path,
then sets the published root back to 0500 and validates the complete target tree with the
same exact mappings. Status must change only by adding the target identity to the release
inventory; setup and active `current` remain unchanged.

Rename outcomes are explicit. Source-present/target-absent after an exception is a clean
failure only if the source root can be re-frozen. Source-absent/target-present is accepted
as completed only after immutable-tree and status revalidation. Both-present,
both-missing, unreadable paths, tampered target bytes or modes, or post-rename status
drift require reconciliation and never trigger retry or deletion. Public reports expose
only identities, bounded counts/bytes, publication booleans, and reconciliation state.

The successful internal token retains the exact published extracted-tree plan for a later
activation-plan adaptation. This increment does not switch `current`, activate, back up,
migrate, control services, issue approval, or touch Admin/browser, radio, watchdog,
command, lease, keying, TX, or RF state.

The thirty-fourth M8B increment composes those callerless boundaries into the operational
transaction while preserving their token identities. Extracted activation adaptation
retains the full immutable file/directory inventory and binds each service role to its
fixed published role root. The current-pointer executor therefore proves every file,
digest, path, directory count, and 0400/0500 owner mode before switching, rather than
validating only the earlier five compressed bundle files.

One `ReleaseUpdateTransactionCoordinator` owns a serialized exact transaction. Prepare
performs preflight, installation-plan composition, verified staging, extraction, inactive
publication, activation adaptation, configuration backup, staged migration, service and
health planning, and rollback planning. Activate consumes fresh approval, closes lease
admission, waits for natural lease drain, collects radio-authoritative session/radio/
watchdog evidence, executes pre-switch service stop, pointer switch, post-switch service
start, health verification, and final readiness. It reopens lease admission only with the
same closure authority and revokes approval. Post-switch failure invokes the exact
retained rollback plan; manual rollback requires new approval and the original successful
pointer/rollback tokens. No step has radio-command, lease-force-release, watchdog-arming,
keying, or TX authority.

The coordinator runs in `aethersdr-release-updater.service`, a separate hardened process
that is not one of the services being restarted. Gateway and terminal callers use a fixed
owner-private Unix socket under installation state. The protocol has only prepare,
activate, rollback, and status operations with bounded length-prefixed JSON. An atomic
owner-only journal records redacted phase/identity state. Terminal state can be reported
after process restart; interrupted nonterminal state becomes reconciliation because
object-reference authority is deliberately not reconstructed from disk.

The Admin surface requires the existing Admin policy plus antiforgery on every mutation.
The browser supplies a canonical release identity, never a filesystem path. The server
derives the architecture-specific verified download inventory child and derives approval
subject, role, and authentication time from the principal. Only a hashed subject binding
crosses the local supervisor socket. CLI mutations additionally require an interactive
terminal, an explicit approval switch, and typed exact release/transaction confirmation.

Remote service control is not an arbitrary broker command. The AetherRemote protocol
allows only two phases, two actions, two service roles, two reviewed unit names, one exact
release identity, and a 128-bit correlation ID. The broker authenticates administration
and correlates the result to the same station connection. The agent validates the frame
and forwards it to a separate owner-private `AetherRemote.Updater`; only that daemon calls
direct `systemctl --user` for the fixed units. Both station capability and execution
configuration default disabled.

Host restart is a separate process-lifetime boundary. For one signed host-restart plan,
the transaction coordinator bypasses ordinary service-stop execution, performs the exact
atomic pointer switch, restores lease admission, revokes in-memory approval, and journals
the transaction as `RestartPending`. The reboot transport then requires that transaction
identity and pointer evidence, writes one owner-only marker bound to transaction, setup
revision, installed/target identities, release root, current-pointer path, update channel,
pinned identity, and TX-support policy, and sends one direct nonblocking systemd reboot
request. An unconsumed marker cannot be overwritten.

After the rebooted gateway has started listening, a hosted continuation double-reads
setup and release status, requires the exact target to remain active, and executes only
the fixed bounded unit, loopback-health, and topology-required fresh broker-link checks.
It writes an owner-only terminal result, atomically moves the exact matching transaction
journal from `RestartPending` to `Completed`, and then removes a successful marker.
Failure moves the journal to `ReconciliationRequired` and retains the marker. Recovered
pending state blocks another installation. Failure, journal mismatch, staleness,
tampering, observation drift, or an existing failed result remains explicit
reconciliation and is never retried automatically. Approval, post-reboot rollback,
pointer-switch, service-control, radio-command, lease, watchdog, and TX authority are not
reconstructed from durable state.

Release publication is separated from signing. `draft-release.yml` remains the only
private-key workflow and creates one protected draft. `publish-release.yml` is manual,
main-only, and protected independently. It receives only the public verification key,
requires one exact existing draft/tag/commit and ten exact assets, verifies both
architecture bundles with the production offline verifier, then changes only the draft
flag and proves target plus asset name/size/digest inventories are unchanged.

## Trust boundaries

### Browser

The browser is untrusted for authorization and TX safety. Disabled controls are
only a usability affordance. Every intent is re-authorized on the server.

### Web gateway

The gateway validates OIDC issuer/signature through the ASP.NET Core handler,
uses role claims for policy, validates WebSocket origin and message size,
allows only enumerated intent/property combinations, and bounds each client
queue.

The production gateway is allowed to request AetherD capabilities on behalf of
an authenticated user, but it is not allowed to manufacture capabilities.

### AetherD

AetherD is authoritative for the radio session, radio state, client
capability grants, TX lease, and force-unkey behavior. A malicious gateway or
browser must not be able to bypass those checks.

## Role and capability mapping

| AD app role | Gateway permission | Maximum AetherD grant |
|---|---|---|
| Observe | Subscribe to state and streams | Observe |
| Control | Send non-keying shared intents | Control |
| Transmit | Request TX lease | TX eligible, not automatically keyed |
| Admin | Manage sessions/policy | Explicitly configured administrative set |

`Aether.Transmit` is necessary but insufficient for transmit. The user must
also acquire the one active physical-radio lease, the engine must report TX
capability, the operator must deliberately initiate keying, and all interlock
checks must pass. SmartSDR, Maestro, hardware PTT, and other external FLEX
clients remain independent TX actors. FLEX `local_ptt` identifies which GUI
client owns local-PTT authority; it does not prove RF is keyed. A key request
therefore requires one fresh, exclusive Local PTT owner matching the exact
AetherSDR GUI handle, plus an idle radio-authoritative `interlock` state. Actual
TX ownership and every forced-unkey decision use `interlock` plus
`tx_client_handle`; AetherSDR may never force-unkey an external owner.

## Session isolation and client projections

The radio remains authoritative for live state and client admission. The
prototype registry creates one aggregate per browser page and physical radio
endpoint. That aggregate owns a unique FLEX GUI client ID, coordinator, command
router, radio connection, slices, panadapters, and audio stream. Two pages
signed in as the same user therefore consume two radio GUI sessions, just as
two desktop clients would. A WebSocket reconnect from the same page reuses its
aggregate; no other page receives its session ID or state. New clients receive:

1. Protocol/capability `welcome`.
2. Full session snapshot.
3. Ordered model deltas.
4. Bounded binary stream frames where latest frame wins.

Operator presence is deliberately outside those per-browser aggregates. A
radio-keyed presence registry publishes one row per authenticated identity to
every session using the same physical radio. Multiple browser connections from
one identity are aggregated into a connection count; no slice, panadapter,
audio, or control state crosses the session boundary.

Administrators receive a read-only projection of each aggregate for diagnosis:
GUI/client identity, current transport and stream IDs, owned panadapters and
slices, last frame times, and browser queue pressure. The projection reads
existing session state only; it cannot mutate radio state or manufacture a
capability.

Discovery-reported `available_clients` and `mf_enable` values are displayed as
hints only because UDP discovery can be stale. The live `client gui` response
is the admission decision. A rejected page waits and retries without evicting
or taking over an existing SmartSDR, Maestro, or web GUI client.

If a client detects a version gap, it requests a fresh snapshot rather than
guessing or replaying stale local state over the radio.

## TX state machine

The M7 foundation implements a process-wide lease authority keyed by physical
radio, bounded opaque lease IDs, expiry/disconnect/session cleanup, and a
station-local occupancy registry driven by FLEX `interlock` state plus
`tx_client_handle`. The same fresh observation carries Local PTT authority, so
an idle radio cannot be keyed through AetherSDR while SmartSDR owns Local PTT.

A browser-inaccessible station TX gate now models key-pending, radio-confirmed
keyed, unkey-pending, and fault states. It requires the exact lease, session,
browser, FLEX handle, fresh idle interlock, and exclusive AetherSDR Local PTT
authority. A 100 ms private watchdog reconciles lease loss and bounded unkey
retries. Unknown network outcomes retain the guarded intent until the radio
interlock resolves ownership. Through Phase 2S, the real `xmit 1`/`xmit 0`
adapter was compiled only when `EnableTxHil=true`, and normal production
publishes contained neither command string. Phase 2T adds a separate reviewed
production adapter behind disabled configuration, an exact radio allowlist, and
the still-disabled command gate. Production therefore remains receive-only with
`CanTransmit=false`.

The Phase 2A production lifecycle registers the accepted command gate, safety
supervisor, and authentication/engine/gateway monitors once per isolated radio
session, but only behind purpose-built unavailable transports. The command gate
is always constructed with transmit disabled, the supervisor remains disarmed,
and no arm, key, unkey, microphone, TUNE, or CW caller is registered. A bounded
single-reader observation queue records exact gateway instance, engine instance,
session, browser connection, authentication, local FLEX handle, and lease
changes. Queue failure releases only that session's lease and marks the lifecycle
faulted. The read-only lifecycle snapshot is included in administrative session
diagnostics, while Admin-only `/api/admin/diagnostics/health` proves that the
lifecycle is registered and both
command transport and supervisor arming remain unavailable.

Phase 2B adds monotonic exact-identity observation sequences and timestamps for
the gateway, browser authority, station FLEX heartbeat, and lease. Every parsed
message on an admitted browser WebSocket refreshes only its current connection
identity, and every successful station FLEX ping refreshes only the exact
connected FLEX handle. Browser freshness reflects the ClaimsPrincipal admitted
for that WebSocket; it does not independently refresh or revalidate an Entra
token mid-socket. Mismatched browser IDs and handles are ignored. An exact
authenticated-to-unauthenticated browser activity transition immediately
releases only that browser's physical-radio lease and is forwarded to the
accepted authentication-loss monitor. These observations are diagnostic and
authority-revoking only; they cannot arm the supervisor or reach either
unavailable transport. The admin session grid renders the gate/supervisor state,
per-boundary sequence counts, timestamps, and continued absence of TX transports.

Phase 2C adds a one-second, in-process stale-authority watchdog. A tracked lease
remains fresh only while the exact admitted browser principal has been observed
within six seconds, the exact connected FLEX handle has completed a station
heartbeat within ten seconds, and gateway activity has been observed within ten
seconds. Explicit engine or gateway disconnect releases the exact tracked lease
immediately; a stale boundary releases it on the next watchdog evaluation.
Mismatched or untracked browser leases are never released. Later fresh
observations update diagnostics but cannot recreate the revoked lease or TX
authority. This watchdog is an authority-revocation layer inside the running
gateway, not the future independent emergency-unkey process, and it cannot arm,
key, unkey, or reach either unavailable production transport.

Phase 2D introduces the first separate-process boundary without moving radio
authority into it. `AetherSDR.TxWatchdog` is a standalone console host with no
reference to the web gateway, TX gate, occupancy registry, HIL assembly, or FLEX
transport. Its versioned local stdio protocol accepts only bounded `status`,
`register`, `heartbeat`, and `disconnect` observations. Registration binds one
exact radio/session/browser/gateway/engine/connection/lease/FLEX-handle tuple and a
strictly increasing sequence; mismatched or stale observations are rejected.
Every new OS process creates a new host instance, starts empty and Disarmed, and
never restores or infers the prior process's observation state. The host keeps
the opaque lease ID only for internal exact-equality checks; wire responses
expose `leaseBound` and never echo the lease or full identity. The production
package contains the executable for independent artifact inspection, but the web
service does not launch or connect to it yet. It has no timer, lease operation,
arming operation, radio connection, command transport, or emergency action.

Phase 2E makes that process boundary live without adding a radio boundary. Each
isolated radio session supervises exactly one watchdog child inside the web
service's existing least-privileged systemd cgroup. Standard input and output
remain the private IPC transport; no listener, network socket, shared file, or
persistent authority store is introduced. The session starts the child before
its receive transport, validates a new empty `Disarmed` status, and only then
continues receive startup. A missing or invalid child degrades watchdog health
and is retried, but it does not block receive-only operation.

The gateway registers the child only after the exact browser, gateway, engine,
FLEX handle, and opaque lease identity are all current. Ordinary browser,
station-engine, and gateway observations can heartbeat only a Disarmed
registration; they cannot arm or renew a safety deadline. Phase 2V's
lifecycle-owned transaction participant alone may send an exact arm, safety
heartbeat, or disarm. Authority loss sends an exact disconnect. A Disarmed child
may be reset, but an armed child remains alive and disconnected until its
heartbeat deadline so controlling-process loss cannot erase the safety arm.
Child exit, malformed response, request-ID mismatch, stale or mismatched
identity, timeout, or reconciliation-required outcome publishes a loss event and
revokes only the tracked physical-radio lease.

The gateway parses child responses with the same strict 4096-character boundary
as requests. Protocol version 2 permits only `Disarmed`, `Armed`, `Unkeying`, or
`ReconciliationRequired` with internally consistent registration, deadline, and
bounded one-shot outcome fields. The process has no key, unkey, lease, reset,
retry, or arbitrary-command request. Its optional TCP adapter sends the fixed
`xmit 0` only after fresh client/interlock status names the exact protected
handle as current TX owner; idle or mismatched ownership sends no command. After
dispatch, the arm clears only when the matching response and a fresh radio-idle
interlock observation both arrive. Missing idle confirmation is an unknown
outcome and remains reconciliation-required.

The first browser-integration increment exposes only a separately configured
ownership lease. `Radio:BrowserTxLeaseEnabled` defaults to false and is distinct
from the reserved `Radio:AllowTransmit` switch. The gateway derives lease
eligibility from its authenticated role set, exact live connection state, fresh
radio-authoritative occupancy, and the process-wide physical-radio lease. The
welcome message keeps the compatibility keying capability false and separately
reports lease eligibility plus explicit false values for keying, microphone,
TUNE, and CW. A lease cannot reach the hidden command gate and is not operator
intent to transmit.

Phase 2F adds deliberate browser intent validation without adding command
execution. TX ownership messages use their own strict version-1 envelope,
JavaScript-safe positive request/sequence numbers, monotonic per-WebSocket
sequence, bounded replay set, exact opaque lease ID, and unique intent ID. A
reconnect discards the browser's lease secret and starts a new sequence; the
server remains authoritative for disconnect release and expiry. Unknown fields,
duplicate JSON properties, non-object roots, stale sequence, replayed intent ID,
invalid duration, malformed lease ID, and invalid action payload fail before
authority evaluation. The browser bounds outstanding TX requests to 16 and
cannot generate an intent ID without a cryptographic random source.

The validation boundary never trusts a browser-supplied radio, session, user,
role, client, engine, FLEX handle, lease holder, occupancy, or capability. It
re-derives the current authenticated connection and requires the exact lease,
fresh idle occupancy, matching production lifecycle connection and FLEX handle,
and the same registered, connected, lease-bound Disarmed watchdog epoch. Only
then can a deliberate `mox.set`, `ptt.set`, `tune.set`, `microphone.set`, or
`cw.send` request become `validated`. The only successful Phase 2F terminal
outcome is still `transport-unavailable`; the result is `ok:false`, no command
gate method is called, and no radio transport exists.

The browser keeps the real MOX, TUNE, and CWX controls hidden and disabled unless
the server separately grants the corresponding executable capability. Phase 2F
grants only `intentValidationAvailable` after exact authority. A separate,
clearly labeled validation-only panel may acquire/release the lease and submit a
dry-run intent; it is hidden under the default production configuration. A
renewal may extend authority only while fresh idle occupancy and the exact
watchdog-bound lifecycle still hold. Authority loss releases that exact lease as
`renewal-authority-lost`. The browser also discards its local secret if renewal
is rejected, an unsupported lease-event version arrives, or no exact renewal
response is confirmed before the current server expiry. Local PC microphone
metering remains browser-only and no microphone samples enter the TX protocol.
Admin diagnostics show the lease holder name, expiry or revocation reason, and
latest validated/denied intent outcome without exposing the opaque lease ID.

Phase 2G seals a separate station-local command boundary without registering a
radio command adapter. It uses a deterministic version-1 signing payload and
ECDSA P-256 verification over the exact command ID, monotonic sequence, bounded
issue/expiry times, station, radio, web session, browser client, lease, gateway
instance, engine instance, protected FLEX handle, action, and enabled value.
The boundary revalidates fresh authentication, lifecycle authority,
radio-authoritative idle occupancy, exclusive Local PTT authority, and an exact
freshly Armed safety-supervisor identity before an adapter can be called. Replay,
clock, signature, identity, lease, occupancy, and supervisor failures consume no
radio command path. Audit records are bounded and store only a short lease
fingerprint, never the opaque lease secret or signature.

Production constructs this boundary disabled with no verification key, no
adapter, no arming capability, and no set-transmit capability. It has no browser,
HTTP, WebSocket, AetherRemote, watchdog, or timer entry point. Health and Admin
diagnostics expose only those fail-closed capability bits. Unit tests may use an
in-memory recording adapter to prove that only a fully signed and exactly bound
command reaches the adapter interface; this adapter is never registered in a
production publish.

Phase 2H adds an immutable station-scoped public-key trust ring without adding a
command source or transport. `StationTxCommandTrust` owns the complete setting:
verification enablement plus at most four key ID/path entries for bounded key
rotation. Startup loads every configured trust anchor even while verification is
disabled, so an invalid staged key cannot remain latent until activation. Each
anchor must be an exact ECDSA P-256 SubjectPublicKeyInfo `PUBLIC KEY` PEM in a
bounded regular file and regular containing directory that are not writable by
group or other users. Direct symbolic links, relative path segments, duplicate
IDs or paths, private keys, unsupported curves, multiple PEM blocks, trailing
data, malformed UTF-8, unknown configuration properties, and oversized files
fail startup. Invalid key IDs are rejected without echoing their untrusted text
into startup errors.

The singleton registry owns and disposes the imported public keys. Per-session
command boundaries receive only its verifier interface; they do not receive key
paths, key bytes, a signer, or a method that accepts an envelope. When reviewed
configuration enables verification, health and Admin may report `signature
available`, but `boundaryEnabled`, `commandAdapterRegistered`, `armingAvailable`,
and `setTransmitAvailable` remain false. This deliberately proves trust-anchor
readiness independently from command reachability.

Phase 2I adds a separate station-scoped private signing authority without adding
a command source or destination. `StationTxCommandSigning` owns one enable bit,
one canonical key ID, and one absolute private-key path. A configured key is
loaded even while signing is disabled. The file must be one exact UTF-8,
unencrypted PKCS#8 ECDSA P-256 `PRIVATE KEY` PEM in a bounded regular,
non-symlink file; Unix mode must be 0400 or 0600 and the immediate containing
directory cannot be writable by group or other users. Public-only keys,
encrypted keys, other curves, extra PEM blocks, trailing data, invalid UTF-8,
unknown properties, path indirection, and unsafe permissions fail startup.

The singleton authority owns and disposes the private key and serializes signing
under one lock because the imported `ECDsa` object is not shared concurrently.
Its internal request contains only the exact station/radio/session/browser/
lease/gateway/engine/FLEX tuple, the supported action, and its boolean value.
The authority itself supplies a canonical command UUID, a strictly increasing
process-local sequence, current issue time, five-second expiry, configured key
ID, and base64url P-256/SHA-256 signature over the existing version-1 payload.
Diagnostics expose only enablement, readiness, key ID, and a short public-key
fingerprint. The private path and private material never leave the authority.

Production resolves this authority at startup solely to validate configuration
and publish fail-closed health bits. The signer is not injected into a radio
session, lifecycle, command boundary, browser route, HTTP/WebSocket endpoint,
AetherRemote path, watchdog, or timer. There is no externally reachable
envelope-submission method, and the boundary, adapter, arming, and set-transmit
capabilities remain false. This proves private-key readiness independently from
both command reachability and public-key verification readiness.

Phase 2J adds a station-scoped internal envelope coordinator.
`StationTxCommandEnvelopeCoordinator` owns one submission enable bit and defaults
false. The singleton receives the signer and trust verifier only; it does not own
a radio boundary or adapter. Its public surface exposes diagnostics only. The
internal submission method requires a caller-owned boundary, one server-owned
`StationTxCommandAuthority`, and one fresh already-validated operator intent.
Only MOX/PTT Boolean intent is accepted; TUNE, microphone, and CW remain outside
SetTransmit. Intent IDs are canonical, intent sequence is positive, and
observation age is limited to five seconds with one second of future clock skew.

The coordinator derives every signed identity and Boolean value from the
validated intent plus authority; callers cannot supply an envelope, signature,
key ID, command ID, command sequence, or timestamp. A bounded in-memory replay
tracker consumes each intent ID once and requires strictly increasing intent
sequence for each session/browser owner. Cancellation, unknown adapter outcome,
boundary rejection, or signing failure never makes that intent retryable. Before
signing, the coordinator requires submission enabled, signer and verifier ready,
an enabled caller boundary, registered adapter, arming capability, and
SetTransmit availability. It then self-verifies the generated fixed-width P-256
signature against the station trust ring before the boundary independently
revalidates the envelope and exact authority.

Phase 2K adds one internal `StationTxCommandSessionComposition` to every radio
session. `RadioSessionRegistry` passes the station-scoped coordinator into the
session lifecycle through an internal submitter interface. The lifecycle owns
its existing disabled command boundary and the composition attaches that exact
boundary to the coordinator. Neither `RadioCoordinator` nor the WebSocket
endpoint receives the coordinator, submitter, composition, or submission
method.

The composition request contains only the current WebSocket connection ID, the
already-parsed browser intent, its positive JavaScript-safe sequence, and the
server observation time. It derives the station-command identity, canonical
radio, session, stable browser-page identity, exact active connection-owned
lease and expiry, gateway instance, engine instance, and FLEX handle from the
lifecycle. The gateway instance remains the station identity already owned by
the lifecycle command boundary. Radio-authoritative occupancy and the safety
snapshot are read directly from their station-owned registries. A browser cannot
supply or override any command-authority field.

Connection replacement, missing or mismatched lease, lease expiry, stale
browser/engine/gateway observations, missing FLEX handle, unsupported action,
missing Boolean value, cancellation, or authority-resolution failure stops
before coordinator submission. The composition does not retry an unknown or
faulted submitter outcome. Its diagnostics report whether coordinator, boundary,
authority, and submission are available plus bounded attempt/forward/outcome
counts; lease IDs, signatures, key paths, and key material are not exposed.

Production now reports coordinator and per-session composition registration,
but submission remains disabled, the attached boundary remains disabled, and
signer, verifier, arming, and SetTransmit capabilities remain unavailable under
default configuration. The Phase 2M adapter is registered only because its
executor terminates at the disabled command gate. There is still no browser,
HTTP, WebSocket, AetherRemote, watchdog, or timer submission caller, so the
external envelope-submission route remains absent and no FLEX command or RF path
can be invoked.

Phase 2L adds one `StationTxCommandAdapterComposition` beneath each session's
signed command boundary. It implements `IStationTxCommandAdapter`, treats a
validated command as a request rather than fresh authority, and re-resolves the
current lifecycle-owned authority immediately before delegation. The session
registry, radio coordinator, WebSocket endpoint, AetherRemote, watchdog, and
timers do not accept the executor type.

Phase 2M adds one lifecycle-owned `StationTxCommandGateExecutor` implementing the
internal executor contract. A validated SetTransmit true command maps only to
`StationTxCommandGate.RequestKeyAsync`; false maps only to
`RequestUnkeyAsync`. The executor owns no FLEX router, safety supervisor, lease,
occupancy registry, browser route, retry loop, or timer. Gate rejection remains
a known adapter rejection, while the two unknown command-outcome codes remain
unknown so radio-authoritative reconciliation continues in the gate.

The adapter composition independently checks the exact station, canonical radio,
web session, stable browser identity, active lease and expiry, gateway, engine,
FLEX handle, authentication/freshness flags, and matching freshly Armed safety
identity. A key request additionally requires fresh idle occupancy and exclusive
Local PTT for that exact handle. An unkey request instead permits only already
idle state or fresh proof that the exact handle is the single AetherSDR TX owner.
External, ambiguous, stale, or replaced ownership stops before the gate. The
command must remain inside its signed lifetime, and mismatch, capability loss,
cancellation, rejection, unknown outcome, or exception never causes an executor
retry. Diagnostics publish only attachment/readiness and bounded
attempt/forward/outcome counts.

Production constructs the gate with `allowTransmit:false` and the unavailable
command transport. Consequently the gate executor and command adapter report
registered, while executor arming, SetTransmit, boundary execution, and envelope
submission remain false. The HIL-only FLEX command transport is not linked into
the normal production path.

Phase 2N adds one lifecycle-owned `StationTxSafetyArmComposition` around the
existing supervisor. Its request records contain no station, radio, session,
browser, lease, gateway, engine, or FLEX-handle fields. A request may carry only
the current connection identity plus a bounded heartbeat timeout or abort
reason. The composition re-resolves the complete `StationTxCommandAuthority`
from lifecycle state, validates it against the supervisor and fresh occupancy,
and asks an optional internal `IStationTxSafetyArmAuthority` to authorize the
exact operation before forwarding one call to the supervisor. It performs no
retry and does not expose a lifecycle method or external route.

Arm requires a current authenticated lease, fresh browser/engine/gateway
observations, fresh idle occupancy, exclusive Local PTT for the protected handle,
and a Disarmed supervisor on the same radio. Heartbeat requires the exact active
arm; while idle it also requires Local PTT to remain exact, and while transmitting
it requires the protected handle to be the fresh single AetherSDR owner. Abort
requires the exact active arm and permits only already-idle state or that same
exact transmit owner. External, ambiguous, stale, expired, replaced, or
mismatched authority stops before the supervisor. An idle abort clears only the
matching arm without a radio command.

Phase 2O attaches one lifecycle-owned `StationTxSafetyArmAuthority`. Its
capability snapshot reads the signed command boundary, adapter composition, gate
executor, command gate, supervisor, and a newly resolved lifecycle authority.
It independently compares the complete station/radio/session/browser/lease/
gateway/engine/FLEX-handle tuple before any authorization. Arm requires the full
normal command path plus idle/Local-PTT readiness. Heartbeat requires that path
to remain ready and the safety identity to remain exact and fresh. Abort remains
independent of normal command-path availability so a later capability loss
cannot remove the ownership-safe abort decision; it still requires the exact
active arm and idle or exact single-owner AetherSDR TX state.

Production reports the authority attached and registered, but the signed
boundary is disabled, the gate has `allowTransmit:false`, command and emergency
unkey transports default unavailable, and no operation caller exists.
Diagnostics therefore keep arm, heartbeat, abort, boundary execution,
SetTransmit, and submission unavailable with zero attempts. Both supervisors
remain Disarmed; the independent watchdog may report a configured unkey-only
transport but has no invocation request. No browser, HTTP, WebSocket,
AetherRemote, reconnect, or timer caller can invoke the composition.

Phase 2P adds one lifecycle-owned `StationTxCommandTransactionComposition`
above the safety-arm and signed-command compositions. It accepts only a current
connection identity, one already-validated MOX/PTT Boolean intent with sequence
and observation time, and a bounded heartbeat timeout. It serializes all
operations through one lane and resolves lifecycle authority before arming,
after arming, and before later active-transaction operations. Browser input can
never supply a radio, session, lease, gateway, engine, FLEX handle, safety
identity, signature, command ID, or envelope.

A key transaction arms once, verifies that the stable station/radio/session/
browser/lease-expiry/gateway/engine/FLEX-handle tuple is unchanged and that the
new safety identity is exact, then submits one signed command. Known rejection
performs one ownership-safe abort cleanup. Unknown command outcome,
cancellation, or exception retains the arm and moves diagnostics to
`reconciling`; no automatic retry or success inference occurs. A second key is
rejected while a transaction is active.

An unkey transaction requires that exact active transaction, refreshes one
safety heartbeat, submits one false command, and clears the arm only after
confirmed acceptance. Known rejection retains the arm. Unknown command or
cleanup outcome retains it for reconciliation. Explicit heartbeat and abort
operations remain internal and exact-connection-bound. Production constructs
the composition for diagnostics only: submission, boundary, gate, and transports
remain disabled, no operation caller exists, and key, heartbeat, unkey, and
abort capabilities all remain false with zero attempts.

Phase 2Q removes the older internal lifecycle method that delegated directly to
the command-session composition. The lifecycle now exposes only three internal,
typed transaction operations: submit a validated key/unkey intent, refresh the
exact active transaction heartbeat, or abort the exact active transaction. Each
method delegates immediately to `StationTxCommandTransactionComposition` and
returns its accepted/rejected/unknown result. No method returns a command-session
result, and no registry, coordinator, WebSocket, HTTP, AetherRemote, watchdog,
reconnect, timer, or browser type receives a transaction request or result.
Production still has no caller, and all operations stop at disabled prerequisites
before arm or command forwarding.

Phase 2R places a typed browser-intent ingress adapter inside the lifecycle but
leaves it execution-disabled. The adapter requires the parsed request and the
server validation result to match exactly by sequence, intent ID, and action. It
also requires the validation-only outcome and current intent-validation capability,
rejects validation older than two seconds or more than one second in the future,
accepts only Boolean MOX/PTT, derives the five-second transaction heartbeat bound
server-side, checks current key/unkey capability, forwards at most once, and
preserves unknown outcomes for reconciliation. TUNE, microphone, CW, missing or
mismatched values, and stale/unavailable validation or transaction capability fail before the
transaction boundary. No coordinator, WebSocket, HTTP route, reconnect path,
timer, watchdog, or AetherRemote type receives the adapter, request, or result.

Phase 2S introduces one pure production-readiness policy rather than allowing
individual callers to infer readiness from partial capabilities. The policy
consumes existing configuration and live infrastructure facts only: transmit and
browser-lease configuration; coordinator attachment and submission; signing and
verification; boundary, adapter, gate, command transport, SetTransmit, and
emergency-unkey availability; safety-arm authority registration; and independent
watchdog supervision, process, IPC, unkey transport, and arming state. It returns
one readiness decision plus a deterministic complete list of missing
prerequisites. It owns no lease, browser identity, transaction, retry, or radio
operation. The lifecycle also gains one internal typed ingress operation that can
only delegate a `BrowserTxTransactionIngressRequest` to the Phase 2R adapter.
At the Phase 2S checkpoint, production kept that adapter execution-disabled and
exposed no caller; Phase 2Z later binds its single WebSocket caller conditionally.

Phase 2T introduces one production-primary command transport without connecting
it to browser execution. `StationTxCommandTransport` is one owned configuration
object with a disabled default, an exact bounded radio allowlist, and a bounded
command timeout. A local `FlexRx` session constructs the adapter; remote and
simulation sessions are ineligible. The adapter remains unavailable unless the
feature switch is enabled, the exact normalized radio ID is allowlisted, the
FLEX command router is attached, and the router has a non-zero client handle.
Every send receives the exact expected handle from the command gate. The router
checks that expected handle while holding the same lock that captures the
control session, preventing a detach/reconnect race from redirecting a command
to a replacement FLEX client. The adapter performs one send only, distinguishes
known FLEX rejection from unknown socket/timeout outcomes, propagates caller
cancellation, and bounds untrusted result text.

The primary adapter is registered in the lifecycle but the Phase 2T command gate
is still constructed transmit-disabled. Browser ingress remains execution-
disabled and callerless, and signing/submission/boundary prerequisites remain
disabled.

Phase 2U adds two separate unkey-only transports. The per-session emergency
adapter shares the exact-handle FLEX router but exposes only
`RequestUnkeyAsync(expectedProtectedClientHandle)`. The independent watchdog
adapter owns a minimal TCP client with no arbitrary-command or key method; its
only encoded radio command is `xmit 0`. The web process supplies the watchdog
endpoint only after global enablement, exact radio allowlisting, and local
`FlexRx` eligibility all match.

Phase 2V adds a separate disabled arming switch and protocol-v2 one-shot deadline
controller. `StationTxIndependentSafetyArmParticipant` wraps the existing
lifecycle safety participant inside the transaction composition. It resolves the
exact watchdog identity from current lifecycle authority, arms the independent
process before the local supervisor, renews it only from transaction safety
heartbeats, and disarms it only after local radio-confirmed Disarmed state. A
local-arm failure attempts to clear the independent arm; a rejected or unknown
independent unkey remains reconciliation-required. No browser, HTTP, WebSocket,
AetherRemote, reconnect, or ordinary lifecycle heartbeat receives these methods.

Phase 2W introduces a read-only production activation composition between the
lifecycle and the Phase 2S readiness policy. The composition owns no authority,
configuration, lease, transaction, or radio operation. Its only dependency is a
provider for the current typed infrastructure prerequisites, and every snapshot
re-evaluates the deterministic readiness policy rather than caching a prior
result. Diagnostics distinguish composition attachment from activation
availability and preserve the policy's exact first blocking reason. At the Phase
2W checkpoint, production health declared the composition registered, activation
unavailable, reason `transmit-disabled`, and no registered activation caller.

Phase 2X inserts a feature-owned static configuration interlock ahead of that
composition. `StationTxProductionActivation:Enabled` is a request to assemble
reviewed configuration, not an execution switch. When requested, startup
requires local `FlexRx` mode, explicit transmit and browser-lease opt-ins,
configured trust and signing keys, envelope submission, allowlisted primary and
emergency transports, and supervised watchdog unkey plus arming. Missing fields
fail startup in deterministic order. The default unrequested state is valid and
keeps the activation composition unavailable at `activation-not-requested` while
retaining the nested dynamic readiness result for diagnosis. No caller, command,
lease, gate, transport, watchdog operation, or radio authority is added.

Phase 2Y adds an immutable activation-plan layer between the static interlock and
the read-only activation composition. The plan has exactly four Boolean switch
intentions—command boundary, command-gate transmit, browser transaction ingress
execution, and browser keying-capability projection—and produces either all four
true after a valid explicit request or all four false.

Phase 2Z adds a single immutable per-session binding between that plan and the
four existing runtime constructor switches. The binder requires a complete plan,
a local `FlexRx` endpoint, explicit transmit configuration, and browser lease
configuration; it rejects partial plans and binds all four false for remote,
simulation, absent, or incomplete sessions. The lifecycle receives one binding
before it constructs the gate, command boundary, or browser ingress, so no later
request can mutate activation state. Browser capability is projected from that
same binding plus fresh dynamic readiness and exact session authority.

The only new caller is browser TX protocol v2. A strict `tx.intent` for Boolean
MOX/PTT delegates the unchanged parsed request and server validation through the
existing transaction ingress. The transaction still arms local and independent
safety before key, signs and verifies the station command envelope, traverses
the command gate, and confirms radio state. A strict `tx.heartbeat` may renew
only the active transaction owned by the same authenticated connection and
opaque lease. It runs every two seconds with a five-second maximum watchdog
deadline; ordinary socket keepalive, lease renewal, reconnect, timer, and status
traffic cannot renew TX authority. Active lease renewal and unkey are accepted
only while fresh occupancy proves the exact protected AetherSDR handle is the
sole TX owner.

Normal web artifact inspection now requires exactly one reviewed `xmit 1`, one
runtime-deduplicated reviewed `xmit 0`, and type markers for both the primary and
emergency transports. The watchdog artifact requires exactly one reviewed
`xmit 0` and zero `xmit 1`; both artifacts still reject HIL process, CWX, and
TX-audio surfaces. Thus source and binary contain the approved primary and
safety primitives. Default configuration still creates no executable production
TX or unkey path because the activation request, transmit/lease opt-ins, primary
and emergency transports, watchdog arming, signing, submission, and binding all
remain disabled. A reviewed complete configuration can bind the existing path
without creating a second gate or transport.

The independent, station-local supervisor has no key method and an unkey-only
transport. Its arm is purpose-bound to one engine
instance, lease, session/browser owner, exact protected FLEX client handle, and
bounded heartbeat deadline. A separate non-GUI FLEX observer may classify the
engine handle as external relative to itself; the supervisor therefore compares
the fresh single TX occupant directly with the protected arm handle rather than
trusting observer-relative ownership labels. It can issue unkey only for that
exact handle. SmartSDR, Maestro, hardware PTT, ambiguous/stale ownership, or a
replaced handle is never globally unkeyed. A newly started supervisor begins
disarmed and never infers ownership of an already-active transmission.

Phase 3B closes the cross-process cleanup gap after an accepted independent-
watchdog deadline unkey. Each active transaction captures the exact watchdog
host instance and cumulative accepted-unkey count that existed before keying.
Only a later accepted `deadline-unkey-accepted` result from that same watchdog
host, with a strictly greater count, the exact radio/session/connection/lease/
gateway/engine/FLEX-handle identity, and fresh radio-authoritative idle may enter
cleanup. Stale counts, a restarted watchdog, identity mismatch, non-idle or stale
radio state, and incomplete watchdog authority leave the transaction active in
explicit reconciliation.

The cleanup participant is lifecycle-only and executes while the transaction's
single-operation lock is held. It owns no radio command transport. It first
proves that any remaining gate intent and local safety arm belong to the same
active transaction, then asks the gate to consume the already-observed idle state
and asks the local supervisor to reset from that same fresh idle evidence.
Neither operation can key or unkey. The transaction is cleared only after a
second fresh-idle check plus fully empty `Idle` gate and `Disarmed` safety
snapshots. Lease release may arrive before this reconciliation; the lifecycle
retains the exact registered watchdog identity long enough to reconcile, then
disconnects and resets the watchdog registration. No browser command, heartbeat,
retry, or inferred success is introduced.

A separate engine-connection monitor has no radio command transport. It binds
to the active supervisor arm's exact engine instance, lease, and protected FLEX
handle, and may signal `station-engine-connection-lost` only after observing
that exact identity connected and then disconnected. Startup while disconnected,
mismatched identity, stale reports, and repeated disconnected reports cannot
invent ownership or create duplicate immediate unkeys.

HIL covers both connection boundaries. The first injects loss of only the
engine TX command channel while retaining a status and cleanup session for
evidence. The second launches the engine as a separate one-time child process,
binds it through a 30-second mode-0600 plan to the authorizing parent and exact
radio topology, and terminates the entire process tree. The parent accepts the verified OS-process exit only after seeing the exact
child connected, and signals `station-engine-connection-lost` immediately after
that exit. FLEX roster disappearance is still required as a later postcondition
and cleanup proof, but it does not delay emergency reconciliation. TCP closure
may make the radio idle autonomously; otherwise the observer may issue one
unkey only while fresh occupancy still proves the dead child's exact FLEX
handle is the sole TX owner. After idle and old-handle removal, HIL launches a
fresh replacement engine under a new one-time plan. Its PID, engine instance,
session, browser identity, lease, and FLEX handle must all differ from the dead
engine. It may reconcile only from fresh idle and must exit with zero key and
zero unkey commands, no active TX intent, no inherited resources, and the known
station baseline restored. A new cleanup/identification session starts only
after that replacement check and never inherits TX ownership from either
process.

Production states:

```text
No capability
    -> TX denied

Eligible, no lease
    -> may request lease

Lease held, idle, exact AetherSDR Local PTT authority
    -> deliberate keying intent may be evaluated

Lease held, keyed
    -> watchdog + interlock + authenticated client heartbeat

lease/auth/client loss or ambiguous state
    -> force-unkey locally at AetherD only when ownership proves AetherSDR keyed

external SmartSDR/Maestro Local PTT authority or active external TX
    -> browser key denied; never unkey the external owner
```

No reconnect, model reconciliation, timer, profile load, or status echo is an
operator keying intent.

## M8F AetherRemote bootstrap and station release boundary

A gateway topology that accepts remote stations publishes one bounded bootstrap
surface from the exact active locally verified signed release. The public
`/.well-known/aethersdr` document contains only release/protocol identities and
same-gateway locations for the installer, signed manifest, verification key,
architecture-specific Agent/station-engine packages, enrollment endpoint, and
prefixed broker route. Publication additionally binds the running content root to
the active release's signed `gateway-web` tree. No enrollment code, station
credential, broker credential, private key, administrator authority, radio state,
or TX authority is public bootstrap data.

The Admin enrollment workflow deliberately keeps two channels separate. It
requests the signed install guide without a station secret and creates the
single-use enrollment code through the existing protected administration
boundary. The generated install command pins the installer digest and release-key
digest but contains no enrollment code. The station installer independently
requires HTTPS, validates architecture, release signature, package hashes and
lengths, and safe archive paths, installs only signed service assets, performs a
bounded no-command local FLEX discovery, and reads the one-time enrollment code
interactively with terminal echo disabled.

The reverse proxy is the only public bridge to the broker. It strips the fixed
`/aetherremote/broker` prefix and forwards only that subtree to loopback port
5090; all ordinary gateway traffic stays on loopback port 5080. Remote-accepting
installer plans transactionally provision separate runtime and administration
credentials. Plaintext remains in fixed owner-only files readable by the gateway;
the broker environment receives only SHA-256 verifiers. The credential material
never enters the deterministic plan ID or process arguments.

Station release update is a capability-gated fixed-purpose protocol, not remote
execution. The gateway can select only its active verified signed release and the
wire request carries only the station identity, random correlation, and canonical
release identity. The Agent derives same-gateway URLs, independently re-verifies
the pinned release key, signed manifest, architecture, TX-disabled declaration,
and exact Agent/station-engine packages, then stages only beneath a fixed private
root. The root updater accepts only fixed local `apply`, `rollback`, `confirm`,
and `acknowledge` actions over a private Unix socket; it has no network address
family and accepts no arbitrary path, URL, executable, service identity, shell,
or command payload.

Activation is two phase and crash safe. Before switching, the updater records the
previous and target releases plus a bounded confirmation deadline. It switches
only the fixed Agent, station-engine, and updater symlinks and signed units, then
restarts the station engine. If the new Agent does not confirm startup before the
deadline, the updater restores the prior release and persists a rollback
completion. A confirmed new Agent likewise persists a successful completion.
That completion is retained until the Agent reconnects, reports the exact
correlation/release result, receives `broker.release.update-ack`, and performs a
matching local acknowledgement. The broker retains a bounded recent-request set
so an exact duplicate completion after reconnect receives the same acknowledgement;
altered or untracked results fail closed. Local acknowledgement is idempotent and
retained across response loss, preventing either lost final outcomes or repeated
updater restarts.

This bootstrap/update path never grants radio command, TX lease, watchdog arming,
key/unkey, or browser TX authority. Remote station radio policy continues to obey
the station-local safety and ownership boundaries.

## M8G backup, restore, diagnostics, and operations

The supported backup boundary is an explicit offline maintenance operation. The
standalone CLI accepts create, inspect, and restore commands but never accepts a
passphrase in argv; passphrases are read only from an interactive local terminal.
Production create/restore first prove every fixed AetherSDR/AetherRemote systemd
unit is inactive and refuse to continue otherwise. The CLI never stops or starts
a service itself.

Backup schema 1 captures each AetherSDR-owned durable root exactly once, including
nested secrets, plus installer-owned managed-Caddy state when its ownership marker
is present. It includes identity/MFA, Data Protection, radio/onboarding policy,
audit, station credential/broker credential, signing/trust, configuration, and
state authority. Release binaries, downloads, logs, and prior backups are not
recursively embedded; only the validated current and rollback release identities
are retained. External DNS, externally managed proxy/TLS state, provider-side
OIDC/Entra registration and secret lifecycle, and signed release package bytes are
reported as explicit external dependencies.

The backup payload is bounded, schema-validated, compressed, and authenticated
with AES-256-GCM. Its key is derived from a locally entered passphrase using
PBKDF2-HMAC-SHA256 with a random salt. Restore requires the recorded immutable
release identities to be installed first, reconstructs files only beneath fixed
validated target roots, remaps the validated setup `InstallationPaths` object for
a replacement host, and maps logical `root`/`aethersdr`/`aetherremote` ownership
to the destination host rather than copying numeric UIDs/GIDs. A root-external
durable journal records `prepared` then `committed`: pre-commit interruption is
rolled back, while post-commit recovery can only finish cleanup and never revert
the committed restored state. The `current` pointer switches as part of that same
bounded transaction.

Normal runtime adds one passive `OperationsReadinessService`. It projects setup,
canonical URL, storage free space, backup age, release/update/rollback readiness,
FLEX discovery health, broker/station state, signed AetherRemote compatibility,
authentication readiness, browser WebSocket registration, and TX-policy
prerequisites without issuing a radio command or acquiring authority. An explicit
Admin POST may additionally probe only the persisted canonical HTTPS origin and
fixed health, authentication-callback, browser-WebSocket, and station-broker paths.
TLS chain/certificate expiry and required public security headers are observed in
that probe. Active probes and diagnostic ZIP creation are antiforgery/admin
protected and rate-limited; no arbitrary URL is accepted.

The downloadable diagnostic bundle is built in memory under a configured byte
bound from already-redacted projections. It contains runtime/version metadata,
aggregate readiness/alerts/metrics, identifier-free radio/station health, release
identities, and aggregate audit action/result counts. It intentionally excludes
raw configuration/logs/environment, URLs, headers, user/actor/radio/station
identifiers, serials/addresses, passwords/hashes, MFA material, Data Protection
keys, signing-key bytes, station/runtime credentials, enrollment codes, auth
client secrets, and bearer/session/CSRF tokens. Setup-only composition remains
isolated; its preflight merely enumerates the exact post-install operational checks
that must later be executed from Admin.

CI performs a direct-and-transitive NuGet vulnerability query and fails closed if
NuGet reports an advisory. Operator procedures and the support matrix live in
`docs/OPERATIONS.md` and `docs/SUPPORT-MATRIX.md`; versioned M8G notes live under
`docs/releases/`.

## Browser rendering

The prototype uses one Canvas 2D spectrum path with a compact binary frame and
performs the waterfall scroll locally. The former stacked-trace selector,
browser preference, trace-history buffer, and alternate drawing path are
removed, so there is no dormant second renderer. Startup deletes the obsolete
preference key but never reads or writes it. Production can move to
WebGL/WebGPU only after measuring the Canvas implementation; rendering
technology cannot change the wire contract.

RX audio should use Opus frames decoded in an `AudioWorklet`, with a bounded
jitter buffer. Microphone capture and TX audio require a separate, explicit
operator permission and are out of scope until the engine TX gate is proven.
