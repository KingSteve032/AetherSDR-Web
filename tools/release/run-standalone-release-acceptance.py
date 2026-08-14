#!/usr/bin/env python3
"""M8H native Ubuntu packaged-release acceptance.

Runs only packaged AetherSDR product binaries for setup, installation, update,
rollback, backup/restore, health, and uninstall. Repository code is used only as
this orchestration harness and the setup HTTPS client helper.
"""

from __future__ import annotations

import grp
import hashlib
import json
import os
import pty
import re
import select
import secrets
import shutil
import ssl
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.request
from pathlib import Path

PREVIOUS_VERSION = "8.8.0-acceptance.1"
TARGET_VERSION = "8.8.0-acceptance.2"
FAILURE_VERSION = "8.8.0-acceptance.3"
PREVIOUS_ID = f"aethersdr-{PREVIOUS_VERSION}"
TARGET_ID = f"aethersdr-{TARGET_VERSION}"
FAILURE_ID = f"aethersdr-{FAILURE_VERSION}"
KEY_ID = "m8h-ephemeral"
PUBLIC_URL = "https://aethersdr.test"
BACKUP_PASSPHRASE = "M8H!" + secrets.token_urlsafe(32)

PRODUCT_UNITS = [
    "aethersdr-web.service",
    "aethersdr-release-updater.service",
    "aetherremote-broker.service",
    "aetherremote-station-engine.service",
    "aetherremote-agent.service",
    "aetherremote-release-updater.service",
]


def die(message: str) -> "NoReturn":
    raise SystemExit(f"M8H standalone acceptance failed: {message}")


def run(
    argv: list[str],
    *,
    env: dict[str, str] | None = None,
    check: bool = True,
    capture: bool = True,
    timeout: int = 120,
) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        argv,
        env=env,
        check=False,
        text=True,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.STDOUT if capture else None,
        timeout=timeout,
    )
    if check and result.returncode != 0:
        output = (result.stdout or "")[-4000:]
        die(f"command failed ({result.returncode}): {' '.join(argv)}\n{output}")
    return result


def safe_extract(archive: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=False)
    root = destination.resolve()
    with tarfile.open(archive, "r:gz") as tar:
        members = tar.getmembers()
        if len(members) > 4096:
            die("package archive contains too many entries")
        total = 0
        for member in members:
            if member.issym() or member.islnk() or member.isdev():
                die("package archive contains an unsupported link/device")
            total += max(0, member.size)
            if total > 512 * 1024 * 1024:
                die("package archive exceeds the acceptance extraction bound")
            candidate = (root / member.name).resolve()
            if candidate != root and root not in candidate.parents:
                die("package archive path escaped its extraction root")
        tar.extractall(root, members=members, filter="data")


def parse_json_lines(output: str) -> list[dict]:
    reports: list[dict] = []
    for line in output.replace("\r", "").splitlines():
        line = line.strip()
        if not line.startswith("{") or not line.endswith("}"):
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict):
            reports.append(value)
    return reports


def redact_interactive_diagnostics(
    text: str,
    prompt_responses: list[tuple[str, str]],
) -> str:
    redacted = text
    for _, response in prompt_responses:
        if response:
            redacted = redacted.replace(response, "<redacted-response>")
    redacted = re.sub(
        r'(?i)("?(?:password|secret|token|credential|enrollmentCode)"?\s*[:=]\s*)"?[^\s"\r\n]+"?',
        r'\1<redacted>',
        redacted,
    )
    return redacted[-4000:]


def release_updater_failure_diagnostic() -> str:
    sections: list[str] = []
    for argv in (
        ["/usr/bin/systemctl", "status", "aethersdr-release-updater.service", "--no-pager", "--full"],
        ["/usr/bin/journalctl", "-u", "aethersdr-release-updater.service", "-n", "80", "--no-pager", "--output=short-precise"],
    ):
        result = run(argv, check=False, timeout=30)
        text = (result.stdout or "").strip()
        if text:
            sections.append(text)
    socket = Path("/var/lib/aethersdr/release-update-supervisor/control.sock")
    if socket.exists():
        stat = socket.stat()
        sections.append(
            "updater-socket "
            f"mode={oct(stat.st_mode & 0o777)} uid={stat.st_uid} gid={stat.st_gid}"
        )
    else:
        sections.append("updater-socket missing")
    return "\n".join(sections)[-12000:]


