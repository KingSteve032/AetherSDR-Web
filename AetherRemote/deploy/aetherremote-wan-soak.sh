#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

STATE_FILE="/run/aetherremote-wan-soak.state"
DEFAULT_BROKER_HOST="flexweb.w4car.org"
DEFAULT_BROKER_PORT=443

usage() {
  cat <<'EOF'
Apply a narrowly scoped outbound WAN impairment profile to the AetherRemote
station link. Only IPv4 TCP traffic to the selected broker IP and port is
impaired; SSH and all other station traffic remain on the normal qdisc.

Usage:
  sudo aetherremote-wan-soak apply PROFILE [options]
  sudo aetherremote-wan-soak run PROFILE DURATION_SECONDS [options]
  sudo aetherremote-wan-soak clear
  sudo aetherremote-wan-soak status

Profiles:
  mild         40 ms delay, 10 ms jitter, 0.2% loss, 8 Mbit/s
  constrained  90 ms delay, 25 ms jitter, 1.0% loss, 3 Mbit/s
  severe       180 ms delay, 60 ms jitter, 3.0% loss, 1.2 Mbit/s

Options:
  --broker-host HOST   Broker DNS name (default: flexweb.w4car.org)
  --broker-ip IPV4     Pin the exact broker IPv4 address instead of DNS lookup
  --broker-port PORT   Broker TCP port (default: 443)
  --interface IFACE    Pin the egress interface instead of route discovery

The helper refuses to replace an unknown root qdisc. On this deployment the
expected normal qdisc is fq_codel. `run` always clears the impairment on exit,
including Ctrl-C and ordinary shell termination.
EOF
}

fail() {
  echo "WAN soak error: $*" >&2
  exit 1
}

require_root() {
  [[ "${EUID}" -eq 0 ]] || fail "run this command with sudo"
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "missing command: $1"
}

validate_ipv4() {
  local value="$1"
  local octet=""
  local -a parts=()
  IFS='.' read -r -a parts <<<"${value}"
  [[ "${#parts[@]}" -eq 4 ]] || return 1
  for octet in "${parts[@]}"; do
    [[ "${octet}" =~ ^[0-9]{1,3}$ ]] || return 1
    ((10#${octet} <= 255)) || return 1
  done
}

validate_host() {
  [[ "$1" =~ ^[A-Za-z0-9.-]{1,253}$ ]] &&
    [[ "$1" != .* ]] && [[ "$1" != *. ]]
}

validate_interface() {
  [[ "$1" =~ ^[A-Za-z0-9_.:-]{1,32}$ ]]
}

validate_port() {
  [[ "$1" =~ ^[0-9]{1,5}$ ]] && ((10#$1 >= 1 && 10#$1 <= 65535))
}

resolve_broker_ip() {
  local host="$1"
  local resolved=""
  resolved="$(getent ahostsv4 "${host}" | awk '{print $1}' | sort -u | head -n 1)"
  [[ -n "${resolved}" ]] || fail "could not resolve an IPv4 address for ${host}"
  validate_ipv4 "${resolved}" || fail "resolver returned an invalid IPv4 address"
  printf '%s\n' "${resolved}"
}

discover_interface() {
  local broker_ip="$1"
  local interface=""
  interface="$(ip -4 route get "${broker_ip}" | awk '{for (i=1; i<=NF; i++) if ($i == "dev") {print $(i+1); exit}}')"
  [[ -n "${interface}" ]] || fail "could not determine the route to ${broker_ip}"
  validate_interface "${interface}" || fail "route returned an invalid interface"
  [[ "${interface}" != "lo" ]] || fail "refusing to impair the loopback interface"
  printf '%s\n' "${interface}"
}

root_qdisc_kind() {
  local interface="$1"
  tc qdisc show dev "${interface}" |
    awk '$0 ~ / root([[:space:]]|$)/ {print $2; exit}'
}

profile_values() {
  case "$1" in
    mild)
      printf '%s\n' "40ms" "10ms" "0.2%" "8mbit"
      ;;
    constrained)
      printf '%s\n' "90ms" "25ms" "1%" "3mbit"
      ;;
    severe)
      printf '%s\n' "180ms" "60ms" "3%" "1200kbit"
      ;;
    *)
      fail "unknown profile '$1'"
      ;;
  esac
}

