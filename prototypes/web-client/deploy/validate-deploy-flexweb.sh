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
WATCHDOG_PROJECT="${REPO_ROOT}/prototypes/tx-watchdog/AetherSDR.TxWatchdog/AetherSDR.TxWatchdog.csproj"
WATCHDOG_TEST_PROJECT="${REPO_ROOT}/prototypes/tx-watchdog/AetherSDR.TxWatchdog.Tests/AetherSDR.TxWatchdog.Tests.csproj"
HIL_TEST_PROJECT="${REPO_ROOT}/prototypes/web-client/tx-hil-tests/AetherSDR.TxHil.Tests.csproj"
REMOTE_TEST_PROJECT="${REPO_ROOT}/AetherRemote/tests/AetherRemote.Tests/AetherRemote.Tests.csproj"
UI_TEST_DIR="${REPO_ROOT}/prototypes/web-client/tests-ui"
ACTIVATOR="${REPO_ROOT}/prototypes/web-client/deploy/activate-release.sh"

for required_path in \
  "${WEB_PROJECT}" \
  "${WEB_TEST_PROJECT}" \
  "${WATCHDOG_PROJECT}" \
  "${WATCHDOG_TEST_PROJECT}" \
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

rollback() {
  local status=$?
  trap - EXIT INT TERM

  if [[ "${activated}" == true && "${deployment_succeeded}" != true ]]; then
    echo "Deployment verification failed; rolling FlexWeb back." >&2
    if [[ "${previous_release}" =~ ^/home/flexweb/aethersdr/releases/[0-9A-Za-z._-]+$ &&
          "${previous_release}" != "${expected_release}" ]]; then
      ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
        "rollback_link='/home/flexweb/aethersdr/.current.rollback.$$'; ln -s '${previous_release}' \"\${rollback_link}\"; mv -Tf \"\${rollback_link}\" /home/flexweb/aethersdr/current"
      ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
        'systemctl --user restart aethersdr-web.service' || true
    else
      echo "Previous release path was not safe to restore: ${previous_release}" >&2
    fi
  fi

  if [[ -n "${remote_dir}" ]]; then
    ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
      "rm -rf -- '${remote_dir}'" >/dev/null 2>&1 || true
  fi
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
    "txLifecycleWatchdogRegistered": True,
    "txBrowserIntentProtocolVersion": 1,
    "txBrowserIntentValidationRegistered": True,
    "txBrowserIntentCommandTransportRegistered": False,
    "txStationCommandProtocolVersion": 1,
    "txStationCommandBoundaryRegistered": True,
    "txStationCommandBoundaryEnabled": False,
    "txStationCommandTrustVerificationEnabled": False,
    "txStationCommandTrustedKeyCount": 0,
    "txStationCommandSignatureVerificationAvailable": False,
    "txStationCommandSigningEnabled": False,
    "txStationCommandSigningKeyConfigured": False,
    "txStationCommandSigningAvailable": False,
    "txStationCommandSessionCompositionRegistered": True,
    "txStationCommandSessionCompositionBrowserIngressRegistered": False,
    "txStationCommandAdapterCompositionRegistered": True,
    "txStationCommandAdapterExecutorAttached": True,
    "txStationCommandAdapterExecutorRegistered": True,
    "txStationCommandGateExecutorRegistered": True,
    "txStationCommandGateExecutorTransmitEnabled": False,
    "txStationCommandGateExecutorCommandTransportAvailable": False,
    "txStationCommandGateExecutorSetTransmitAvailable": False,
    "txStationCommandGateExecutorBrowserIngressRegistered": False,
    "txStationCommandAdapterCompositionBrowserIngressRegistered": False,
    "txStationCommandSafetyArmCompositionRegistered": True,
    "txStationCommandSafetyArmAuthorityAttached": True,
    "txStationCommandSafetyArmAuthorityRegistered": True,
    "txStationCommandSafetyArmAuthorityBoundaryEnabled": False,
    "txStationCommandSafetyArmAuthorityCommandTransportAvailable": False,
    "txStationCommandSafetyArmAuthoritySetTransmitAvailable": False,
    "txStationCommandSafetyArmAuthorityBrowserIngressRegistered": False,
    "txStationCommandSafetyArmAvailable": False,
    "txStationCommandSafetyHeartbeatAvailable": False,
    "txStationCommandSafetyAbortAvailable": False,
    "txStationCommandSafetyArmCompositionBrowserIngressRegistered": False,
    "txStationCommandTransactionCompositionRegistered": True,
    "txStationCommandTransactionLifecycleBoundaryRegistered": True,
    "txStationCommandDirectSessionSubmissionRegistered": False,
    "txStationCommandTransactionSafetyArmAttached": True,
    "txStationCommandTransactionCommandCompositionAttached": True,
    "txStationCommandTransactionKeyAvailable": False,
    "txStationCommandTransactionHeartbeatAvailable": False,
    "txStationCommandTransactionUnkeyAvailable": False,
    "txStationCommandTransactionAbortAvailable": False,
    "txStationCommandTransactionActive": False,
    "txStationCommandTransactionReconciliationRequired": False,
    "txStationCommandTransactionBrowserIngressRegistered": False,
    "txStationCommandTransactionLifecycleBrowserIngressRegistered": False,
    "txBrowserTxTransactionIngressRegistered": True,
    "txBrowserTxTransactionIngressExecutionEnabled": False,
    "txBrowserTxTransactionIngressBoundaryAttached": True,
    "txBrowserTxTransactionIngressKeyAvailable": False,
    "txBrowserTxTransactionIngressUnkeyAvailable": False,
    "txBrowserTxTransactionIngressWebSocketCallerRegistered": False,
    "txBrowserTxTransactionIngressHttpCallerRegistered": False,
    "txBrowserTxTransactionIngressAetherRemoteCallerRegistered": False,
    "txBrowserTxTransactionIngressWatchdogCallerRegistered": False,
    "txBrowserTxTransactionIngressReconnectCallerRegistered": False,
    "txBrowserTxTransactionIngressTimerCallerRegistered": False,
    "txProductionCommandTransportRegistered": True,
    "txProductionCommandTransportConfiguredEnabled": False,
    "txProductionCommandTransportAllowedRadioCount": 0,
    "txProductionCommandTransportCommandTimeoutMilliseconds": 2000,
    "txProductionCommandTransportAvailable": False,
    "txProductionCommandTransportSetTransmitAvailable": False,
    "txProductionCommandTransportReason": "transport-disabled",
    "txProductionCommandTransportWebSocketCallerRegistered": False,
    "txProductionEmergencyUnkeyTransportRegistered": True,
    "txProductionEmergencyUnkeyTransportConfiguredEnabled": False,
    "txProductionEmergencyUnkeyTransportAllowedRadioCount": 0,
    "txProductionEmergencyUnkeyTransportCommandTimeoutMilliseconds": 2000,
    "txProductionEmergencyUnkeyTransportAvailable": False,
    "txProductionEmergencyUnkeyTransportUnkeyAvailable": False,
    "txProductionEmergencyUnkeyTransportReason": "transport-disabled",
    "txProductionEmergencyUnkeyTransportWebSocketCallerRegistered": False,
    "txProductionReadinessPolicyRegistered": True,
    "txProductionReadinessReady": False,
    "txProductionReadinessReason": "transmit-disabled",
    "txProductionReadinessLifecycleIngressRegistered": True,
    "txProductionReadinessWebSocketCallerRegistered": False,
    "txStationCommandEnvelopeSubmissionRegistered": False,
    "txStationCommandAdapterRegistered": True,
    "txStationCommandArmingAvailable": False,
    "txStationCommandSetTransmitAvailable": False,
    "txIndependentWatchdogHostPackaged": True,
    "txIndependentWatchdogSupervisionRegistered": True,
    "txIndependentWatchdogUnkeyTransportRegistered": True,
    "txIndependentWatchdogUnkeyTransportConfiguredEnabled": False,
    "txIndependentWatchdogUnkeyTransportAllowedRadioCount": 0,
    "txIndependentWatchdogUnkeyTransportCommandTimeoutMilliseconds": 2000,
    "txIndependentWatchdogUnkeyTransportAvailable": False,
    "txIndependentWatchdogUnkeyTransportWebSocketCallerRegistered": False,
    "txIndependentWatchdogCommandTransportRegistered": False,
    "txIndependentWatchdogArmingAvailable": False,
    "txCommandTransportRegistered": True,
    "txCommandTransportAvailable": False,
    "txSafetySupervisorArmingAvailable": False,
}
for key, value in expected.items():
    if payload.get(key) != value:
        raise SystemExit(
            f"{source} health field {key!r} was {payload.get(key)!r}; expected {value!r}")
