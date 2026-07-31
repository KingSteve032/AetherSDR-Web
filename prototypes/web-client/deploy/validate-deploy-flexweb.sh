#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

usage() {
  cat <<'EOF'
Run the complete pre-push validation gate, publish a receive-only FlexWeb build,
deploy it atomically to the FlexWeb host, and roll back automatically if the
service or health checks fail.

Usage:
  bash prototypes/web-client/deploy/validate-deploy-flexweb.sh [options]

Options:
  --release NAME   Use an explicit immutable release name.
  --validate-only  Run all tests and production artifact checks without deploy.
  -h, --help       Show this help.

Environment overrides:
  FLEXWEB_HOST       SSH destination (default: flexweb-gateway, which resolves
                     to flexweb@10.2.0.254)
  PUBLIC_HEALTH_URL  Public health endpoint
                     (default: https://flexweb.w4car.org/healthz)
  DEPLOY_LOG_DIR     Local deployment log directory

This script intentionally has no skip-tests option and performs no Git commit or
push. Browser acceptance through Browser Bridge is a separate required gate.
EOF
}

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
FLEXWEB_HOST="${FLEXWEB_HOST:-flexweb-gateway}"
PUBLIC_HEALTH_URL="${PUBLIC_HEALTH_URL:-https://flexweb.w4car.org/healthz}"
release_name="${RELEASE_NAME:-$(date -u +%Y%m%d-%H%M%S)-flexweb-validation}"
deploy=true

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --release)
      [[ "$#" -ge 2 ]] || { echo "--release requires a value." >&2; exit 2; }
      release_name="$2"
      shift 2
      ;;
    --validate-only)
      deploy=false
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

if [[ ! "${release_name}" =~ ^[0-9A-Za-z._-]{1,96}$ ]]; then
  echo "Invalid release name: ${release_name}" >&2
  exit 2
fi

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "Required command is missing: $1" >&2
    exit 1
  }
}

for command_name in \
  awk curl dotnet git grep mktemp node python3 scp seq sha256sum ssh strings tar tee; do
  require_command "${command_name}"
done

WEB_PROJECT="${REPO_ROOT}/prototypes/web-client/AetherSDR.Web.csproj"
WEB_TEST_PROJECT="${REPO_ROOT}/prototypes/web-client/tests/AetherSDR.Web.Tests.csproj"
HIL_TEST_PROJECT="${REPO_ROOT}/prototypes/web-client/tx-hil-tests/AetherSDR.TxHil.Tests.csproj"
REMOTE_TEST_PROJECT="${REPO_ROOT}/AetherRemote/tests/AetherRemote.Tests/AetherRemote.Tests.csproj"
UI_TEST_DIR="${REPO_ROOT}/prototypes/web-client/tests-ui"
ACTIVATOR="${REPO_ROOT}/prototypes/web-client/deploy/activate-release.sh"

for required_path in \
  "${WEB_PROJECT}" \
  "${WEB_TEST_PROJECT}" \
  "${HIL_TEST_PROJECT}" \
  "${REMOTE_TEST_PROJECT}" \
  "${UI_TEST_DIR}" \
  "${ACTIVATOR}"; do
  [[ -e "${required_path}" ]] || {
    echo "Required path is missing: ${required_path}" >&2
    exit 1
  }
done

DEPLOY_LOG_DIR="${DEPLOY_LOG_DIR:-${XDG_STATE_HOME:-${HOME}/.local/state}/aethersdr-web/deploy-logs}"
mkdir -p -- "${DEPLOY_LOG_DIR}"
DEPLOY_LOG_FILE="${DEPLOY_LOG_DIR}/${release_name}-flexweb-validation.txt"
: > "${DEPLOY_LOG_FILE}"
ln -sfn -- "$(basename -- "${DEPLOY_LOG_FILE}")" \
  "${DEPLOY_LOG_DIR}/latest-flexweb-validation.txt"
exec > >(tee -a "${DEPLOY_LOG_FILE}") 2>&1

work_dir="$(mktemp -d "${TMPDIR:-/tmp}/aethersdr-flexweb.${release_name}.XXXXXX")"
remote_dir=""
previous_release=""
expected_release="/home/flexweb/aethersdr/releases/${release_name}"
activated=false
deployment_succeeded=false
sudo_password=""

