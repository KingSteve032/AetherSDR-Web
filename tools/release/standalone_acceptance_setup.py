#!/usr/bin/env python3
"""Configure one packaged AetherSDR setup-only host through its public HTTPS API.

This is a disposable M8H acceptance helper. Secrets are held in process memory and
are never printed. It intentionally uses no repository source runtime.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import http.client
import json
import os
import pty
import re
import select
import secrets
import ssl
import subprocess
import sys
import time
from http.cookies import SimpleCookie
from pathlib import Path

ORIGIN = "https://127.0.0.1:5443"
HOST = "127.0.0.1"
PORT = 5443
CSRF_COOKIE = "__Host-AetherSdrSetupCsrf"
SESSION_COOKIE = "__Host-AetherSdrSetup"
CSRF_HEADER = "X-Aether-Setup-Csrf"
REVISION_HEADER = "X-Aether-Setup-Revision"


def die(message: str) -> "NoReturn":
    raise SystemExit(f"M8H setup acceptance failed: {message}")


def pty_capture(argv: list[str], env: dict[str, str]) -> str:
    pid, fd = pty.fork()
    if pid == 0:
        os.execve(argv[0], argv, env)
    chunks: list[bytes] = []
    while True:
        ready, _, _ = select.select([fd], [], [], 20)
        if not ready:
            os.kill(pid, 9)
            die("interactive setup command timed out")
        try:
            chunk = os.read(fd, 4096)
        except OSError:
            break
        if not chunk:
            break
        chunks.append(chunk)
    _, status = os.waitpid(pid, 0)
    if os.waitstatus_to_exitcode(status) != 0:
        die("interactive setup command failed")
    return b"".join(chunks).decode("utf-8", "strict")


def issue_bootstrap(binary: Path, env: dict[str, str]) -> str:
    output = pty_capture([str(binary), "--issue-installation-bootstrap-token"], env)
    match = re.search(r"(?:^|\r?\n)Token: ([A-Za-z0-9_-]{20,256})(?:\r?$)", output, re.MULTILINE)
    if match is None:
        die("bootstrap token was not emitted on the interactive terminal")
    return match.group(1)


def totp(secret_base32: str) -> str:
    secret = base64.b32decode(secret_base32, casefold=False)
    counter = int(time.time()) // 30
    message = counter.to_bytes(8, "big")
    digest = hmac.new(secret, message, hashlib.sha1).digest()
    offset = digest[-1] & 0x0F
    value = int.from_bytes(digest[offset : offset + 4], "big") & 0x7FFFFFFF
    return f"{value % 1_000_000:06d}"


class SetupClient:
    def __init__(self) -> None:
        self.cookies: dict[str, str] = {}
        self.context = ssl.create_default_context()
        self.context.check_hostname = False
        self.context.verify_mode = ssl.CERT_NONE

    def _store_cookies(self, headers: list[tuple[str, str]]) -> None:
        for name, value in headers:
            if name.lower() != "set-cookie":
                continue
            parsed = SimpleCookie()
            parsed.load(value)
            for key, morsel in parsed.items():
                if morsel["max-age"] == "0" or not morsel.value:
                    self.cookies.pop(key, None)
                else:
                    self.cookies[key] = morsel.value

    def request(
        self,
        method: str,
        path: str,
        *,
        body: dict | None = None,
        revision: int | None = None,
        navigation: bool = False,
    ) -> dict:
        connection = http.client.HTTPSConnection(
            HOST,
            PORT,
            context=self.context,
            timeout=10,
        )
        headers = {
            "Accept": "application/json",
            "Sec-Fetch-Site": "none" if navigation else "same-origin",
            "Sec-Fetch-Mode": "navigate" if navigation else "cors",
        }
        if not navigation:
            headers["Origin"] = ORIGIN
        if self.cookies:
            headers["Cookie"] = "; ".join(
                f"{name}={value}" for name, value in self.cookies.items()
            )
        if revision is not None:
            headers[REVISION_HEADER] = str(revision)
        payload = None
        if body is not None:
            csrf = self.cookies.get(CSRF_COOKIE)
            if not csrf:
                die("setup CSRF cookie is unavailable")
            headers["Content-Type"] = "application/json; charset=utf-8"
            headers[CSRF_HEADER] = csrf
            payload = json.dumps(body, separators=(",", ":"), ensure_ascii=True).encode()
        connection.request(method, path, body=payload, headers=headers)
        response = connection.getresponse()
        raw = response.read()
        response_headers = response.getheaders()
        self._store_cookies(response_headers)
        content_type = response.getheader("Content-Type", "")
        try:
            parsed = json.loads(raw) if content_type.startswith("application/json") else None
        except json.JSONDecodeError:
            parsed = None
        if not 200 <= response.status < 300:
            die(f"setup request {method} {path} returned {response.status}: {parsed!r}")
        if parsed is None:
            die(f"setup request {method} {path} returned no JSON contract")
        return parsed


def wait_for_https(process: subprocess.Popen[bytes]) -> None:
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        if process.poll() is not None:
            die("packaged setup-only host exited before becoming ready")
        try:
            connection = http.client.HTTPSConnection(HOST, PORT, context=context, timeout=1)
            connection.request("GET", "/setup", headers={
                "Sec-Fetch-Site": "none",
                "Sec-Fetch-Mode": "navigate",
            })
            response = connection.getresponse()
            response.read()
            if response.status == 200:
                return
        except OSError:
            pass
        time.sleep(0.25)
    die("packaged setup-only host did not become ready")


def main() -> int:
    if len(sys.argv) != 5:
        die("usage: standalone_acceptance_setup.py <gateway-binary> <cert-pfx> <cert-password> <public-url>")
    binary = Path(sys.argv[1]).resolve()
    certificate = Path(sys.argv[2]).resolve()
    certificate_password = sys.argv[3]
    public_url = sys.argv[4]
    if not binary.is_file() or not certificate.is_file():
        die("packaged gateway binary or TLS certificate is missing")

    env = os.environ.copy()
    env.update(
        {
            "ASPNETCORE_ENVIRONMENT": "Production",
            "DOTNET_ENVIRONMENT": "Production",
            "ASPNETCORE_URLS": ORIGIN,
            "ASPNETCORE_Kestrel__Certificates__Default__Path": str(certificate),
            "ASPNETCORE_Kestrel__Certificates__Default__Password": certificate_password,
            "InstallationSetupOnly__Enabled": "true",
            "InstallationSetupOnly__CanonicalAccessUrl": ORIGIN,
            "InstallationRuntime__Enabled": "false",
        }
    )

    bootstrap = issue_bootstrap(binary, env)
    host = subprocess.Popen(
        [str(binary)],
        env=env,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
    )
    try:
        wait_for_https(host)
        client = SetupClient()
        page = client.request("GET", "/setup", navigation=True)
        revision = int(page["status"]["revision"])
        claim = client.request(
            "POST",
            "/setup/api/claim",
            body={"expectedRevision": revision, "bootstrapToken": bootstrap},
        )
        bootstrap = ""
        revision = int(claim["session"]["setupRevision"])

        topology = os.environ.get(
            "M8H_SETUP_TOPOLOGY",
            "personalSingleStation",
        )
        if topology not in {
            "personalSingleStation",
            "localStationGateway",
            "remoteStationGateway",
            "hybridGateway",
        }:
            die("unsupported M8H setup topology")
        mutations = [
            ("/setup/api/topology", {"topology": topology}),
            ("/setup/api/public-url", {"canonicalPublicUrl": public_url}),
            (
                "/setup/api/paths",
                {
                    "configurationDirectory": "/etc/aethersdr",
                    "stateDirectory": "/var/lib/aethersdr",
                    "secretDirectory": "/var/lib/aethersdr/secrets",
                    "releaseDirectory": "/opt/aethersdr/releases",
                    "backupDirectory": "/var/backups/aethersdr",
                    "logDirectory": "/var/log/aethersdr",
                },
            ),
            ("/setup/api/update-channel", {"updateChannel": "beta", "pinnedRelease": None}),
            ("/setup/api/backup", {"confirmed": True}),
            (
                "/setup/api/transmit-support",
                {
                    "installTransmitSupport": False,
                    "acknowledgedInstallationDoesNotEnableTransmit": True,
                },
            ),
        ]
        for path, values in mutations:
            body = {"expectedRevision": revision, **values}
            response = client.request("POST", path, body=body)
            revision = int(response["session"]["setupRevision"])

        preflight = client.request("GET", "/setup/api/preflight", revision=revision)
        if bool(preflight["preflight"].get("installTransmitSupport", True)):
            die("setup preflight did not remain receive-only")

        password = "M8H!" + secrets.token_urlsafe(32)
        enrollment = client.request(
            "POST",
            "/setup/api/administrator/enroll",
            body={
                "expectedRevision": revision,
                "userName": "m8h-admin",
                "displayName": "M8H Acceptance Admin",
                "email": None,
                "password": password,
            },
        )
        secret = enrollment["sharedSecretBase32"]
        if len(enrollment.get("recoveryCodes") or []) < 1:
            die("first administrator did not receive recovery codes")
        confirmation = client.request(
            "POST",
            "/setup/api/administrator/confirm",
            body={"expectedRevision": revision, "totpCode": totp(secret)},
        )
        if not confirmation.get("completed") or not confirmation["status"].get("setupComplete"):
            die("first administrator handoff did not complete setup")

        credential_file = os.environ.get("M8H_ACCEPTANCE_CREDENTIAL_FILE", "")
        if credential_file:
            target = Path(credential_file).resolve()
            descriptor = os.open(
                target,
                os.O_WRONLY | os.O_CREAT | os.O_EXCL,
                0o600,
            )
            try:
                os.write(
                    descriptor,
                    json.dumps(
                        {
                            "userName": "m8h-admin",
                            "password": password,
                            "sharedSecretBase32": secret,
                        },
                        separators=(",", ":"),
                    ).encode("utf-8"),
                )
                os.fsync(descriptor)
            finally:
                os.close(descriptor)

        print(json.dumps({
            "schemaVersion": 1,
            "setupRevision": int(confirmation["status"]["revision"]),
            "administrator": "created-with-totp-and-recovery-codes",
            "transmitSupportInstalled": False,
        }, separators=(",", ":")))

        try:
            host.wait(timeout=15)
        except subprocess.TimeoutExpired:
            die("setup-only host did not terminate after administrator completion")
        if host.returncode != 0:
            die("setup-only host exited unsuccessfully after administrator completion")
        return 0
    finally:
        bootstrap = ""
        if host.poll() is None:
            host.terminate()
            try:
                host.wait(timeout=5)
            except subprocess.TimeoutExpired:
                host.kill()
                host.wait(timeout=5)


if __name__ == "__main__":
    raise SystemExit(main())
