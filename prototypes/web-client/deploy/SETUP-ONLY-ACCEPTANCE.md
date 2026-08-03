# M8A setup-only host acceptance

This runbook defines the clean-host boundary for the M8A first-run foundation.
It is intentionally narrower than the M8C installer and M8D account-provider
work. It does not install packages, create service users, configure a reverse
proxy, create an administrator, touch a radio, or enable transmit.

## Disposable VM baseline

Use a clean Ubuntu Server 24.04 VM with at least 2 vCPUs, 4 GB RAM, and 30 GB of
disk. Keep it off the public Internet unless a reviewed firewall and trusted TLS
termination path already exist. Do not attach a radio or copy production
credentials, station credentials, signing keys, or TX configuration into the VM.
Take a pristine snapshot before testing.

M8A requires one exact HTTPS origin with a certificate trusted by the test
workstation. A temporary operator-managed TLS proxy is acceptable for this
acceptance boundary. M8C remains responsible for the supported Caddy/Nginx and
certificate-management workflow.

## Setup-only configuration

Initialize the setup document and issue the first bootstrap token only from the
local interactive CLI. Configure the process with:

```text
InstallationSetupOnly__Enabled=true
InstallationSetupOnly__CanonicalAccessUrl=https://setup-host.example
InstallationRuntime__Enabled=false
```

The six `InstallationPaths` values must be absolute, distinct directories owned
for the test installation. Normal authentication, radio, remote-station,
watchdog, command, and TX settings must remain unused by the setup-only process.

## Read-only host smoke test

From a workstation that trusts the VM certificate, run:

```bash
bash prototypes/web-client/deploy/validate-setup-only-host.sh \
  https://setup-host.example
```

The script sends only `GET` requests. It verifies:

- the HTTPS-only setup document at `/setup/center`;
- fixed same-origin CSS and JavaScript assets;
- the redacted JSON status contract at `/setup`;
- no-store, CSP, and nosniff response headers;
- strict CSRF-cookie issuance;
- absence of token material and browser persistence; and
- failure of a successful cleartext HTTP setup response.

It does not claim setup, send a bootstrap token, create a session, or mutate the
setup document.

## Manual browser acceptance

1. Open `/setup/center` through the exact configured HTTPS origin.
2. Enter the locally displayed bootstrap token. Confirm that it is never added to
   the address bar, browser storage, page markup, or response JSON.
3. Complete topology, canonical URL, paths, update channel, backup confirmation,
   and TX-support package intent in order.
4. Generate preflight and confirm it reports planned changes without applying
   users, packages, services, proxy rules, firewall rules, migrations, radio
   operations, watchdog operations, or TX operations.
5. Restart the setup-only process. Confirm the old browser session no longer
   resumes.
6. Issue a new bootstrap token from the local CLI, reload the page, reclaim setup,
   and confirm the workflow resumes at the preserved preflight-ready revision.
7. Revoke the browser session and confirm both strict setup cookies are deleted.

## Completion and shutdown boundary

M8A does not create the production administrator. Automated acceptance uses the
existing trusted first-administrator verifier contract to prove the transition.
When a future M8D provider supplies exact enabled `Aether.Admin` evidence and the
handoff marks setup complete, the setup-only lifecycle monitor must stop the host
within its bounded polling interval.

The monitor also stops fail-closed if the setup document disappears, becomes
malformed, is replaced with another setup identity, rolls back to an older
revision, or changes to a topology that no longer runs the gateway here. A
completed installation cannot restart in setup-only mode. Only the exact
completed normal-runtime binding may start afterward.

## Snapshot and evidence

Retain the pristine snapshot and a second snapshot after the preflight-ready
workflow. Record only non-secret evidence: build identity, VM architecture,
Ubuntu version, canonical test origin, setup revision, test results, and whether
the smoke test passed. Never retain bootstrap tokens, session cookies, CSRF
values, account credentials, or other secret material in logs or screenshots.
