#!/usr/bin/env bash
set -euo pipefail

environment_file="${HOME}/.config/aethersdr-web/environment"
if [[ ! -r "${environment_file}" ]]; then
    echo "Missing readable environment file: ${environment_file}" >&2
    exit 1
fi

set -a
# The deployment environment file is intentionally compatible with both
# systemd EnvironmentFile syntax and this temporary no-sudo launcher.
source "${environment_file}"
set +a

cd "${HOME}/aethersdr/current"
exec ./AetherSDR.Web --urls http://0.0.0.0:5080
