# AetherSDR-Web Support Matrix

Matrix version: 1 (M8G)

This matrix defines the combinations the standalone release workflow is designed
to support. M8H performs packaged-release acceptance across the matrix; rows
marked **M8H acceptance pending** are not represented as already hardware-soaked.

## Server operating system and architecture

| Platform | Architecture | Status | Notes |
|---|---|---|---|
| Ubuntu Server 24.04 LTS | `linux-x64` | Supported | Signed release builder, installer, systemd, proxy, backup/restore, and CI paths target this combination. |
| Ubuntu Server 24.04 LTS | `linux-arm64` | Supported | Signed release builder and AetherRemote/gateway package shape support arm64; M8H clean-host rehearsal remains required for the release candidate. |
| Other Linux distributions | any | Unsupported | No installer/service/proxy compatibility guarantee. |
| Windows/macOS server | any | Unsupported for production hosting | Development may run elsewhere, but standalone production installation and restore require Linux. |

## Reverse proxy

| Mode | Status | Boundary |
|---|---|---|
| Installer-managed Caddy | Supported | AetherSDR owns only its reviewed generated configuration/state and can include that managed configuration in encrypted backup. |
| Existing Caddy | Supported with operator-managed integration | Must satisfy `prototypes/web-client/deploy/installer/proxy/existing-proxy-requirements.md`; external TLS/private-key/config state remains externally backed up. |
| Existing Nginx | Supported with operator-managed integration | Must satisfy the reviewed Nginx template/requirements; external TLS/private-key/config state remains externally backed up. |
| Other reverse proxies/CDNs | Not in supported matrix | May work if equivalent HTTPS, header, WebSocket, size, and timeout behavior is provided, but is not a release-acceptance target. |

The station broker is never a separate public listener. Supported gateway proxy
config exposes only `/aetherremote/broker/*` and forwards it to loopback port
5090. Ordinary gateway traffic remains on loopback port 5080.

## Station topology

| Topology | Gateway | Broker | Station engine | Agent | Remote stations | Status |
|---|---:|---:|---:|---:|---:|---|
| Personal single station | yes | yes | yes | no | no | Supported |
| Local station gateway | yes | yes | yes | no | no | Supported |
| Remote station gateway | yes | yes | no | no | yes | Supported |
| Hybrid gateway | yes | yes | yes | no | yes | Supported |
| Remote station node | no | no | yes | yes | outbound to gateway | Supported |

AetherRemote station-node packages support `linux-x64` and `linux-arm64`. Remote
updates accept only the exact gateway-published signed release identity and keep
rollback/restart authority station-local.

## Authentication

| Mode | Status | Notes |
|---|---|---|
| Protected local accounts + TOTP MFA | Supported | Local identity/MFA and Data Protection state are included in the encrypted backup. |
| Microsoft Entra ID | Supported | Provider registration/tenant policy and provider-side secret lifecycle remain external dependencies. |
| Generic OpenID Connect | Supported | Provider registration/policy and provider-side secret lifecycle remain external dependencies. |
| Combined local + external provider | Supported | Both local and external requirements apply. |
| Development auth | Development only | Rejected as a production authentication mode. |

## Browser and device policy

The browser protocol requires modern standards support for secure cookies,
ES modules, Fetch, WebSocket, Web Audio, Canvas, and the Web platform APIs used by
the client. The release support policy is:

| Client class | Support target | Status |
|---|---|---|
| Desktop Chromium-family browser (Chrome/Edge) | Current stable and previous stable major | Supported target; M8H packaged browser acceptance required for final RC evidence. |
| Desktop Firefox | Current stable and previous stable major | Supported target; M8H packaged browser acceptance required for final RC evidence. |
| Desktop Safari on a currently supported macOS | Current stable major | Supported target; M8H packaged browser acceptance required for final RC evidence. |
| Mobile Safari / Chromium on currently supported iOS/Android | Current stable major | M8H acceptance pending; mobile/VPN recovery is explicitly an M8H release gate. |
| Embedded/legacy WebViews, Internet Explorer | none | Unsupported. |

A version falling outside this window is not blocked solely by user-agent string;
the support designation is an operator/release policy, not browser-side
authorization.

## Device/radio boundary

FLEX radios reachable through the repository's documented SmartSDR-compatible
protocol behavior are the radio target. The radio remains authoritative for live
state, interlock, client ownership, and stream identity. A radio being visible in
Admin does not imply TX support: every radio begins receive-only until explicitly
onboarded and, if desired, made TX-eligible under the separate production TX
safety requirements.

## Operations acceptance levels

- **Supported** means the source, installer, packaging, protocol, and operational
  workflow intentionally support the combination.
- **M8H acceptance pending** means M8G defines and diagnoses the combination but
  the packaged release candidate still requires the clean-machine/mobile/hardware
  rehearsal specified by M8H.
- **Unsupported** means release engineering does not claim compatibility and a
  defect may require reproduction on a supported combination before correction.
