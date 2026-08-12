#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

usage() {
  cat <<'EOF'
Usage: build-github-release-assets.sh \
  --version <semver> \
  --channel <stable|beta|pinned> \
  --minimum-previous-version <semver> \
  --target-configuration-schema-version <n> \
  --minimum-configuration-schema-version <n> \
  --maximum-configuration-schema-version <n> \
  --minimum-protocol-version <n> \
  --maximum-protocol-version <n> \
  --release-title <text> \
  --release-summary <text> \
  --key-id <id> \
  --private-key <absolute-pkcs8-pem-path> \
  --source-date-epoch <unix-seconds> \
  --output <new-directory>

Builds ten GitHub Release assets: one signed manifest and four deterministic
packages for each of linux-x64 and linux-arm64. The private key is read only by
the standalone build-time signer and is never copied into an asset or publish tree.
EOF
}

version=""
channel=""
minimum_previous_version=""
target_configuration_schema_version=""
minimum_configuration_schema_version=""
maximum_configuration_schema_version=""
minimum_protocol_version=""
maximum_protocol_version=""
release_title=""
release_summary=""
key_id=""
private_key=""
source_date_epoch=""
output=""

while (($# > 0)); do
  case "$1" in
    --version) version="${2-}"; shift 2 ;;
    --channel) channel="${2-}"; shift 2 ;;
    --minimum-previous-version) minimum_previous_version="${2-}"; shift 2 ;;
    --target-configuration-schema-version) target_configuration_schema_version="${2-}"; shift 2 ;;
    --minimum-configuration-schema-version) minimum_configuration_schema_version="${2-}"; shift 2 ;;
    --maximum-configuration-schema-version) maximum_configuration_schema_version="${2-}"; shift 2 ;;
    --minimum-protocol-version) minimum_protocol_version="${2-}"; shift 2 ;;
    --maximum-protocol-version) maximum_protocol_version="${2-}"; shift 2 ;;
    --release-title) release_title="${2-}"; shift 2 ;;
    --release-summary) release_summary="${2-}"; shift 2 ;;
    --key-id) key_id="${2-}"; shift 2 ;;
    --private-key) private_key="${2-}"; shift 2 ;;
    --source-date-epoch) source_date_epoch="${2-}"; shift 2 ;;
    --output) output="${2-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown or incomplete option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

for value_name in \
  version channel minimum_previous_version \
  target_configuration_schema_version \
  minimum_configuration_schema_version \
  maximum_configuration_schema_version \
  minimum_protocol_version maximum_protocol_version \
  release_title key_id private_key source_date_epoch output; do
  [[ -n "${!value_name}" ]] || {
    echo "Missing required value: ${value_name}" >&2
    usage >&2
    exit 2
  }
done

case "${channel}" in
  stable|beta|pinned) ;;
  *) echo "Channel must be stable, beta, or pinned." >&2; exit 2 ;;
esac
[[ "${source_date_epoch}" =~ ^[1-9][0-9]*$ ]] || {
  echo "Source date epoch must be a canonical positive integer." >&2
  exit 2
}
for integer_value in \
  "${target_configuration_schema_version}" \
  "${minimum_configuration_schema_version}" \
  "${maximum_configuration_schema_version}" \
  "${minimum_protocol_version}" \
  "${maximum_protocol_version}"; do
  [[ "${integer_value}" =~ ^[1-9][0-9]*$ ]] || {
    echo "Compatibility versions must be canonical positive integers." >&2
    exit 2
  }
