#!/usr/bin/env bash
set -euo pipefail

secret_file="${1:-${HOME}/.config/aethersdr-web/client-secret}"
secret_directory="$(dirname "${secret_file}")"

read -r -s -p "Paste the Entra client secret value: " client_secret
printf "\n"

if [[ -z "${client_secret}" ]]; then
    echo "No secret was entered; nothing changed." >&2
    exit 1
fi

umask 077
mkdir -p "${secret_directory}"
temporary_file="$(mktemp "${secret_file}.tmp.XXXXXX")"
trap 'rm -f "${temporary_file}"' EXIT

printf "%s" "${client_secret}" > "${temporary_file}"
chmod 600 "${temporary_file}"
mv -f "${temporary_file}" "${secret_file}"
trap - EXIT
unset client_secret

echo "Client secret saved with owner-only permissions."
