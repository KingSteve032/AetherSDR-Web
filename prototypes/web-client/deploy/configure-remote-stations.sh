#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 4 ]]; then
  echo "Usage: $0 <environment-file> <broker-url> <runtime-credential-file> <administration-credential-file>" >&2
  exit 1
fi

environment_file="$(realpath "$1")"
broker_url="$2"
runtime_credential_file="$(realpath "$3")"
administration_credential_file="$(realpath "$4")"
if [[ ! -f "${environment_file}" ||
      ! -f "${runtime_credential_file}" ||
      ! -f "${administration_credential_file}" ||
      "${runtime_credential_file}" == "${administration_credential_file}" ||
      "${broker_url}" != http://127.0.0.1:* ]]; then
  echo "The remote-station configuration is invalid." >&2
  exit 1
fi

temporary_file="$(mktemp "${environment_file}.tmp.XXXXXX")"
trap 'rm -f "${temporary_file}"' EXIT
grep -v '^RemoteStations__' "${environment_file}" > "${temporary_file}"
{
  printf 'RemoteStations__Enabled=true\n'
  printf 'RemoteStations__BrokerUrl=%s\n' "${broker_url}"
  printf 'RemoteStations__RuntimeCredentialFile=%s\n' "${runtime_credential_file}"
  printf 'RemoteStations__AdministrationCredentialFile=%s\n' "${administration_credential_file}"
  printf 'RemoteStations__RefreshSeconds=3\n'
} >> "${temporary_file}"
chmod 0600 "${temporary_file}"
mv -f "${temporary_file}" "${environment_file}"
trap - EXIT
