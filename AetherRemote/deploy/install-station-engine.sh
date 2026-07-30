#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this installer with sudo." >&2
  exit 1
fi
if [[ "$#" -ne 1 ]]; then
  echo "Usage: $0 <published-aethersdr-web-directory>" >&2
  exit 1
fi

publish_dir="$(realpath "$1")"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_dir="/opt/aetherremote/station-engine"
config_dir="/etc/aetherremote/station-engine"
state_dir="/var/lib/aetherremote/station-engine"

if [[ ! -x "${publish_dir}/AetherSDR.Web" ]]; then
  echo "The publish directory does not contain AetherSDR.Web." >&2
  exit 1
fi
if ! id -u aetherremote >/dev/null 2>&1; then
  useradd --system --home /nonexistent --shell /usr/sbin/nologin aetherremote
fi

install -d -o root -g root -m 0755 "${install_dir}"
install -d -o root -g aetherremote -m 0750 "${config_dir}"
install -d -o aetherremote -g aetherremote -m 0750 "${state_dir}"
find "${install_dir}" -mindepth 1 -maxdepth 1 -delete
cp -a "${publish_dir}/." "${install_dir}/"
chown -R root:root "${install_dir}"
chmod 0755 "${install_dir}" "${install_dir}/AetherSDR.Web"

config_temp="$(mktemp)"
trap 'rm -f "${config_temp}"' EXIT
cat > "${config_temp}" <<'EOF'
{
  "Auth": {
    "Mode": "Development",
    "DevelopmentUser": {
      "ObjectId": "aetherremote-station-engine",
      "Name": "AetherRemote Station Engine",
      "Email": "station-engine@localhost",
      "Roles": [
        "Aether.Observe",
        "Aether.Control",
        "Aether.Admin"
      ]
    }
  },
  "Radio": {
    "Mode": "FlexRx",
    "AllowTransmit": false,
    "Host": "127.0.0.1",
    "TcpPort": 4992,
    "CenterFrequencyHz": 14280000,
    "BandwidthHz": 200000,
    "InitialSliceFrequencyHz": 14074000,
    "SecondarySliceFrequencyHz": 14100000,
    "MinDbm": -130,
    "MaxDbm": -40,
    "XPixels": 1024,
    "YPixels": 700,
    "FramesPerSecond": 15,
    "NetworkMtu": 1200,
    "LowBandwidthConnect": false,
    "StationName": "AETHER-REMOTE-RX"
  },
  "RadioAccess": {
    "PolicyPath": "/var/lib/aetherremote/station-engine/policies.json",
    "AuditPath": "/var/lib/aetherremote/station-engine/audit.json"
  },
  "DataProtection": {
    "KeyPath": "/var/lib/aetherremote/station-engine/data-protection"
  },
  "RemoteStations": {
    "Enabled": false
  },
  "AllowedOrigins": [
    "http://127.0.0.1:5081"
  ],
  "ReverseProxy": {
    "Enabled": false,
    "KnownProxies": []
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "127.0.0.1"
}
EOF
install -o root -g aetherremote -m 0640 \
  "${config_temp}" "${config_dir}/appsettings.json"
install -o root -g root -m 0644 \
  "${script_dir}/aetherremote-station-engine.service" \
  "/etc/systemd/system/aetherremote-station-engine.service"

systemctl daemon-reload
systemctl enable aetherremote-station-engine.service
systemctl restart aetherremote-station-engine.service
echo "Station receive engine installed on http://127.0.0.1:5081"