missing = payload.get("txProductionReadinessMissingPrerequisites")
if not isinstance(missing, list) or not missing:
    raise SystemExit(
        f"{source} production readiness prerequisites were not a non-empty list")
if missing[0] != payload["txProductionReadinessReason"]:
    raise SystemExit(
        f"{source} production readiness reason did not match its first missing prerequisite")
required_missing = {
    "transmit-disabled",
    "browser-tx-lease-disabled",
    "command-submission-disabled",
    "command-signing-unavailable",
    "command-verification-unavailable",
    "command-boundary-disabled",
    "command-gate-transmit-disabled",
    "command-transport-unavailable",
    "set-transmit-unavailable",
    "emergency-unkey-transport-unavailable",
    "watchdog-unkey-transport-unavailable",
    "watchdog-arming-unavailable",
}
if not required_missing.issubset(set(missing)):
    raise SystemExit(
        f"{source} production readiness omitted required fail-closed prerequisites: {missing!r}")
if len(missing) != len(set(missing)):
    raise SystemExit(
        f"{source} production readiness repeated a missing prerequisite: {missing!r}")
state = payload.get("txIndependentWatchdogState")
if state not in {
        "supervised-empty-disarmed",
        "supervised-disarmed",
        "supervised-degraded-disarmed"}:
    raise SystemExit(
        f"{source} watchdog state was {state!r}; expected a supervised Disarmed state")
