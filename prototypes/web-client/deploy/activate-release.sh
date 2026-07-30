#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 3 ]]; then
  echo "Usage: $0 <deployment-root> <release-name> <expected-sha256>" >&2
  exit 1
fi

deployment_root="$(realpath "$1")"
release_name="$2"
expected_sha256="${3,,}"
if [[ ! "${release_name}" =~ ^[0-9A-Za-z._-]{1,96}$ ||
      ! "${expected_sha256}" =~ ^[0-9a-f]{64}$ ]]; then
  echo "The release identity is invalid." >&2
  exit 1
fi

archive="${deployment_root}/incoming/${release_name}.tar.gz"
releases="${deployment_root}/releases"
destination="${releases}/${release_name}"
if [[ ! -f "${archive}" || -e "${destination}" ]]; then
  echo "The release archive is missing or the destination already exists." >&2
  exit 1
fi

actual_sha256="$(sha256sum "${archive}" | cut -d' ' -f1)"
if [[ "${actual_sha256}" != "${expected_sha256}" ]]; then
  echo "The release checksum does not match." >&2
  exit 1
fi
if tar -tzf "${archive}" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then
  echo "The release archive contains an unsafe path." >&2
  exit 1
fi

temporary_directory="$(mktemp -d "${releases}/.${release_name}.XXXXXX")"
temporary_link="${deployment_root}/.current.${release_name}.$$"
cleanup() {
  case "${temporary_directory}" in
    "${releases}/.${release_name}."*)
      rm -rf --one-file-system "${temporary_directory}"
      ;;
  esac
  rm -f "${temporary_link}"
}
trap cleanup EXIT
tar --extract --gzip --file "${archive}" \
  --directory "${temporary_directory}" \
  --no-same-owner \
  --no-same-permissions
if [[ ! -f "${temporary_directory}/AetherSDR.Web" ]]; then
  echo "The release does not contain AetherSDR.Web." >&2
  exit 1
fi
chmod 0755 "${temporary_directory}/AetherSDR.Web"
mv "${temporary_directory}" "${destination}"
ln -s "${destination}" "${temporary_link}"
previous_release="$(readlink -f "${deployment_root}/current" || true)"
mv -Tf "${temporary_link}" "${deployment_root}/current"
trap - EXIT

echo "Previous release: ${previous_release}"
echo "Active release: ${destination}"
