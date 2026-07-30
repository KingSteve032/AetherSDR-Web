#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this installer with sudo." >&2
  exit 1
fi
if [[ "$#" -ne 2 ]]; then
  echo "Usage: $0 <published-broker-directory> <broker-appsettings.json>" >&2
  exit 1
fi

publish_dir="$(realpath "$1")"
config_file="$(realpath "$2")"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_dir="/opt/aetherremote/broker"
config_dir="/etc/aetherremote/broker"
state_dir="/var/lib/aetherremote/broker"

if [[ ! -x "${publish_dir}/AetherRemote.Broker" ]]; then
  echo "The publish directory does not contain AetherRemote.Broker." >&2
  exit 1
fi
if [[ ! -f "${config_file}" ]]; then
  echo "The broker configuration file does not exist." >&2
  exit 1
fi

if ! id -u aetherremote >/dev/null 2>&1; then
  useradd --system --home /nonexistent --shell /usr/sbin/nologin aetherremote
fi
install -d -o root -g root -m 0755 "${install_dir}"
install -d -o root -g aetherremote -m 0750 "${config_dir}"
install -d -o aetherremote -g aetherremote -m 0700 "${state_dir}"
find "${install_dir}" -mindepth 1 -maxdepth 1 -delete
cp -a "${publish_dir}/." "${install_dir}/"
chown -R root:root "${install_dir}"
chmod 0755 "${install_dir}"
chmod 0755 "${install_dir}/AetherRemote.Broker"
install -o root -g aetherremote -m 0640 \
  "${config_file}" "${config_dir}/appsettings.json"
install -o root -g root -m 0644 \
  "${script_dir}/aetherremote-broker.service" \
  "/etc/systemd/system/aetherremote-broker.service"

systemctl daemon-reload
systemctl enable aetherremote-broker.service
systemctl restart aetherremote-broker.service
echo "Broker installed on http://127.0.0.1:5090"
