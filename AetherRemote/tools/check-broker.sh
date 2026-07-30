#!/usr/bin/env bash
set -euo pipefail

credential_file="${1:-}"
broker_url="${2:-http://127.0.0.1:5090}"
if [[ ! -f "${credential_file}" ]]; then
  echo "Usage: $0 <management-credential-file> [broker-url]" >&2
  exit 1
fi

credential="$(tr -d '\r\n' < "${credential_file}")"
if [[ "${#credential}" -lt 32 ]]; then
  echo "The management credential is invalid." >&2
  exit 1
fi

curl --fail --silent --show-error \
  -H "Authorization: Bearer ${credential}" \
  "${broker_url}/api/stations"
echo