count_fields = [
    "txIndependentWatchdogSessionCount",
    "txIndependentWatchdogProcessCount",
    "txIndependentWatchdogConnectedProcessCount",
    "txIndependentWatchdogRegisteredIdentityCount",
    "txIndependentWatchdogRestartCount",
]
for key in count_fields:
    value = payload.get(key)
    if not isinstance(value, int) or isinstance(value, bool) or value < 0:
        raise SystemExit(f"{source} health field {key!r} was not a non-negative integer")
if payload["txIndependentWatchdogRegisteredIdentityCount"] != 0:
    raise SystemExit(
        f"{source} reported a registered watchdog identity while browser TX leases are disabled")
if payload["txIndependentWatchdogConnectedProcessCount"] > payload["txIndependentWatchdogProcessCount"]:
    raise SystemExit(f"{source} reported more connected watchdogs than running processes")
connected = payload.get("txIndependentWatchdogConnected")
if connected != (payload["txIndependentWatchdogConnectedProcessCount"] > 0):
    raise SystemExit(f"{source} watchdog connected flag did not match its process count")
print(f"{source} health is fail-closed: {payload}")
PY
}

assert_watchdog_disarmed() {
  local payload="$1"
  local source="$2"
  WATCHDOG_PAYLOAD="${payload}" python3 - "${source}" <<'PY'
import json
import os
import sys

source = sys.argv[1]
try:
    payload = json.loads(os.environ["WATCHDOG_PAYLOAD"])
except (KeyError, json.JSONDecodeError) as exc:
    raise SystemExit(f"{source} did not return valid watchdog JSON: {exc}")
expected = {
    "protocolVersion": 1,
    "requestId": "artifact-status",
    "ok": True,
}
for key, value in expected.items():
    if payload.get(key) != value:
        raise SystemExit(
            f"{source} field {key!r} was {payload.get(key)!r}; expected {value!r}")
snapshot = payload.get("snapshot") or {}
expected_snapshot = {
    "state": "Disarmed",
    "reason": "unkey-transport-disabled-disarmed",
    "radioCommandTransportAvailable": False,
    "armingAvailable": False,
    "registered": False,
    "connected": False,
    "leaseBound": False,
    "lastSequence": 0,
    "lastObservation": "process-started-disarmed",
}
for key, value in expected_snapshot.items():
    if snapshot.get(key) != value:
        raise SystemExit(
            f"{source} snapshot field {key!r} was {snapshot.get(key)!r}; expected {value!r}")
print(f"{source} starts empty and disarmed: {snapshot}")
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

assert_string_occurs() {
  local needle="$1"
  local expected_count="$2"
  local ascii_file="$3"
  local utf16_file="$4"
  local count
  count="$({ grep -Fo -- "${needle}" "${ascii_file}" || true; \
             grep -Fo -- "${needle}" "${utf16_file}" || true; } | wc -l)"
  if [[ "${count}" -ne "${expected_count}" ]]; then
    echo "Production publish contained ${count} copies of reviewed string ${needle}; expected exactly ${expected_count}." >&2
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
    'test "$(id -un)" = flexweb && systemctl --user is-active --quiet aethersdr-web.service'
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

echo "Running independent TX watchdog tests..."
dotnet test "${WATCHDOG_TEST_PROJECT}" --configuration Release --no-build

echo "Running TX-HIL isolation tests..."
dotnet test "${HIL_TEST_PROJECT}" --configuration Release --no-build

echo "Running AetherRemote tests..."
dotnet test "${REMOTE_TEST_PROJECT}" --configuration Release --no-build

echo "Running browser tests..."
node --check "${REPO_ROOT}/prototypes/web-client/wwwroot/admin-page.js"
node --check "${REPO_ROOT}/prototypes/web-client/wwwroot/tx-controls.js"
node --test "${UI_TEST_DIR}"/*.test.mjs

echo "Publishing RX-only-default FlexWeb artifact with disabled production TX and emergency-unkey transports..."
publish_dir="${work_dir}/publish"
archive="${work_dir}/${release_name}.tar.gz"
mkdir -p -- "${publish_dir}"
dotnet publish "${WEB_PROJECT}" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  -p:EnableTxHil=false \
  --output "${publish_dir}"

watchdog_publish_dir="${publish_dir}/watchdog"
mkdir -p -- "${watchdog_publish_dir}"
echo "Publishing Disarmed independent watchdog artifact with disabled unkey transport..."
dotnet publish "${WATCHDOG_PROJECT}" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  --output "${watchdog_publish_dir}"

binary="${publish_dir}/AetherSDR.Web"
managed_binary="${publish_dir}/AetherSDR.Web.dll"
watchdog_binary="${watchdog_publish_dir}/AetherSDR.TxWatchdog"
watchdog_managed_binary="${watchdog_publish_dir}/AetherSDR.TxWatchdog.dll"
for published_executable in "${binary}" "${watchdog_binary}"; do
  [[ -f "${published_executable}" ]] || {
    echo "Published executable is missing: ${published_executable}" >&2
    exit 1
  }
  # Some reviewed shared worktrees are hosted on CIFS, which strips the
  # generated apphost execute bit before dotnet publish copies it. Normalize
  # only the two known Linux entry points in the local publish tree.
  chmod 0755 -- "${published_executable}"
done
[[ -x "${binary}" ]] || {
  echo "Published AetherSDR.Web binary is not executable." >&2
  exit 1
}
[[ -s "${managed_binary}" ]] || {
  echo "Published AetherSDR.Web managed assembly is missing or empty." >&2
  exit 1
}
[[ -s "${publish_dir}/wwwroot/tx-controls.js" ]] || {
  echo "Published tx-controls.js module is missing or empty." >&2
  exit 1
}
renderer_files=(
  "${publish_dir}/wwwroot/index.html"
  "${publish_dir}/wwwroot/app.js"
  "${publish_dir}/wwwroot/waterfall.js"
  "${publish_dir}/wwwroot/slice-controls.js"
  "${publish_dir}/wwwroot/styles.css"
)
for renderer_file in "${renderer_files[@]}"; do
  [[ -s "${renderer_file}" ]] || {
    echo "Published receive renderer file is missing or empty: ${renderer_file}" >&2
    exit 1
  }
done
for forbidden_renderer in \
  'data-spectrum-mode' \
  'normalizeSpectrumMode' \
  'setRenderMode' \
  'drawStackedSpectrum' \
  'traceHistory' \
  'display-mode-switch' \
  '3D stacked'; do
  if grep -F -- "${forbidden_renderer}" "${renderer_files[@]}" >/dev/null; then
    echo "Production publish contains removed alternate renderer surface: ${forbidden_renderer}" >&2
    exit 1
  fi
done
if ! grep -F -- 'window.localStorage.removeItem("aether.web.spectrumMode")' \
    "${publish_dir}/wwwroot/app.js" >/dev/null; then
  echo "Production publish does not clear the removed renderer preference." >&2
  exit 1
fi
if grep -E -- 'localStorage\.(getItem|setItem)\("aether\.web\.spectrumMode"\)' \
    "${publish_dir}/wwwroot/app.js" >/dev/null; then
  echo "Production publish still reads or writes the removed renderer preference." >&2
  exit 1
fi
[[ -x "${watchdog_binary}" ]] || {
  echo "Published AetherSDR.TxWatchdog binary is not executable." >&2
  exit 1
}
[[ -s "${watchdog_managed_binary}" ]] || {
  echo "Published AetherSDR.TxWatchdog managed assembly is missing or empty." >&2
  exit 1
}

ascii_strings="${work_dir}/production-ascii.txt"
utf16_strings="${work_dir}/production-utf16.txt"
watchdog_ascii_strings="${work_dir}/watchdog-production-ascii.txt"
watchdog_utf16_strings="${work_dir}/watchdog-production-utf16.txt"
{
  strings -a "${binary}"
  strings -a "${managed_binary}"
} > "${ascii_strings}"
{
  strings -el "${binary}"
  strings -el "${managed_binary}"
} > "${utf16_strings}"
{
  strings -a "${watchdog_binary}"
  strings -a "${watchdog_managed_binary}"
} > "${watchdog_ascii_strings}"
{
  strings -el "${watchdog_binary}"
  strings -el "${watchdog_managed_binary}"
} > "${watchdog_utf16_strings}"
assert_string_occurs 'xmit 1' 1 "${ascii_strings}" "${utf16_strings}"
assert_string_occurs 'xmit 0' 1 "${ascii_strings}" "${utf16_strings}"
if ! grep -F -- 'StationTxProductionCommandTransport' "${utf16_strings}" >/dev/null ||
   ! grep -F -- 'StationTxProductionEmergencyUnkeyTransport' "${utf16_strings}" >/dev/null; then
  echo "Production web artifact is missing a reviewed primary or emergency transport type marker." >&2
  exit 1
fi
assert_forbidden_string_absent \
  'xmit 1' "${watchdog_ascii_strings}" "${watchdog_utf16_strings}"
assert_string_occurs \
  'xmit 0' 1 "${watchdog_ascii_strings}" "${watchdog_utf16_strings}"
for forbidden in \
  'cwx send' \
  'HilGatewayAuthorityChild' \
  'internal-engine-process-child' \
  'AETHERSDR_TX_HIL' \
  'dax tx'; do
  assert_forbidden_string_absent \
    "${forbidden}" "${ascii_strings}" "${utf16_strings}"
  assert_forbidden_string_absent \
    "${forbidden}" "${watchdog_ascii_strings}" "${watchdog_utf16_strings}"
done
if ! grep -F -- '"/tx-controls.js"' \
    "${REPO_ROOT}/prototypes/web-client/Program.cs" >/dev/null; then
  echo "Production Program.cs does not contain the authenticated tx-controls.js route." >&2
  exit 1
fi
PUBLISHED_APPSETTINGS="${publish_dir}/appsettings.json" python3 - <<'PY'
import json
import os
from pathlib import Path

path = Path(os.environ["PUBLISHED_APPSETTINGS"])
payload = json.loads(path.read_text(encoding="utf-8"))
transport = payload.get("StationTxCommandTransport")
expected = {
    "Enabled": False,
    "AllowedRadioIds": [],
    "CommandTimeoutMilliseconds": 2000,
}
if transport != expected:
    raise SystemExit(
        f"Published StationTxCommandTransport defaults were {transport!r}; expected {expected!r}")
PY

echo "Production web artifact contains one reviewed key string and one deduplicated unkey string with both reviewed transport type markers; the watchdog contains one reviewed unkey string, zero key strings, and all other TX/HIL surfaces remain absent."

watchdog_status="$(
  printf '%s\n' \
    '{"protocolVersion":1,"requestId":"artifact-status","type":"status"}' |
    "${watchdog_binary}" --stdio
)"
assert_watchdog_disarmed \
  "${watchdog_status}" "local independent watchdog artifact"

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

ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
  'systemctl --user restart aethersdr-web.service'

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
remote_module_status="$(ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
  'curl -sS --max-time 5 -o /dev/null -w "%{http_code}" -H "Host: flexweb.w4car.org" http://127.0.0.1:5080/tx-controls.js')"
case "${remote_module_status}" in
  200|302|401|403) ;;
  *)
    echo "Internal tx-controls.js route returned HTTP ${remote_module_status}." >&2
    exit 1
    ;;
esac

active_release="$(ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
  'readlink -f /home/flexweb/aethersdr/current')"
[[ "${active_release}" == "${expected_release}" ]] || {
  echo "Active release mismatch: ${active_release}" >&2
  exit 1
}
ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
  'systemctl --user is-active --quiet aethersdr-web.service'
remote_watchdog_status="$(ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
  'test -x /home/flexweb/aethersdr/current/watchdog/AetherSDR.TxWatchdog && printf '\''%s\n'\'' '\''{"protocolVersion":1,"requestId":"artifact-status","type":"status"}'\'' | /home/flexweb/aethersdr/current/watchdog/AetherSDR.TxWatchdog --stdio')"
assert_watchdog_disarmed \
  "${remote_watchdog_status}" "deployed independent watchdog artifact"

public_health="$(curl --fail --silent --show-error --max-time 15 \
  "${PUBLIC_HEALTH_URL}")"
assert_health_fail_closed "${public_health}" "public FlexWeb"
public_module_status="$(curl --silent --show-error --max-time 15 \
  --output /dev/null --write-out '%{http_code}' \
  'https://flexweb.w4car.org/tx-controls.js')"
case "${public_module_status}" in
  200|302|401|403) ;;
  *)
    echo "Public tx-controls.js route returned HTTP ${public_module_status}." >&2
    exit 1
    ;;
esac

deployment_succeeded=true
trap - EXIT INT TERM
if [[ -n "${remote_dir}" ]]; then
  ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
    "rm -rf -- '${remote_dir}'" >/dev/null 2>&1 || true
fi
rm -rf -- "${work_dir}"

echo
echo "FlexWeb validation deployment completed successfully."
echo "Active release: ${active_release}"
echo "Previous release retained for rollback: ${previous_release}"
echo "Deployment log: ${DEPLOY_LOG_FILE}"
echo "Git was not committed or pushed. Browser Bridge acceptance is still required."
