#!/usr/bin/env python3
"""M8H clean remote-station packaged acceptance on Ubuntu 24.04 x64."""

from __future__ import annotations

import base64
import hashlib
import hmac
import http.cookiejar
import importlib.util
import json
import os
import secrets
import shutil
import ssl
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

STATION_ID = "m8h-station"
STATION_FAILURE_VERSION = "8.8.0-acceptance.4"
STATION_FAILURE_ID = f"aethersdr-{STATION_FAILURE_VERSION}"


def die(message: str) -> "NoReturn":
    raise SystemExit(f"M8H remote-station acceptance failed: {message}")


def load_common(path: Path):
    spec = importlib.util.spec_from_file_location("m8h_common", path)
    if spec is None or spec.loader is None:
        die("standalone acceptance helper could not be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def totp(secret_base32: str) -> str:
    secret = base64.b32decode(secret_base32, casefold=False)
    counter = int(time.time()) // 30
    digest = hmac.new(secret, counter.to_bytes(8, "big"), hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = int.from_bytes(digest[offset : offset + 4], "big") & 0x7FFFFFFF
    return f"{value % 1_000_000:06d}"


class AdminClient:
    def __init__(self, credentials: dict[str, str]) -> None:
        self.credentials = credentials
        self.jar = http.cookiejar.CookieJar()
        self.context = ssl.create_default_context()
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(self.jar),
            urllib.request.HTTPSHandler(context=self.context),
        )
        self.antiforgery_header = ""
        self.antiforgery_token = ""

    def request(
        self,
        method: str,
        path: str,
        body: dict | None = None,
        *,
        antiforgery: bool = False,
    ) -> dict:
        headers = {"Accept": "application/json"}
        payload = None
        if body is not None:
            headers["Content-Type"] = "application/json; charset=utf-8"
            payload = json.dumps(body, separators=(",", ":")).encode()
        if antiforgery:
            if not self.antiforgery_header or not self.antiforgery_token:
                die("Admin antiforgery authority is unavailable")
            headers[self.antiforgery_header] = self.antiforgery_token
        request = urllib.request.Request(
            "https://aethersdr.test" + path,
            data=payload,
            headers=headers,
            method=method,
        )
        try:
            with self.opener.open(request, timeout=15) as response:
                raw = response.read()
        except urllib.error.HTTPError as exception:
            raw = exception.read()
            die(f"Admin request {method} {path} returned {exception.code}: {raw[:1000]!r}")
        try:
            value = json.loads(raw)
        except json.JSONDecodeError:
            die(f"Admin request {method} {path} returned malformed JSON")
        if not isinstance(value, dict):
            die(f"Admin request {method} {path} returned an unexpected contract")
        return value

    def login(self) -> None:
        options = self.request("GET", "/api/auth/options?returnUrl=%2Fadmin")
        antiforgery = options.get("antiforgery") or {}
        header = antiforgery.get("headerName")
        token = antiforgery.get("requestToken")
        if not isinstance(header, str) or not isinstance(token, str) or not header or not token:
            die("local authentication did not issue antiforgery authority")
        self.antiforgery_header = header
        self.antiforgery_token = token
        password = self.request(
            "POST",
            "/api/auth/local/password",
            {
                "userName": self.credentials["userName"],
                "password": self.credentials["password"],
            },
            antiforgery=True,
        )
        challenge = password.get("challengeToken")
        if not isinstance(challenge, str) or not challenge:
            die("local password authentication did not issue an MFA challenge")
        mfa = self.request(
            "POST",
            "/api/auth/local/mfa",
            {
                "challengeToken": challenge,
                "verificationCode": totp(self.credentials["sharedSecretBase32"]),
                "returnUrl": "/admin",
            },
            antiforgery=True,
        )
        if mfa.get("redirectUrl") != "/admin":
            die("local MFA authentication did not establish the Admin session")
        token_contract = self.request("GET", "/api/antiforgery")
        self.antiforgery_header = str(token_contract.get("headerName") or "")
        self.antiforgery_token = str(token_contract.get("requestToken") or "")
        if not self.antiforgery_header or not self.antiforgery_token:
            die("authenticated Admin antiforgery authority is unavailable")


def install_runtime_trust(common, public_key_source: Path) -> Path:
    directory = Path("/etc/aethersdr/release-trust")
    directory.mkdir(parents=True, exist_ok=True)
    common.run(["/usr/bin/chown", "root:aethersdr", str(directory)])
    directory.chmod(0o750)
    target = directory / "m8h-release.pem"
    shutil.copyfile(public_key_source, target)
    common.run(["/usr/bin/chown", "root:aethersdr", str(target)])
    target.chmod(0o440)
    return target


def seed_bootstrap_bundle(source: Path, identity: str) -> None:
    target = Path("/var/lib/aethersdr/release-downloads") / f"{identity}-linux-x64"
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target, symlinks=False)
    for entry in target.rglob("*"):
        if entry.is_dir():
            entry.chmod(0o555)
        elif entry.is_file():
            entry.chmod(0o444)
    target.chmod(0o555)