prompt_for_sudo_password() {
  if ssh -o BatchMode=yes "${FLEXWEB_HOST}" 'sudo -n true' >/dev/null 2>&1; then
    return 0
  fi

  if [[ ! -r /dev/tty || ! -w /dev/tty ]]; then
    echo "The FlexWeb host requires a sudo password, but no interactive terminal is available." >&2
    exit 1
  fi

  printf 'Sudo password for %s: ' "${FLEXWEB_HOST}" >/dev/tty
  IFS= read -r -s sudo_password </dev/tty
  printf '\n' >/dev/tty
  if [[ -z "${sudo_password}" ]]; then
    echo "A sudo password is required." >&2
    exit 1
  fi

  if ! printf '%s\n' "${sudo_password}" |
      ssh -o BatchMode=yes "${FLEXWEB_HOST}" 'sudo -S -p "" -v'; then
    sudo_password=""
    echo "The sudo password was rejected." >&2
    exit 1
  fi
}

remote_sudo() {
  local remote_command="$1"
  if [[ -z "${sudo_password}" ]]; then
    ssh -o BatchMode=yes "${FLEXWEB_HOST}" "sudo -n ${remote_command}"
  else
    printf '%s\n' "${sudo_password}" |
      ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
        "sudo -S -p '' ${remote_command}"
  fi
}

rollback() {
  local status=$?
  trap - EXIT INT TERM

  if [[ "${activated}" == true && "${deployment_succeeded}" != true ]]; then
    echo "Deployment verification failed; rolling FlexWeb back." >&2
    if [[ "${previous_release}" =~ ^/home/flexweb/aethersdr/releases/[0-9A-Za-z._-]+$ &&
          "${previous_release}" != "${expected_release}" ]]; then
      ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
        "rollback_link='/home/flexweb/aethersdr/.current.rollback.$$'; ln -s '${previous_release}' \"\${rollback_link}\"; mv -Tf \"\${rollback_link}\" /home/flexweb/aethersdr/current"
      remote_sudo 'systemctl restart aethersdr-web.service' || true
    else
      echo "Previous release path was not safe to restore: ${previous_release}" >&2
    fi
  fi

  if [[ -n "${remote_dir}" ]]; then
    ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
      "rm -rf -- '${remote_dir}'" >/dev/null 2>&1 || true
  fi
  sudo_password=""
  rm -rf -- "${work_dir}"
  exit "${status}"
}
trap rollback EXIT INT TERM

assert_health_fail_closed() {
  local payload="$1"
  local source="$2"
  HEALTH_PAYLOAD="${payload}" python3 - "${source}" <<'PY'
import json
import os
import sys

source = sys.argv[1]
try:
    payload = json.loads(os.environ["HEALTH_PAYLOAD"])
except (KeyError, json.JSONDecodeError) as exc:
    raise SystemExit(f"{source} did not return valid health JSON: {exc}")

expected = {
    "status": "ok",
    "transmitEnabled": False,
    "browserTxLeaseEnabled": False,
    "txGateLifecycleRegistered": True,
    "txCommandTransportRegistered": False,
    "txSafetySupervisorArmingAvailable": False,
}
for key, value in expected.items():
    if payload.get(key) != value:
        raise SystemExit(
            f"{source} health field {key!r} was {payload.get(key)!r}; expected {value!r}")
print(f"{source} health is fail-closed: {payload}")
PY
}

assert_forbidden_string_absent() {
  local needle="$1"
  local ascii_file="$2"
  local utf16_file="$3"
  if grep -F -- "${needle}" "${ascii_file}" >/dev/null ||
     grep -F -- "${needle}" "${utf16_file}" >/dev/null; then
    echo "Production publish contains forbidden TX/HIL string: ${needle}" >&2
    exit 1
  fi
}

printf 'Release: %s\n' "${release_name}"
printf 'Checkout: %s\n' "${REPO_ROOT}"
printf 'Branch: %s\n' "$(git -C "${REPO_ROOT}" branch --show-current)"
printf 'Target: %s\n' "${FLEXWEB_HOST}"

if [[ "${deploy}" == true ]]; then
  echo "Checking SSH identity and current service state..."
  ssh -o BatchMode=yes -o ConnectTimeout=10 "${FLEXWEB_HOST}" \
    'test "$(id -un)" = flexweb && systemctl is-active --quiet aethersdr-web.service'
  previous_release="$(ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
    'readlink -f /home/flexweb/aethersdr/current')"
  if [[ ! "${previous_release}" =~ ^/home/flexweb/aethersdr/releases/[0-9A-Za-z._-]+$ ]]; then
    echo "Current release path is not a safe rollback target: ${previous_release}" >&2
    exit 1
  fi
  printf 'Rollback release: %s\n' "${previous_release}"
fi

echo "Running complete solution build..."
dotnet build "${REPO_ROOT}/AetherSDR-Web.slnx" --configuration Release