def release_activation_failure_diagnostic() -> str:
    sections: list[str] = []
    for unit in (
        "aethersdr-web.service",
        "aethersdr-release-updater.service",
        "aetherremote-broker.service",
        "aetherremote-station-engine.service",
    ):
        for argv in (
            ["/usr/bin/systemctl", "status", unit, "--no-pager", "--full", "--lines=20"],
            ["/usr/bin/journalctl", "-u", unit, "-n", "40", "--no-pager", "--output=short-precise"],
        ):
            result = run(argv, check=False, timeout=30)
            text = (result.stdout or "").strip()
            if text:
                sections.append(f"{unit}:\n{text}")
    return "\n".join(sections)[-16000:]


def pty_capture_sequence(
    argv: list[str],
    *,
    env: dict[str, str],
    prompt_responses: list[tuple[str, str]],
    allowed_exit_codes: set[int] = {0},
    timeout: int = 180,
) -> tuple[int, str]:
    pid, fd = pty.fork()
    if pid == 0:
        os.execve(argv[0], argv, env)
    output = bytearray()
    pending = list(prompt_responses)
    deadline = time.monotonic() + timeout
    status: int | None = None
    while True:
        if time.monotonic() > deadline:
            os.kill(pid, 9)
            die(f"interactive command timed out: {' '.join(argv)}")
        ready, _, _ = select.select([fd], [], [], 0.25)
        if ready:
            try:
                chunk = os.read(fd, 4096)
            except OSError:
                chunk = b""
            if chunk:
                output.extend(chunk)
                text = output.decode("utf-8", "replace")
                if pending and pending[0][0] in text:
                    _, response = pending.pop(0)
                    os.write(fd, (response + "\n").encode())
            else:
                _, status = os.waitpid(pid, 0)
                break
        else:
            ended, wait_status = os.waitpid(pid, os.WNOHANG)
            if ended == pid:
                status = wait_status
                break
    assert status is not None
    exit_code = os.waitstatus_to_exitcode(status)
    text = output.decode("utf-8", "replace")
    if pending:
        diagnostic = text
        if "The dedicated release updater is unavailable." in text:
            diagnostic += "\n--- updater service diagnostic ---\n" + release_updater_failure_diagnostic()
        diagnostic = redact_interactive_diagnostics(diagnostic, prompt_responses)
        die(
            "interactive command did not request expected prompt: "
            f"{pending[0][0]}\n{diagnostic}"
        )
    if exit_code not in allowed_exit_codes:
        diagnostic = redact_interactive_diagnostics(text, prompt_responses)
        die(
            f"interactive command exited {exit_code}: {' '.join(argv)}\n{diagnostic}"
        )
    return exit_code, text


def tree_digest(path: Path) -> str:
    if not path.exists():
        return "missing"
    digest = hashlib.sha256()
    for entry in sorted(path.rglob("*"), key=lambda item: str(item.relative_to(path))):
        relative = entry.relative_to(path).as_posix().encode()
        if entry.is_symlink():
            digest.update(b"L\0" + relative + b"\0" + os.readlink(entry).encode() + b"\0")
        elif entry.is_dir():
            digest.update(b"D\0" + relative + b"\0")
        elif entry.is_file():
            digest.update(b"F\0" + relative + b"\0")
            with entry.open("rb") as stream:
                while chunk := stream.read(1024 * 1024):
                    digest.update(chunk)
    return digest.hexdigest()