def configure_web_trust(common, public_key: Path) -> None:
    directory = Path("/etc/systemd/system/aethersdr-web.service.d")
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / "m8h-bootstrap.conf"
    target.write_text(
        "\n".join(
            [
                "[Service]",
                "Environment=ReleaseManifestTrust__VerificationEnabled=true",
                f"Environment=ReleaseManifestTrust__Keys__0__KeyId={common.KEY_ID}",
                "Environment=ReleaseManifestTrust__Keys__0__Algorithm=EcdsaP256Sha256",
                f"Environment=ReleaseManifestTrust__Keys__0__PublicKeyPath={public_key}",
                "",
            ]
        ),
        encoding="utf-8",
    )
    target.chmod(0o644)
    common.run(["/usr/bin/systemctl", "daemon-reload"])
    common.run(["/usr/bin/systemctl", "restart", "aethersdr-web.service"])
    common.wait_health()


def prepare_systemd_container(common) -> None:
    docker = "/usr/bin/docker"
    common.run([docker, "rm", "-f", "m8h-station-prep"], check=False)
    common.run([docker, "rm", "-f", "m8h-station"], check=False)
    common.run([docker, "image", "rm", "-f", "aethersdr-m8h-station:local"], check=False)
    common.run([docker, "run", "-d", "--name", "m8h-station-prep", "ubuntu:24.04", "sleep", "infinity"], timeout=60)
    common.run([docker, "exec", "m8h-station-prep", "apt-get", "update"], timeout=180)
    common.run(
        [
            docker, "exec", "-e", "DEBIAN_FRONTEND=noninteractive", "m8h-station-prep",
            "apt-get", "install", "-y", "systemd", "systemd-sysv", "curl", "openssl",
            "ca-certificates", "python3", "sudo", "passwd", "iproute2", "procps",
        ],
        timeout=240,
    )
    common.run([docker, "commit", "m8h-station-prep", "aethersdr-m8h-station:local"], timeout=120)
    common.run([docker, "rm", "-f", "m8h-station-prep"])
    common.run(
        [
            docker,
            "run",
            "-d",
            "--name",
            "m8h-station",
            "--hostname",
            "m8h-station",
            "--privileged",
            "--cgroupns=host",
            "--add-host",
            "aethersdr.test:host-gateway",
            "-v",
            "/sys/fs/cgroup:/sys/fs/cgroup:rw",
            "aethersdr-m8h-station:local",
            "/sbin/init",
        ],
        timeout=60,
    )
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        state = common.run(
            [docker, "exec", "m8h-station", "systemctl", "is-system-running"],
            check=False,
        )
        if (state.stdout or "").strip() in {"running", "degraded"}:
            return
        time.sleep(1)
    die("Ubuntu station container did not reach a systemd running/degraded state")


def station_install(common, command: str, enrollment_code: str) -> None:
    ca = Path("/usr/local/share/ca-certificates/aethersdr-caddy-local.crt")
    if not ca.is_file():
        die("installer-managed Caddy CA is unavailable for station TLS trust")
    common.run([
        "/usr/bin/docker", "cp", str(ca),
        "m8h-station:/usr/local/share/ca-certificates/aethersdr-caddy-local.crt",
    ])
    common.run([
        "/usr/bin/docker", "exec", "m8h-station", "update-ca-certificates",
    ])
    discovery = (
        "import socket,time;"
        "p=b'model=FLEX-6700 serial=M8H-REMOTE-1 nickname=M8HRemote status=Available "
        "ip=127.0.0.1 port=4992 available_clients=1 licensed_clients=2';"
        "s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);"
        "exec('while True:\\n s.sendto(p,(\\\"127.0.0.1\\\",4992))\\n time.sleep(0.2)')"
    )
    common.run([
        "/usr/bin/docker", "exec", "-d", "m8h-station", "python3", "-c", discovery,
    ])
    _, output = common.pty_capture_sequence(
        [
            "/usr/bin/docker", "exec", "-it", "m8h-station",
            "/bin/bash", "-lc", command,
        ],
        env=os.environ.copy(),
        prompt_responses=[(
            "One-time enrollment code (input hidden): ",
            enrollment_code,
        )],
        timeout=300,
    )
    if "station receive engine is not active" in output.lower():
        die("remote station installer did not start the receive engine")


