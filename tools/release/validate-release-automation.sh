#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(cd -- "${script_dir}/../.." && pwd -P)"
package_script="${repo_root}/tools/release/build-github-release-assets.sh"
workflow="${repo_root}/.github/workflows/draft-release.yml"

for path in "${package_script}" "${workflow}"; do
  [[ -f "${path}" ]] || {
    echo "Required release automation file is missing: ${path}" >&2
    exit 1
  }
done

bash -n "${package_script}"

require_text() {
  local text="$1"
  local path="$2"
  grep -F -- "${text}" "${path}" >/dev/null || {
    echo "Release automation is missing required contract text: ${text}" >&2
    exit 1
  }
}

require_text 'workflow_dispatch:' "${workflow}"
require_text "if: github.ref == 'refs/heads/main'" "${workflow}"
require_text 'environment: release-signing' "${workflow}"
require_text 'AETHERSDR_RELEASE_SIGNING_KEY_PKCS8_BASE64' "${workflow}"
require_text 'Run production validation-only gate' "${workflow}"
require_text 'Remove private signing key' "${workflow}"
require_text '--draft' "${workflow}"
require_text 'build-github-release-assets.sh' "${workflow}"

if grep -Eq \
  '^[[:space:]]+(push|pull_request|schedule|workflow_run):' \
  "${workflow}"; then
  echo "Draft release automation must remain manual-only." >&2
  exit 1
fi
if grep -Eiq \
  'gh[[:space:]]+release[[:space:]]+(publish|edit.*--draft=false)|--draft=false' \
  "${workflow}"; then
  echo "Draft release automation contains an automatic publication path." >&2
  exit 1
fi
if grep -Eq \
  '(^|[[:space:]])(gh|curl|scp|ssh)[[:space:]]' \
  "${package_script}"; then
  echo "The local package builder must not contact GitHub or deployment hosts." >&2
  exit 1
fi

require_text 'release-manifest-${runtime}.json' "${package_script}"
require_text 'aethersdr-gateway-${runtime}.tar.gz' "${package_script}"
require_text 'aethersdr-broker-${runtime}.tar.gz' "${package_script}"
require_text 'aetherremote-agent-${runtime}.tar.gz' "${package_script}"
require_text 'aethersdr-station-engine-${runtime}.tar.gz' "${package_script}"

require_text 'gzip -n -9' "${package_script}"
require_text '--sort=name' "${package_script}"
require_text '--numeric-owner' "${package_script}"
require_text 'AetherSDR.ReleaseBuilder.dll' "${package_script}"
require_text 'No GitHub release, deployment, service, radio, command, lease, TX, or RF action was performed.' "${package_script}"

echo "Release automation remains manual, draft-only, protected, deterministic, and non-deploying."
