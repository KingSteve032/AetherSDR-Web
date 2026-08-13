#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

fail() {
  echo "AetherRemote bootstrap failed: $*" >&2
  exit 2
}

usage() {
  cat <<'EOF'
Install one exact signed AetherRemote station release from an AetherSDR gateway.

Usage:
  sudo bash aetherremote-install.sh \
    --gateway https://radio.example.com \
    --station-id shack-east \
    --release-key-sha256 <64-hex-fingerprint>

The one-time enrollment code is never accepted as a command-line argument. It is
prompted locally after package verification and installation.
EOF
}

gateway_url=""
station_id=""
release_key_sha256=""

while [[ "$#" -gt 0 ]]; do
  case "$1" in
    --gateway)
      [[ "$#" -ge 2 ]] || fail "--gateway requires a value"
      gateway_url="$2"
      shift 2
      ;;
    --station-id)
      [[ "$#" -ge 2 ]] || fail "--station-id requires a value"
      station_id="$2"
      shift 2
      ;;
    --release-key-sha256)
      [[ "$#" -ge 2 ]] || fail "--release-key-sha256 requires a value"
      release_key_sha256="${2,,}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      fail "unknown argument: $1"
      ;;
  esac
done

[[ "${EUID}" -eq 0 ]] || fail "run this installer with sudo"

for command_name in \
  base64 cat chmod chown cp curl cut find getent id install ln mkdir mktemp mv \
  openssl python3 readlink rm seq sha256sum sleep stat systemctl timeout tr uname \
  useradd; do
  command -v "${command_name}" >/dev/null ||
    fail "required command is unavailable: ${command_name}"
done

if [[ -z "${gateway_url}" ]]; then
  [[ -r /dev/tty ]] || fail "--gateway is required when no interactive terminal is available"
  printf 'AetherSDR gateway URL (https://...): ' >/dev/tty
  IFS= read -r gateway_url </dev/tty
fi
if [[ -z "${station_id}" ]]; then
  [[ -r /dev/tty ]] || fail "--station-id is required when no interactive terminal is available"
  printf 'Station name/ID: ' >/dev/tty
  IFS= read -r station_id </dev/tty
fi
[[ "${release_key_sha256}" =~ ^[0-9a-f]{64}$ ]] ||
  fail "--release-key-sha256 must contain exactly 64 lowercase or uppercase hex characters"
[[ "${station_id}" =~ ^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$ ]] ||
  fail "station ID must use 1-64 letters, numbers, periods, underscores, colons, or hyphens"

canonical_gateway="$(GATEWAY_URL="${gateway_url}" python3 - <<'PY'
import os
from urllib.parse import urlsplit, urlunsplit

value = os.environ["GATEWAY_URL"]
parsed = urlsplit(value)
if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
    raise SystemExit(2)
if parsed.query or parsed.fragment or parsed.path not in ("", "/"):
    raise SystemExit(2)
port = parsed.port
host = parsed.hostname.lower()
if ":" in host and not host.startswith("["):
    host = f"[{host}]"
netloc = host if port in (None, 443) else f"{host}:{port}"
print(urlunsplit(("https", netloc, "", "", "")))
PY
)" || fail "the gateway must be one canonical HTTPS origin with no path, query, user info, or fragment"
[[ "${gateway_url%/}" == "${canonical_gateway}" ]] ||
  fail "the gateway URL must already be canonical: ${canonical_gateway}"

gateway_host="$(GATEWAY_URL="${canonical_gateway}" python3 - <<'PY'
import os
from urllib.parse import urlsplit
print(urlsplit(os.environ["GATEWAY_URL"]).hostname)
PY
)"
getent ahosts "${gateway_host}" >/dev/null ||
  fail "DNS resolution failed for the gateway"

case "$(uname -m)" in
  x86_64|amd64)
    architecture="linux-x64"
    ;;
  aarch64|arm64)
    architecture="linux-arm64"
    ;;
  *)
    fail "this release supports only linux-x64 and linux-arm64"
    ;;
esac

work_dir="$(mktemp -d "${TMPDIR:-/tmp}/aetherremote-bootstrap.XXXXXX")"
cleanup() {
  local status=$?
  chmod -R u+rwX -- "${work_dir}" 2>/dev/null || true
  rm -rf -- "${work_dir}"
  exit "${status}"
}
trap cleanup EXIT INT TERM
umask 077

