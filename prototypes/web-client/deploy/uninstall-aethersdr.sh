#!/usr/bin/env bash
set -Eeuo pipefail

# Supported M8H standalone uninstall. This removes only proven installer-owned
# integration. Durable configuration, state, secrets, backups, immutable releases,
# service users, policies, credentials, trust material, and audit records are retained.

readonly current_link="/opt/aethersdr/current"
readonly releases_root="/opt/aethersdr/releases"
readonly systemd_root="/etc/systemd/system"
readonly caddy_config="/etc/caddy/Caddyfile"
readonly caddy_marker="/var/lib/aethersdr-installer/proxy/managed-caddy.sha256"
readonly internal_ca="/usr/local/share/ca-certificates/aethersdr-caddy-local.crt"
readonly internal_ca_marker="/var/lib/aethersdr-installer/proxy/internal-ca.sha256"
readonly update_ca_certificates="/usr/sbin/update-ca-certificates"
readonly systemctl="/usr/bin/systemctl"
readonly sha256sum="/usr/bin/sha256sum"
readonly cmp_bin="/usr/bin/cmp"
readonly rm_bin="/usr/bin/rm"
readonly readlink_bin="/usr/bin/readlink"

readonly -a units=(
  "aetherremote-agent.service"
  "aetherremote-station-engine.service"
  "aetherremote-broker.service"
  "aethersdr-web.service"
  "aethersdr-release-updater.service"
)

fail() {
  printf 'AetherSDR uninstall rejected: %s\n' "$1" >&2
  exit 2
}

[[ "$(id -u)" == "0" ]] || fail "root is required"
[[ "$(uname -s)" == "Linux" ]] || fail "Linux is required"
[[ -x "${systemctl}" && -x "${sha256sum}" && -x "${cmp_bin}" && -x "${rm_bin}" && -x "${readlink_bin}" && -x "${update_ca_certificates}" ]] || \
  fail "fixed maintenance executables are unavailable"

script_path="$(${readlink_bin} -f -- "$0")"
script_root="$(dirname -- "${script_path}")"
package_root="$(dirname -- "${script_root}")"
unit_assets="${package_root}/installer/systemd"
[[ -d "${unit_assets}" && ! -L "${unit_assets}" ]] || fail "packaged systemd assets are unavailable"

# The installed service and proxy must already be inactive. Uninstall never stops
# a potentially transmitting or operator-owned process on the caller's behalf.
for unit in "${units[@]}"; do
  state="$(${systemctl} is-active -- "${unit}" 2>/dev/null || true)"
  case "${state}" in
    inactive|failed|unknown) ;;
    active|activating|reloading|deactivating)
      fail "${unit} is ${state}; enter an offline maintenance window first"
      ;;
    *) fail "${unit} activity is ambiguous" ;;
  esac
done

if [[ -f "${caddy_marker}" ]]; then
  caddy_state="$(${systemctl} is-active -- caddy.service 2>/dev/null || true)"
  case "${caddy_state}" in
    inactive|failed|unknown) ;;
    active|activating|reloading|deactivating)
      fail "caddy.service is ${caddy_state}; stop the installer-managed proxy first"
      ;;
    *) fail "caddy.service activity is ambiguous" ;;
  esac
fi

# Validate every integration target before deleting any of them.
for unit in "${units[@]}"; do
  installed="${systemd_root}/${unit}"
  packaged="${unit_assets}/${unit}"
  if [[ -e "${installed}" || -L "${installed}" ]]; then
    [[ -f "${installed}" && ! -L "${installed}" ]] || fail "${installed} is not a regular file"
    [[ -f "${packaged}" && ! -L "${packaged}" ]] || fail "the packaged ${unit} asset is unavailable"
    "${cmp_bin}" -s -- "${installed}" "${packaged}" || fail "${installed} differs from the packaged reviewed unit"
  fi
done

