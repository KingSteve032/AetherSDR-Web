#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SYSTEM_UNIT="${SCRIPT_DIR}/aethersdr-web.service"
USER_UNIT="${SCRIPT_DIR}/user/aethersdr-web.service"
PILOT_ENVIRONMENT="${SCRIPT_DIR}/environment.development.example"

fail() {
  echo "Deployment unit validation failed: $*" >&2
  exit 1
}

require_line() {
  local file="$1"
  local line="$2"
  grep -Fx -- "${line}" "${file}" >/dev/null ||
    fail "${file} must contain the exact line: ${line}"
}

reject_pattern() {
  local file="$1"
  local pattern="$2"
  if grep -Eq -- "${pattern}" "${file}"; then
    fail "${file} contains a forbidden system/user-service directive matching: ${pattern}"
  fi
}

for file in "${SYSTEM_UNIT}" "${USER_UNIT}" "${PILOT_ENVIRONMENT}"; do
  [[ -f "${file}" ]] || fail "required file is missing: ${file}"
done

require_line "${SYSTEM_UNIT}" "User=flexweb"
require_line "${SYSTEM_UNIT}" "Group=flexweb"
require_line "${SYSTEM_UNIT}" "StateDirectory=aethersdr-web"
require_line "${SYSTEM_UNIT}" "StateDirectoryMode=0700"
require_line "${SYSTEM_UNIT}" "ReadWritePaths=/var/lib/aethersdr-web"
require_line "${SYSTEM_UNIT}" "WantedBy=multi-user.target"
reject_pattern "${SYSTEM_UNIT}" '^ConditionUser='
reject_pattern "${SYSTEM_UNIT}" '^WantedBy=default\.target$'

require_line "${USER_UNIT}" "ConditionUser=flexweb"
require_line "${USER_UNIT}" "PrivateUsers=true"
require_line "${USER_UNIT}" "StateDirectory=aethersdr-web"
require_line "${USER_UNIT}" "StateDirectoryMode=0700"
require_line "${USER_UNIT}" "WantedBy=default.target"
reject_pattern "${USER_UNIT}" '^(User|Group)='
reject_pattern "${USER_UNIT}" '^WantedBy=multi-user\.target$'
reject_pattern "${USER_UNIT}" '/var/lib/aethersdr-web'

require_line "${PILOT_ENVIRONMENT}" \
  "DataProtection__KeyPath=/home/flexweb/.local/state/aethersdr-web/keys"
require_line "${PILOT_ENVIRONMENT}" \
  "RadioAccess__PolicyPath=/home/flexweb/.local/state/aethersdr-web/radio-access.json"
require_line "${PILOT_ENVIRONMENT}" \
  "RadioAccess__AuditPath=/home/flexweb/.local/state/aethersdr-web/audit.json"
reject_pattern "${PILOT_ENVIRONMENT}" '/var/lib/aethersdr-web'

echo "Deployment system/user unit boundaries are valid."
