#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

usage() {
  cat <<'EOF'
Usage: build-standalone-acceptance-assets.sh (--generate-ephemeral-key | --private-key <path> --public-key <path>) --output <new-dir>
       build-standalone-acceptance-assets.sh --cleanup-interrupted

Builds four ephemeral signed M8H acceptance releases for linux-x64 and
linux-arm64. The private key is used only while constructing the artifact and is
never copied into the output. The generated-key mode keeps its private key under
a mode-0700 user cache directory and removes it on exit.
EOF
}

private_key=""
public_key=""
generate_ephemeral_key=false
cleanup_interrupted=false
output=""
while (($# > 0)); do
  case "$1" in
    --private-key) private_key="${2-}"; shift 2 ;;
    --public-key) public_key="${2-}"; shift 2 ;;
    --generate-ephemeral-key) generate_ephemeral_key=true; shift ;;
    --cleanup-interrupted) cleanup_interrupted=true; shift ;;
    --output) output="${2-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown or incomplete option: $1" >&2; usage >&2; exit 2 ;;
  esac
done
if [[ "${cleanup_interrupted}" == "true" ]]; then
  [[ -z "${private_key}" && -z "${public_key}" && "${generate_ephemeral_key}" == "false" && -z "${output}" ]] || {
    echo "Interrupted-staging cleanup cannot be combined with build options." >&2
    exit 2
  }
  shopt -s nullglob
  for stale in "${TMPDIR:-/tmp}"/aethersdr-m8h-assets.* "${HOME}/.cache"/aethersdr-m8h-key.*; do
    [[ -d "${stale}" && ! -L "${stale}" && -O "${stale}" ]] || {
      echo "Refusing to clean an unexpected acceptance staging path: ${stale}" >&2
      exit 2
    }
    chmod -R u+rwX -- "${stale}"
    rm -rf -- "${stale}"
  done
  exit 0
fi

[[ -n "${output}" ]] || {
  usage >&2
  exit 2
}
if [[ "${generate_ephemeral_key}" == "true" ]]; then
  [[ -z "${private_key}" && -z "${public_key}" ]] || {
    echo "Generated acceptance keys cannot be combined with supplied key paths." >&2
    exit 2
  }
