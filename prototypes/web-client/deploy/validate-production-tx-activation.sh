#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 <exact-radio-id> [environment-file] [web-app]" >&2
  echo "Validates production TX activation configuration without starting the web host or connecting to a radio." >&2
}

if [[ $# -lt 1 || $# -gt 3 ]]; then
  usage
  exit 64
fi

radio_id="$1"
environment_file="${2:-${HOME}/.config/aethersdr-web/environment}"
web_app="${3:-${HOME}/aethersdr/current/AetherSDR.Web}"

if [[ -z "${radio_id}" || "${radio_id}" =~ [[:cntrl:]] ]]; then
  echo "The exact radio ID is invalid." >&2
  exit 64
fi
if [[ -L "${environment_file}" || ! -f "${environment_file}" || ! -r "${environment_file}" ]]; then
  echo "The activation environment must be one readable regular file, not a symbolic link." >&2
  exit 66
fi
if [[ "$(stat -c '%u' "${environment_file}")" != "$(id -u)" ]]; then
  echo "The activation environment must be owned by the current service account." >&2
  exit 77
fi
case "$(stat -c '%a' "${environment_file}")" in
  400|600) ;;
  *)
    echo "The activation environment must have exact mode 0400 or 0600." >&2
    exit 77
    ;;
esac
if [[ -L "${web_app}" || ! -f "${web_app}" || ! -x "${web_app}" ]]; then
  echo "The reviewed AetherSDR.Web application is unavailable." >&2
  exit 69
fi
if [[ "$(basename "${web_app}")" != "AetherSDR.Web" ]]; then
  echo "The preflight application must be the reviewed AetherSDR.Web executable." >&2
  exit 69
fi

umask 077
set -a
# The deployment environment is intentionally compatible with both systemd
# EnvironmentFile syntax and the existing no-sudo launcher. It is operator-owned
# and mode-restricted before it is sourced; values are never echoed here.
# shellcheck disable=SC1090
source "${environment_file}"
set +a

exec "${web_app}" \
  --validate-production-tx-activation \
  --production-tx-radio-id "${radio_id}"
