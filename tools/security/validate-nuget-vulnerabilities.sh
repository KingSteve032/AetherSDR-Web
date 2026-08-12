#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
report="$(mktemp "${TMPDIR:-/tmp}/aethersdr-nuget-vulnerabilities.XXXXXX.json")"
cleanup() {
  rm -f -- "${report}"
}
trap cleanup EXIT INT TERM

dotnet list "${repo_root}/AetherSDR-Web.slnx" package \
  --vulnerable \
  --include-transitive \
  --format json >"${report}"

python3 - "${report}" <<'PY'
import json
import sys

path = sys.argv[1]
with open(path, "r", encoding="utf-8") as stream:
    report = json.load(stream)

findings = []
for project in report.get("projects", []):
    project_path = project.get("path", "unknown-project")
    for framework in project.get("frameworks", []):
        framework_name = framework.get("framework", "unknown-framework")
        for bucket in ("topLevelPackages", "transitivePackages"):
            for package in framework.get(bucket, []):
                vulnerabilities = package.get("vulnerabilities") or []
                for vulnerability in vulnerabilities:
                    findings.append(
                        (
                            project_path,
                            framework_name,
                            package.get("id", "unknown-package"),
                            package.get("resolvedVersion", "unknown-version"),
                            vulnerability.get("severity", "unknown-severity"),
                            vulnerability.get("advisoryurl", "unknown-advisory"),
                        )
                    )

if findings:
    print("NuGet vulnerability gate failed:", file=sys.stderr)
    for finding in findings:
        print("  " + " | ".join(finding), file=sys.stderr)
    raise SystemExit(1)

print(
    f"NuGet vulnerability gate passed: {len(report.get('projects', []))} projects, "
    "no vulnerable direct or transitive packages reported."
)
PY