load_state() {
  [[ -f "${STATE_FILE}" ]] || fail "no WAN soak is active"
  # The state file is root-owned and every value is validated before writing.
  # shellcheck disable=SC1090
  source "${STATE_FILE}"
  validate_interface "${SOAK_INTERFACE:-}" || fail "state file has an invalid interface"
  validate_ipv4 "${SOAK_BROKER_IP:-}" || fail "state file has an invalid broker IP"
  validate_port "${SOAK_BROKER_PORT:-}" || fail "state file has an invalid broker port"
}

clear_soak() {
  if [[ ! -f "${STATE_FILE}" ]]; then
    echo "No WAN soak is active."
    return 0
  fi

  load_state
  tc qdisc del dev "${SOAK_INTERFACE}" root 2>/dev/null || true
  rm -f -- "${STATE_FILE}"
  sleep 0.2

  local restored=""
  restored="$(root_qdisc_kind "${SOAK_INTERFACE}")"
  [[ "${restored}" != "prio" ]] || fail "the impairment qdisc is still active"
  echo "WAN soak cleared from ${SOAK_INTERFACE}; root qdisc is ${restored:-kernel-default}."
}

apply_soak() {
  local profile="$1"
  local broker_host="$2"
  local broker_ip="$3"
  local broker_port="$4"
  local interface="$5"
  local current_qdisc=""
  local delay=""
  local jitter=""
  local loss=""
  local rate=""
  local -a values=()

  [[ ! -e "${STATE_FILE}" ]] || fail "a WAN soak is already active; run clear first"
  validate_host "${broker_host}" || fail "invalid broker host"
  validate_port "${broker_port}" || fail "invalid broker port"

  if [[ -z "${broker_ip}" ]]; then
    broker_ip="$(resolve_broker_ip "${broker_host}")"
  fi
  validate_ipv4 "${broker_ip}" || fail "invalid broker IPv4 address"

  if [[ -z "${interface}" ]]; then
    interface="$(discover_interface "${broker_ip}")"
  fi
  validate_interface "${interface}" || fail "invalid interface"

  current_qdisc="$(root_qdisc_kind "${interface}")"
  case "${current_qdisc}" in
    fq_codel|pfifo_fast|noqueue)
      ;;
    *)
      fail "refusing to replace unexpected root qdisc '${current_qdisc:-unknown}' on ${interface}"
      ;;
  esac

  mapfile -t values < <(profile_values "${profile}")
  delay="${values[0]}"
  jitter="${values[1]}"
  loss="${values[2]}"
  rate="${values[3]}"

  cleanup_partial() {
    tc qdisc del dev "${interface}" root 2>/dev/null || true
    rm -f -- "${STATE_FILE}"
  }
  trap cleanup_partial ERR

  tc qdisc replace dev "${interface}" root handle 1: prio bands 3
  tc qdisc add dev "${interface}" parent 1:1 handle 10: fq_codel
  tc qdisc add dev "${interface}" parent 1:3 handle 30: netem \
    delay "${delay}" "${jitter}" distribution normal \
    loss random "${loss}" \
    rate "${rate}"
  tc filter add dev "${interface}" protocol ip parent 1: prio 1 u32 \
    match ip dst "${broker_ip}/32" \
    match ip protocol 6 0xff \
    match ip dport "${broker_port}" 0xffff \
    flowid 1:3
  tc filter add dev "${interface}" protocol ip parent 1: prio 100 u32 \
    match u32 0 0 \
    flowid 1:1

  cat >"${STATE_FILE}" <<EOF
