#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

usage() {
  cat <<'EOF'
Build, transfer, activate, verify, and automatically roll back the Aether stack.

Usage:
  bash prototypes/web-client/deploy/deploy-aether-stack.sh [options]

Options:
  --release NAME     Use an explicit release name.
  --skip-tests       Skip automated tests before publishing.
  --gateway-only     Deploy FlexWeb and the AetherRemote broker only.
  --remote-only      Deploy the station engine and AetherRemote agent only.
  -h, --help         Show this help.

Environment overrides:
  AETHERREMOTE_ROOT  Path to the AetherRemote checkout.
  FLEXWEB_HOST       SSH alias for the gateway (default: flexweb-gateway).
  REMOTE_HOST        SSH alias for the station (default: aetherremote-station).
  PUBLIC_HEALTH_URL  Optional public FlexWeb health URL.
  DEPLOY_LOG_DIR     Log directory (default: ~/.local/state/aethersdr-web/deploy-logs).

The script preserves live secrets and state, applies only explicit versioned
configuration migrations with rollback backups, and deploys published binaries
and static assets. Station and gateway credentials, FlexWeb secrets, Data
Protection keys, policy state, and audit state are never regenerated.
EOF
}

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
WEB_REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
AETHERREMOTE_ROOT="${AETHERREMOTE_ROOT:-${WEB_REPO_ROOT}/AetherRemote}"
FLEXWEB_HOST="${FLEXWEB_HOST:-flexweb-gateway}"
REMOTE_HOST="${REMOTE_HOST:-aetherremote-station}"
PUBLIC_HEALTH_URL="${PUBLIC_HEALTH_URL:-https://flexweb.w4car.org/healthz}"

release_name="${RELEASE_NAME:-$(date -u +%Y%m%d-%H%M%S)-aether-stack}"
run_tests=true
deploy_gateway=true
deploy_remote=true

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --release)
      [[ "$#" -ge 2 ]] || { echo "--release requires a value." >&2; exit 2; }
      release_name="$2"
      shift 2
      ;;
    --skip-tests)
      run_tests=false
      shift
      ;;
    --gateway-only)
      deploy_gateway=true
      deploy_remote=false
      shift
      ;;
    --remote-only)
      deploy_gateway=false
      deploy_remote=true
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

DEPLOY_LOG_DIR="${DEPLOY_LOG_DIR:-${XDG_STATE_HOME:-${HOME}/.local/state}/aethersdr-web/deploy-logs}"
mkdir -p -- "${DEPLOY_LOG_DIR}"
DEPLOY_LOG_FILE="${DEPLOY_LOG_DIR}/${release_name}.txt"
: > "${DEPLOY_LOG_FILE}"
ln -sfn -- "$(basename -- "${DEPLOY_LOG_FILE}")" \
  "${DEPLOY_LOG_DIR}/latest.txt"
exec > >(tee -a "${DEPLOY_LOG_FILE}") 2>&1

WEB_PROJECT="${WEB_REPO_ROOT}/prototypes/web-client/AetherSDR.Web.csproj"
WEB_TEST_PROJECT="${WEB_REPO_ROOT}/prototypes/web-client/tests/AetherSDR.Web.Tests.csproj"
WEB_UI_TESTS="${WEB_REPO_ROOT}/prototypes/web-client/tests-ui"
WEB_ACTIVATOR="${WEB_REPO_ROOT}/prototypes/web-client/deploy/activate-release.sh"
BROKER_PROJECT="${AETHERREMOTE_ROOT}/src/AetherRemote.Broker/AetherRemote.Broker.csproj"
AGENT_PROJECT="${AETHERREMOTE_ROOT}/src/AetherRemote.Agent/AetherRemote.Agent.csproj"
REMOTE_TEST_PROJECT="${AETHERREMOTE_ROOT}/tests/AetherRemote.Tests/AetherRemote.Tests.csproj"
WAN_SOAK_HELPER="${AETHERREMOTE_ROOT}/deploy/aetherremote-wan-soak.sh"

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "Required command is missing: $1" >&2
    exit 1
  }
}

for command_name in dotnet ssh scp tar sha256sum mktemp awk grep; do
  require_command "${command_name}"
done
if [[ "${run_tests}" == true ]]; then
  require_command node
fi

for required_path in \
  "${WEB_PROJECT}" \
  "${WEB_TEST_PROJECT}" \
  "${WEB_ACTIVATOR}" \
  "${BROKER_PROJECT}" \
  "${AGENT_PROJECT}" \
  "${REMOTE_TEST_PROJECT}" \
  "${WAN_SOAK_HELPER}"; do
  if [[ ! -e "${required_path}" ]]; then
    echo "Required project file is missing: ${required_path}" >&2
    exit 1
  fi
done

work_dir="$(mktemp -d "${TMPDIR:-/tmp}/aether-stack.${release_name}.XXXXXX")"
flexweb_remote_dir=""
station_remote_dir=""

