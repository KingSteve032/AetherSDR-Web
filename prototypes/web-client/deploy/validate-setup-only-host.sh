#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: bash $0 <exact-https-origin>" >&2
  echo "Performs a read-only M8A setup-only host smoke test. The origin must have a trusted TLS certificate." >&2
}

if [[ $# -ne 1 ]]; then
  usage
  exit 64
fi

origin="${1%/}"
if [[ ! "${origin}" =~ ^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?$ ]]; then
  echo "The setup origin must be one exact HTTPS origin with no path, query, fragment, userinfo, or whitespace." >&2
  exit 64
fi

work="$(mktemp -d)"
trap 'rm -rf "${work}"' EXIT
cookie_jar="${work}/cookies.txt"

curl_common=(
  --silent
  --show-error
  --fail-with-body
  --proto '=https'
  --tlsv1.2
  --connect-timeout 10
  --max-time 30
)

header_value() {
  local file="$1"
  local name="$2"
  awk -v expected="${name}" '
    BEGIN { IGNORECASE=1 }
    index($0, expected ":") == 1 {
      sub(/^[^:]+:[[:space:]]*/, "")
      sub(/\r$/, "")
      print
      exit
    }
  ' "${file}"
}

require_header() {
  local file="$1"
  local name="$2"
  local expected="$3"
  local actual
  actual="$(header_value "${file}" "${name}")"
  if [[ "${actual}" != "${expected}" ]]; then
    echo "Expected ${name}: ${expected}; received '${actual}'." >&2
    exit 65
  fi
}

fetch() {
  local path="$1"
  local site="$2"
  local mode="$3"
  local accept="$4"
  local prefix="$5"
  shift 5
  "${curl_common[@]}" \
    --dump-header "${work}/${prefix}.headers" \
    --output "${work}/${prefix}.body" \
    --cookie "${cookie_jar}" \
    --cookie-jar "${cookie_jar}" \
    --header "Accept: ${accept}" \
    --header "Sec-Fetch-Site: ${site}" \
    --header "Sec-Fetch-Mode: ${mode}" \
    "$@" \
    "${origin}${path}"
}

fetch "/setup/center" "none" "navigate" "text/html" "page"
require_header "${work}/page.headers" "Cache-Control" "no-store, max-age=0"
require_header "${work}/page.headers" "X-Content-Type-Options" "nosniff"
if [[ -z "$(header_value "${work}/page.headers" "Content-Security-Policy")" ]]; then
  echo "The setup document did not publish a Content-Security-Policy header." >&2
  exit 65
fi
if ! grep -Fq 'id="setup-center"' "${work}/page.body" ||
   grep -Fq '{{' "${work}/page.body"; then
  echo "The setup document is missing or contains unresolved template material." >&2
  exit 65
fi
if ! grep -Fq '__Host-AetherSdrSetupCsrf=' "${work}/page.headers"; then
  echo "The setup document did not issue the strict CSRF cookie." >&2
  exit 65
fi
if grep -Eiq 'bootstrapTokenHash|sessionToken|csrfToken' "${work}/page.body"; then
  echo "The setup document exposed forbidden token material." >&2
  exit 65
fi

fetch "/setup/assets/setup.css" "same-origin" "no-cors" "text/css" "style" \
  --header "Origin: ${origin}"
require_header "${work}/style.headers" "Cache-Control" "no-store, max-age=0"
if ! grep -Fq '.setup-shell' "${work}/style.body"; then
  echo "The setup stylesheet was not served correctly." >&2
  exit 65
fi

fetch "/setup/assets/setup.js" "same-origin" "no-cors" "text/javascript" "script" \
  --header "Origin: ${origin}"
require_header "${work}/script.headers" "Cache-Control" "no-store, max-age=0"
if ! grep -Fq 'setupSteps' "${work}/script.body"; then
  echo "The setup script was not served correctly." >&2
  exit 65
fi
if grep -Eq 'localStorage|sessionStorage|indexedDB' "${work}/script.body"; then
  echo "The setup script uses forbidden browser persistence." >&2
  exit 65
fi

fetch "/setup" "none" "navigate" "application/json" "status"
require_header "${work}/status.headers" "Cache-Control" "no-store, max-age=0"
if ! grep -Fq '"status"' "${work}/status.body" ||
   ! grep -Fq '"securityContract"' "${work}/status.body"; then
  echo "The redacted setup status contract was not returned." >&2
  exit 65
fi
if grep -Eiq 'bootstrapTokenHash|sessionToken|csrfToken' "${work}/status.body"; then
  echo "The setup status response exposed forbidden token material." >&2
  exit 65
fi

http_origin="http://${origin#https://}"
http_code="$(curl \
  --silent \
  --show-error \
  --output "${work}/cleartext.body" \
  --write-out '%{http_code}' \
  --connect-timeout 5 \
  --max-time 10 \
  --header 'Sec-Fetch-Site: none' \
  --header 'Sec-Fetch-Mode: navigate' \
  "${http_origin}/setup/center" 2>/dev/null || true)"
if [[ "${http_code}" =~ ^2 ]]; then
  echo "The setup document was reachable successfully over cleartext HTTP." >&2
  exit 65
fi

echo "M8A setup-only host read-only acceptance passed for ${origin}."