def authority_snapshot() -> dict[str, str]:
    paths = {
        "identity": Path("/var/lib/aethersdr/identity"),
        "dataProtection": Path("/var/lib/aethersdr/secrets/data-protection"),
        "gatewayEnvironment": Path("/etc/aethersdr/environment"),
        "setup": Path("/var/lib/aethersdr/setup"),
    }
    values: dict[str, str] = {}
    for name, path in paths.items():
        if not path.exists():
            continue
        values[name] = (
            tree_digest(path)
            if path.is_dir()
            else hashlib.sha256(path.read_bytes()).hexdigest()
        )
    return values


def assert_authority(expected: dict[str, str], stage: str) -> None:
    actual = authority_snapshot()
    for name, value in expected.items():
        if actual.get(name) != value:
            die(f"durable authority '{name}' changed during {stage}")


def decode_environment_file_value(value: str) -> str:
    if not value.startswith('"'):
        if '"' in value or "\\" in value or any(ord(ch) < 0x20 for ch in value):
            die("installed environment file contains an unsafe unquoted value")
        return value
    if len(value) < 2 or not value.endswith('"'):
        die("installed environment file contains an unterminated quoted value")
    decoded: list[str] = []
    index = 1
    end = len(value) - 1
    while index < end:
        character = value[index]
        if ord(character) < 0x20:
            die("installed environment file contains a control character")
        if character != "\\":
            decoded.append(character)
            index += 1
            continue
        index += 1
        if index >= end or value[index] not in {'"', "\\"}:
            die("installed environment file contains an unsupported escape")
        decoded.append(value[index])
        index += 1
    return "".join(decoded)


def load_environment_file(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if "=" not in line:
            die("installed environment file contains a malformed line")
        key, value = line.split("=", 1)
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key):
            die("installed environment file contains an invalid key")
        values[key] = decode_environment_file_value(value)
    return values


def update_environment(
    base: dict[str, str],
    public_key: Path,
    expected_station_id: str = "",
) -> dict[str, str]:
    if expected_station_id and not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,63}", expected_station_id):
        die("release update station identity is not canonical")
    env = base.copy()
    env.update(load_environment_file(Path("/etc/aethersdr/environment")))
    env.update(
        {
            "ASPNETCORE_ENVIRONMENT": "Production",
            "DOTNET_ENVIRONMENT": "Production",
            "ReleaseManifestTrust__VerificationEnabled": "true",
            "ReleaseManifestTrust__Keys__0__KeyId": KEY_ID,
            "ReleaseManifestTrust__Keys__0__Algorithm": "EcdsaP256Sha256",
            "ReleaseManifestTrust__Keys__0__PublicKeyPath": str(public_key),
            "ReleaseUpdateTransaction__ExecutionEnabled": "true",
            "ReleaseUpdateTransaction__LeaseDrainSeconds": "1",
            "ReleaseActivationServiceControl__ExecutionEnabled": "true",
            "ReleaseActivationCurrentPointerSwitch__ExecutionEnabled": "true",
            "ReleaseActivationHealthVerification__ExecutionEnabled": "true",
            "ReleaseActivationRollback__ExecutionEnabled": "true",
            "ReleaseActivationOperatorApproval__AuthorityEnabled": "true",
        }
    )
    if expected_station_id:
        env["ReleaseActivationHealthVerification__ExpectedStationId"] = (
            expected_station_id
        )
        env["ReleaseActivationRollback__ExpectedStationId"] = expected_station_id
    return env


def install_runtime_release_trust(public_key_source: Path) -> Path:
    directory = Path("/etc/aethersdr/release-trust")
    directory.mkdir(parents=True, exist_ok=True)
    run(["/usr/bin/chown", "root:aethersdr", str(directory)])
    directory.chmod(0o750)
    target = directory / "m8h-release.pem"
    shutil.copyfile(public_key_source, target)
    run(["/usr/bin/chown", "root:aethersdr", str(target)])
    target.chmod(0o440)
    return target


def stage_runtime_bundle(source: Path, identity: str) -> Path:
    root = Path("/var/lib/aethersdr/m8h-release-inputs")
    root.mkdir(parents=True, exist_ok=True)
    target = root / identity
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(source, target, symlinks=False)
    for entry in target.rglob("*"):
        if entry.is_dir():
            entry.chmod(0o555)
        elif entry.is_file():
            entry.chmod(0o444)
    target.chmod(0o555)
    return target