cleanup() {
  local status=$?
  if [[ -n "${flexweb_remote_dir}" ]]; then
    ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
      "rm -rf -- '${flexweb_remote_dir}'" >/dev/null 2>&1 || true
  fi
  if [[ -n "${station_remote_dir}" ]]; then
    ssh -o BatchMode=yes "${REMOTE_HOST}" \
      "rm -rf -- '${station_remote_dir}'" >/dev/null 2>&1 || true
  fi
  rm -rf -- "${work_dir}"
  exit "${status}"
}
trap cleanup EXIT INT TERM

run_remote_root() {
  local host="$1"
  local label="$2"
  local remote_command="$3"
  local password=""
  local status=0

  if ssh -o BatchMode=yes "${host}" 'sudo -n true' >/dev/null 2>&1; then
    ssh -o BatchMode=yes "${host}" "sudo -n ${remote_command}"
    return
  fi

  printf 'Remote sudo password for %s (%s): ' "${host}" "${label}" >/dev/tty
  IFS= read -r -s password </dev/tty
  printf '\n' >/dev/tty

  set +e
  printf '%s\n' "${password}" |
    ssh -o BatchMode=yes "${host}" "sudo -S -p '' ${remote_command}"
  status=$?
  set -e
  unset password
  return "${status}"
}

package_directory() {
  local source_directory="$1"
  local archive_path="$2"
  tar --create --gzip --file "${archive_path}" \
    --directory "${source_directory}" .
}

