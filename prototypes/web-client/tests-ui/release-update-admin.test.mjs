import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const html = readFileSync(join(here, "../wwwroot/admin.html"), "utf8");
const page = readFileSync(join(here, "../wwwroot/admin-page.js"), "utf8");
const adapter = readFileSync(
  join(here, "../Releases/ReleaseUpdateHttpAdapter.cs"),
  "utf8");

test("Admin release workflow selects only canonical release identity, never a path", () => {
  assert.match(html, /id="admin-release-identity"/);
  assert.match(html, /id="admin-release-activate"/);
  assert.match(html, /id="admin-release-rollback"/);
  assert.doesNotMatch(html, /admin-release-(bundle-)?path/);
  assert.doesNotMatch(page, /bundleDirectory\s*:/);
  assert.match(adapter, /DeriveBundleDirectory/);
  assert.match(adapter, /ReleaseDownloadDirectory/);
});

test("Admin release mutations require antiforgery and server authentication evidence", () => {
  assert.match(page, /\/api\/admin\/releases\/antiforgery/);
  assert.match(page, /\[antiforgery\.headerName\]: antiforgery\.requestToken/);
  assert.match(adapter, /ValidateRequestAsync/);
  assert.match(adapter, /ReleaseUpdateOperatorAuthenticationEvidenceFactory/);
  assert.match(adapter, /RequireAuthorization\(AetherPolicies\.Admin\)/);
  assert.doesNotMatch(adapter, /AdministratorAuthorized\s*=\s*request/);
});

test("Admin release callers expose only prepare, exact activate, exact rollback, and status", () => {
  assert.match(adapter, /"\/prepare"/);
  assert.match(adapter, /"\/\{transactionId\}\/activate"/);
  assert.match(adapter, /"\/\{transactionId\}\/rollback"/);
  assert.match(adapter, /"\/transaction"/);
  assert.doesNotMatch(
    adapter,
    /ProcessStartInfo|UseShellExecute|commandText|argumentList|ssh\s/i);
  assert.match(page, /releaseIdentity: elements\.releaseIdentity/);
  assert.match(page, /installedReleaseIdentity:/);
  assert.doesNotMatch(page, /prepareReleaseUpdate[\s\S]{0,1200}radioId\s*:/);
});