def write_update_dropin(public_key: Path, expected_station_id: str = "") -> None:
    if expected_station_id and not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,63}", expected_station_id):
        die("release updater station identity is not canonical")
    dropin = Path("/etc/systemd/system/aethersdr-release-updater.service.d")
    dropin.mkdir(parents=True, exist_ok=True)
    dropin.chmod(0o755)
    lines = [
        "[Service]",
        "Environment=ReleaseManifestTrust__VerificationEnabled=true",
        f"Environment=ReleaseManifestTrust__Keys__0__KeyId={KEY_ID}",
        "Environment=ReleaseManifestTrust__Keys__0__Algorithm=EcdsaP256Sha256",
        f"Environment=ReleaseManifestTrust__Keys__0__PublicKeyPath={public_key}",
        "Environment=ReleaseUpdateTransaction__ExecutionEnabled=true",
        "Environment=ReleaseUpdateTransaction__LeaseDrainSeconds=1",
        "Environment=ReleaseActivationServiceControl__ExecutionEnabled=true",
        "Environment=ReleaseActivationCurrentPointerSwitch__ExecutionEnabled=true",
        "Environment=ReleaseActivationHealthVerification__ExecutionEnabled=true",
        "Environment=ReleaseActivationRollback__ExecutionEnabled=true",
        "Environment=ReleaseActivationOperatorApproval__AuthorityEnabled=true",
    ]
    if expected_station_id:
        lines.append(
            "Environment=ReleaseActivationHealthVerification__ExpectedStationId=" +
            expected_station_id
        )
        lines.append(
            "Environment=ReleaseActivationRollback__ExpectedStationId=" +
            expected_station_id
        )
    lines.append("")
    content = "\n".join(lines)
    target = dropin / "m8h-acceptance.conf"
    target.write_text(content, encoding="utf-8")
    target.chmod(0o644)
    run(["/usr/bin/systemctl", "daemon-reload"])
    run(["/usr/bin/systemctl", "restart", "aethersdr-release-updater.service"])


def wait_release_updater_ready(binary: Path, env: dict[str, str]) -> None:
    deadline = time.monotonic() + 15
    last_output = ""
    while time.monotonic() < deadline:
        status = run([str(binary), "--release-transaction-status"], env=env, check=False, timeout=5)
        last_output = status.stdout or ""
        reports = parse_json_lines(last_output)
        if status.returncode == 0 and reports and reports[-1].get("failureCode") != "executionDisabled":
            return
        time.sleep(0.25)
    die("release updater did not become protocol-ready after restart\n" + release_updater_diagnostic())


def current_identity() -> str:
    current = Path("/opt/aethersdr/current")
    if not current.is_symlink():
        die("current release pointer is not a symbolic link")
    resolved = current.resolve()
    releases = Path("/opt/aethersdr/releases").resolve()
    if resolved.parent != releases:
        die("current release pointer escaped the immutable release root")
    return resolved.name


def wait_health() -> None:
    context = ssl.create_default_context()
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(
                PUBLIC_URL + "/healthz", context=context, timeout=3
            ) as response:
                payload = json.loads(response.read())
                if response.status == 200 and payload.get("status") in {"ok", "ready"}:
                    return
        except Exception:
            pass
        time.sleep(0.5)
    die("installed gateway did not become healthy through its managed TLS proxy")


def stop_units() -> None:
    for unit in PRODUCT_UNITS:
        run(["/usr/bin/systemctl", "stop", unit], check=False)
    run(["/usr/bin/systemctl", "stop", "caddy.service"], check=False)


def assert_release_dirs(*identities: str) -> None:
    for identity in identities:
        path = Path("/opt/aethersdr/releases") / identity
        if not path.is_dir() or path.is_symlink():
            die(f"immutable release is not retained: {identity}")