done
[[ "${private_key}" = /* ]] || {
  echo "Private key path must be absolute." >&2
  exit 2
}

for command_name in \
  awk basename chmod cmp cp dirname dotnet find gzip grep mkdir mktemp mv \
  python3 rm sha256sum strings tar wc; do
  command -v "${command_name}" >/dev/null || {
    echo "Required command is unavailable: ${command_name}" >&2
    exit 2
  }
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd -- "${script_dir}/../.." && pwd -P)"
web_project="${repo_root}/prototypes/web-client/AetherSDR.Web.csproj"
watchdog_project="${repo_root}/prototypes/tx-watchdog/AetherSDR.TxWatchdog/AetherSDR.TxWatchdog.csproj"
broker_project="${repo_root}/AetherRemote/src/AetherRemote.Broker/AetherRemote.Broker.csproj"
agent_project="${repo_root}/AetherRemote/src/AetherRemote.Agent/AetherRemote.Agent.csproj"
updater_project="${repo_root}/AetherRemote/src/AetherRemote.Updater/AetherRemote.Updater.csproj"
updater_unit="${repo_root}/AetherRemote/deploy/aetherremote-release-updater.service"
agent_unit="${repo_root}/AetherRemote/deploy/aetherremote-agent.service"
station_engine_unit="${repo_root}/AetherRemote/deploy/aetherremote-station-engine.service"
enrollment_helper="${repo_root}/AetherRemote/deploy/enroll-station.sh"
builder_project="${repo_root}/tools/release/AetherSDR.ReleaseBuilder/AetherSDR.ReleaseBuilder.csproj"
builder_dll="${repo_root}/tools/release/AetherSDR.ReleaseBuilder/bin/Release/net10.0/AetherSDR.ReleaseBuilder.dll"

output_parent="$(dirname -- "${output}")"
mkdir -p -- "${output_parent}"
output_parent="$(cd -- "${output_parent}" && pwd -P)"
output="${output_parent}/$(basename -- "${output}")"
[[ ! -e "${output}" ]] || {
  echo "Output directory already exists: ${output}" >&2
  exit 2
}

work_dir="$(mktemp -d "${TMPDIR:-/tmp}/aethersdr-release-assets.XXXXXX")"
staging_dir="$(mktemp -d "${output_parent}/.$(basename -- "${output}").staging.XXXXXX")"
cleanup() {
  rm -rf -- "${work_dir}"
  if [[ -n "${staging_dir}" ]]; then
    chmod -R u+rwX -- "${staging_dir}" 2>/dev/null || true
    rm -rf -- "${staging_dir}"
  fi
}
trap cleanup EXIT INT TERM

normalize_tree() {
  local root="$1"
  shift
  find "${root}" -type d -exec chmod 0755 -- {} +
  find "${root}" -type f -exec chmod 0644 -- {} +
  local executable
  for executable in "$@"; do
    [[ -f "${root}/${executable}" ]] || {
      echo "Expected published executable is missing: ${executable}" >&2
      exit 2
    }
    chmod 0755 -- "${root}/${executable}"
  done
  if [[ -d "${root}/tools" ]]; then
    while IFS= read -r -d '' executable; do
      chmod 0755 -- "${executable}"
    done < <(find "${root}/tools" -type f -name '*.sh' -print0)
  fi
}

count_exact_string() {
  local needle="$1"
  shift
  local count
  count="$(
    (grep -Fxc -- "${needle}" "$@" 2>/dev/null || true) |
      awk -F: '{sum += $NF} END {print sum + 0}'
  )"
  printf '%s' "${count}"
}

inspect_web_tree() {
  local root="$1"
  local label="$2"
  local scratch="$3"
  local binary="${root}/AetherSDR.Web"
  local managed="${root}/AetherSDR.Web.dll"
  local watchdog_binary="${root}/watchdog/AetherSDR.TxWatchdog"
  local watchdog_managed="${root}/watchdog/AetherSDR.TxWatchdog.dll"
  for path in "${binary}" "${managed}" "${watchdog_binary}" "${watchdog_managed}"; do
    [[ -s "${path}" ]] || {
      echo "${label} is missing a required web/watchdog artifact." >&2
      exit 2
    }
  done

  local web_ascii="${scratch}/web-ascii.txt"
  local web_utf16="${scratch}/web-utf16.txt"
  local watchdog_ascii="${scratch}/watchdog-ascii.txt"
  local watchdog_utf16="${scratch}/watchdog-utf16.txt"
  { strings -a "${binary}"; strings -a "${managed}"; } >"${web_ascii}"
  { strings -el "${binary}"; strings -el "${managed}"; } >"${web_utf16}"
  { strings -a "${watchdog_binary}"; strings -a "${watchdog_managed}"; } >"${watchdog_ascii}"
  { strings -el "${watchdog_binary}"; strings -el "${watchdog_managed}"; } >"${watchdog_utf16}"

  [[ "$(count_exact_string 'xmit 1' "${web_ascii}" "${web_utf16}")" == 1 ]] || {
    echo "${label} web artifact does not contain exactly one reviewed xmit 1 string." >&2
    exit 2
  }
  [[ "$(count_exact_string 'xmit 0' "${web_ascii}" "${web_utf16}")" == 1 ]] || {
    echo "${label} web artifact does not contain exactly one deduplicated xmit 0 string." >&2
    exit 2
  }
  [[ "$(count_exact_string 'xmit 1' "${watchdog_ascii}" "${watchdog_utf16}")" == 0 ]] || {
    echo "${label} watchdog artifact contains a forbidden xmit 1 string." >&2
    exit 2
  }
  [[ "$(count_exact_string 'xmit 0' "${watchdog_ascii}" "${watchdog_utf16}")" == 1 ]] || {
    echo "${label} watchdog artifact does not contain exactly one reviewed xmit 0 string." >&2
    exit 2
  }
  grep -F -- 'StationTxProductionCommandTransport' "${web_utf16}" >/dev/null
  grep -F -- 'StationTxProductionEmergencyUnkeyTransport' "${web_utf16}" >/dev/null
  for forbidden in \
    'cwx send' 'HilGatewayAuthorityChild' 'internal-engine-process-child' \
    'AETHERSDR_TX_HIL' 'dax tx'; do
    ! grep -F -- "${forbidden}" \
      "${web_ascii}" "${web_utf16}" \
      "${watchdog_ascii}" "${watchdog_utf16}" >/dev/null || {
      echo "${label} contains forbidden TX/HIL marker: ${forbidden}" >&2
      exit 2
    }
  done

  PUBLISHED_APPSETTINGS="${root}/appsettings.json" python3 - <<'PY'
import json
import os
from pathlib import Path

path = Path(os.environ["PUBLISHED_APPSETTINGS"])
payload = json.loads(path.read_text(encoding="utf-8"))
expected = {
    "Enabled": False,
    "AllowedRadioIds": [],
    "CommandTimeoutMilliseconds": 2000,
}
if payload.get("StationTxCommandTransport") != expected:
    raise SystemExit("Published StationTxCommandTransport defaults are not fail-closed")
if payload.get("ReleaseGitHubSource", {}).get("Enabled") is not False:
    raise SystemExit("Published ReleaseGitHubSource default is not disabled")
PY
}

package_directory() {
  local source_directory="$1"
  local archive_path="$2"
  local temporary_path="${archive_path}.tmp"
  LC_ALL=C TZ=UTC tar \
    --sort=name \
    --format=gnu \
    --mtime="@${source_date_epoch}" \
    --owner=0 \
    --group=0 \
    --numeric-owner \
    --create \
    --file=- \
    --directory="${source_directory}" . |
    gzip -n -9 >"${temporary_path}"
  chmod 0644 -- "${temporary_path}"
  mv -- "${temporary_path}" "${archive_path}"
}

echo "Building release signer..."
dotnet build "${builder_project}" --configuration Release

for runtime in linux-x64 linux-arm64; do
  echo "Publishing ${runtime} release packages..."
  runtime_root="${work_dir}/${runtime}"
  web_publish="${runtime_root}/web"
  broker_publish="${runtime_root}/broker"
  agent_publish="${runtime_root}/agent"
  updater_publish="${runtime_root}/updater"
  architecture_assets="${runtime_root}/assets"
  mkdir -p -- \
    "${web_publish}" "${broker_publish}" "${agent_publish}" \
    "${updater_publish}" "${architecture_assets}"

  common_publish=(
    --configuration Release
    --runtime "${runtime}"
    --self-contained true
    -p:ContinuousIntegrationBuild=true
    -p:Deterministic=true
    -p:DebugType=None
    -p:DebugSymbols=false
    -p:Version="${version}"
    -p:PathMap="${repo_root}=/src"
  )

  dotnet publish "${web_project}" \
    "${common_publish[@]}" \
    -p:EnableTxHil=false \
    --output "${web_publish}"
  mkdir -p -- "${web_publish}/watchdog"
  dotnet publish "${watchdog_project}" \
    "${common_publish[@]}" \
    --output "${web_publish}/watchdog"
  dotnet publish "${broker_project}" \
    "${common_publish[@]}" \
    --output "${broker_publish}"
  dotnet publish "${agent_project}" \
    "${common_publish[@]}" \
    --output "${agent_publish}"
  dotnet publish "${updater_project}" \
    "${common_publish[@]}" \
    --output "${updater_publish}"
  mkdir -p -- "${agent_publish}/updater"
  cp -a -- "${updater_publish}/." "${agent_publish}/updater/"
  cp -- "${updater_unit}" \
    "${agent_publish}/aetherremote-release-updater.service"
  cp -- "${agent_unit}" \
    "${agent_publish}/aetherremote-agent.service"
  cp -- "${station_engine_unit}" \
    "${agent_publish}/aetherremote-station-engine.service"
  cp -- "${enrollment_helper}" \
    "${agent_publish}/enroll-station.sh"

  normalize_tree \
    "${web_publish}" \
    AetherSDR.Web \
    watchdog/AetherSDR.TxWatchdog
  normalize_tree "${broker_publish}" AetherRemote.Broker
  normalize_tree \
    "${agent_publish}" \
    AetherRemote.Agent \
    updater/AetherRemote.Updater \
    enroll-station.sh
  inspect_web_tree "${web_publish}" "${runtime}" "${runtime_root}"

  gateway_archive="${architecture_assets}/aethersdr-gateway-${runtime}.tar.gz"
  station_archive="${architecture_assets}/aethersdr-station-engine-${runtime}.tar.gz"
  package_directory "${web_publish}" "${gateway_archive}"
  cp -- "${gateway_archive}" "${station_archive}"
  cmp --silent "${gateway_archive}" "${station_archive}" || {
    echo "Gateway and station-engine packages drifted despite sharing the reviewed web tree." >&2
    exit 2
  }
  package_directory \
    "${broker_publish}" \
    "${architecture_assets}/aethersdr-broker-${runtime}.tar.gz"
  package_directory \
    "${agent_publish}" \
    "${architecture_assets}/aetherremote-agent-${runtime}.tar.gz"

  manifest_path="${architecture_assets}/release-manifest-${runtime}.json"
  [[ -s "${builder_dll}" ]] || {
    echo "The built release signer assembly is unavailable." >&2
    exit 2
  }
  dotnet "${builder_dll}" \
    --asset-directory "${architecture_assets}" \
    --output-manifest "${manifest_path}" \
    --private-key "${private_key}" \
    --key-id "${key_id}" \
    --version "${version}" \
    --channel "${channel}" \
    --architecture "${runtime}" \
    --minimum-previous-version "${minimum_previous_version}" \
    --target-configuration-schema-version "${target_configuration_schema_version}" \
    --minimum-configuration-schema-version "${minimum_configuration_schema_version}" \
    --maximum-configuration-schema-version "${maximum_configuration_schema_version}" \
    --minimum-protocol-version "${minimum_protocol_version}" \
    --maximum-protocol-version "${maximum_protocol_version}" \
    --release-title "${release_title}" \
    --release-summary "${release_summary}"

  asset_count="$(find "${architecture_assets}" -maxdepth 1 -type f | wc -l)"
  [[ "${asset_count}" == 5 ]] || {
    echo "${runtime} did not produce exactly five release assets." >&2
    exit 2
  }
  cp -- "${architecture_assets}"/* "${staging_dir}/"
done

final_count="$(find "${staging_dir}" -maxdepth 1 -type f | wc -l)"
[[ "${final_count}" == 10 ]] || {
  echo "Release packaging did not produce exactly ten assets." >&2
  exit 2
}
find "${staging_dir}" -maxdepth 1 -type f -exec chmod 0444 -- {} +
chmod 0755 -- "${staging_dir}"
mv -- "${staging_dir}" "${output}"
staging_dir=""

echo "Release assets created at ${output}"
sha256sum "${output}"/*
echo "No GitHub release, deployment, service, radio, command, lease, TX, or RF action was performed."