def poll_station(admin: AdminClient, identity: str, timeout: int = 90) -> dict:
    deadline = time.monotonic() + timeout
    last: dict = {}
    while time.monotonic() < deadline:
        snapshot = admin.request("GET", "/api/admin/stations")
        for station in snapshot.get("stations") or []:
            if station.get("stationId") == STATION_ID:
                last = station
                if station.get("state") == "online" and station.get("releaseIdentity") == identity:
                    return station
        time.sleep(2)
    die(f"station did not reach online release {identity}: {last}")


def broker_release_update(identity: str) -> dict:
    credential_path = Path(
        "/var/lib/aethersdr/secrets/remote-stations/administration-credential"
    )
    credential = credential_path.read_text(encoding="ascii").strip()
    body = json.dumps(
        {"stationId": STATION_ID, "releaseIdentity": identity},
        separators=(",", ":"),
    ).encode()
    request = urllib.request.Request(
        "http://127.0.0.1:5090/api/release-updates",
        data=body,
        method="POST",
        headers={
            "Content-Type": "application/json",
            "Accept": "application/json",
            "Authorization": f"Bearer {credential}",
        },
    )
    credential = ""
    try:
        with urllib.request.urlopen(request, timeout=210) as response:
            result = json.loads(response.read())
    except urllib.error.HTTPError as exception:
        die(f"broker release update returned {exception.code}: {exception.read()[:1000]!r}")
    if not isinstance(result, dict):
        die("broker release update returned an invalid contract")
    return result