if [[ -e "${current_link}" || -L "${current_link}" ]]; then
  [[ -L "${current_link}" ]] || fail "${current_link} is not the supported symbolic link"
  current_target="$(${readlink_bin} -f -- "${current_link}")"
  [[ -n "${current_target}" && "${current_target}" == "${releases_root}/"* ]] || \
    fail "the current link does not resolve beneath the immutable release root"
  release_name="${current_target#${releases_root}/}"
  [[ -n "${release_name}" && "${release_name}" != */* ]] || fail "the current release target is not a direct immutable release child"
  [[ -d "${current_target}" && ! -L "${current_target}" ]] || fail "the current immutable release is unavailable"
fi

remove_caddy=false
if [[ -f "${caddy_marker}" ]]; then
  [[ -f "${caddy_config}" && ! -L "${caddy_config}" ]] || fail "the managed Caddy marker exists but its configuration is unavailable"
  IFS= read -r marker_line < "${caddy_marker}" || fail "the managed Caddy ownership marker is unreadable"
  [[ "${marker_line}" == sha256=* ]] || fail "the managed Caddy ownership marker is malformed"
  marker_hash="${marker_line#sha256=}"
  [[ "${marker_hash}" =~ ^[0-9a-f]{64}$ ]] || fail "the managed Caddy ownership marker is malformed"
  read -r actual_hash _ < <("${sha256sum}" -- "${caddy_config}") || fail "the managed Caddy configuration could not be hashed"
  [[ "${actual_hash}" == "${marker_hash}" ]] || fail "the Caddy configuration is not proven installer-owned"
  remove_caddy=true
fi

remove_internal_ca=false
if [[ -f "${internal_ca_marker}" ]]; then
  [[ -f "${internal_ca}" && ! -L "${internal_ca}" ]] || fail "the internal CA ownership marker exists but its certificate is unavailable"
  IFS= read -r ca_marker_line < "${internal_ca_marker}" || fail "the internal CA ownership marker is unreadable"
  [[ "${ca_marker_line}" == sha256=* ]] || fail "the internal CA ownership marker is malformed"
  ca_marker_hash="${ca_marker_line#sha256=}"
  [[ "${ca_marker_hash}" =~ ^[0-9a-f]{64}$ ]] || fail "the internal CA ownership marker is malformed"
  read -r ca_actual_hash _ < <("${sha256sum}" -- "${internal_ca}") || fail "the internal CA certificate could not be hashed"
  [[ "${ca_actual_hash}" == "${ca_marker_hash}" ]] || fail "the internal CA certificate is not proven installer-owned"
  remove_internal_ca=true
fi

for unit in "${units[@]}"; do
  installed="${systemd_root}/${unit}"
  if [[ -f "${installed}" && ! -L "${installed}" ]]; then
    "${rm_bin}" -- "${installed}"
  fi
done

if [[ -L "${current_link}" ]]; then
  "${rm_bin}" -- "${current_link}"
fi

if [[ "${remove_caddy}" == "true" ]]; then
  "${rm_bin}" -- "${caddy_config}"
  "${rm_bin}" -- "${caddy_marker}"
fi
if [[ "${remove_internal_ca}" == "true" ]]; then
  "${rm_bin}" -- "${internal_ca}"
  "${rm_bin}" -- "${internal_ca_marker}"
  "${update_ca_certificates}"
fi

"${systemctl}" daemon-reload

for unit in "${units[@]}"; do
  [[ ! -e "${systemd_root}/${unit}" && ! -L "${systemd_root}/${unit}" ]] || \
    fail "${unit} remains installed after uninstall"
done
[[ ! -e "${current_link}" && ! -L "${current_link}" ]] || fail "the current release link remains installed"
if [[ "${remove_caddy}" == "true" ]]; then
  [[ ! -e "${caddy_config}" && ! -L "${caddy_config}" && ! -e "${caddy_marker}" ]] || fail "the installer-managed Caddy integration remains installed"
fi
if [[ "${remove_internal_ca}" == "true" ]]; then
  [[ ! -e "${internal_ca}" && ! -L "${internal_ca}" && ! -e "${internal_ca_marker}" ]] || fail "the installer-managed internal CA integration remains installed"
fi

printf '%s\n' '{"schemaVersion":1,"outcome":"uninstalled","durableDataPreserved":true,"immutableReleasesPreserved":true,"serviceUsersPreserved":true,"firewallPolicyPreserved":true}'