def installer_args(architecture: str, release_id: str) -> list[str]:
    return [
        "--installation-architecture",
        architecture,
        "--installation-reverse-proxy",
        "lan-internal-certificate",
        "--installation-release",
        release_id,
        "--installation-firewall",
        "guidance",
        "--installation-authentication",
        "local",
    ]


def run_install(binary: Path, bundle: Path, architecture: str, env: dict[str, str]) -> None:
    plan = run(
        [str(binary), "--installation-installer-plan", *installer_args(architecture, PREVIOUS_ID)],
        env=env,
    )
    try:
        plan_payload = json.loads(plan.stdout or "")
        plan_id = plan_payload["PlanId"]
    except (json.JSONDecodeError, KeyError) as exception:
        die(f"installer plan did not return an exact plan ID: {exception}")
    apply = run(
        [
            str(binary),
            "--installation-installer-apply",
            *installer_args(architecture, PREVIOUS_ID),
            "--confirm-installation-plan",
            plan_id,
            "--installation-bundle",
            str(bundle),
            "--installation-configuration-schema",
            "1",
            "--installation-protocol-version",
            "2",
        ],
        env=env,
        timeout=300,
    )
    try:
        result = json.loads(apply.stdout or "")
    except json.JSONDecodeError as exception:
        die(f"installer apply did not return JSON: {exception}")
    if str(result.get("Outcome", "")).lower() not in {"applied", "converged"}:
        die(f"installer apply was not accepted: {result}")


def update_args(bundle: Path, installed_id: str, installed_version: str) -> list[str]:
    return [
        "--install-offline-release",
        str(bundle),
        "--release-install-installed-identity",
        installed_id,
        "--release-check-installed-version",
        installed_version,
        "--release-check-configuration-schema-version",
        "1",
        "--release-check-protocol-version",
        "2",
        "--approve-release-transaction",
    ]


def activate(
    binary: Path,
    bundle: Path,
    installed_id: str,
    installed_version: str,
    target_id: str,
    env: dict[str, str],
    *,
    expect_failure_rollback: bool = False,
) -> dict:
    prompt_responses = [(
        f"Type {target_id} to activate this exact release: ",
        target_id,
    )]
    exit_code, output = pty_capture_sequence(
        [str(binary), *update_args(bundle, installed_id, installed_version)],
        env=env,
        prompt_responses=prompt_responses,
        allowed_exit_codes={0, 2},
        timeout=300,
    )
    reports = parse_json_lines(output)
    if len(reports) < 2:
        die(f"release transaction did not emit prepare/final reports: {output[-4000:]}")
    final = reports[-1]
    if expect_failure_rollback:
        if exit_code != 2 or final.get("failureCode") not in {
            "postSwitchServiceControlFailed",
            "healthVerificationFailed",
            "finalReadinessFailed",
        }:
            die(f"failed release did not exercise automatic rollback: {final}")
    elif exit_code != 0 or not final.get("succeeded") or not final.get("activationCompleted"):
        diagnostic = redact_interactive_diagnostics(
            release_activation_failure_diagnostic(),
            prompt_responses,
        )
        die(
            f"release activation did not complete: {final}\n"
            f"--- installed service diagnostic ---\n{diagnostic}"
        )
    return {"prepared": reports[0], "final": final}


def manual_rollback(binary: Path, transaction_id: str, env: dict[str, str]) -> dict:
    _, output = pty_capture_sequence(
        [
            str(binary),
            "--rollback-release-transaction",
            transaction_id,
            "--approve-release-transaction",
        ],
        env=env,
        prompt_responses=[(
            f"Type {transaction_id} to roll back this exact transaction: ",
            transaction_id,
        )],
        timeout=300,
    )
    reports = parse_json_lines(output)
    if not reports or not reports[-1].get("succeeded") or not reports[-1].get("rollbackPerformed"):
        die(f"manual rollback did not complete: {output[-4000:]}")
    return reports[-1]


