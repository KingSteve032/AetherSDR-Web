#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this installer with sudo." >&2
  exit 1
fi
if [[ "$#" -lt 3 || "$#" -gt 4 ]]; then
  echo "Usage: $0 <published-agent-directory> <station-id> <wss-broker-url> [credential-source]" >&2
  exit 1
fi

publish_dir="$(realpath "$1")"
station_id="$2"
broker_url="$3"
credential_source="${4:-}"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_dir="/opt/aetherremote/agent"
config_dir="/etc/aetherremote/agent"
credential_file="/etc/aetherremote/station-credential"

if [[ ! -x "${publish_dir}/AetherRemote.Agent" ]]; then
  echo "The publish directory does not contain AetherRemote.Agent." >&2
  exit 1
fi
if [[ ! "${station_id}" =~ ^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$ ]]; then
  echo "The station ID is invalid." >&2
  exit 1
fi
case "${broker_url}" in
  wss://*)
    ;;
  *)
    echo "The broker URL must use wss://." >&2
    exit 1
    ;;
esac

if ! id -u aetherremote >/dev/null 2>&1; then
  useradd --system --home /nonexistent --shell /usr/sbin/nologin aetherremote
fi
install -d -o root -g root -m 0755 "${install_dir}"
install -d -o root -g aetherremote -m 0750 "${config_dir}"
find "${install_dir}" -mindepth 1 -maxdepth 1 -delete
cp -a "${publish_dir}/." "${install_dir}/"
chown -R root:root "${install_dir}"
chmod 0755 "${install_dir}"
chmod 0755 "${install_dir}/AetherRemote.Agent"

if [[ ! -f "${credential_file}" ]]; then
  if [[ -n "${credential_source}" ]]; then
    credential_source="$(realpath "${credential_source}")"
    if [[ ! -f "${credential_source}" ]]; then
      echo "The credential source does not exist." >&2
      exit 1
    fi
    install -o aetherremote -g aetherremote -m 0600 \
      "${credential_source}" "${credential_file}"
  else
    umask 077
    openssl rand -hex 32 > "${credential_file}"
  fi
fi
chown aetherremote:aetherremote "${credential_file}"
chmod 0600 "${credential_file}"

config_temp="$(mktemp)"
trap 'rm -f "${config_temp}"' EXIT
cat > "${config_temp}" <<EOF
{
  "Agent": {
    "BrokerUrl": "${broker_url}",
    "StationId": "${station_id}",
    "CredentialFile": "${credential_file}",
    "DiscoveryEnabled": true,
    "InventorySeconds": 5,
    "RadioOfflineSeconds": 15,
    "LocalEngineUrl": "http://127.0.0.1:5081",
    "LocalEngineOrigin": "http://127.0.0.1:5081",
    "AllowInsecureDevelopmentTransport": false,
    "ConfiguredRadios": []
  }
}
EOF
install -o root -g aetherremote -m 0640 \
  "${config_temp}" "${config_dir}/appsettings.json"
install -o root -g root -m 0644 \
  "${script_dir}/aetherremote-agent.service" \
  "/etc/systemd/system/aetherremote-agent.service"
install -o root -g root -m 0755 \
  "${script_dir}/enroll-station.sh" \
  "/usr/local/sbin/aetherremote-enroll"

systemctl daemon-reload
systemctl enable aetherremote-agent.service
systemctl restart aetherremote-agent.service

credential="$(tr -d '\r\n' < "${credential_file}")"
verifier="$(printf '%s' "${credential}" | sha256sum | cut -d' ' -f1)"
echo "Station installed: ${station_id}"
echo "Add this SHA-256 verifier to the broker: ${verifier}"
echo "Or enroll with a one-time Admin code:"
echo "  sudo aetherremote-enroll https://your-gateway.example"