metadata_path="${work_dir}/bootstrap.json"
manifest_path="${work_dir}/release-manifest.json"
agent_archive="${work_dir}/agent.tar.gz"
engine_archive="${work_dir}/station-engine.tar.gz"
key_der="${work_dir}/release-key.der"
key_pem="${work_dir}/release-key.pem"
signing_path="${work_dir}/manifest-signing.json"
signature_der="${work_dir}/manifest-signature.der"
agent_extract="${work_dir}/agent"
engine_extract="${work_dir}/station-engine"
mkdir -m 0700 -- "${agent_extract}" "${engine_extract}"

curl_https() {
  local url="$1"
  local output="$2"
  curl \
    --proto '=https' \
    --tlsv1.2 \
    --fail \
    --silent \
    --show-error \
    --connect-timeout 10 \
    --max-time 120 \
    --output "${output}" \
    "${url}"
}

curl_https "${canonical_gateway}/.well-known/aethersdr" "${metadata_path}" ||
  fail "the gateway bootstrap document could not be downloaded over trusted HTTPS"

mapfile -t bootstrap_fields < <(
  METADATA="${metadata_path}" \
  GATEWAY="${canonical_gateway}" \
  ARCHITECTURE="${architecture}" \
  EXPECTED_KEY_SHA="${release_key_sha256}" \
  python3 - <<'PY'
import base64
import json
import os
import re
from urllib.parse import urlsplit

class DuplicateKey(ValueError):
    pass

def pairs_hook(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise DuplicateKey(key)
        result[key] = value
    return result

def die(message):
    raise SystemExit(message)

def same_https_origin(url, gateway, expected_path=None):
    target = urlsplit(url)
    base = urlsplit(gateway)
    if target.scheme != "https" or target.username or target.password or target.query or target.fragment:
        die("unsafe HTTPS URL in bootstrap metadata")
    tport = target.port or 443
    bport = base.port or 443
    if target.hostname.lower() != base.hostname.lower() or tport != bport:
        die("bootstrap URL escaped the canonical gateway origin")
    if expected_path is not None and target.path != expected_path:
        die("bootstrap URL used an unexpected path")

def same_wss_origin(url, gateway, expected_path):
    target = urlsplit(url)
    base = urlsplit(gateway)
    if target.scheme != "wss" or target.username or target.password or target.query or target.fragment:
        die("unsafe broker WebSocket URL in bootstrap metadata")
    if target.hostname.lower() != base.hostname.lower() or (target.port or 443) != (base.port or 443):
        die("broker WebSocket URL escaped the canonical gateway origin")
    if target.path != expected_path:
        die("broker WebSocket URL used an unexpected path")

with open(os.environ["METADATA"], "r", encoding="utf-8") as handle:
    document = json.load(handle, object_pairs_hook=pairs_hook)
if document.get("schemaVersion") != 1:
    die("unsupported AetherRemote bootstrap schema")
release_identity = document.get("releaseIdentity", "")
release_version = document.get("releaseVersion", "")
if not re.fullmatch(r"[A-Za-z0-9._-]{1,96}", release_identity):
    die("invalid release identity")
if not re.fullmatch(r"[0-9A-Za-z.+_-]{1,96}", release_version):
    die("invalid release version")
key = document.get("releaseVerificationKey") or {}
key_sha = str(key.get("sha256", "")).lower()
if key_sha != os.environ["EXPECTED_KEY_SHA"]:
    die("release verification key fingerprint does not match the pinned Admin value")
if key.get("algorithm") != "ecdsa-p256-sha256":
    die("unsupported release verification algorithm")
if not re.fullmatch(r"[A-Za-z0-9._:-]{1,64}", str(key.get("keyId", ""))):
    die("invalid release verification key ID")
try:
    spki = base64.b64decode(key.get("subjectPublicKeyInfoBase64", ""), validate=True)
except Exception as exc:
    die(f"invalid release verification key: {exc}")
if len(spki) < 64 or len(spki) > 1024:
    die("release verification key length is invalid")

same_wss_origin(
    document.get("brokerWebSocketUrl", ""),
    os.environ["GATEWAY"],
    "/aetherremote/broker/station/v1")
same_https_origin(
    document.get("brokerTokenUrl", ""),
    os.environ["GATEWAY"],
    "/aetherremote/broker/station/v1/token")
same_https_origin(
    document.get("enrollmentUrl", ""),
    os.environ["GATEWAY"],
    "/api/station-enrollment/redeem")
same_https_origin(
    document.get("installerUrl", ""),
    os.environ["GATEWAY"],
    "/aetherremote/install")

architectures = document.get("architectures")
if not isinstance(architectures, list) or not 1 <= len(architectures) <= 2:
    die("invalid architecture inventory")
matches = [item for item in architectures if item.get("architecture") == os.environ["ARCHITECTURE"]]
if len(matches) != 1:
    die("the gateway does not host one exact package for this architecture")
selected = matches[0]
for field, suffix in (
    ("manifestUrl", "/manifest"),
    ("agentPackageUrl", "/agent"),
    ("stationEnginePackageUrl", "/station-engine"),
):
    url = selected.get(field, "")
    same_https_origin(url, os.environ["GATEWAY"])
    if not url.endswith(f"/{os.environ['ARCHITECTURE']}{suffix}"):
        die("architecture asset URL is inconsistent")

print(release_identity)
print(release_version)
print(document["brokerWebSocketUrl"])
print(document["brokerTokenUrl"])
print(document["enrollmentUrl"])
print(selected["manifestUrl"])
print(selected["agentPackageUrl"])
print(selected["stationEnginePackageUrl"])
print(key["keyId"])
print(key["subjectPublicKeyInfoBase64"])
PY
) || fail "the gateway bootstrap document failed strict validation"

[[ "${#bootstrap_fields[@]}" -eq 10 ]] ||
  fail "the validated gateway bootstrap document was incomplete"
release_identity="${bootstrap_fields[0]}"
release_version="${bootstrap_fields[1]}"
broker_websocket_url="${bootstrap_fields[2]}"
broker_token_url="${bootstrap_fields[3]}"
enrollment_url="${bootstrap_fields[4]}"
manifest_url="${bootstrap_fields[5]}"
agent_url="${bootstrap_fields[6]}"
engine_url="${bootstrap_fields[7]}"
release_key_id="${bootstrap_fields[8]}"
release_key_spki="${bootstrap_fields[9]}"

printf '%s' "${release_key_spki}" | base64 --decode >"${key_der}" ||
  fail "the release public key could not be decoded"
actual_key_sha="$(sha256sum "${key_der}" | cut -d' ' -f1)"
[[ "${actual_key_sha}" == "${release_key_sha256}" ]] ||
  fail "the decoded release public key fingerprint does not match the pinned value"
openssl pkey -pubin -inform DER -in "${key_der}" -out "${key_pem}" >/dev/null 2>&1 ||
  fail "the release public key is not a valid SubjectPublicKeyInfo value"

# A WebSocket handshake with no station token must reach the broker and fail at
# authentication. 401 proves DNS, TLS, proxy path stripping, HTTP upgrade, and
# broker routing without consuming or exposing an enrollment secret.
broker_probe_url="https://${broker_websocket_url#wss://}"
broker_status="$(
  curl \
    --proto '=https' \
    --tlsv1.2 \
    --silent \
    --show-error \
    --output /dev/null \
    --write-out '%{http_code}' \
    --connect-timeout 10 \
    --max-time 15 \
    --http1.1 \
    --header 'Connection: Upgrade' \
    --header 'Upgrade: websocket' \
    --header 'Sec-WebSocket-Version: 13' \
    --header 'Sec-WebSocket-Key: MDEyMzQ1Njc4OWFiY2RlZg==' \
    --header 'Sec-WebSocket-Protocol: aetherremote.station.v1' \
    --header "X-Aether-Station-Id: ${station_id}" \
    "${broker_probe_url}" || true
)"
[[ "${broker_status}" == "401" ]] ||
  fail "the broker WebSocket route did not reach its expected unauthenticated boundary (HTTP ${broker_status:-none})"