else
  [[ -n "${private_key}" && -n "${public_key}" ]] || {
    usage >&2
    exit 2
  }
  [[ "${private_key}" = /* && "${public_key}" = /* ]] || {
    echo "Acceptance signing key paths must be absolute." >&2
    exit 2
  }
  [[ -f "${private_key}" && -f "${public_key}" ]] || {
    echo "Acceptance signing key material is unavailable." >&2
    exit 2
  }
fi
[[ "${output}" = /* ]] || {
  echo "Acceptance output must be an absolute path." >&2
  exit 2
}
[[ ! -e "${output}" ]] || {
  echo "Acceptance output already exists: ${output}" >&2
  exit 2
}

for command_name in basename chmod cp dirname dotnet find gzip ln mkdir mktemp mv openssl rm tar; do
  command -v "${command_name}" >/dev/null || {
    echo "Required command unavailable: ${command_name}" >&2
    exit 2
  }
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
builder_script="${script_dir}/build-github-release-assets.sh"
builder_dll="${script_dir}/AetherSDR.ReleaseBuilder/bin/Release/net10.0/AetherSDR.ReleaseBuilder.dll"

readonly previous_version="8.8.0-acceptance.1"
readonly target_version="8.8.0-acceptance.2"
readonly failure_version="8.8.0-acceptance.3"
readonly station_failure_version="8.8.0-acceptance.4"
readonly key_id="m8h-ephemeral"
readonly source_date_epoch="1786576800"

key_root=""
if [[ "${generate_ephemeral_key}" == "true" ]]; then
  cache_root="${HOME}/.cache"
  mkdir -p -- "${cache_root}"
  chmod 0700 -- "${cache_root}"
  key_root="$(mktemp -d "${cache_root}/aethersdr-m8h-key.XXXXXX")"
  chmod 0700 -- "${key_root}"
  private_key="${key_root}/private.pem"
  public_key="${key_root}/public.pem"
  openssl genpkey \
    -algorithm EC \
    -pkeyopt ec_paramgen_curve:P-256 \
    -out "${private_key}"
  chmod 0600 -- "${private_key}"
  openssl pkey \
    -in "${private_key}" \
    -pubout \
    -out "${public_key}"
  chmod 0444 -- "${public_key}"
fi

work="$(mktemp -d "${TMPDIR:-/tmp}/aethersdr-m8h-assets.XXXXXX")"
cleanup() {
  chmod -R u+rwX -- "${work}" 2>/dev/null || true
  rm -rf -- "${work}"
  if [[ -n "${key_root}" ]]; then
    chmod -R u+rwX -- "${key_root}" 2>/dev/null || true
    rm -rf -- "${key_root}"
  fi
}
trap cleanup EXIT INT TERM

build_flat_release() {
  local version="$1"
  local minimum_previous="$2"
  local title="$3"
  local destination="$4"
  bash "${builder_script}" \
    --version "${version}" \
    --channel beta \
    --minimum-previous-version "${minimum_previous}" \
    --target-configuration-schema-version 1 \
    --minimum-configuration-schema-version 1 \
    --maximum-configuration-schema-version 1 \
    --minimum-protocol-version 2 \
    --maximum-protocol-version 2 \
    --release-title "${title}" \
    --release-summary "Ephemeral acceptance-only signed release; never publish." \
    --key-id "${key_id}" \
    --private-key "${private_key}" \
    --source-date-epoch "${source_date_epoch}" \
    --output "${destination}"
}

previous_flat="${work}/previous-flat"
target_flat="${work}/target-flat"
build_flat_release \
  "${previous_version}" \
  "8.7.0" \
  "M8H packaged acceptance previous" \
  "${previous_flat}"
build_flat_release \
  "${target_version}" \
  "${previous_version}" \
  "M8H packaged acceptance target" \
  "${target_flat}"

mkdir -p -- "${output}"
cp -- "${public_key}" "${output}/release-verification-key.pem"
chmod 0444 -- "${output}/release-verification-key.pem"

link_or_copy() {
  local source="$1"
  local destination="$2"
  if ! ln -- "${source}" "${destination}" 2>/dev/null; then
    cp -- "${source}" "${destination}"
  fi
}

package_bundle() {
  local version="$1"
  local runtime="$2"
  local assets="$3"
  local manifest="$4"
  local bundle="${output}/${runtime}/aethersdr-${version}"
  mkdir -p -- "${bundle}/packages"
  local package
  for package in "${assets}"/*.tar.gz; do
    link_or_copy "${package}" "${bundle}/packages/$(basename -- "${package}")"
  done
  cp -- "${manifest}" "${bundle}/release-manifest.json"
}

sign_manifest() {
  local version="$1"
  local runtime="$2"
  local assets="$3"
  local minimum_previous="$4"
  local manifest="${assets}/release-manifest-${runtime}.json"
  dotnet "${builder_dll}" \
    --asset-directory "${assets}" \
    --output-manifest "${manifest}" \
    --private-key "${private_key}" \
    --key-id "${key_id}" \
    --version "${version}" \
    --channel beta \
    --architecture "${runtime}" \
    --minimum-previous-version "${minimum_previous}" \
    --target-configuration-schema-version 1 \
    --minimum-configuration-schema-version 1 \
    --maximum-configuration-schema-version 1 \
    --minimum-protocol-version 2 \
    --maximum-protocol-version 2 \
    --release-title "M8H packaged acceptance ${version}" \
    --release-summary "Ephemeral acceptance-only signed release; never publish."
}

repack_tree() {
  local root="$1"
  local archive="$2"
  LC_ALL=C TZ=UTC tar \
    --sort=name \
    --format=gnu \
    --mtime="@${source_date_epoch}" \
    --owner=0 \
    --group=0 \
    --numeric-owner \
    --create \
    --file=- \
    --directory="${root}" . |
    gzip -n -9 >"${archive}.new"
  chmod 0644 -- "${archive}.new"
  mv -- "${archive}.new" "${archive}"
}

repack_invalid_gateway_startup() {
  local archive="$1"
  local label="$2"
  local root="${work}/broken-${label}"
  mkdir -p -- "${root}"
  tar -xzf "${archive}" -C "${root}"
  [[ -f "${root}/appsettings.json" && ! -L "${root}/appsettings.json" ]] || {
    echo "Packaged appsettings.json is unavailable for failure injection." >&2
    exit 2
  }
  printf '%s\n' '{"M8H deliberately invalid startup JSON"' >"${root}/appsettings.json"
  repack_tree "${root}" "${archive}"
}

repack_invalid_agent_startup() {
  local archive="$1"
  local label="$2"
  local root="${work}/broken-${label}"
  mkdir -p -- "${root}"
  tar -xzf "${archive}" -C "${root}"
  [[ -f "${root}/AetherRemote.Agent" && ! -L "${root}/AetherRemote.Agent" ]] || {
    echo "Packaged AetherRemote.Agent is unavailable for failure injection." >&2
    exit 2
  }
  printf '%s\n' 'M8H deliberately invalid Agent executable format' >"${root}/AetherRemote.Agent"
  chmod 0755 -- "${root}/AetherRemote.Agent"
  repack_tree "${root}" "${archive}"
}

for runtime in linux-x64 linux-arm64; do
  previous_assets="${work}/previous-${runtime}"
  target_assets="${work}/target-${runtime}"
  mkdir -p -- "${previous_assets}" "${target_assets}"
  for stem in aethersdr-gateway aethersdr-broker aetherremote-agent aethersdr-station-engine; do
    link_or_copy \
      "${previous_flat}/${stem}-${runtime}.tar.gz" \
      "${previous_assets}/${stem}-${runtime}.tar.gz"
    link_or_copy \
      "${target_flat}/${stem}-${runtime}.tar.gz" \
      "${target_assets}/${stem}-${runtime}.tar.gz"
  done
  package_bundle \
    "${previous_version}" \
    "${runtime}" \
    "${previous_assets}" \
    "${previous_flat}/release-manifest-${runtime}.json"
  package_bundle \
    "${target_version}" \
    "${runtime}" \
    "${target_assets}" \
    "${target_flat}/release-manifest-${runtime}.json"

  failure_assets="${work}/failure-${runtime}"
  mkdir -p -- "${failure_assets}"
  for package in "${target_assets}"/*.tar.gz; do
    case "$(basename -- "${package}")" in
      aethersdr-gateway-*) cp -- "${package}" "${failure_assets}/" ;;
      *) link_or_copy "${package}" "${failure_assets}/$(basename -- "${package}")" ;;
    esac
  done
  repack_invalid_gateway_startup \
    "${failure_assets}/aethersdr-gateway-${runtime}.tar.gz" \
    "gateway-${runtime}"
  sign_manifest "${failure_version}" "${runtime}" "${failure_assets}" "${previous_version}"
  package_bundle \
    "${failure_version}" \
    "${runtime}" \
    "${failure_assets}" \
    "${failure_assets}/release-manifest-${runtime}.json"

  station_failure_assets="${work}/station-failure-${runtime}"
  mkdir -p -- "${station_failure_assets}"
  for package in "${target_assets}"/*.tar.gz; do
    case "$(basename -- "${package}")" in
      aetherremote-agent-*) cp -- "${package}" "${station_failure_assets}/" ;;
      *) link_or_copy "${package}" "${station_failure_assets}/$(basename -- "${package}")" ;;
    esac
  done
  repack_invalid_agent_startup \
    "${station_failure_assets}/aetherremote-agent-${runtime}.tar.gz" \
    "station-agent-${runtime}"
  sign_manifest \
    "${station_failure_version}" \
    "${runtime}" \
    "${station_failure_assets}" \
    "${target_version}"
  package_bundle \
    "${station_failure_version}" \
    "${runtime}" \
    "${station_failure_assets}" \
    "${station_failure_assets}/release-manifest-${runtime}.json"
done

find "${output}" -type f -exec chmod 0444 -- {} +
find "${output}" -type d -exec chmod 0555 -- {} +

printf '%s\n' \
  "previous=aethersdr-${previous_version}" \
  "target=aethersdr-${target_version}" \
  "failure=aethersdr-${failure_version}" \
  "stationFailure=aethersdr-${station_failure_version}" \
  "keyId=${key_id}"
