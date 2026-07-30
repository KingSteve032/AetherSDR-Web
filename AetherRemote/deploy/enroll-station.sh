#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this enrollment command with sudo." >&2
  exit 1
fi
if [[ "$#" -lt 1 || "$#" -gt 2 ]]; then
  echo "Usage: $0 <https-gateway-url> [credential-file]" >&2
  exit 1
fi

gateway_url="${1%/}"
credential_file="${2:-/etc/aetherremote/station-credential}"
case "${gateway_url}" in
  https://*)
    ;;
  *)
    echo "The gateway URL must use https://." >&2
    exit 1
    ;;
esac
if [[ ! -f "${credential_file}" ]]; then
  echo "The station credential file does not exist." >&2
  exit 1
fi

IFS= read -r -s -p "One-time enrollment code: " enrollment_code
printf '\n'
if [[ ! "${enrollment_code}" =~ ^[0-9a-fA-F]{64}$ ]]; then
  echo "The enrollment code must contain 64 hexadecimal characters." >&2
  exit 1
fi

credential="$(tr -d '\r\n' < "${credential_file}")"
if [[ "${#credential}" -lt 32 || "${#credential}" -gt 512 ]]; then
  echo "The station credential file is invalid." >&2
  exit 1
fi
verifier="$(printf '%s' "${credential}" | sha256sum | cut -d' ' -f1)"

response="$(
  printf '{"enrollmentCode":"%s","credentialSha256":"%s"}' \
    "${enrollment_code}" "${verifier}" |
  curl --fail-with-body --silent --show-error \
    --header 'Accept: application/json' \
    --header 'Content-Type: application/json' \
    --data-binary @- \
    "${gateway_url}/api/station-enrollment/redeem"
)"
unset enrollment_code credential verifier

printf '%s\n' "${response}"
systemctl restart aetherremote-agent.service
echo "Station enrollment accepted; the agent is reconnecting."