def create_backup(binary: Path, env: dict[str, str]) -> Path:
    _, output = pty_capture_sequence(
        [str(binary), "--create-encrypted-backup"],
        env=env,
        prompt_responses=[
            ("Backup passphrase: ", BACKUP_PASSPHRASE),
            ("Confirm backup passphrase: ", BACKUP_PASSPHRASE),
        ],
        timeout=180,
    )
    match = re.search(r'"backupPath"\s*:\s*"([^"]+\.aebak)"', output)
    if match is None:
        die(f"backup command did not return a backup path: {output[-4000:]}")
    path = Path(match.group(1))
    if not path.is_file():
        die("encrypted backup artifact is unavailable")
    return path


def restore_backup(binary: Path, backup: Path, env: dict[str, str]) -> None:
    _, output = pty_capture_sequence(
        [str(binary), "--restore-encrypted-backup", "--backup-file", str(backup)],
        env=env,
        prompt_responses=[("Backup passphrase: ", BACKUP_PASSPHRASE)],
        timeout=240,
    )
    if '"succeeded": true' not in output and '"succeeded":true' not in output:
        die(f"backup restore did not report success: {output[-4000:]}")


def main() -> int:
    if os.geteuid() != 0:
        die("acceptance must run as root on a disposable Ubuntu runner")
    if len(sys.argv) != 4:
        die("usage: run-standalone-release-acceptance.py <artifact-root> <linux-x64|linux-arm64> <setup-helper>")
    artifact_root = Path(sys.argv[1]).resolve()
    architecture = sys.argv[2]
    setup_helper = Path(sys.argv[3]).resolve()
    if architecture not in {"linux-x64", "linux-arm64"}:
        die("unsupported acceptance architecture")
    public_key_source = artifact_root / "release-verification-key.pem"
    if not public_key_source.is_file() or not setup_helper.is_file():
        die("acceptance artifact or setup helper is missing")

    previous_bundle = artifact_root / architecture / PREVIOUS_ID
    target_bundle = artifact_root / architecture / TARGET_ID
    failure_bundle = artifact_root / architecture / FAILURE_ID
    for bundle in (previous_bundle, target_bundle, failure_bundle):
        if not (bundle / "release-manifest.json").is_file():
            die(f"canonical signed bundle is missing: {bundle}")
        for path in bundle.rglob("*"):
            if path.is_dir():
                path.chmod(0o555)
            elif path.is_file():
                path.chmod(0o444)
        bundle.chmod(0o555)

    hosts = Path("/etc/hosts")
    hosts_text = hosts.read_text(encoding="utf-8")
    if "aethersdr.test" not in hosts_text:
        hosts.write_text(hosts_text + "\n127.0.0.1 aethersdr.test\n", encoding="utf-8")

    with tempfile.TemporaryDirectory(prefix="aethersdr-m8h-") as temporary_name:
        temporary = Path(temporary_name)
        gateway_package = previous_bundle / "packages" / f"aethersdr-gateway-{architecture}.tar.gz"
        packaged_setup = temporary / "packaged-setup"
        safe_extract(gateway_package, packaged_setup)
        gateway = packaged_setup / "AetherSDR.Web"
        if not gateway.is_file():
            die("packaged gateway executable is missing")

        key = temporary / "setup.key"
        cert = temporary / "setup.crt"
        pfx = temporary / "setup.pfx"
        pfx_password = secrets.token_urlsafe(32)
        run([
            "/usr/bin/openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
            "-keyout", str(key), "-out", str(cert), "-days", "1", "-subj", "/CN=127.0.0.1",
        ])
        run([
            "/usr/bin/openssl", "pkcs12", "-export", "-out", str(pfx),
            "-inkey", str(key), "-in", str(cert), "-passout", f"pass:{pfx_password}",
        ])
        setup_result = run(
            [sys.executable, str(setup_helper), str(gateway), str(pfx), pfx_password, PUBLIC_URL],
            timeout=120,
        )
        try:
            setup_metadata = json.loads((setup_result.stdout or "").strip().splitlines()[-1])
        except (json.JSONDecodeError, IndexError):
            die("packaged setup acceptance did not return its redacted metadata")
        if setup_metadata.get("transmitSupportInstalled") is not True or \
            setup_metadata.get("transmitEnabled") is not False:
            die("packaged setup did not retain dormant TX support with transmit disabled")

        trust_dir = Path("/root/.aethersdr-m8h-trust")
        trust_dir.mkdir(mode=0o700, exist_ok=False)
        installer_public_key = trust_dir / "release.pem"
        shutil.copyfile(public_key_source, installer_public_key)
        installer_public_key.chmod(0o400)

        installer_env = os.environ.copy()
        installer_env.update(
            {
                "ASPNETCORE_ENVIRONMENT": "Production",
                "DOTNET_ENVIRONMENT": "Production",
                "InstallationInstaller__Enabled": "true",
                "InstallationInstallerUbuntu__MutationEnabled": "true",
                "ReleaseManifestTrust__VerificationEnabled": "true",
                "ReleaseManifestTrust__Keys__0__KeyId": KEY_ID,
                "ReleaseManifestTrust__Keys__0__Algorithm": "EcdsaP256Sha256",
                "ReleaseManifestTrust__Keys__0__PublicKeyPath": str(installer_public_key),
            }
        )
        run_install(gateway, previous_bundle, architecture, installer_env)
        if current_identity() != PREVIOUS_ID:
            die("initial installer did not activate the previous acceptance release")
        assert_release_dirs(PREVIOUS_ID)
        wait_health()
        for line in Path("/etc/aethersdr/environment").read_text(encoding="utf-8").splitlines():
            if line in {
                "Radio__AllowTransmit=true",
                "Radio__BrowserTxLeaseEnabled=true",
                "StationTxProductionActivation__Enabled=true",
            }:
                die("standalone installer enabled a TX authority")

        public_key = install_runtime_release_trust(public_key_source)
        target_bundle = stage_runtime_bundle(target_bundle, TARGET_ID)
        failure_bundle = stage_runtime_bundle(failure_bundle, FAILURE_ID)
        write_update_dropin(public_key)
        update_env = update_environment(os.environ.copy(), public_key)
        installed_gateway = Path("/opt/aethersdr/current/gateway-web/AetherSDR.Web")
        if not installed_gateway.is_file():
            die("installed packaged gateway executable is unavailable")
        socket = Path("/var/lib/aethersdr/release-update-supervisor/control.sock")
        wait_release_updater_ready(installed_gateway, update_env)
        if not socket.exists() or (socket.stat().st_mode & 0o777) != 0o660:
            die("release updater socket is not service-group private 0660")
        if socket.stat().st_gid != grp.getgrnam("aethersdr").gr_gid:
            die("release updater socket is not owned by the aethersdr service group")

        authority = authority_snapshot()
        if "identity" not in authority or "gatewayEnvironment" not in authority:
            die("protected local identity/configuration authority was not installed")

        target = activate(
            installed_gateway,
            target_bundle,
            PREVIOUS_ID,
            PREVIOUS_VERSION,
            TARGET_ID,
            update_env,
        )
        transaction_id = str(target["final"].get("transactionId", ""))
        if len(transaction_id) != 32 or current_identity() != TARGET_ID:
            die("successful signed update did not activate the exact target")
        assert_release_dirs(PREVIOUS_ID, TARGET_ID)
        assert_authority(authority, "successful update")
        wait_health()
        wait_release_updater_ready(
            Path("/opt/aethersdr/current/gateway-web/AetherSDR.Web"),
            update_env,
        )

        manual_rollback(
            Path("/opt/aethersdr/current/gateway-web/AetherSDR.Web"),
            transaction_id,
            update_env,
        )
        if current_identity() != PREVIOUS_ID:
            die("manual rollback did not restore the previous release")
        assert_release_dirs(PREVIOUS_ID, TARGET_ID)
        assert_authority(authority, "manual rollback")
        wait_health()
        wait_release_updater_ready(
            Path("/opt/aethersdr/current/gateway-web/AetherSDR.Web"),
            update_env,
        )

        failure = activate(
            Path("/opt/aethersdr/current/gateway-web/AetherSDR.Web"),
            failure_bundle,
            PREVIOUS_ID,
            PREVIOUS_VERSION,
            FAILURE_ID,
            update_env,
            expect_failure_rollback=True,
        )
        if current_identity() != PREVIOUS_ID:
            die(f"failed signed target was not automatically rolled back: {failure['final']}")
        assert_release_dirs(PREVIOUS_ID, TARGET_ID, FAILURE_ID)
        assert_authority(authority, "automatic rollback")
        wait_health()

        stop_units()
        backup_binary = Path(f"/opt/aethersdr/releases/{PREVIOUS_ID}/gateway-web/AetherSDR.Web")
        backup_env = update_environment(os.environ.copy(), public_key)
        backup_path = create_backup(backup_binary, backup_env)
        backup_digest = hashlib.sha256(backup_path.read_bytes()).hexdigest()

        for root in (Path("/etc/aethersdr"), Path("/var/lib/aethersdr")):
            if root.exists():
                shutil.rmtree(root)
        current = Path("/opt/aethersdr/current")
        if current.is_symlink():
            current.unlink()
        current.symlink_to(Path("releases") / TARGET_ID)
        if Path("/etc/caddy/Caddyfile").exists():
            Path("/etc/caddy/Caddyfile").unlink()

        restore_backup(backup_binary, backup_path, backup_env)
        if hashlib.sha256(backup_path.read_bytes()).hexdigest() != backup_digest:
            die("encrypted backup artifact changed during restore")
        if current_identity() != PREVIOUS_ID:
            die("backup restore did not restore the recorded active release")
        assert_authority(authority, "encrypted backup restore")
        run(["/usr/bin/systemctl", "daemon-reload"])
        for unit in [
            "aethersdr-release-updater.service",
            "aetherremote-broker.service",
            "aetherremote-station-engine.service",
            "aethersdr-web.service",
            "caddy.service",
        ]:
            run(["/usr/bin/systemctl", "start", unit], check=False)
        wait_health()

        stop_units()
        dropin = Path("/etc/systemd/system/aethersdr-release-updater.service.d/m8h-acceptance.conf")
        if dropin.exists():
            dropin.unlink()
        uninstall = Path("/opt/aethersdr/current/gateway-web/tools/uninstall-aethersdr.sh")
        if not uninstall.is_file():
            die("packaged supported uninstall tool is missing")
        uninstall_result = run([str(uninstall)], timeout=120)
        if '"outcome":"uninstalled"' not in (uninstall_result.stdout or ""):
            die("supported uninstall did not report completion")
        if Path("/opt/aethersdr/current").exists() or Path("/opt/aethersdr/current").is_symlink():
            die("uninstall retained the active current integration link")
        if Path("/etc/systemd/system/aethersdr-web.service").exists():
            die("uninstall retained the web systemd integration")
        assert_release_dirs(PREVIOUS_ID, TARGET_ID, FAILURE_ID)
        assert_authority(authority, "uninstall")
        if not backup_path.is_file() or hashlib.sha256(backup_path.read_bytes()).hexdigest() != backup_digest:
            die("uninstall did not preserve the encrypted backup")
        run(["/usr/bin/id", "aethersdr"])
        run(["/usr/bin/id", "aetherremote"])

        print(json.dumps({
            "schemaVersion": 1,
            "architecture": architecture,
            "packagedSetup": True,
            "localAdministratorProtected": True,
            "transmitSupportInstalled": True,
            "initialInstall": PREVIOUS_ID,
            "successfulUpdate": TARGET_ID,
            "manualRollback": PREVIOUS_ID,
            "failedTargetAutomaticRollback": PREVIOUS_ID,
            "encryptedBackupRestore": True,
            "supportedUninstall": True,
            "durableAuthorityPreserved": True,
            "immutableReleasesPreserved": [PREVIOUS_ID, TARGET_ID, FAILURE_ID],
            "liveRfPerformed": False,
        }, separators=(",", ":")))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
