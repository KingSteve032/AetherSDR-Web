# M8H RC4 operator acceptance evidence

Candidate identity:

- Commit: `039e5a7d09d79ee74947a316a0d58ff85aea59f0`
- Tag: `aethersdr-8.8.0-rc.4`
- Release state: production-signed draft prerelease; not published
- Production key ID: `aethersdr-prod-2026-01`
- linux-x64 manifest SHA-256: `74d94e1fd9a1fe2efdd988b4e714a32bcb6ecd347ecd46be35f5c988c94562be`
- gateway package SHA-256: `55ad37fdbb47ce71f40552e51afcedef3497e91e8326ade54171f28df6c2867b`
- broker package SHA-256: `70b890931301f9dc9d6c946d52f0a1ecfd2c9f5412430481459848b9cc77621e`
- agent package SHA-256: `67ab6baf72c6a92c77001c4523f50f294ae829f12b35a78431d1b4d442ea5e0e`
- station-engine package SHA-256: `55ad37fdbb47ce71f40552e51afcedef3497e91e8326ade54171f28df6c2867b`

Private infrastructure addresses and credentials are intentionally omitted.

## Candidate automated gate — PASS

- Exact candidate commit `039e5a7d09d79ee74947a316a0d58ff85aea59f0` has successful GitHub Actions runs for normal `CI` (`2026-08-15T11:51:50Z`), `Standalone release acceptance` (`2026-08-15T11:51:50Z`), and `Draft signed release` (`2026-08-15T12:05:45Z`).
- The successful draft-signed-release run is the workflow that produced the production-signed RC4 identity and digests recorded above; no later candidate identity is substituted in this evidence document.

Result: **PASS** for the automated candidate-gate requirement on the exact production-signed RC4 commit.

## Existing Caddy — PASS

- Acceptance window: `2026-08-16T15:39Z` through `2026-08-16T15:45Z`.
- Operator: interactive `devspace` maintenance session; sudo authorization was entered by the operator.
- Host/client: replacement Ubuntu Server 24.04.1 LTS `x86_64` VM, `curl 8.5.0`, Caddy `2.6.2`.
- Installed candidate resolved exactly to `/opt/aethersdr/releases/aethersdr-8.8.0-rc.4`.
- Installed topology: `PersonalSingleStation` (`AcceptsRemoteStations=false`).
- RC4 read-only installer plan was run with `--installation-reverse-proxy existing-caddy`, `linux-x64`, guidance-only firewall, local authentication, and the exact RC4 release identity. The plan returned `PLAN_RC=0` and included `configureReverseProxy` target `ExistingCaddy` plus canonical HTTPS health/TLS verification actions.
- The packaged RC4 `installer/proxy/Caddyfile.template` was rendered for the persisted public authority with the Existing-Caddy TLS placeholder left operator-owned. `caddy validate --adapter caddyfile` returned `Valid configuration` and `VALIDATE_RC=0`. The only emitted warning was Caddyfile formatting; provisioning/validation succeeded.
- The live `/etc/caddy/Caddyfile` SHA-256 was `bfe4d54ff66f63e0e6f7d9dcaff72819821ac7a76d20116a6561103e6c43606c` before the RC4 Existing-Caddy plan, after the plan, after rendered-fragment validation, and at final cleanup. This proves the read-only Existing-Caddy path did not replace or modify the active Caddy configuration.
- Trusted HTTPS was exercised from the host without a TLS bypass. `/healthz` returned HTTP 200 with HSTS, `X-Content-Type-Options`, `Referrer-Policy`, and the minimal body `{"status":"ok","radioMode":"Simulation","transmitEnabled":false}`.
- Browser WebSocket boundary: an HTTP/1.1 WebSocket-upgrade-shaped request to `/ws/radio` reached the application authentication boundary and returned HTTP 302 to the HTTPS login route with `returnUrl=/ws/radio`, matching the accepted protected-boundary outcomes used by `OperationalReadiness`.
- Station broker prefix: an HTTP/1.1 upgrade-shaped request through `/aetherremote/broker/station/v1` was logged by `AetherRemote.Broker` as the stripped `/station/v1` endpoint, proving the scoped Caddy prefix reached loopback broker port 5090 with the prefix removed. The endpoint returned 404 both through Caddy and directly on loopback because `PersonalSingleStation` intentionally has `AcceptsRemoteStations=false`; its broker station-link environment is therefore not installed and the station WebSocket is not applicable for this topology. The 404 is not a proxy-routing failure.
- Forwarded-header contract: the packaged template uses Caddy v2 `reverse_proxy`; Caddy's documented defaults preserve the incoming `Host` and set or augment `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host`. The browser authentication redirect observed through the loopback HTTP upstream was generated as HTTPS, consistent with the gateway's trusted forwarded-header processing.
- Final service state: `caddy`, `aethersdr-web`, `aetherremote-broker`, `aetherremote-station-engine`, and `aethersdr-release-updater` were all active.
- Temporary rendered/probe files under `/tmp` were removed.
- No product release file, system proxy configuration, service configuration, setup state, authentication secret, signing key, radio state, TX policy, or RF/HIL state was mutated by this acceptance row.

