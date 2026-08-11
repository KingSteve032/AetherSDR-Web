import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const adapter = await readFile(
  new URL("../Auth/Identity/AetherLocalAuthenticationHttpAdapter.cs", import.meta.url),
  "utf8");
const administrationAdapter = await readFile(
  new URL("../Auth/Identity/AetherIdentityAdministrationHttpAdapter.cs", import.meta.url),
  "utf8");
const authenticationEvents = await readFile(
  new URL("../Auth/Identity/AetherAuthenticationEvents.cs", import.meta.url),
  "utf8");
const composition = await readFile(
  new URL("../Auth/AetherAuthenticationComposition.cs", import.meta.url),
  "utf8");
const loginHtml = await readFile(
  new URL("../wwwroot/login.html", import.meta.url),
  "utf8");
const loginScript = await readFile(
  new URL("../wwwroot/login.js", import.meta.url),
  "utf8");

test("local credential endpoints are bounded antiforgery and rate-limit boundaries", () => {
  assert.match(adapter, /MaximumRequestBodyBytes = 4096/);
  assert.match(adapter, /UnmappedMemberHandling = JsonUnmappedMemberHandling\.Disallow/);
  assert.match(adapter, /RejectDuplicateProperties\(payload\)/);
  assert.match(adapter, /CryptographicOperations\.ZeroMemory\(payload\)/);
  assert.match(
    adapter,
    /MapPost\(PasswordPath[\s\S]{0,300}RequireRateLimiting[\s\S]{0,300}RequireAetherAntiforgery/);
  assert.match(
    adapter,
    /MapPost\(MfaPath[\s\S]{0,300}RequireRateLimiting[\s\S]{0,300}RequireAetherAntiforgery/);
  assert.match(adapter, /local-authentication-rejected/);
  assert.doesNotMatch(adapter, /Results\.Redirect\(body\./);
});

test("identity administration endpoints retain strict request and reauthentication boundaries", () => {
  assert.match(administrationAdapter, /MaximumRequestBodyBytes = 8192/);
  assert.match(
    administrationAdapter,
    /UnmappedMemberHandling = JsonUnmappedMemberHandling\.Disallow/);
  assert.match(administrationAdapter, /RejectDuplicateProperties\(payload\)/);
  assert.match(
    administrationAdapter,
    /CryptographicOperations\.ZeroMemory\(payload\)/);
  assert.match(
    administrationAdapter,
    /LocalPasswordReauthenticationRequest[\s\S]{0,120}Password/);
  assert.doesNotMatch(
    administrationAdapter,
    /LocalPasswordReauthenticationRequest[\s\S]{0,120}UserName/);
  assert.equal(
    administrationAdapter.match(/\.RequireAetherAntiforgery\(\)/g)?.length,
    9);
  assert.match(
    administrationAdapter,
    /ExternalReauthenticationPath[\s\S]{0,500}RequireAetherAntiforgery/);
  assert.match(
    administrationAdapter,
    /RedirectUri = LocalReturnUrl\.Normalize\(body\.ReturnUrl\)/);
  const linkRequest = administrationAdapter.match(
    /private sealed class ExternalIdentityLinkRequest[\s\S]*?\n    \}/)?.[0] ?? "";
  assert.match(linkRequest, /ReturnUrl/);
  assert.doesNotMatch(linkRequest, /Issuer|Subject|ProviderId/);
  assert.match(
    authenticationEvents,
    /AuthenticateAsync\([\s\S]{0,100}CookieAuthenticationDefaults\.AuthenticationScheme/);
  assert.match(authenticationEvents, /externalIdentities\.LinkAsync\(/);
  assert.match(authenticationEvents, /context\.HandleResponse\(\)/);
  assert.ok(
    authenticationEvents.indexOf("CompleteExternalIdentityLinkAsync") <
      authenticationEvents.indexOf("externalAuthentication.AuthenticateAsync"));
  assert.doesNotMatch(administrationAdapter, /Results\.Redirect\(body\./);
});

test("local and combined modes retain the hardened canonical cookie", () => {
  assert.match(composition, /Cookie\.Name = "__Host-AetherSdrWeb"/);
  assert.match(composition, /Cookie\.HttpOnly = true/);
  assert.match(composition, /CookieSecurePolicy\.Always/);
  assert.match(composition, /Cookie\.SameSite = SameSiteMode\.Lax/);
  assert.match(composition, /SlidingExpiration = false/);
  assert.match(composition, /options\.LoginPath = "\/login"/);
  assert.match(
    composition,
    /authenticationTopology\.LocalAccountsEnabled \|\| provider is null[\s\S]{0,180}CookieAuthenticationDefaults\.AuthenticationScheme/);
  assert.match(composition, /if \(provider is null\)[\s\S]{0,80}return;/);
});

test("browser sign-in keeps credentials and MFA authority in memory only", () => {
  assert.match(loginHtml, /<script src="\/login\.js\?v=local-auth-1" defer><\/script>/);
  assert.doesNotMatch(loginHtml, /<script(?! src=)/);
  assert.match(loginHtml, /autocomplete="current-password"/);
  assert.match(loginHtml, /autocomplete="one-time-code"/);

  assert.match(loginScript, /\/api\/auth\/options/);
  assert.match(loginScript, /X-Aether-CSRF|csrfHeader/);
  assert.match(loginScript, /credentials: "same-origin"/);
  assert.match(loginScript, /password\.value = ""/);
  assert.match(loginScript, /challengeToken = ""/);
  assert.match(loginScript, /encodeURIComponent\(safeReturnUrl\)/);
  assert.doesNotMatch(loginScript, /localStorage|sessionStorage|document\.cookie/);
  assert.doesNotMatch(loginScript, /innerHTML|outerHTML|insertAdjacentHTML/);
});
