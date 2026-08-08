# Security Policy

## Supported versions

Security fixes are developed on `main` and included in the next release.

| Version | Supported |
|---|---|
| `main` | Yes |
| Latest tagged release | Yes |
| Older releases | No |

Users of an older release should upgrade before requesting a backport. A
maintainer may make an exception when the risk and deployment impact justify it.

## Report a vulnerability

Please use a
[private GitHub security advisory](https://github.com/KingSteve032/AetherSDR-Web/security/advisories/new).
Do not open a public issue, discussion, or pull request for an undisclosed
vulnerability.

Include, when available:

- the affected commit or release and component;
- prerequisites and a minimal reproducible case;
- expected and observed behavior;
- security and radio-safety impact;
- suggested mitigations;
- whether anyone else has received the report.

Remove credentials, private keys, access tokens, callsigns, station identifiers,
public IP addresses, and other personal or deployment-specific data. The
maintainer will acknowledge the report when received, coordinate validation and
a fix, and agree on disclosure timing. This project does not promise a bug
bounty or a fixed response SLA.

## Safe research

Use local, offline test doubles whenever possible. Do not:

- access systems, accounts, stations, or radios you do not own or lack explicit
  permission to test;
- key a transmitter, radiate RF, interfere with an operator, or rely on a live
  radio to demonstrate a finding;
- degrade service, destroy data, extract private data, or bypass provider terms;
- publish a transmit-safety or remotely exploitable issue before a coordinated
  fix is available.

A report about a TX path should demonstrate the software control-flow or
fail-closed violation without causing a transmission. The repository's
maintainer-controlled hardware-in-the-loop procedure is the only acceptable path
for any later live-radio confirmation.
