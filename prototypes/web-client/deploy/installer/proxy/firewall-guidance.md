# AetherSDR firewall guidance

Gateway hosts normally require inbound TCP 80 and 443 for managed public Caddy
certificate issuance. A non-default public HTTPS port must also be allowed.
Existing proxies and LAN-internal Caddy require only the operator-selected HTTPS
port. Never expose the loopback gateway port 5080.

Remote station nodes initiate outbound connections to the gateway and normally
require no inbound AetherSDR port. FLEX discovery/control/media access remains
limited to the station LAN and the operator's existing routed-network policy.

The installer may apply only the exact reviewed UFW rules selected by the plan.
It never flushes existing rules, changes the default policy, deletes
operator-created rules, or enables UFW without explicit operator approval.