Result: **PASS** for the Existing-Caddy operator-owned integration row on the installed `PersonalSingleStation` topology. A remote-station-capable topology must separately exercise the authenticated station WebSocket during the packaged remote-station acceptance row.

## Nginx Proxy Manager compatibility — PASS (supplemental)

- Acceptance window: `2026-08-16T16:12Z` through `2026-08-16T16:16Z`.
- Operator: interactive `devspace` maintenance session plus an operator-controlled browser session.
- Public test origin: `https://flexweb.w4car.org` with operator-owned TLS terminating at a separate Nginx Proxy Manager / OpenResty VM. Private infrastructure addresses are omitted.
- The separate proxy forwarded to the installed RC4 gateway through its already-accepted Caddy listener; no listener, package, proxy configuration, or product release file on the RC4 host was changed.
- Trusted HTTPS `/healthz` returned HTTP 200 with AetherSDR security headers and the exact fail-closed body `{"status":"ok","radioMode":"Simulation","transmitEnabled":false}`.
- Browser WebSocket authentication boundary: an HTTP/1.1 WebSocket-upgrade-shaped request to `/ws/radio` returned HTTP 302 to `https://flexweb.w4car.org/login?returnUrl=%2Fws%2Fradio`. The outer proxy was configured to preserve the public login authority in the upstream `Location` response rather than exposing the inner gateway authority.
- Station broker prefix: the same WebSocket-shaped request through `/aetherremote/broker/station/v1` returned HTTP 404, which is the expected disabled station-link outcome for this installed `PersonalSingleStation` topology after the path traversed the outer proxy and the already-accepted Caddy broker prefix.
- The operator then completed a real authenticated browser login through `https://flexweb.w4car.org` and reported normal application/Admin use through the double-proxy path. Browser family/device was not recorded, so this does not independently satisfy a named browser/device matrix row.
- No authentication secret, session token, signing key, radio state, TX policy, or RF/HIL state was recorded or changed by this compatibility check.

Result: **PASS** for Nginx Proxy Manager / OpenResty compatibility as an operator-owned outer proxy in front of the accepted RC4 Caddy topology. This is supplemental evidence only; it does not replace the formal `Existing Nginx` checklist row because the packaged reviewed Nginx fragment was not installed or exercised directly.

## Existing Nginx — PASS

- Acceptance window: `2026-08-18T12:37Z` through `2026-08-18T13:03Z`.
- Operator/client: interactive `proxymanager` shell on a separate operator-owned Nginx Proxy Manager VM; OpenResty/Nginx `1.29.2.5` was invoked from the already-present `jc21/nginx-proxy-manager` image. The live NPM container and its 80/81/443 listeners remained separate from the acceptance listener.
- Installed RC4 topology remained `PersonalSingleStation` (`AcceptsRemoteStations=false`); the private gateway authority is intentionally omitted from retained evidence.
- The exact packaged RC4 `gateway-web/installer/proxy/nginx-aethersdr.conf.template` was copied read-only from `/opt/aethersdr/current`; its SHA-256 was `725167994d18803f526986690f26588d6192f4c45ea8c5fb7a2c8b9b04992b6b`.
- The packaged fragment was rendered only under `/tmp` with an isolated HTTPS listener on port 9443, the installed RC4 canonical authority, and one-day operator-created test TLS material. The reviewed fragment retained `client_max_body_size 32m`, `proxy_http_version 1.1`, `Host`, `X-Forwarded-Host`, `X-Forwarded-Proto`, `X-Forwarded-For`, WebSocket Upgrade/Connection forwarding, 3600-second read/send timeouts, buffering disabled, gateway loopback 5080, and broker-prefix stripping to loopback 5090.
- `nginx -t` against the rendered packaged fragment returned `syntax is ok` and `test is successful`. Two earlier validation attempts failed only because the deliberately minimal disposable OpenResty wrapper lacked its normal image startup user/cache preparation; the wrapper was corrected without changing the packaged AetherSDR fragment.
- The isolated OpenResty container used host networking solely so the fragment's reviewed loopback upstreams could target two temporary SSH local forwards to the installed RC4 gateway and broker. No RC4 listener, proxy configuration, service configuration, release file, or package was changed.
- Trusted test HTTPS through the packaged fragment returned HTTP 200 and the exact fail-closed RC4 body `{"status":"ok","radioMode":"Simulation","transmitEnabled":false}` with the expected application security headers.
- Browser WebSocket boundary: an HTTP/1.1 WebSocket-upgrade-shaped request through the packaged Nginx fragment reached RC4 authentication and returned HTTP 302 to the HTTPS login route with `returnUrl=/ws/radio`, preserving the configured authority.
- Station broker prefix: the matching request through `/aetherremote/broker/station/v1` returned HTTP 404, the expected disabled station-link result for `PersonalSingleStation`. The reviewed Nginx location strips `/aetherremote/broker/` before forwarding to the broker loopback tunnel, while the direct tunnel baseline for `/station/v1` returned the same 404.
- Cleanup proof: before cleanup, only the disposable acceptance container listened on 9443 and one exact SSH process owned loopback 5080/5090; the live NPM container remained on 80/81/443. The acceptance container was stopped, the exact tunnel process was killed, and all temporary template, rendered config, wrapper, host-key, certificate, and private-key files were removed. After cleanup, no 5080, 5090, or 9443 listener remained and only the pre-existing NPM 80/81/443 listeners remained.
- Post-cleanup `https://flexweb.w4car.org/healthz` still returned the exact fail-closed RC4 health body, proving the operator's live NPM path remained healthy.
- Earlier on `2026-08-16`, an LXD compatibility-wrapper probe on the RC4 replacement host unexpectedly installed the LXD snap while exploring an isolated Nginx environment. It was immediately purged, verified absent, and did not contribute to this successful Nginx acceptance run.

