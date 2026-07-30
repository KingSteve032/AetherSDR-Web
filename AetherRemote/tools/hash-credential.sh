#!/usr/bin/env bash
set -euo pipefail

credential_file="${1:-}"
if [[ ! -f "${credential_file}" ]]; then
  echo "Usage: $0 <credential-file>" >&2
  exit 1
fi

credential="$(tr -d '\r\n' < "${credential_file}")"
if [[ "${#credential}" -lt 32 ]]; then
  echo "The credential is invalid." >&2
  exit 1
fi

printf '%s' "${credential}" | sha256sum | cut -d' ' -f1