curl_https "${manifest_url}" "${manifest_path}" ||
  fail "the signed release manifest could not be downloaded"
curl_https "${agent_url}" "${agent_archive}" ||
  fail "the signed Agent package could not be downloaded"
curl_https "${engine_url}" "${engine_archive}" ||
  fail "the signed station-engine package could not be downloaded"

mapfile -t manifest_fields < <(
  MANIFEST="${manifest_path}" \
  SIGNING="${signing_path}" \
  SIGNATURE="${signature_der}" \
  RELEASE="${release_identity}" \
  VERSION="${release_version}" \
  ARCHITECTURE="${architecture}" \
  KEY_ID="${release_key_id}" \
  python3 - <<'PY'
import base64
import json
import os
import re
from pathlib import Path

class DuplicateKey(ValueError):
    pass

def pairs_hook(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise DuplicateKey(key)
        result[key] = value
    return result

def die(message):
    raise SystemExit(message)

def b64url_decode(value):
    if not re.fullmatch(r"[A-Za-z0-9_-]{40,256}", value or ""):
        die("invalid manifest signature encoding")
    return base64.urlsafe_b64decode(value + "=" * ((4 - len(value) % 4) % 4))

def der_integer(value):
    encoded = value.to_bytes((value.bit_length() + 7) // 8 or 1, "big")
    if encoded[0] & 0x80:
        encoded = b"\x00" + encoded
    return b"\x02" + bytes([len(encoded)]) + encoded

raw = Path(os.environ["MANIFEST"]).read_bytes()
if not raw or len(raw) > 1024 * 1024 or raw.startswith(b"\xef\xbb\xbf"):
    die("manifest size or encoding is invalid")
try:
    document = json.loads(raw, object_pairs_hook=pairs_hook)
except Exception as exc:
    die(f"manifest JSON is invalid: {exc}")
payload = document.get("payload") or {}
signature = document.get("signature") or {}
if payload.get("schemaVersion") != 1:
    die("unsupported release manifest schema")
if payload.get("releaseIdentity") != os.environ["RELEASE"]:
    die("manifest release identity mismatch")
if payload.get("version") != os.environ["VERSION"]:
    die("manifest version mismatch")
expected_arch = {"linux-x64": "linuxX64", "linux-arm64": "linuxArm64"}[os.environ["ARCHITECTURE"]]
if payload.get("architecture") != expected_arch:
    die("manifest architecture mismatch")
if signature.get("algorithm") != "ecdsaP256Sha256" or signature.get("keyId") != os.environ["KEY_ID"]:
    die("manifest signing key mismatch")
if (payload.get("txSupport") or {}).get("enablesTransmit") is not False:
    die("station bootstrap refuses a release that declares transmit enabled")

packages = payload.get("packages")
if not isinstance(packages, list) or len(packages) != 4:
    die("manifest must declare exactly four release package roles")
by_role = {}
for package in packages:
    role = package.get("role")
    if role in by_role:
        die("duplicate package role")
    by_role[role] = package
for role in ("gatewayWeb", "broker", "aetherRemoteAgent", "stationEngine"):
    if role not in by_role:
        die("required package role is missing")

def validate_package(role):
    package = by_role[role]
    name = package.get("fileName", "")
    digest = str(package.get("sha256", "")).lower()
    length = package.get("length")
    if not re.fullmatch(r"packages/[A-Za-z0-9._-]{1,160}", name) or "\\" in name:
        die("unsafe package file name")
    if not re.fullmatch(r"[0-9a-f]{64}", digest):
        die("invalid package digest")
    if not isinstance(length, int) or length <= 0 or length > 1024 * 1024 * 1024:
        die("invalid package length")
    return name, digest, length

agent = validate_package("aetherRemoteAgent")
engine = validate_package("stationEngine")
sig_value = signature.get("value", "")
sig_raw = b64url_decode(sig_value)
if len(sig_raw) != 64:
    die("ECDSA P-256 signature must be 64-byte P1363")
# The release signer serializes the signing document exactly as the final JSON
# except for the final signature.value property. Preserve the exact raw payload
# bytes rather than reserializing untrusted JSON.
suffix = b',"value":"' + sig_value.encode("ascii") + b'"}}'
if not raw.endswith(suffix):
    die("manifest does not have the canonical release-signer shape")
signing = raw[:-len(suffix)] + b'}}'
Path(os.environ["SIGNING"]).write_bytes(signing)
r = int.from_bytes(sig_raw[:32], "big")
s = int.from_bytes(sig_raw[32:], "big")
sequence = der_integer(r) + der_integer(s)
if len(sequence) >= 128:
    die("unexpected ECDSA DER length")
Path(os.environ["SIGNATURE"]).write_bytes(b"\x30" + bytes([len(sequence)]) + sequence)
print(agent[0])
print(agent[1])
print(agent[2])
print(engine[0])
print(engine[1])
print(engine[2])
PY
) || fail "the signed release manifest failed strict validation"

[[ "${#manifest_fields[@]}" -eq 6 ]] ||
  fail "the signed release manifest was incomplete"
agent_name="${manifest_fields[0]}"
agent_sha="${manifest_fields[1]}"
agent_length="${manifest_fields[2]}"
engine_name="${manifest_fields[3]}"
engine_sha="${manifest_fields[4]}"
engine_length="${manifest_fields[5]}"

openssl dgst -sha256 -verify "${key_pem}" \
  -signature "${signature_der}" "${signing_path}" >/dev/null 2>&1 ||
  fail "the release manifest signature is invalid"
[[ "$(stat -c '%s' "${agent_archive}")" == "${agent_length}" ]] ||
  fail "Agent package length does not match the signed manifest"
[[ "$(stat -c '%s' "${engine_archive}")" == "${engine_length}" ]] ||
  fail "station-engine package length does not match the signed manifest"
[[ "$(sha256sum "${agent_archive}" | cut -d' ' -f1)" == "${agent_sha}" ]] ||
  fail "Agent package SHA-256 does not match the signed manifest"
[[ "$(sha256sum "${engine_archive}" | cut -d' ' -f1)" == "${engine_sha}" ]] ||
  fail "station-engine package SHA-256 does not match the signed manifest"

safe_extract() {
  local archive="$1"
  local destination="$2"
  ARCHIVE="${archive}" DESTINATION="${destination}" python3 - <<'PY'
import os
import shutil
import tarfile
from pathlib import Path, PurePosixPath

archive = os.environ["ARCHIVE"]
destination = Path(os.environ["DESTINATION"]).resolve()
max_entries = 10000
max_total = 2 * 1024 * 1024 * 1024
count = 0
total = 0
with tarfile.open(archive, "r:gz") as tar:
    members = tar.getmembers()
    for member in members:
        count += 1
        if count > max_entries:
            raise SystemExit("archive entry limit exceeded")
        path = PurePosixPath(member.name)
        parts = [part for part in path.parts if part not in ("", ".")]
        if path.is_absolute() or any(part == ".." for part in parts):
            raise SystemExit("unsafe archive path")
        if not parts:
            if member.isdir() and member.name in {".", "./"}:
                continue
            raise SystemExit("unsafe archive path")
        if not (member.isfile() or member.isdir()):
            raise SystemExit("archive contains link, device, or unsupported entry")
        if member.isfile():
            if member.size < 0 or member.size > 1024 * 1024 * 1024:
                raise SystemExit("archive file size is invalid")
            total += member.size
            if total > max_total:
                raise SystemExit("archive expanded size limit exceeded")
    for member in members:
        path = PurePosixPath(member.name)
        parts = [part for part in path.parts if part not in ("", ".")]
        target = destination.joinpath(*parts)
        resolved_parent = target.parent.resolve()
        if destination != resolved_parent and destination not in resolved_parent.parents:
            raise SystemExit("archive target escaped destination")
        if member.isdir():
            target.mkdir(parents=True, exist_ok=True)
            target.chmod(0o755)
            continue
        target.parent.mkdir(parents=True, exist_ok=True)
        source = tar.extractfile(member)
        if source is None:
            raise SystemExit("archive file payload is missing")
        with source, open(target, "xb") as output:
            shutil.copyfileobj(source, output, length=1024 * 1024)
        mode = 0o755 if (member.mode & 0o111) else 0o644
        target.chmod(mode)
PY
}

safe_extract "${agent_archive}" "${agent_extract}" ||
  fail "the Agent package contains an unsafe archive entry"
safe_extract "${engine_archive}" "${engine_extract}" ||
  fail "the station-engine package contains an unsafe archive entry"
[[ -x "${agent_extract}/AetherRemote.Agent" ]] ||
  fail "verified Agent package is missing AetherRemote.Agent"
[[ -x "${engine_extract}/AetherSDR.Web" ]] ||
  fail "verified station-engine package is missing AetherSDR.Web"
for signed_asset in \
  aetherremote-agent.service \
  aetherremote-station-engine.service \
  aetherremote-release-updater.service \
  enroll-station.sh; do
  [[ -f "${agent_extract}/${signed_asset}" ]] ||
    fail "verified Agent package is missing signed deployment asset ${signed_asset}"
done

# Use the exact Agent parser for a bounded receive-only discovery observation.
# It listens for normal FLEX discovery advertisements and sends no radio command.
echo "Checking for station-local FLEX discovery advertisements..."
set +e
discovery_output="$(
  timeout 5 "${agent_extract}/AetherRemote.Agent" --discover-once --seconds 3 2>/dev/null
)"
discovery_status=$?
set -e
if [[ "${discovery_status}" -ne 0 && "${discovery_status}" -ne 124 ]]; then
  fail "the verified Agent could not perform its no-command discovery check"
fi
if [[ -n "${discovery_output}" ]]; then
  printf '%s\n' "${discovery_output}"
else
  echo "No FLEX discovery advertisement was observed during the bounded pre-enrollment window."
fi

release_root="/opt/aetherremote/releases/${release_identity}"
agent_install="${release_root}/agent"
engine_install="${release_root}/station-engine"
credential_file="/etc/aetherremote/station-credential"
agent_config_dir="/etc/aetherremote/agent"
engine_config_dir="/etc/aetherremote/station-engine"
engine_state_dir="/var/lib/aetherremote/station-engine"
release_state_dir="/var/lib/aetherremote/releases"
release_staging_dir="/var/lib/aetherremote/release-staging"
trust_dir="/etc/aetherremote/release-trust"

if ! id -u aetherremote >/dev/null 2>&1; then
  useradd --system --home /nonexistent --shell /usr/sbin/nologin aetherremote
fi
install -d -o root -g root -m 0755 /opt/aetherremote /opt/aetherremote/releases
install -d -o root -g aetherremote -m 0750 \
  /etc/aetherremote "${agent_config_dir}" "${engine_config_dir}" "${trust_dir}"
install -d -o aetherremote -g aetherremote -m 0750 \
  /var/lib/aetherremote "${engine_state_dir}" "${release_state_dir}"
install -d -o aetherremote -g aetherremote -m 0700 \
  "${release_staging_dir}"

if [[ ! -e "${release_root}" ]]; then
  staging_root="/opt/aetherremote/releases/.${release_identity}.bootstrap-staging"
  [[ ! -e "${staging_root}" ]] || fail "a prior release staging directory requires reconciliation"
  install -d -o root -g root -m 0755 "${staging_root}"
  cp -a -- "${agent_extract}" "${staging_root}/agent"
  cp -a -- "${engine_extract}" "${staging_root}/station-engine"
  chown -R root:root "${staging_root}"
  find "${staging_root}" -type d -exec chmod 0755 -- {} +
  find "${staging_root}" -type f -exec chmod 0644 -- {} +
  chmod 0755 \
    "${staging_root}/agent/AetherRemote.Agent" \
    "${staging_root}/agent/updater/AetherRemote.Updater" \
    "${staging_root}/agent/enroll-station.sh" \
    "${staging_root}/station-engine/AetherSDR.Web"
  mv -- "${staging_root}" "${release_root}"
else
  [[ -x "${agent_install}/AetherRemote.Agent" && -x "${engine_install}/AetherSDR.Web" ]] ||
    fail "the existing exact release directory is incomplete and will not be overwritten"
fi

# Preserve the operator's station credential across reinstall/repair. A new
# random credential is created locally only when none exists yet.
if [[ ! -f "${credential_file}" ]]; then
  credential_temp="${work_dir}/station-credential"
  openssl rand -hex 32 >"${credential_temp}"
  install -o aetherremote -g aetherremote -m 0600 \
    "${credential_temp}" "${credential_file}"
fi
chown aetherremote:aetherremote "${credential_file}"
chmod 0600 "${credential_file}"

install -o root -g aetherremote -m 0640 "${key_der}" \
  "${trust_dir}/release-public-key.der"
printf '%s\n' "${release_key_sha256}" >"${work_dir}/release-key.sha256"
install -o root -g aetherremote -m 0640 "${work_dir}/release-key.sha256" \
  "${trust_dir}/release-public-key.sha256"

agent_config="${work_dir}/agent-appsettings.json"
AGENT_CONFIG="${agent_config}" \
BROKER_URL="${broker_websocket_url}" \
GATEWAY_URL="${canonical_gateway}" \
STATION_ID="${station_id}" \
RELEASE_IDENTITY="${release_identity}" \
RELEASE_VERSION="${release_version}" \
CREDENTIAL_FILE="${credential_file}" \
python3 - <<'PY'
import json
import os
payload = {
    "Agent": {
        "BrokerUrl": os.environ["BROKER_URL"],
        "GatewayUrl": os.environ["GATEWAY_URL"],
        "StationId": os.environ["STATION_ID"],
        "CredentialFile": os.environ["CREDENTIAL_FILE"],
        "ReleaseIdentity": os.environ["RELEASE_IDENTITY"],
        "StationEngineVersion": os.environ["RELEASE_VERSION"],
        "ReleaseVerificationKeyPath": "/etc/aetherremote/release-trust/release-public-key.der",
        "ReleaseVerificationKeySha256File": "/etc/aetherremote/release-trust/release-public-key.sha256",
        "DiscoveryEnabled": True,
        "InventorySeconds": 5,
        "RadioOfflineSeconds": 15,
        "LocalEngineUrl": "http://127.0.0.1:5081",
        "LocalEngineOrigin": "http://127.0.0.1:5081",
        "AllowInsecureDevelopmentTransport": False,
        "ReleaseServiceControlEnabled": True,
        "ReleaseUpdateEnabled": True,
        "Capabilities": [
            "receive-projection-v1",
            "release-service-control-v1",
            "release-update-v1"
        ],
        "ConfiguredRadios": []
    }
}
with open(os.environ["AGENT_CONFIG"], "w", encoding="utf-8") as handle:
    json.dump(payload, handle, indent=2)
    handle.write("\n")
PY
install -o root -g aetherremote -m 0640 \
  "${agent_config}" "${agent_config_dir}/appsettings.json"

engine_config="${work_dir}/station-engine-appsettings.json"
cat >"${engine_config}" <<'EOF'
{
  "Auth": {
    "Mode": "Development",
    "DevelopmentUser": {
      "ObjectId": "aetherremote-station-engine",
      "Name": "AetherRemote Station Engine",
      "Email": "station-engine@localhost",
      "Roles": ["Aether.Observe", "Aether.Control", "Aether.Admin"]
    }
  },
  "Radio": {
    "Mode": "FlexRx",
    "AllowTransmit": false,
    "Host": "127.0.0.1",
    "TcpPort": 4992,
    "CenterFrequencyHz": 14280000,
    "BandwidthHz": 200000,
    "InitialSliceFrequencyHz": 14074000,
    "SecondarySliceFrequencyHz": 14100000,
    "MinDbm": -130,
    "MaxDbm": -40,
    "XPixels": 1024,
    "YPixels": 700,
    "FramesPerSecond": 15,
    "NetworkMtu": 1200,
    "LowBandwidthConnect": false,
    "StationName": "AETHER-REMOTE-RX"
  },
  "RadioAccess": {
    "PolicyPath": "/var/lib/aetherremote/station-engine/policies.json",
    "AuditPath": "/var/lib/aetherremote/station-engine/audit.json"
  },
  "DataProtection": {
    "KeyPath": "/var/lib/aetherremote/station-engine/data-protection"
  },
  "RemoteStations": { "Enabled": false },
  "AllowedOrigins": ["http://127.0.0.1:5081"],
  "ReverseProxy": { "Enabled": false, "KnownProxies": [] },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "127.0.0.1"
}
EOF
install -o root -g aetherremote -m 0640 \
  "${engine_config}" "${engine_config_dir}/appsettings.json"

# Stable service paths point to immutable versioned release directories. Use a
# same-parent temporary symlink followed by rename so a reader never sees a
# half-written target.
atomic_link() {
  local target="$1"
  local link="$2"
  local temporary="${link}.bootstrap-new"
  rm -f -- "${temporary}"
  ln -s -- "${target}" "${temporary}"
  mv -Tf -- "${temporary}" "${link}"
}
atomic_link "${agent_install}" /opt/aetherremote/agent
atomic_link "${engine_install}" /opt/aetherremote/station-engine
atomic_link "${agent_install}/updater" /opt/aetherremote/updater

install -o root -g root -m 0644 \
  "${agent_install}/aetherremote-agent.service" \
  /etc/systemd/system/aetherremote-agent.service
install -o root -g root -m 0644 \
  "${agent_install}/aetherremote-station-engine.service" \
  /etc/systemd/system/aetherremote-station-engine.service
install -o root -g root -m 0644 \
  "${agent_install}/aetherremote-release-updater.service" \
  /etc/systemd/system/aetherremote-release-updater.service
install -o root -g root -m 0755 \
  "${agent_install}/enroll-station.sh" \
  /usr/local/sbin/aetherremote-enroll

systemctl daemon-reload
systemctl enable \
  aetherremote-release-updater.service \
  aetherremote-station-engine.service \
  aetherremote-agent.service >/dev/null
systemctl restart aetherremote-release-updater.service
systemctl restart aetherremote-station-engine.service

health_ok=false
for _ in $(seq 1 20); do
  if curl --silent --fail --max-time 2 http://127.0.0.1:5081/healthz >/dev/null; then
    health_ok=true
    break
  fi
  sleep 1
done
[[ "${health_ok}" == true ]] ||
  fail "the station receive engine did not become healthy; Agent enrollment was not started"

credential="$(tr -d '\r\n' <"${credential_file}")"
[[ "${credential}" =~ ^[0-9a-f]{64}$ ]] ||
  fail "the local station credential is invalid"
credential_sha="$(printf '%s' "${credential}" | sha256sum | cut -d' ' -f1)"
unset credential

[[ -r /dev/tty ]] ||
  fail "a local interactive terminal is required for the one-time enrollment code"
printf 'One-time enrollment code (input hidden): ' >/dev/tty
IFS= read -r -s enrollment_code </dev/tty
printf '\n' >/dev/tty
[[ "${enrollment_code}" =~ ^[0-9a-fA-F]{64}$ ]] || {
  unset enrollment_code
  fail "the enrollment code must contain exactly 64 hexadecimal characters"
}

enrollment_body="${work_dir}/enrollment.json"
ENROLLMENT_CODE="${enrollment_code}" CREDENTIAL_SHA="${credential_sha}" \
python3 - <<'PY' >"${enrollment_body}"
import json
import os
print(json.dumps({
    "enrollmentCode": os.environ["ENROLLMENT_CODE"],
    "credentialSha256": os.environ["CREDENTIAL_SHA"],
}, separators=(",", ":")))
PY
unset enrollment_code credential_sha

curl \
  --proto '=https' \
  --tlsv1.2 \
  --fail \
  --silent \
  --show-error \
  --connect-timeout 10 \
  --max-time 30 \
  --header 'Content-Type: application/json' \
  --data-binary "@${enrollment_body}" \
  "${enrollment_url}" >/dev/null ||
  fail "the gateway rejected or could not complete the one-time station enrollment"

systemctl restart aetherremote-agent.service
sleep 2
systemctl is-active --quiet aetherremote-station-engine.service ||
  fail "the station receive engine is not active after enrollment"
systemctl is-active --quiet aetherremote-agent.service ||
  fail "the AetherRemote Agent is not active after enrollment"

echo "AetherRemote station enrollment completed."
echo "Station: ${station_id}"
echo "Release: ${release_identity} (${architecture})"
echo "Gateway: ${canonical_gateway}"
echo "The enrollment code and station credential were not written to command history or output."
echo "Transmit remains disabled in the station engine; no radio command or RF action was performed."