Result: **PASS** for the formal Existing-Nginx operator-owned integration row against the exact packaged RC4 reviewed Nginx fragment, with isolated operator-owned TLS material and complete cleanup.

## Browser/device matrix — IN PROGRESS

### Desktop Chromium-class — behavior PASS; device metadata pending

- Acceptance time: approximately `2026-08-18T16:47Z`.
- Operator: operator-controlled authenticated browser session against the exact RC4 candidate through `https://flexweb.w4car.org`.
- The operator completed the requested desktop Chromium-class sequence and reported `Chromium pass`: normal login, main application load, Admin load, background/foreground transition recovery, and explicit refresh/reconnect all behaved normally.
- No browser-created TX authority was reported; the installed candidate remained the same fail-closed RC4 release used by the proxy acceptance rows.
- Exact browser product/version, operating system, and device model were not recorded in the operator report. Per the M8H evidence contract, those client/device fields must be supplied before this named matrix row is considered fully closed.

Result: **behavior PASS / evidence metadata pending** for the Desktop Chromium-class row.

## Recovery checks — behavior PASS; evidence metadata partially pending

- Earlier RC4 operator acceptance reported safe browser recovery after a gateway-service restart, recovery after a temporary NetBird/VPN path interruption, and foreground/background browser recovery. The observed release remained fail-closed and no browser-created TX authority was reported.
- The retained operator transcript did not preserve all required UTC timestamp and client/device fields for those earlier recovery observations, so they are retained as behavior evidence rather than silently promoted to fully closed M8H rows.
- Direct-LAN recovery has not been substituted by the VPN evidence. A fresh route check from the repository host still reaches the installed gateway through the NetBird `wt0` interface, so the direct-LAN row remains pending.

Result: **behavior PASS / evidence metadata pending** for gateway-restart and VPN-path recovery; **PENDING** for direct-LAN recovery.

## Backup and replacement-host restore — behavior PASS; timestamp metadata pending

- Encrypted backup creation and inspection completed against RC4. Backup identifier `a1b7b78290154f3a55794362bea94559` had SHA-256 `85b2b379591f36162f904befd583746c04c54ef5f3f487d4b5ce0748e85659cd`.
- The exact backup was restored onto a pristine supported Ubuntu 24.04 x64 replacement VM. The packaged restore reported `succeeded:true` and `replacementHostCompatible:true`; setup advanced from original revision 10 to replacement-host revision 11, `setupComplete:true`, and no bootstrap token remained.
- The restored current release resolved to the exact RC4 release. Dedicated `aethersdr` and `aetherremote` service identities existed, and `aetherremote-broker`, `aetherremote-station-engine`, `aethersdr-web`, and `aethersdr-release-updater` were active.
- Trusted HTTPS health after restore returned the exact fail-closed body `{"status":"ok","radioMode":"Simulation","transmitEnabled":false}`.
- The operator successfully used the restored local administrator identity with MFA and Admin functionality, providing behavioral evidence that local identity and protected application authority survived replacement-host restore.
- The durable transcript available for this evidence consolidation does not contain the exact UTC timestamps required by the M8H operator-evidence contract for the backup/restore session. Those rows therefore remain metadata-incomplete even though the functional restore behavior passed.

Result: **behavior PASS / exact UTC timestamp metadata pending** for encrypted backup, replacement-host restore, and restored local authority continuity.

## Remaining required operator evidence — PENDING

The exact RC4 candidate is not yet M8H-complete. Required rows still lacking complete operator evidence are:

- Microsoft Entra ID redirect/callback/sign-in/Admin/logout against an operator-owned test registration.
- Generic OIDC redirect/callback/sign-in/Admin/logout against an operator-owned supported provider.
- Browser/device rows not already evidenced with the required client/device metadata: Firefox, Safari/macOS where available, iPhone/iPad Safari, Android Chromium-class, and Surface/Windows touch; the Chromium-class row above also still lacks exact device metadata.
- Direct-LAN foreground/background recovery.
- One-hour multi-client receive soak and at least two simultaneous supported browser clients.
- Two distinct radios with different persisted TX policies, TX-eligible-radio disable/authority disappearance, and external SmartSDR/Maestro/hardware-PTT ownership preservation.
- Packaged remote station with a physical FLEX radio through the supported reconnect path.

No pending row is treated as a failure, and no hardware/RF result is inferred from automated or synthetic evidence.