def main() -> int:
    if os.geteuid() != 0:
        die("remote-station acceptance requires root on a disposable runner")
    if len(sys.argv) != 5:
        die("usage: run-remote-station-acceptance.py <artifact-root> <common-harness> <setup-helper> <gateway-package>")
    artifact_root = Path(sys.argv[1]).resolve()
    common_path = Path(sys.argv[2]).resolve()
    setup_helper = Path(sys.argv[3]).resolve()
    gateway_package = Path(sys.argv[4]).resolve()
    common = load_common(common_path)

    previous_bundle = artifact_root / "linux-x64" / common.PREVIOUS_ID
    target_bundle = artifact_root / "linux-x64" / common.TARGET_ID
    station_failure_bundle = artifact_root / "linux-x64" / STATION_FAILURE_ID
    for bundle in (previous_bundle, target_bundle, station_failure_bundle):
        if not (bundle / "release-manifest.json").is_file():
            die(f"remote acceptance bundle is missing: {bundle}")
        for path in bundle.rglob("*"):
            path.chmod(0o555 if path.is_dir() else 0o444)
        bundle.chmod(0o555)

    hosts = Path("/etc/hosts")
    if "aethersdr.test" not in hosts.read_text(encoding="utf-8"):
        hosts.write_text(
            hosts.read_text(encoding="utf-8") + "\n127.0.0.1 aethersdr.test\n",
            encoding="utf-8",
        )

    with tempfile.TemporaryDirectory(prefix="aethersdr-m8h-remote-") as temp_name:
        temp = Path(temp_name)
        packaged = temp / "gateway"
        common.safe_extract(gateway_package, packaged)
        gateway = packaged / "AetherSDR.Web"
        key = temp / "setup.key"
        cert = temp / "setup.crt"
        pfx = temp / "setup.pfx"
        credential_file = temp / "admin.json"
        pfx_password = secrets.token_urlsafe(32)
        common.run([
            "/usr/bin/openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
            "-keyout", str(key), "-out", str(cert), "-days", "1", "-subj", "/CN=127.0.0.1",
        ])
        common.run([
            "/usr/bin/openssl", "pkcs12", "-export", "-out", str(pfx),
            "-inkey", str(key), "-in", str(cert), "-passout", f"pass:{pfx_password}",
        ])
        setup_env = os.environ.copy()
        setup_env["M8H_SETUP_TOPOLOGY"] = "remoteStationGateway"
        setup_env["M8H_ACCEPTANCE_CREDENTIAL_FILE"] = str(credential_file)
        common.run(
            [sys.executable, str(setup_helper), str(gateway), str(pfx), pfx_password, common.PUBLIC_URL],
            env=setup_env,
            timeout=120,
        )
        if credential_file.stat().st_mode & 0o077:
            die("temporary Admin credential handoff is not owner-only")
        credentials = json.loads(credential_file.read_text(encoding="utf-8"))
        credential_file.unlink()

        initial_key_dir = Path("/root/.aethersdr-m8h-remote-trust")
        initial_key_dir.mkdir(mode=0o700)
        initial_key = initial_key_dir / "release.pem"
        shutil.copyfile(artifact_root / "release-verification-key.pem", initial_key)
        initial_key.chmod(0o400)
        installer_env = os.environ.copy()
        installer_env.update(
            {
                "ASPNETCORE_ENVIRONMENT": "Production",
                "DOTNET_ENVIRONMENT": "Production",
                "InstallationInstaller__Enabled": "true",
                "InstallationInstallerUbuntu__MutationEnabled": "true",
                "ReleaseManifestTrust__VerificationEnabled": "true",
                "ReleaseManifestTrust__Keys__0__KeyId": common.KEY_ID,
                "ReleaseManifestTrust__Keys__0__Algorithm": "EcdsaP256Sha256",
                "ReleaseManifestTrust__Keys__0__PublicKeyPath": str(initial_key),
            }
        )
        common.run_install(gateway, previous_bundle, "linux-x64", installer_env)
        common.wait_health()

        public_key = install_runtime_trust(common, artifact_root / "release-verification-key.pem")
        for source, identity in (
            (previous_bundle, common.PREVIOUS_ID),
            (target_bundle, common.TARGET_ID),
            (station_failure_bundle, STATION_FAILURE_ID),
        ):
            seed_bootstrap_bundle(source, identity)
        configure_web_trust(common, public_key)
        common.write_update_dropin(public_key)

        admin = AdminClient(credentials)
        admin.login()
        credentials.clear()
        guide = admin.request(
            "GET",
            "/api/admin/stations/bootstrap?" + urllib.parse.urlencode({"stationId": STATION_ID}),
        )
        if not guide.get("ready") or not guide.get("installCommand"):
            die(f"Admin bootstrap guide is not ready: {guide}")
        enrollment = admin.request(
            "POST",
            "/api/admin/stations/enrollment-codes",
            {"stationId": STATION_ID},
            antiforgery=True,
        )
        enrollment_code = str(enrollment.get("enrollmentCode") or "")
        if len(enrollment_code) != 64:
            die("Admin did not issue one bounded one-time station code")

        prepare_systemd_container(common)
        try:
            station_install(common, str(guide["installCommand"]), enrollment_code)
            enrollment_code = ""
            station = poll_station(admin, common.PREVIOUS_ID)
            radios = station.get("radios") or []
            if not any(radio.get("serial") == "M8H-REMOTE-1" for radio in radios):
                die("remote station did not publish the synthetically discovered FLEX radio")

            update_env = common.update_environment(os.environ.copy(), public_key)
            gateway_update = common.activate(
                Path("/opt/aethersdr/current/gateway-web/AetherSDR.Web"),
                target_bundle,
                common.PREVIOUS_ID,
                common.PREVIOUS_VERSION,
                common.TARGET_ID,
                update_env,
            )
            if not gateway_update["final"].get("activationCompleted"):
                die("remote gateway did not advance to the target signed release")
            common.wait_health()

            station_target = broker_release_update(common.TARGET_ID)
            if not station_target.get("succeeded") or station_target.get("rolledBack"):
                die(f"station signed update did not succeed: {station_target}")
            station = poll_station(admin, common.TARGET_ID)
            if station.get("stationEngineVersion") != common.TARGET_VERSION:
                die("station engine version did not advance with the signed station release")

            gateway_station_failure = common.activate(
                Path("/opt/aethersdr/current/gateway-web/AetherSDR.Web"),
                station_failure_bundle,
                common.TARGET_ID,
                common.TARGET_VERSION,
                STATION_FAILURE_ID,
                update_env,
            )
            if not gateway_station_failure["final"].get("activationCompleted"):
                die("gateway could not host the station failure acceptance release")
            common.wait_health()
            station_failure = broker_release_update(STATION_FAILURE_ID)
            if station_failure.get("succeeded") or not station_failure.get("rolledBack"):
                die(f"station failure did not roll back locally: {station_failure}")
            if station_failure.get("activeReleaseIdentity") != common.TARGET_ID:
                die("station local rollback did not restore its previous target release")
            poll_station(admin, common.TARGET_ID)

            print(json.dumps({
                "schemaVersion": 1,
                "gatewayTopology": "remoteStationGateway",
                "stationIdRedacted": True,
                "guidedBootstrap": True,
                "oneTimeEnrollment": True,
                "syntheticFlexDiscovery": True,
                "stationAppearedInAdmin": True,
                "stationSignedUpdate": common.TARGET_ID,
                "stationFailedUpdateRolledBack": common.TARGET_ID,
                "stationCredentialPersisted": True,
                "liveRfPerformed": False,
            }, separators=(",", ":")))
        finally:
            enrollment_code = ""
            common.run(["/usr/bin/docker", "rm", "-f", "m8h-station"], check=False)
            common.run(["/usr/bin/docker", "image", "rm", "-f", "aethersdr-m8h-station:local"], check=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
