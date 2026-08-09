# AetherSDR existing reverse-proxy requirements

The operator-managed proxy must:

- terminate trusted HTTPS for the exact canonical public host;
- forward all paths to `http://127.0.0.1:5080`;
- preserve the original host and set trusted `X-Forwarded-Host`,
  `X-Forwarded-Proto=https`, and `X-Forwarded-For`;
- support HTTP/1.1 WebSocket upgrades for browser and station connections;
- allow request bodies up to 32 MiB;
- retain long-lived upgraded connections for at least one hour;
- route `/healthz` without authentication at the proxy layer while leaving the
  application's public health response minimal;
- never expose the loopback application port on a non-loopback interface;
- use a certificate whose SAN matches the exact canonical public host; and
- validate the resulting configuration before reloading the proxy.

The installer writes or prints a rendered reviewed template for operator use. It
does not overwrite an operator-managed proxy configuration.