echo "Running FlexWeb server tests..."
dotnet test "${WEB_TEST_PROJECT}" --configuration Release --no-build

echo "Running TX-HIL isolation tests..."
dotnet test "${HIL_TEST_PROJECT}" --configuration Release --no-build

echo "Running AetherRemote tests..."
dotnet test "${REMOTE_TEST_PROJECT}" --configuration Release --no-build

echo "Running browser tests..."
node --check "${REPO_ROOT}/prototypes/web-client/wwwroot/admin-page.js"
node --test "${UI_TEST_DIR}"/*.test.mjs

echo "Publishing receive-only FlexWeb artifact..."
publish_dir="${work_dir}/publish"
archive="${work_dir}/${release_name}.tar.gz"
mkdir -p -- "${publish_dir}"
dotnet publish "${WEB_PROJECT}" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  -p:EnableTxHil=false \
  --output "${publish_dir}"

binary="${publish_dir}/AetherSDR.Web"
[[ -x "${binary}" ]] || {
  echo "Published AetherSDR.Web binary is missing." >&2
  exit 1
}

ascii_strings="${work_dir}/production-ascii.txt"
utf16_strings="${work_dir}/production-utf16.txt"
strings -a "${binary}" > "${ascii_strings}"
strings -el "${binary}" > "${utf16_strings}"
for forbidden in \
  'xmit 1' \
  'xmit 0' \
  'cwx send' \
  'HilGatewayAuthorityChild' \
  'internal-engine-process-child' \
  'AETHERSDR_TX_HIL' \
  'dax tx'; do
  assert_forbidden_string_absent \
    "${forbidden}" "${ascii_strings}" "${utf16_strings}"
done
echo "Production artifact contains no forbidden TX/HIL command surface."

if [[ "${deploy}" == true ]]; then
  prompt_for_sudo_password
  echo "FlexWeb sudo credentials accepted."
fi

if [[ "${deploy}" != true ]]; then
  deployment_succeeded=true
  trap - EXIT INT TERM
  rm -rf -- "${work_dir}"
  echo
  echo "Validation-only gate completed successfully."
  echo "Deployment log: ${DEPLOY_LOG_FILE}"
  echo "No server, Git commit, or Git remote was changed."
  exit 0
fi

tar --create --gzip --file "${archive}" --directory "${publish_dir}" .
archive_sha="$(sha256sum "${archive}" | awk '{print $1}')"

echo "Creating remote staging directory..."
remote_dir="$(ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
  'mktemp -d /tmp/aethersdr-web-validation.XXXXXX')"
scp "${archive}" "${ACTIVATOR}" "${FLEXWEB_HOST}:${remote_dir}/"

echo "Activating release ${release_name}..."
ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
  "install -m 0640 '${remote_dir}/${release_name}.tar.gz' '/home/flexweb/aethersdr/incoming/${release_name}.tar.gz'; bash '${remote_dir}/activate-release.sh' /home/flexweb/aethersdr '${release_name}' '${archive_sha}'"
activated=true

remote_sudo 'systemctl restart aethersdr-web.service'

remote_health=""
for _ in $(seq 1 45); do
  if remote_health="$(ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
      'curl -fsS --max-time 5 -H "Host: flexweb.w4car.org" http://127.0.0.1:5080/healthz' 2>/dev/null)"; then
    break
  fi
  sleep 1
done
[[ -n "${remote_health}" ]] || {
  echo "FlexWeb did not return internal health after deployment." >&2
  exit 1
}
assert_health_fail_closed "${remote_health}" "internal FlexWeb"

active_release="$(ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
  'readlink -f /home/flexweb/aethersdr/current')"
[[ "${active_release}" == "${expected_release}" ]] || {
  echo "Active release mismatch: ${active_release}" >&2
  exit 1
}
ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
  'systemctl is-active --quiet aethersdr-web.service'

public_health="$(curl --fail --silent --show-error --max-time 15 \
  "${PUBLIC_HEALTH_URL}")"
assert_health_fail_closed "${public_health}" "public FlexWeb"

deployment_succeeded=true
trap - EXIT INT TERM
if [[ -n "${remote_dir}" ]]; then
  ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
    "rm -rf -- '${remote_dir}'" >/dev/null 2>&1 || true
fi
sudo_password=""
rm -rf -- "${work_dir}"

echo
echo "FlexWeb validation deployment completed successfully."
echo "Active release: ${active_release}"
echo "Previous release retained for rollback: ${previous_release}"
echo "Deployment log: ${DEPLOY_LOG_FILE}"
echo "Git was not committed or pushed. Browser Bridge acceptance is still required."