write_gateway_helper() {
  cat > "${work_dir}/install-gateway.sh" <<'REMOTE_GATEWAY'
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

fail() {
  echo "Gateway deployment failed: $*" >&2
  exit 1
}

[[ "${EUID}" -eq 0 ]] || fail "this helper must run as root"
[[ "$#" -eq 5 ]] || fail "expected bundle, release, web checksum, broker checksum, station-link requirement"

bundle_dir="$1"
release_name="$2"
web_sha="$3"
broker_sha="$4"
require_station_link="$5"

[[ "${release_name}" =~ ^[0-9A-Za-z._-]{1,96}$ ]] || fail "invalid release name"
[[ "${web_sha}" =~ ^[0-9a-f]{64}$ ]] || fail "invalid web checksum"
[[ "${broker_sha}" =~ ^[0-9a-f]{64}$ ]] || fail "invalid broker checksum"
[[ "${require_station_link}" == true || "${require_station_link}" == false ]] ||
  fail "invalid station-link requirement"

web_archive="${bundle_dir}/web.tar.gz"
broker_archive="${bundle_dir}/broker.tar.gz"
activator="${bundle_dir}/activate-release.sh"
web_root="/home/flexweb/aethersdr"
broker_dir="/opt/aetherremote/broker"
staging_root="/opt/aetherremote/.staging"
rollback_root="/opt/aetherremote/.rollback"
staged_broker="${staging_root}/broker-${release_name}-$$"
previous_web="$(readlink -f "${web_root}/current" || true)"
previous_broker=""
web_activated=false
broker_replaced=false
deployment_succeeded=false
broker_config="/etc/aetherremote/broker/appsettings.json"
web_environment="/home/flexweb/.config/aethersdr-web/environment"
administration_credential_file="/home/flexweb/aetherremote-deploy/administration-credential"
broker_config_backup="${bundle_dir}/broker-appsettings.rollback.json"
web_environment_backup="${bundle_dir}/flexweb-environment.rollback"
configuration_backup_ready=false
administration_credential_created=false

for command_name in sha256sum tar awk curl systemctl runuser readlink mv ln jq openssl cp install grep chmod chown mktemp tr; do
  command -v "${command_name}" >/dev/null 2>&1 || fail "missing command: ${command_name}"
done

verify_archive() {
  local archive="$1"
  local expected="$2"
  local required_file="$3"
  local actual=""

  [[ -f "${archive}" ]] || fail "missing archive: ${archive}"
  actual="$(sha256sum "${archive}" | awk '{print $1}')"
  [[ "${actual}" == "${expected}" ]] || fail "checksum mismatch for ${archive}"
  if tar -tzf "${archive}" | awk '
    $0 ~ /^\// || $0 ~ /(^|\/)\.\.(\/|$)/ { bad = 1 }
    END { exit bad ? 0 : 1 }
  '; then
    fail "unsafe path found in ${archive}"
  fi
  tar -tzf "${archive}" | awk -v required="./${required_file}" '
    $0 == required { found = 1 }
    END { exit found ? 0 : 1 }
  ' || fail "${archive} does not contain ${required_file}"
}

wait_for_url() {
  local url="$1"
  local attempts="${2:-30}"
  local host_header="${3:-}"
  local index=0
  local curl_arguments=(
    --fail --silent --show-error --max-time 2
  )
  if [[ -n "${host_header}" ]]; then
    curl_arguments+=(--header "Host: ${host_header}")
  fi
  for ((index = 1; index <= attempts; index++)); do
    if curl "${curl_arguments[@]}" "${url}" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  return 1
}

read_environment_value() {
  local key="$1"
  awk -F= -v key="${key}" '
    $1 == key {
      value = substr($0, index($0, "=") + 1)
    }
    END { print value }
  ' "${web_environment}"
}

migrate_gateway_credentials() {
  local runtime_credential_file=""
  local runtime_credential=""
  local administration_credential=""
  local runtime_hash=""
  local administration_hash=""
  local credential_temp=""
  local broker_temp=""
  local environment_temp=""

  [[ -f "${broker_config}" ]] || fail "broker configuration is missing"
  [[ -f "${web_environment}" ]] || fail "FlexWeb environment is missing"
  cp -a -- "${broker_config}" "${broker_config_backup}"
  cp -a -- "${web_environment}" "${web_environment_backup}"
  configuration_backup_ready=true

  runtime_credential_file="$(read_environment_value \
    RemoteStations__RuntimeCredentialFile)"
  if [[ -z "${runtime_credential_file}" ]]; then
    runtime_credential_file="$(read_environment_value \
      RemoteStations__ManagementCredentialFile)"
  fi
  [[ "${runtime_credential_file}" == /* &&
      -f "${runtime_credential_file}" ]] ||
    fail "the existing runtime credential file is invalid"
  [[ "${runtime_credential_file}" != "${administration_credential_file}" ]] ||
    fail "runtime and administration credential paths must differ"

  if [[ ! -f "${administration_credential_file}" ]]; then
    credential_temp="$(mktemp \
      "${administration_credential_file}.tmp.XXXXXX")"
    chmod 0600 "${credential_temp}"
    openssl rand -hex 32 > "${credential_temp}"
    install -o flexweb -g flexweb -m 0600 \
      "${credential_temp}" "${administration_credential_file}"
    rm -f -- "${credential_temp}"
    administration_credential_created=true
  fi
  [[ -f "${administration_credential_file}" ]] ||
    fail "the administration credential file is missing"
  chown flexweb:flexweb "${administration_credential_file}"
  chmod 0600 "${administration_credential_file}"
  runtime_credential="$(tr -d '\r\n' < "${runtime_credential_file}")"
  administration_credential="$(tr -d '\r\n' < \
    "${administration_credential_file}")"
  [[ "${runtime_credential}" =~ ^[0-9A-Fa-f]{64}$ &&
      "${administration_credential}" =~ ^[0-9A-Fa-f]{64}$ ]] ||
    fail "gateway credentials must contain 64 hexadecimal characters"
  [[ "${runtime_credential}" != "${administration_credential}" ]] ||
    fail "runtime and administration credentials must be distinct"

  runtime_hash="$(printf '%s' "${runtime_credential}" | \
    sha256sum | awk '{print $1}')"
  administration_hash="$(printf '%s' "${administration_credential}" | \
    sha256sum | awk '{print $1}')"
  unset runtime_credential administration_credential
  [[ "${runtime_hash}" =~ ^[0-9a-f]{64}$ &&
      "${administration_hash}" =~ ^[0-9a-f]{64}$ &&
      "${runtime_hash}" != "${administration_hash}" ]] ||
    fail "credential verifier generation failed"

  broker_temp="$(mktemp "${broker_config}.tmp.XXXXXX")"
  jq --arg runtime "${runtime_hash}" \
     --arg administration "${administration_hash}" '
      .StationLink.RuntimeCredentialSha256 = $runtime |
      .StationLink.AdministrationCredentialSha256 = $administration |
      .StationLink.LinkTokenSeconds =
        (.StationLink.LinkTokenSeconds // 60) |
      del(.StationLink.ManagementCredentialSha256)
    ' "${broker_config}" > "${broker_temp}"
  jq -e '
      (.StationLink.RuntimeCredentialSha256 | type == "string" and length == 64) and
      (.StationLink.AdministrationCredentialSha256 | type == "string" and length == 64) and
      (.StationLink.LinkTokenSeconds | type == "number" and
       . >= 15 and . <= 300) and
      (.StationLink.RuntimeCredentialSha256 !=
       .StationLink.AdministrationCredentialSha256) and
      (.StationLink | has("ManagementCredentialSha256") | not)
    ' "${broker_temp}" >/dev/null ||
    fail "the migrated broker credential configuration is invalid"
  chown --reference="${broker_config}" "${broker_temp}"
  chmod --reference="${broker_config}" "${broker_temp}"
  mv -f -- "${broker_temp}" "${broker_config}"

  environment_temp="$(mktemp "${web_environment}.tmp.XXXXXX")"
  awk '
    !/^RemoteStations__(ManagementCredentialFile|RuntimeCredentialFile|AdministrationCredentialFile)=/
  ' "${web_environment}" > "${environment_temp}"
  {
    printf 'RemoteStations__RuntimeCredentialFile=%s\n' \
      "${runtime_credential_file}"
    printf 'RemoteStations__AdministrationCredentialFile=%s\n' \
      "${administration_credential_file}"
  } >> "${environment_temp}"
  chown --reference="${web_environment}" "${environment_temp}"
  chmod --reference="${web_environment}" "${environment_temp}"
  mv -f -- "${environment_temp}" "${web_environment}"
}

credential_status() {
  local credential_file="$1"
  local url="$2"
  local status=""

  status="$(curl --silent --show-error --output /dev/null \
    --write-out '%{http_code}' --max-time 5 \
    --header @<(printf 'Authorization: Bearer %s\n' \
      "$(<"${credential_file}")") \
    "${url}" || true)"
  printf '%s' "${status}"
}

wait_for_token_station_link() {
  local credential_file="$1"
  local attempts="${2:-60}"
  local index=0
  local payload=""

  for ((index = 1; index <= attempts; index++)); do
    payload="$(curl --fail --silent --show-error --max-time 5 \
      --header @<(printf 'Authorization: Bearer %s\n' \
        "$(<"${credential_file}")") \
      http://127.0.0.1:5090/api/stations 2>/dev/null || true)"
    if jq -e '
        .stations | any(
          .state == "online" and
          (.capabilities | index("receive-projection-v1") != null))
      ' <<<"${payload}" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
  done
  return 1
}

rollback() {
  local status=$?
  trap - EXIT

  if [[ "${deployment_succeeded}" != true ]]; then
    echo "Rolling the gateway back to its previous binaries and credentials." >&2
    systemctl stop aethersdr-web.service >/dev/null 2>&1 || true
    systemctl stop aetherremote-broker.service >/dev/null 2>&1 || true

    if [[ "${broker_replaced}" == true && -n "${previous_broker}" && -d "${previous_broker}" ]]; then
      rm -rf -- "${broker_dir}"
      mv -- "${previous_broker}" "${broker_dir}"
    fi

    if [[ "${web_activated}" == true ]]; then
      if [[ -n "${previous_web}" && -d "${previous_web}" ]]; then
        rollback_link="${web_root}/.current.rollback.$$"
        ln -s "${previous_web}" "${rollback_link}"
        mv -Tf "${rollback_link}" "${web_root}/current"
      else
        rm -f -- "${web_root}/current"
      fi
      rm -rf -- "${web_root}/releases/${release_name}"
    fi

    if [[ "${configuration_backup_ready}" == true ]]; then
      cp -a -- "${broker_config_backup}" "${broker_config}"
      cp -a -- "${web_environment_backup}" "${web_environment}"
    fi
    if [[ "${administration_credential_created}" == true ]]; then
      rm -f -- "${administration_credential_file}"
    fi

    systemctl start aetherremote-broker.service >/dev/null 2>&1 || true
    systemctl start aethersdr-web.service >/dev/null 2>&1 || true
    journalctl -u aetherremote-broker.service -n 40 --no-pager >&2 || true
    journalctl -u aethersdr-web.service -n 40 --no-pager >&2 || true
  fi

  rm -rf -- "${staged_broker}" "${bundle_dir}"
  exit "${status}"
}
trap rollback EXIT

[[ -x "${activator}" || -f "${activator}" ]] || fail "release activator is missing"
[[ -f /etc/aetherremote/broker/appsettings.json ]] || fail "broker configuration is missing"
[[ -d "${broker_dir}" ]] || fail "current broker installation is missing"
[[ -f "${broker_dir}/AetherRemote.Broker" ]] || fail "current broker binary is missing"
[[ -d "${web_root}/incoming" && -d "${web_root}/releases" ]] ||
  fail "FlexWeb release directories are missing"

verify_archive "${web_archive}" "${web_sha}" "AetherSDR.Web"
verify_archive "${broker_archive}" "${broker_sha}" "AetherRemote.Broker"

mkdir -p -- "${staging_root}" "${rollback_root}"
rm -rf -- "${staged_broker}"
mkdir -m 0755 -- "${staged_broker}"
tar --extract --gzip --file "${broker_archive}" \
  --directory "${staged_broker}" \
  --no-same-owner --no-same-permissions
chown -R root:root "${staged_broker}"
chmod 0755 "${staged_broker}" "${staged_broker}/AetherRemote.Broker"

migrate_gateway_credentials

install -o flexweb -g flexweb -m 0640 \
  "${web_archive}" "${web_root}/incoming/${release_name}.tar.gz"
runuser -u flexweb -- bash "${activator}" \
  "${web_root}" "${release_name}" "${web_sha}"
web_activated=true

systemctl stop aethersdr-web.service
systemctl stop aetherremote-broker.service
previous_broker="${rollback_root}/broker-${release_name}-$(date -u +%Y%m%d-%H%M%S)-$$"
mv -- "${broker_dir}" "${previous_broker}"
mv -- "${staged_broker}" "${broker_dir}"
broker_replaced=true

systemctl start aetherremote-broker.service
wait_for_url "http://127.0.0.1:5090/healthz" 30 ||
  fail "broker did not pass its health check"

systemctl start aethersdr-web.service
wait_for_url "http://127.0.0.1:5080/healthz" 45 "flexweb.w4car.org" ||
  fail "FlexWeb did not pass its health check"

systemctl is-active --quiet aetherremote-broker.service || fail "broker is not active"
systemctl is-active --quiet aethersdr-web.service || fail "FlexWeb is not active"

runtime_credential_file="$(read_environment_value \
  RemoteStations__RuntimeCredentialFile)"
[[ "$(credential_status "${runtime_credential_file}" \
  http://127.0.0.1:5090/api/stations)" == "200" ]] ||
  fail "runtime credential cannot read station inventory"
[[ "$(credential_status "${runtime_credential_file}" \
  http://127.0.0.1:5090/api/station-credentials)" == "401" ]] ||
  fail "runtime credential crossed into station administration"
[[ "$(credential_status "${administration_credential_file}" \
  http://127.0.0.1:5090/api/station-credentials)" == "200" ]] ||
  fail "administration credential cannot read station security inventory"
[[ "$(credential_status "${administration_credential_file}" \
  http://127.0.0.1:5090/api/stations)" == "401" ]] ||
  fail "administration credential crossed into runtime inventory"
if [[ "${require_station_link}" == true ]]; then
  wait_for_token_station_link "${runtime_credential_file}" 75 ||
    fail "no capability-bound station reconnected through a short-lived token"
fi

deployment_succeeded=true
trap - EXIT
rm -rf -- "${bundle_dir}"

echo "Gateway deployment completed."
echo "  FlexWeb release: $(readlink -f "${web_root}/current")"
echo "  Broker binary:  ${broker_dir}/AetherRemote.Broker"
echo "  Broker backup:  ${previous_broker}"
REMOTE_GATEWAY
}

write_station_helper() {
  cat > "${work_dir}/install-station.sh" <<'REMOTE_STATION'
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

fail() {
  echo "Station deployment failed: $*" >&2
  exit 1
}

[[ "${EUID}" -eq 0 ]] || fail "this helper must run as root"
[[ "$#" -eq 5 ]] || fail "expected bundle, release, engine checksum, agent checksum, soak-helper checksum"

bundle_dir="$1"
release_name="$2"
engine_sha="$3"
agent_sha="$4"
soak_helper_sha="$5"

[[ "${release_name}" =~ ^[0-9A-Za-z._-]{1,96}$ ]] || fail "invalid release name"
[[ "${engine_sha}" =~ ^[0-9a-f]{64}$ ]] || fail "invalid engine checksum"
[[ "${agent_sha}" =~ ^[0-9a-f]{64}$ ]] || fail "invalid agent checksum"
[[ "${soak_helper_sha}" =~ ^[0-9a-f]{64}$ ]] || fail "invalid soak-helper checksum"

engine_archive="${bundle_dir}/web.tar.gz"
agent_archive="${bundle_dir}/agent.tar.gz"
soak_helper_source="${bundle_dir}/aetherremote-wan-soak.sh"
soak_helper_path="/usr/local/sbin/aetherremote-wan-soak"
soak_sudoers_path="/etc/sudoers.d/92-aetherremote-wan-soak"
engine_dir="/opt/aetherremote/station-engine"
agent_dir="/opt/aetherremote/agent"
agent_config="/etc/aetherremote/agent/appsettings.json"
staging_root="/opt/aetherremote/.staging"
rollback_root="/opt/aetherremote/.rollback"
staged_engine="${staging_root}/station-engine-${release_name}-$$"
staged_agent="${staging_root}/agent-${release_name}-$$"
previous_engine=""
previous_agent=""
previous_agent_config=""
engine_replaced=false
agent_replaced=false
agent_config_updated=false
deployment_succeeded=false

for command_name in sha256sum tar awk bash curl install systemctl journalctl mv visudo jq cp chown chmod grep mktemp date; do
  command -v "${command_name}" >/dev/null 2>&1 || fail "missing command: ${command_name}"
done

verify_archive() {
  local archive="$1"
  local expected="$2"
  local required_file="$3"
  local actual=""

  [[ -f "${archive}" ]] || fail "missing archive: ${archive}"
  actual="$(sha256sum "${archive}" | awk '{print $1}')"
  [[ "${actual}" == "${expected}" ]] || fail "checksum mismatch for ${archive}"
  if tar -tzf "${archive}" | awk '
    $0 ~ /^\// || $0 ~ /(^|\/)\.\.(\/|$)/ { bad = 1 }
    END { exit bad ? 0 : 1 }
  '; then
    fail "unsafe path found in ${archive}"
  fi
  tar -tzf "${archive}" | awk -v required="./${required_file}" '
    $0 == required { found = 1 }
    END { exit found ? 0 : 1 }
  ' || fail "${archive} does not contain ${required_file}"
}

wait_for_url() {
  local url="$1"
  local attempts="${2:-30}"
  local index=0
  for ((index = 1; index <= attempts; index++)); do
    if curl --fail --silent --show-error --max-time 2 "${url}" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  return 1
}

wait_for_agent_link() {
  local started_at="$1"
  local attempts="${2:-45}"
  local index=0
  for ((index = 1; index <= attempts; index++)); do
    systemctl is-active --quiet aetherremote-agent.service || return 1
    if journalctl -u aetherremote-agent.service \
      --since "@${started_at}" --no-pager 2>/dev/null |
      grep -Fq 'connected to broker as'; then
      return 0
    fi
    sleep 1
  done
  return 1
}

ensure_explicit_station_capabilities() {
  local temporary=""

  jq -e '.Agent | type == "object"' "${agent_config}" >/dev/null ||
    fail "agent configuration does not contain an Agent object"
  if jq -e '.Agent.Capabilities != null' "${agent_config}" >/dev/null; then
    return
  fi

  previous_agent_config="${rollback_root}/agent-config-${release_name}-$(date -u +%Y%m%d-%H%M%S)-$$.json"
  cp --preserve=all -- "${agent_config}" "${previous_agent_config}"
  temporary="$(mktemp "${agent_config}.tmp.XXXXXX")"
  jq '.Agent.Capabilities = ["receive-projection-v1"]' \
    "${agent_config}" > "${temporary}"
  chown --reference="${agent_config}" "${temporary}"
  chmod --reference="${agent_config}" "${temporary}"
  mv -f -- "${temporary}" "${agent_config}"
  agent_config_updated=true
}

rollback() {
  local status=$?
  trap - EXIT

  if [[ "${deployment_succeeded}" != true ]]; then
    echo "Rolling the station back to its previous binaries." >&2
    systemctl stop aetherremote-agent.service >/dev/null 2>&1 || true
    systemctl stop aetherremote-station-engine.service >/dev/null 2>&1 || true

    if [[ "${agent_config_updated}" == true &&
          -n "${previous_agent_config}" &&
          -f "${previous_agent_config}" ]]; then
      rm -f -- "${agent_config}"
      mv -- "${previous_agent_config}" "${agent_config}"
    fi
    if [[ "${agent_replaced}" == true && -n "${previous_agent}" && -d "${previous_agent}" ]]; then
      rm -rf -- "${agent_dir}"
      mv -- "${previous_agent}" "${agent_dir}"
    fi
    if [[ "${engine_replaced}" == true && -n "${previous_engine}" && -d "${previous_engine}" ]]; then
      rm -rf -- "${engine_dir}"
      mv -- "${previous_engine}" "${engine_dir}"
    fi

    systemctl start aetherremote-station-engine.service >/dev/null 2>&1 || true
    systemctl start aetherremote-agent.service >/dev/null 2>&1 || true
    journalctl -u aetherremote-station-engine.service -n 40 --no-pager >&2 || true
    journalctl -u aetherremote-agent.service -n 60 --no-pager >&2 || true
  fi

  rm -rf -- "${staged_engine}" "${staged_agent}" "${bundle_dir}"
  exit "${status}"
}
trap rollback EXIT

[[ -f "${agent_config}" ]] || fail "agent configuration is missing"
[[ -f /etc/aetherremote/station-engine/appsettings.json ]] || fail "station-engine configuration is missing"
[[ -f /etc/aetherremote/station-credential ]] || fail "station credential is missing"
[[ -f "${agent_dir}/AetherRemote.Agent" ]] || fail "current agent binary is missing"
[[ -f "${engine_dir}/AetherSDR.Web" ]] || fail "current station-engine binary is missing"

verify_archive "${engine_archive}" "${engine_sha}" "AetherSDR.Web"
verify_archive "${agent_archive}" "${agent_sha}" "AetherRemote.Agent"
[[ -f "${soak_helper_source}" ]] || fail "WAN soak helper is missing"
[[ "$(sha256sum "${soak_helper_source}" | awk '{print $1}')" == "${soak_helper_sha}" ]] ||
  fail "checksum mismatch for WAN soak helper"
bash -n "${soak_helper_source}" || fail "WAN soak helper has invalid shell syntax"

mkdir -p -- "${staging_root}" "${rollback_root}"
rm -rf -- "${staged_engine}" "${staged_agent}"
mkdir -m 0755 -- "${staged_engine}" "${staged_agent}"
tar --extract --gzip --file "${engine_archive}" \
  --directory "${staged_engine}" \
  --no-same-owner --no-same-permissions
tar --extract --gzip --file "${agent_archive}" \
  --directory "${staged_agent}" \
  --no-same-owner --no-same-permissions
chown -R root:root "${staged_engine}" "${staged_agent}"
chmod 0755 "${staged_engine}" "${staged_engine}/AetherSDR.Web"
chmod 0755 "${staged_agent}" "${staged_agent}/AetherRemote.Agent"

ensure_explicit_station_capabilities

systemctl stop aetherremote-agent.service
systemctl stop aetherremote-station-engine.service

previous_engine="${rollback_root}/station-engine-${release_name}-$(date -u +%Y%m%d-%H%M%S)-$$"
previous_agent="${rollback_root}/agent-${release_name}-$(date -u +%Y%m%d-%H%M%S)-$$"
mv -- "${engine_dir}" "${previous_engine}"
mv -- "${staged_engine}" "${engine_dir}"
engine_replaced=true
mv -- "${agent_dir}" "${previous_agent}"
mv -- "${staged_agent}" "${agent_dir}"
agent_replaced=true

systemctl start aetherremote-station-engine.service
wait_for_url "http://127.0.0.1:5081/healthz" 45 ||
  fail "station engine did not pass its health check"

agent_started_at="$(date +%s)"
systemctl start aetherremote-agent.service
wait_for_agent_link "${agent_started_at}" 60 ||
  fail "station agent did not establish a fresh broker link"

systemctl is-active --quiet aetherremote-station-engine.service ||
  fail "station engine is not active"
systemctl is-active --quiet aetherremote-agent.service ||
  fail "station agent is not active"

sudoers_temp="$(mktemp /tmp/aetherremote-wan-soak-sudoers.XXXXXX)"
printf '%s\n' \
  'aetherremote ALL=(root) NOPASSWD: /usr/local/sbin/aetherremote-wan-soak *' \
  > "${sudoers_temp}"
chmod 0440 "${sudoers_temp}"
visudo -cf "${sudoers_temp}" >/dev/null || fail "WAN soak sudoers rule is invalid"
install -o root -g root -m 0755 "${soak_helper_source}" "${soak_helper_path}"
install -o root -g root -m 0440 "${sudoers_temp}" "${soak_sudoers_path}"
rm -f -- "${sudoers_temp}"
"${soak_helper_path}" status >/dev/null || fail "WAN soak helper did not pass its inactive status check"

deployment_succeeded=true
trap - EXIT
rm -rf -- "${bundle_dir}"

echo "Station deployment completed."
echo "  Station engine: ${engine_dir}/AetherSDR.Web"
echo "  Agent:          ${agent_dir}/AetherRemote.Agent"
echo "  Engine backup:  ${previous_engine}"
echo "  Agent backup:   ${previous_agent}"
if [[ -n "${previous_agent_config}" ]]; then
  echo "  Config backup:  ${previous_agent_config}"
fi
REMOTE_STATION
}

printf 'Release: %s\n' "${release_name}"
printf 'Web checkout: %s\n' "${WEB_REPO_ROOT}"
printf 'AetherRemote checkout: %s\n' "${AETHERREMOTE_ROOT}"

if [[ "${deploy_gateway}" == true ]]; then
  echo "Checking SSH access to ${FLEXWEB_HOST}..."
  ssh -o BatchMode=yes -o ConnectTimeout=10 "${FLEXWEB_HOST}" \
    'test "$(id -un)" = flexweb && systemctl is-active --quiet aethersdr-web.service && systemctl is-active --quiet aetherremote-broker.service'
fi
if [[ "${deploy_remote}" == true ]]; then
  echo "Checking SSH access to ${REMOTE_HOST}..."
  ssh -o BatchMode=yes -o ConnectTimeout=10 "${REMOTE_HOST}" \
    'test "$(id -un)" = aetherremote && systemctl is-active --quiet aetherremote-agent.service && systemctl is-active --quiet aetherremote-station-engine.service'
fi

if [[ "${run_tests}" == true ]]; then
  echo "Running FlexWeb server tests..."
  dotnet test "${WEB_TEST_PROJECT}" --configuration Release

  echo "Running FlexWeb browser tests..."
  node --check "${WEB_REPO_ROOT}/prototypes/web-client/wwwroot/admin-page.js"
  node --test "${WEB_UI_TESTS}"/*.test.mjs

  echo "Running AetherRemote tests..."
  dotnet test "${REMOTE_TEST_PROJECT}" --configuration Release
else
  echo "WARNING: automated tests were skipped."
fi

web_publish="${work_dir}/publish-web"
web_archive="${work_dir}/web.tar.gz"
mkdir -p -- "${web_publish}"
echo "Publishing AetherSDR.Web for linux-x64..."
dotnet publish "${WEB_PROJECT}" \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained true \
  -p:EnableTxHil=false \
  --output "${web_publish}"
package_directory "${web_publish}" "${web_archive}"
web_sha="$(sha256sum "${web_archive}" | awk '{print $1}')"

broker_archive=""
broker_sha=""
if [[ "${deploy_gateway}" == true ]]; then
  broker_publish="${work_dir}/publish-broker"
  broker_archive="${work_dir}/broker.tar.gz"
  mkdir -p -- "${broker_publish}"
  echo "Publishing AetherRemote.Broker for linux-x64..."
  dotnet publish "${BROKER_PROJECT}" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --output "${broker_publish}"
  package_directory "${broker_publish}" "${broker_archive}"
  broker_sha="$(sha256sum "${broker_archive}" | awk '{print $1}')"
fi

agent_archive=""
agent_sha=""
wan_soak_sha=""
if [[ "${deploy_remote}" == true ]]; then
  agent_publish="${work_dir}/publish-agent"
  agent_archive="${work_dir}/agent.tar.gz"
  mkdir -p -- "${agent_publish}"
  echo "Publishing AetherRemote.Agent for linux-x64..."
  dotnet publish "${AGENT_PROJECT}" \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    --output "${agent_publish}"
  package_directory "${agent_publish}" "${agent_archive}"
  agent_sha="$(sha256sum "${agent_archive}" | awk '{print $1}')"
  wan_soak_sha="$(sha256sum "${WAN_SOAK_HELPER}" | awk '{print $1}')"
fi

if [[ "${deploy_remote}" == true ]]; then
  write_station_helper
  station_remote_dir="$(ssh -o BatchMode=yes "${REMOTE_HOST}" \
    'mktemp -d /tmp/aether-deploy.XXXXXX')"
  echo "Transferring station-engine and agent packages to ${REMOTE_HOST}..."
  scp \
    "${web_archive}" \
    "${agent_archive}" \
    "${WAN_SOAK_HELPER}" \
    "${work_dir}/install-station.sh" \
    "${REMOTE_HOST}:${station_remote_dir}/"

  run_remote_root "${REMOTE_HOST}" "station deployment" \
    "bash '${station_remote_dir}/install-station.sh' '${station_remote_dir}' '${release_name}' '${web_sha}' '${agent_sha}' '${wan_soak_sha}'"
  station_remote_dir=""
fi

if [[ "${deploy_gateway}" == true ]]; then
  write_gateway_helper
  flexweb_remote_dir="$(ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
    'mktemp -d /tmp/aether-deploy.XXXXXX')"
  echo "Transferring FlexWeb and broker packages to ${FLEXWEB_HOST}..."
  scp \
    "${web_archive}" \
    "${broker_archive}" \
    "${WEB_ACTIVATOR}" \
    "${work_dir}/install-gateway.sh" \
    "${FLEXWEB_HOST}:${flexweb_remote_dir}/"

  require_station_link=false
  if [[ "${deploy_remote}" == true ]]; then
    require_station_link=true
  fi
  run_remote_root "${FLEXWEB_HOST}" "gateway deployment" \
    "bash '${flexweb_remote_dir}/install-gateway.sh' '${flexweb_remote_dir}' '${release_name}' '${web_sha}' '${broker_sha}' '${require_station_link}'"
  flexweb_remote_dir=""
fi

if [[ "${deploy_gateway}" == true ]]; then
  echo "Verifying gateway services after deployment..."
  ssh -o BatchMode=yes "${FLEXWEB_HOST}" \
    'systemctl is-active aetherremote-broker.service; systemctl is-active aethersdr-web.service; curl -fsS http://127.0.0.1:5090/healthz; echo; curl -fsS -H "Host: flexweb.w4car.org" http://127.0.0.1:5080/healthz; echo; readlink -f /home/flexweb/aethersdr/current'

  if command -v curl >/dev/null 2>&1 && [[ -n "${PUBLIC_HEALTH_URL}" ]]; then
    echo "Checking public FlexWeb endpoint: ${PUBLIC_HEALTH_URL}"
    if curl --fail --silent --show-error --max-time 10 "${PUBLIC_HEALTH_URL}"; then
      echo
    else
      echo "WARNING: the internal health check passed, but the public endpoint check failed." >&2
    fi
  fi
fi

if [[ "${deploy_remote}" == true ]]; then
  echo "Verifying station services after deployment..."
  ssh -o BatchMode=yes "${REMOTE_HOST}" \
    'systemctl is-active aetherremote-station-engine.service; systemctl is-active aetherremote-agent.service; curl -fsS http://127.0.0.1:5081/healthz; echo; sudo -n /usr/local/sbin/aetherremote-wan-soak status; journalctl -u aetherremote-agent.service -n 12 --no-pager'
fi

echo
echo "Deployment ${release_name} completed successfully."
echo "Live configuration and credentials were preserved."
echo "Deployment log: ${DEPLOY_LOG_FILE}"