SOAK_INTERFACE=${interface}
SOAK_BROKER_IP=${broker_ip}
SOAK_BROKER_PORT=${broker_port}
SOAK_PROFILE=${profile}
SOAK_DELAY=${delay}
SOAK_JITTER=${jitter}
SOAK_LOSS=${loss}
SOAK_RATE=${rate}
SOAK_APPLIED_AT=$(date -u +%Y-%m-%dT%H:%M:%SZ)
SOAK_ORIGINAL_QDISC=${current_qdisc}
EOF
  chmod 0600 "${STATE_FILE}"
  trap - ERR

  echo "WAN soak active on ${interface}:"
  echo "  Broker:  ${broker_host} (${broker_ip}:${broker_port})"
  echo "  Profile: ${profile} — delay ${delay} ± ${jitter}, loss ${loss}, rate ${rate}"
  echo "  Normal traffic, including SSH, remains on fq_codel."
}

show_status() {
  if [[ ! -f "${STATE_FILE}" ]]; then
    echo "WAN soak: inactive"
    return 0
  fi
  load_state
  echo "WAN soak: active"
  echo "  Interface: ${SOAK_INTERFACE}"
  echo "  Broker:    ${SOAK_BROKER_IP}:${SOAK_BROKER_PORT}"
  echo "  Profile:   ${SOAK_PROFILE}"
  echo "  Applied:   ${SOAK_APPLIED_AT}"
  echo "  Limits:    delay ${SOAK_DELAY} ± ${SOAK_JITTER}, loss ${SOAK_LOSS}, rate ${SOAK_RATE}"
  tc -s qdisc show dev "${SOAK_INTERFACE}"
  tc filter show dev "${SOAK_INTERFACE}" parent 1:
}

command_name="${1:-}"
case "${command_name}" in
  apply|run)
    require_root
    for required_command in awk date getent head ip sort tc; do
      require_command "${required_command}"
    done
    [[ "$#" -ge 2 ]] || { usage >&2; exit 2; }
    profile="$2"
    shift 2
    duration=""
    if [[ "${command_name}" == "run" ]]; then
      [[ "$#" -ge 1 ]] || fail "run requires a duration in seconds"
      duration="$1"
      shift
      [[ "${duration}" =~ ^[0-9]{1,6}$ ]] || fail "duration must be an integer number of seconds"
      ((10#${duration} >= 10 && 10#${duration} <= 86400)) ||
        fail "duration must be between 10 and 86400 seconds"
    fi

    broker_host="${DEFAULT_BROKER_HOST}"
    broker_ip=""
    broker_port="${DEFAULT_BROKER_PORT}"
    interface=""
    while [[ "$#" -gt 0 ]]; do
      case "$1" in
        --broker-host)
          [[ "$#" -ge 2 ]] || fail "--broker-host requires a value"
          broker_host="$2"
          shift 2
          ;;
        --broker-ip)
          [[ "$#" -ge 2 ]] || fail "--broker-ip requires a value"
          broker_ip="$2"
          shift 2
          ;;
        --broker-port)
          [[ "$#" -ge 2 ]] || fail "--broker-port requires a value"
          broker_port="$2"
          shift 2
          ;;
        --interface)
          [[ "$#" -ge 2 ]] || fail "--interface requires a value"
          interface="$2"
          shift 2
          ;;
        *)
          fail "unknown option '$1'"
          ;;
      esac
    done

    apply_soak "${profile}" "${broker_host}" "${broker_ip}" "${broker_port}" "${interface}"
    if [[ "${command_name}" == "run" ]]; then
      trap clear_soak EXIT INT TERM HUP
      echo "Soak will run for ${duration} seconds."
      sleep "${duration}"
      clear_soak
      trap - EXIT INT TERM HUP
    fi
    ;;
  clear)
    require_root
    require_command tc
    [[ "$#" -eq 1 ]] || fail "clear does not accept arguments"
    clear_soak
    ;;
  status)
    require_root
    require_command tc
    [[ "$#" -eq 1 ]] || fail "status does not accept arguments"
    show_status
    ;;
  -h|--help|help)
    usage
    ;;
  *)
    usage >&2
    exit 2
    ;;
esac
