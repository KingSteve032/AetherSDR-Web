using System.Security.Claims;
using System.Net;
using System.Text.Json;
using AetherSDR.Web.Auth;
using AetherSDR.Web.Radio;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

AuthSettings authSettings =
    builder.Configuration.GetSection(AuthSettings.SectionName).Get<AuthSettings>() ??
    new AuthSettings();
RadioSettings radioSettings =
    builder.Configuration.GetSection(RadioSettings.SectionName).Get<RadioSettings>() ??
    new RadioSettings();
RemoteStationSettings remoteStationSettings =
    builder.Configuration
        .GetSection(RemoteStationSettings.SectionName)
        .Get<RemoteStationSettings>() ??
    new RemoteStationSettings();
IndependentTxWatchdogSettings independentTxWatchdogSettings =
    builder.Configuration
        .GetSection(IndependentTxWatchdogSettings.SectionName)
        .Get<IndependentTxWatchdogSettings>() ??
    new IndependentTxWatchdogSettings();
StationTxCommandTrustSettings stationTxCommandTrustSettings =
    builder.Configuration
        .GetSection(StationTxCommandTrustSettings.SectionName)
        .Get<StationTxCommandTrustSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new StationTxCommandTrustSettings();
StationTxCommandSigningSettings stationTxCommandSigningSettings =
    builder.Configuration
        .GetSection(StationTxCommandSigningSettings.SectionName)
        .Get<StationTxCommandSigningSettings>(options =>
            options.ErrorOnUnknownConfiguration = true) ??
    new StationTxCommandSigningSettings();
StationTxCommandEnvelopeCoordinatorSettings
    stationTxCommandEnvelopeCoordinatorSettings =
        builder.Configuration
            .GetSection(
                StationTxCommandEnvelopeCoordinatorSettings.SectionName)
            .Get<StationTxCommandEnvelopeCoordinatorSettings>(options =>
                options.ErrorOnUnknownConfiguration = true) ??
        new StationTxCommandEnvelopeCoordinatorSettings();
ReverseProxySettings reverseProxySettings =
    builder.Configuration
        .GetSection(ReverseProxySettings.SectionName)
        .Get<ReverseProxySettings>() ??
    new ReverseProxySettings();
string[] allowedOrigins =
    builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddSingleton(Options.Create(authSettings));
builder.Services.AddSingleton(Options.Create(radioSettings));
builder.Services.AddSingleton(Options.Create(remoteStationSettings));
builder.Services.AddSingleton(Options.Create(independentTxWatchdogSettings));
builder.Services.AddSingleton(Options.Create(stationTxCommandTrustSettings));
builder.Services.AddSingleton(Options.Create(stationTxCommandSigningSettings));
builder.Services.AddSingleton(
    Options.Create(stationTxCommandEnvelopeCoordinatorSettings));
builder.Services.AddSingleton<StationTxIndependentWatchdogRegistry>();
builder.Services.AddSingleton<StationTxCommandTrustRegistry>();
builder.Services.AddSingleton<StationTxCommandSigningAuthority>();
builder.Services.AddSingleton<StationTxCommandEnvelopeCoordinator>();
builder.Services.AddSingleton(
    Options.Create(new OriginSettings { Values = allowedOrigins }));
ConfigureReverseProxy(builder.Services, reverseProxySettings);
string dataProtectionPath =
    builder.Configuration["DataProtection:KeyPath"] ??
    Path.Combine(builder.Environment.ContentRootPath, ".data-protection");
string? configuredRadioAccessPolicyPath =
    builder.Configuration["RadioAccess:PolicyPath"];
string radioAccessPolicyPath =
    string.IsNullOrWhiteSpace(configuredRadioAccessPolicyPath)
        ? Path.Combine(
        builder.Environment.ContentRootPath,
        ".radio-access",
        "policies.json")
        : configuredRadioAccessPolicyPath;
string? configuredAdministrativeAuditPath =
    builder.Configuration["RadioAccess:AuditPath"];
string administrativeAuditPath =
    string.IsNullOrWhiteSpace(configuredAdministrativeAuditPath)
        ? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(radioAccessPolicyPath)) ??
            builder.Environment.ContentRootPath,
            "audit.json")
        : configuredAdministrativeAuditPath;
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
    .SetApplicationName("AetherSDR.Web");

ConfigureAuthentication(builder, authSettings);
ConfigureAuthorization(builder.Services);

builder.Services.AddSingleton<RadioSelectionManager>();
builder.Services.AddSingleton<TxLeaseManager>();
builder.Services.AddSingleton<RadioTxOccupancyRegistry>();
builder.Services.AddHostedService<TxLeaseWatchdogService>();
builder.Services.AddSingleton<RadioCapacityHistoryService>();
builder.Services.AddSingleton<IHostedService>(
    services => services.GetRequiredService<RadioCapacityHistoryService>());
builder.Services.AddSingleton(
    services => new RadioAccessPolicyStore(
        radioAccessPolicyPath,
        services.GetRequiredService<
            ILogger<RadioAccessPolicyStore>>()));
builder.Services.AddSingleton(
    services => new AdministrativeAuditStore(
        administrativeAuditPath,
        services.GetRequiredService<
            ILogger<AdministrativeAuditStore>>()));
builder.Services.AddSingleton<RadioSessionRegistry>();
builder.Services.AddSingleton<RadioPresenceRegistry>();
builder.Services.AddSingleton<RadioAdministrationService>();
builder.Services.AddSingleton<IHostedService>(
    services => services.GetRequiredService<RadioSessionRegistry>());
builder.Services.AddHostedService<FlexRadioDiscoveryService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<RemoteStationCatalogService>();
builder.Services.AddSingleton<IHostedService>(
    services => services.GetRequiredService<RemoteStationCatalogService>());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter(
        "websocket",
        limiter =>
        {
            limiter.PermitLimit = 20;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
            limiter.AutoReplenishment = true;
        });
    options.AddFixedWindowLimiter(
        "station-enrollment",
        limiter =>
        {
            limiter.PermitLimit = 10;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
            limiter.AutoReplenishment = true;
        });
});

WebApplication app = builder.Build();
StationTxIndependentWatchdogRegistry independentTxWatchdogRegistry =
    app.Services.GetRequiredService<StationTxIndependentWatchdogRegistry>();
StationTxCommandTrustRegistry stationTxCommandTrustRegistry =
    app.Services.GetRequiredService<StationTxCommandTrustRegistry>();
StationTxCommandSigningAuthority stationTxCommandSigningAuthority =
    app.Services.GetRequiredService<StationTxCommandSigningAuthority>();
StationTxCommandEnvelopeCoordinator stationTxCommandEnvelopeCoordinator =
    app.Services.GetRequiredService<StationTxCommandEnvelopeCoordinator>();

if (reverseProxySettings.Enabled)
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; connect-src 'self' ws: wss:; " +
        "img-src 'self' data:; style-src 'self'; script-src 'self'; " +
        "worker-src 'self'; " +
        "object-src 'none'; base-uri 'none'; frame-ancestors 'none';";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] =
        "camera=(), geolocation=(), microphone=(self), payment=(), usb=()";
    await next();
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseWebSockets(
    new WebSocketOptions
    {
        KeepAliveInterval = TimeSpan.FromSeconds(20),
        AllowedOrigins = { }
    });

app.MapGet(
        "/healthz",
        () =>
        {
            StationTxIndependentWatchdogAggregate watchdog =
                independentTxWatchdogRegistry.Snapshot;
            StationTxCommandTrustDiagnostics commandTrust =
                stationTxCommandTrustRegistry.Snapshot;
            StationTxCommandSigningDiagnostics commandSigning =
                stationTxCommandSigningAuthority.Snapshot;
            StationTxCommandEnvelopeCoordinatorDiagnostics commandCoordinator =
                stationTxCommandEnvelopeCoordinator.Snapshot;
            return Results.Ok(new
            {
                status = "ok",
                radioMode = radioSettings.Mode,
                transmitEnabled = false,
                browserTxLeaseEnabled = radioSettings.BrowserTxLeaseEnabled,
                txGateLifecycleRegistered = true,
                txLifecycleWatchdogRegistered = true,
                txBrowserIntentProtocolVersion =
                    RadioBrowserTxProtocol.Version,
                txBrowserIntentValidationRegistered = true,
                txBrowserIntentCommandTransportRegistered = false,
                txStationCommandProtocolVersion =
                    StationTxCommandBoundary.ProtocolVersion,
                txStationCommandBoundaryRegistered = true,
                txStationCommandBoundaryEnabled = false,
                txStationCommandTrustVerificationEnabled =
                    commandTrust.VerificationEnabled,
                txStationCommandTrustedKeyCount = commandTrust.TrustedKeyCount,
                txStationCommandSignatureVerificationAvailable =
                    commandTrust.SignatureVerificationAvailable,
                txStationCommandSigningEnabled = commandSigning.SigningEnabled,
                txStationCommandSigningKeyConfigured =
                    commandSigning.KeyConfigured,
                txStationCommandSigningAvailable =
                    commandSigning.SigningAvailable,
                txStationCommandEnvelopeCoordinatorRegistered =
                    commandCoordinator.Registered,
                txStationCommandSessionCompositionRegistered = true,
                txStationCommandSessionCompositionBrowserIngressRegistered =
                    false,
                txStationCommandAdapterCompositionRegistered = true,
                txStationCommandAdapterExecutorAttached = true,
                txStationCommandAdapterExecutorRegistered = true,
                txStationCommandGateExecutorRegistered = true,
                txStationCommandGateExecutorTransmitEnabled = false,
                txStationCommandGateExecutorCommandTransportAvailable = false,
                txStationCommandGateExecutorSetTransmitAvailable = false,
                txStationCommandGateExecutorBrowserIngressRegistered = false,
                txStationCommandAdapterCompositionBrowserIngressRegistered =
                    false,
                txStationCommandSafetyArmCompositionRegistered = true,
                txStationCommandSafetyArmAuthorityAttached = true,
                txStationCommandSafetyArmAuthorityRegistered = true,
                txStationCommandSafetyArmAuthorityBoundaryEnabled = false,
                txStationCommandSafetyArmAuthorityCommandTransportAvailable =
                    false,
                txStationCommandSafetyArmAuthoritySetTransmitAvailable = false,
                txStationCommandSafetyArmAuthorityBrowserIngressRegistered =
                    false,
                txStationCommandSafetyArmAvailable = false,
                txStationCommandSafetyHeartbeatAvailable = false,
                txStationCommandSafetyAbortAvailable = false,
                txStationCommandSafetyArmCompositionBrowserIngressRegistered =
                    false,
                txStationCommandEnvelopeSubmissionEnabled =
                    commandCoordinator.SubmissionEnabled,
                txStationCommandEnvelopeSigningAvailable =
                    commandCoordinator.SigningAvailable,
                txStationCommandEnvelopeVerificationAvailable =
                    commandCoordinator.SignatureVerificationAvailable,
                txStationCommandEnvelopeBoundaryAttached =
                    commandCoordinator.BoundaryAttached,
                txStationCommandEnvelopeBoundaryVerificationAvailable =
                    commandCoordinator.BoundarySignatureVerificationAvailable,
                txStationCommandEnvelopeSubmissionAvailable =
                    commandCoordinator.SubmissionAvailable,
                txStationCommandEnvelopeSubmissionRegistered = false,
                txStationCommandAdapterRegistered = true,
                txStationCommandArmingAvailable = false,
                txStationCommandSetTransmitAvailable = false,
                txIndependentWatchdogHostPackaged = true,
                txIndependentWatchdogSupervisionRegistered =
                    watchdog.SupervisionRegistered,
                txIndependentWatchdogState = watchdog.State,
                txIndependentWatchdogConnected =
                    watchdog.ConnectedProcessCount > 0,
                txIndependentWatchdogSessionCount = watchdog.SessionCount,
                txIndependentWatchdogProcessCount =
                    watchdog.RunningProcessCount,
                txIndependentWatchdogConnectedProcessCount =
                    watchdog.ConnectedProcessCount,
                txIndependentWatchdogRegisteredIdentityCount =
                    watchdog.RegisteredIdentityCount,
                txIndependentWatchdogRestartCount = watchdog.RestartCount,
                txIndependentWatchdogCommandTransportRegistered =
                    watchdog.CommandTransportAvailable,
                txIndependentWatchdogArmingAvailable =
                    watchdog.ArmingAvailable,
                txCommandTransportRegistered = false,
                txSafetySupervisorArmingAvailable = false
            });
        })
    .AllowAnonymous();

app.MapGet(
        "/auth/login",
        (HttpContext context, string? returnUrl) =>
        {
            string safeReturnUrl = SafeReturnUrl(returnUrl);
            if (string.Equals(
                    authSettings.Mode,
                    "Development",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.Redirect(safeReturnUrl);
            }

            AuthenticationProperties properties =
                new() { RedirectUri = safeReturnUrl };
            return Results.Challenge(
                properties,
                [OpenIdConnectDefaults.AuthenticationScheme]);
        })
    .AllowAnonymous();

app.MapGet(
        "/auth/logout",
        () =>
        {
            if (string.Equals(
                    authSettings.Mode,
                    "Development",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.Redirect("/");
            }

            AuthenticationProperties properties =
                new() { RedirectUri = "/" };
            return Results.SignOut(
                properties,
                [
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    OpenIdConnectDefaults.AuthenticationScheme
                ]);
        })
    .RequireAuthorization();

app.MapGet(
        "/api/account",
        (ClaimsPrincipal user) =>
        {
            string[] roles = AetherRoles.All.Where(user.IsInRole).ToArray();
            return Results.Ok(new
            {
                user = new
                {
                    id = user.FindFirstValue("oid") ??
                         user.FindFirstValue(ClaimTypes.NameIdentifier),
                    name = user.FindFirstValue("name") ?? user.Identity?.Name,
                    email = user.FindFirstValue("preferred_username") ??
                            user.FindFirstValue(ClaimTypes.Email),
                    roles
                },
                authMode = authSettings.Mode
            });
        })
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/api/session",
        async (
            string? browserClientId,
            string? sessionId,
            ClaimsPrincipal user,
            RadioSessionRegistry sessions,
            CancellationToken cancellationToken) =>
        {
            RadioSession session;
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                if (!sessions.TryGetOwned(sessionId, user, out RadioSession? owned) ||
                    owned is null)
                {
                    return Results.NotFound(
                        new { error = "That radio session is not available." });
                }
                if (!string.IsNullOrWhiteSpace(browserClientId) &&
                    !string.Equals(
                        browserClientId,
                        owned.BrowserClientId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(
                        new
                        {
                            error =
                                "The browser client identifier does not match " +
                                "the requested radio session."
                        });
                }
                session = owned;
                session.Touch();
            }
            else
            {
                try
                {
                    session = await sessions.GetDefaultAsync(
                        user,
                        browserClientId,
                        cancellationToken);
                }
                catch (RadioAccessDeniedException exception)
                {
                    return RadioAccessDeniedResult(exception);
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(new { error = exception.Message });
                }
            }

            string[] roles = AetherRoles.All.Where(user.IsInRole).ToArray();
            return Results.Ok(new
            {
                user = new
                {
                    id = user.FindFirstValue("oid") ??
                         user.FindFirstValue(ClaimTypes.NameIdentifier),
                    name = user.FindFirstValue("name") ?? user.Identity?.Name,
                    email = user.FindFirstValue("preferred_username") ??
                            user.FindFirstValue(ClaimTypes.Email),
                    roles
                },
                authMode = authSettings.Mode,
                radioMode = radioSettings.Mode,
                allowTransmit = false,
                protocol = "aethersdr-web-experimental/v0",
                sessionId = session.SessionId,
                radioClient = new
                {
                    type = "gui",
                    guiClientId = session.GuiClientId
                }
            });
        })
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/api/radios/catalog",
        (RadioSelectionManager selectionManager) =>
            Results.Ok(selectionManager.GetSnapshot()))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/api/radio/state",
        (
            string? sessionId,
            ClaimsPrincipal user,
            RadioSessionRegistry sessions) =>
        {
            if (!sessions.TryGetOwned(sessionId, user, out RadioSession? session) ||
                session is null)
            {
                return Results.NotFound(
                    new { error = "That radio session is not available." });
            }

            return Results.Ok(session.Coordinator.Snapshot);
        })
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/api/radios",
        async (
            string? sessionId,
            string? browserClientId,
            ClaimsPrincipal user,
            RadioSelectionManager selectionManager,
            RadioSessionRegistry sessions,
            CancellationToken cancellationToken) =>
        {
            RadioSession? session;
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                try
                {
                    session = await sessions.GetDefaultAsync(
                        user,
                        browserClientId,
                        cancellationToken);
                }
                catch (RadioAccessDeniedException exception)
                {
                    return RadioAccessDeniedResult(exception);
                }
                catch (ArgumentException exception)
                {
                    return Results.BadRequest(
                        new { error = exception.Message });
                }
            }
            else if (!sessions.TryGetOwned(sessionId, user, out session) ||
                     session is null)
            {
                return Results.NotFound(
                    new { error = "That radio session is not available." });
            }

            return Results.Ok(
                selectionManager.GetSnapshot(
                    session.Endpoint.RadioId,
                    session.Selection.LowBandwidth));
        })
    .RequireAuthorization(AetherPolicies.Observe);

app.MapPost(
        "/api/radios/select",
        async (
            SelectRadioRequest request,
            ClaimsPrincipal user,
            RadioSelectionManager selectionManager,
            RadioSessionRegistry sessions,
            CancellationToken cancellationToken) =>
        {
            if (!selectionManager.TryResolve(
                    request.RadioId,
                    out SelectedRadioEndpoint selected,
                    out string? error))
            {
                return Results.BadRequest(new { error });
            }

            RadioSession? currentSession = null;
            if (!string.IsNullOrWhiteSpace(request.CurrentSessionId) &&
                (!sessions.TryGetOwned(
                    request.CurrentSessionId,
                    user,
                    out currentSession) ||
                 currentSession is null))
            {
                return Results.BadRequest(
                    new { error = "The current radio session is invalid." });
            }
            if (currentSession is not null &&
                !string.IsNullOrWhiteSpace(request.BrowserClientId) &&
                !string.Equals(
                    request.BrowserClientId,
                    currentSession.BrowserClientId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(
                    new
                    {
                        error =
                            "The browser client identifier does not match " +
                            "the current radio session."
                    });
            }

            RadioSession selectedSession;
            try
            {
                selectedSession = await sessions.GetOrCreateAsync(
                    user,
                    currentSession?.BrowserClientId ??
                        request.BrowserClientId,
                    selected,
                    request.LowBandwidth,
                    cancellationToken);
            }
            catch (RadioAccessDeniedException exception)
            {
                return RadioAccessDeniedResult(exception);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            bool connectionChanged =
                currentSession is null ||
                !string.Equals(
                    currentSession.SessionId,
                    selectedSession.SessionId,
                    StringComparison.Ordinal);
            if (connectionChanged && currentSession is not null)
            {
                await sessions.TerminateOwnedSessionAsync(
                    currentSession.SessionId,
                    user);
            }

            return Results.Ok(new
            {
                selected,
                connectionChanged,
                sessionId = selectedSession.SessionId
            });
        })
    .RequireAuthorization(AetherPolicies.Control);

app.MapPost(
        "/api/session/release",
        async (
            string? sessionId,
            ClaimsPrincipal user,
            RadioSessionRegistry sessions) =>
        {
            bool released = await sessions.TerminateOwnedSessionAsync(
                sessionId,
                user);
            return released
                ? Results.NoContent()
                : Results.NotFound(
                    new { error = "That radio session is not available." });
        })
    .RequireAuthorization(AetherPolicies.Observe);

app.MapPost(
        "/api/radio/low-bandwidth",
        (
            SetLowBandwidthRequest request,
            ClaimsPrincipal user,
            RadioSessionRegistry sessions) =>
        {
            if (!sessions.TryGetOwned(
                    request.SessionId,
                    user,
                    out RadioSession? session) ||
                session is null)
            {
                return Results.NotFound(
                    new { error = "That radio session is not available." });
            }

            bool reconnecting = session.SetLowBandwidth(request.Enabled);
            return Results.Ok(new
            {
                enabled = session.Selection.LowBandwidth,
                reconnecting
            });
        })
    .RequireAuthorization(AetherPolicies.Control);

app.MapGet(
        "/api/admin/radios",
        (RadioAdministrationService administration) =>
            Results.Ok(new { radios = administration.GetInventory() }))
    .RequireAuthorization(AetherPolicies.Admin);

app.MapGet(
        "/api/admin/stations",
        (RemoteStationCatalogService remoteStations) =>
            Results.Ok(remoteStations.GetAdministrationSnapshot()))
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/admin/stations/enrollment-codes",
        async (
            CreateRemoteStationEnrollmentRequest request,
            ClaimsPrincipal user,
            RemoteStationCatalogService remoteStations,
            AdministrativeAuditStore audit,
            CancellationToken cancellationToken) =>
        {
            (string administratorId, string administratorName) =
                GetAdministrativeActor(user);
            string stationTarget = $"station:{request.StationId}";
            try
            {
                RemoteStationEnrollmentCodeResult result =
                    await remoteStations.CreateEnrollmentCodeAsync(
                        request.StationId,
                        cancellationToken);
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.CreateStationEnrollmentCode,
                    stationTarget,
                    result.Purpose,
                    AdministrativeAuditResults.Succeeded,
                    $"Created a one-time {result.Purpose} code that expires " +
                    $"at {result.ExpiresAt:O}.");
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.CreateStationEnrollmentCode,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (RemoteStationManagementException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.CreateStationEnrollmentCode,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return RemoteStationManagementFailure(exception);
            }
            catch (Exception exception)
                when (exception is HttpRequestException or
                      IOException or
                      JsonException or
                      InvalidDataException)
            {
                const string message =
                    "Station enrollment is temporarily unavailable.";
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.CreateStationEnrollmentCode,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    message);
                return Results.Json(
                    new { error = message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/admin/stations/{stationId}/{action}",
        async (
            string stationId,
            string action,
            ClaimsPrincipal user,
            RemoteStationCatalogService remoteStations,
            AdministrativeAuditStore audit,
            CancellationToken cancellationToken) =>
        {
            (string administratorId, string administratorName) =
                GetAdministrativeActor(user);
            string stationTarget = $"station:{stationId}";
            string auditAction = action switch
            {
                "enable" =>
                    AdministrativeAuditActions.EnableStationCredential,
                "disable" =>
                    AdministrativeAuditActions.DisableStationCredential,
                "revoke" =>
                    AdministrativeAuditActions.RevokeStationCredential,
                _ => "station.credential.invalid"
            };
            try
            {
                RemoteStationCredentialAdministrationEntry result =
                    await remoteStations.SetCredentialStateAsync(
                        stationId,
                        action,
                        cancellationToken);
                audit.Record(
                    administratorId,
                    administratorName,
                    auditAction,
                    stationTarget,
                    result.State,
                    AdministrativeAuditResults.Succeeded,
                    $"Station credential changed to {result.State}.");
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    auditAction,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (RemoteStationManagementException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    auditAction,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return RemoteStationManagementFailure(exception);
            }
            catch (Exception exception)
                when (exception is HttpRequestException or
                      IOException or
                      JsonException or
                      InvalidDataException)
            {
                const string message =
                    "Station credential management is temporarily " +
                    "unavailable.";
                audit.Record(
                    administratorId,
                    administratorName,
                    auditAction,
                    stationTarget,
                    null,
                    AdministrativeAuditResults.Failed,
                    message);
                return Results.Json(
                    new { error = message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/station-enrollment/redeem",
        async (
            RedeemRemoteStationEnrollmentRequest request,
            RemoteStationCatalogService remoteStations,
            CancellationToken cancellationToken) =>
        {
            try
            {
                RemoteStationEnrollmentResult result =
                    await remoteStations.RedeemEnrollmentAsync(
                        request,
                        cancellationToken);
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (RemoteStationManagementException exception)
            {
                return RemoteStationManagementFailure(exception);
            }
            catch (Exception exception)
                when (exception is HttpRequestException or
                      IOException or
                      JsonException or
                      InvalidDataException)
            {
                return Results.Json(
                    new
                    {
                        error =
                            "Station enrollment is temporarily unavailable."
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
    .AllowAnonymous()
    .RequireRateLimiting("station-enrollment");

app.MapGet(
        "/api/admin/audit",
        (
            int? limit,
            AdministrativeAuditStore audit) =>
            Results.Ok(new
            {
                events = audit.GetRecent(limit ?? 50)
            }))
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/admin/radios/{radioId}/policy",
        (
            string radioId,
            UpdateRadioAccessPolicyRequest request,
            ClaimsPrincipal user,
            RadioAdministrationService administration,
            AdministrativeAuditStore audit) =>
        {
            (string administratorId, string administratorName) =
                GetAdministrativeActor(user);
            if (!RadioSessionRegistry.TryGetUserId(
                    user,
                    out administratorId))
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioPolicy,
                    radioId,
                    request.ReservedUserId,
                    AdministrativeAuditResults.Failed,
                    "A stable administrator ID was not available.");
                return Results.BadRequest(
                    new { error = "A stable administrator ID is required." });
            }

            try
            {
                RadioAccessPolicySnapshot policy =
                    administration.UpdatePolicy(
                        radioId,
                        request,
                        administratorId);
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioPolicy,
                    radioId,
                    policy.ReservedUserId,
                    AdministrativeAuditResults.Succeeded,
                    $"Access changed to {policy.Mode}; reservation " +
                    $"{(policy.ReservedUserId is null ? "cleared" : "set")}.");
                return Results.Ok(policy);
            }
            catch (ArgumentException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioPolicy,
                    radioId,
                    request.ReservedUserId,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (KeyNotFoundException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.UpdateRadioPolicy,
                    radioId,
                    request.ReservedUserId,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.NotFound(new { error = exception.Message });
            }
        })
    .RequireAuthorization(AetherPolicies.Admin);

app.MapPost(
        "/api/admin/radios/{radioId}/operators/{userId}/disconnect",
        async (
            string radioId,
            string userId,
            ClaimsPrincipal user,
            RadioAdministrationService administration,
            AdministrativeAuditStore audit) =>
        {
            (string administratorId, string administratorName) =
                GetAdministrativeActor(user);
            try
            {
                ForceDisconnectResult result =
                    await administration.ForceDisconnectAsync(
                        radioId,
                        userId);
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.ForceDisconnectOperator,
                    radioId,
                    userId,
                    AdministrativeAuditResults.Succeeded,
                    $"Released {result.BrowserConnections} browser " +
                    $"connection(s) and {result.RadioSessions} radio session(s).");
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.ForceDisconnectOperator,
                    radioId,
                    userId,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (KeyNotFoundException exception)
            {
                audit.Record(
                    administratorId,
                    administratorName,
                    AdministrativeAuditActions.ForceDisconnectOperator,
                    radioId,
                    userId,
                    AdministrativeAuditResults.Failed,
                    exception.Message);
                return Results.NotFound(new { error = exception.Message });
            }
        })
    .RequireAuthorization(AetherPolicies.Admin);

app.MapGet(
        "/styles.css",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "styles.css"),
                "text/css"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/portal.css",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "portal.css"),
                "text/css"))
    .AllowAnonymous();

app.MapGet(
    "/admin-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "admin-controls.js"),
                "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/admin-diagnostics.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "admin-diagnostics.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Admin);

app.MapGet(
    "/radio-select.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "radio-select.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/admin-page.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "admin-page.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Admin);

app.MapGet(
        "/app.js",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "app.js"),
                "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/waterfall.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "waterfall.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/slice-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "slice-controls.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/frequency-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "frequency-controls.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/range-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "range-controls.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/layout-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "layout-controls.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/band-plan.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "band-plan.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/audio.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "audio.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/radio-transport.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "radio-transport.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/radio-transport-core.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "radio-transport-core.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/network-profile.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "network-profile.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/tx-controls.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "tx-controls.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/radio-transport-worker.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(
                environment.WebRootPath,
                "radio-transport-worker.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/microphone.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "microphone.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/audio-worklet.js",
    (IWebHostEnvironment environment) =>
        Results.File(
            Path.Combine(environment.WebRootPath, "audio-worklet.js"),
            "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
    "/meter.js",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "meter.js"),
                "text/javascript"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/assets/logo.png",
        () =>
            Results.File(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "wwwroot",
                    "assets",
                    "logo.png"),
                "image/png"))
    .AllowAnonymous();

app.MapGet(
        "/assets/s-meter-v1.json",
        () =>
            Results.File(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "wwwroot",
                    "assets",
                    "s-meter-v1.json"),
                "application/json"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/assets/bandplans/arrl-us.json",
        () =>
            Results.File(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "wwwroot",
                    "assets",
                    "bandplans",
                    "arrl-us.json"),
                "application/json"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/ws/radio",
        RadioWebSocketEndpoint.HandleAsync)
    .RequireAuthorization(AetherPolicies.Observe)
    .RequireRateLimiting("websocket");

app.MapGet(
        "/",
        (HttpContext context, IWebHostEnvironment environment) =>
            context.User.Identity?.IsAuthenticated == true
                ? Results.Redirect("/radios")
                : Results.File(
                    Path.Combine(environment.WebRootPath, "login.html"),
                    "text/html"))
    .AllowAnonymous();

app.MapGet(
        "/login",
        (HttpContext context, IWebHostEnvironment environment) =>
            context.User.Identity?.IsAuthenticated == true
                ? Results.Redirect("/radios")
                : Results.File(
                    Path.Combine(environment.WebRootPath, "login.html"),
                    "text/html"))
    .AllowAnonymous();

app.MapGet(
        "/access-denied",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "access-denied.html"),
                "text/html"))
    .AllowAnonymous();

app.MapGet(
        "/radios",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "radios.html"),
                "text/html"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/radio",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "index.html"),
                "text/html"))
    .RequireAuthorization(AetherPolicies.Observe);

app.MapGet(
        "/admin",
        (IWebHostEnvironment environment) =>
            Results.File(
                Path.Combine(environment.WebRootPath, "admin.html"),
                "text/html"))
    .RequireAuthorization(AetherPolicies.Admin);

app.Run();

static IResult RemoteStationManagementFailure(
    RemoteStationManagementException exception)
{
    int statusCode = exception.StatusCode switch
    {
        HttpStatusCode.BadRequest => StatusCodes.Status400BadRequest,
        HttpStatusCode.NotFound => StatusCodes.Status404NotFound,
        HttpStatusCode.Conflict => StatusCodes.Status409Conflict,
        HttpStatusCode.ServiceUnavailable =>
            StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status502BadGateway
    };
    return Results.Json(
        new { error = exception.Message },
        statusCode: statusCode);
}

static void ConfigureAuthentication(
    WebApplicationBuilder builder,
    AuthSettings authSettings)
{
    bool developmentAuth = string.Equals(
        authSettings.Mode,
        "Development",
        StringComparison.OrdinalIgnoreCase);

    if (developmentAuth)
    {
        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Development authentication is forbidden outside the Development environment.");
        }

        builder.Services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme =
                    DevelopmentAuthenticationDefaults.Scheme;
                options.DefaultChallengeScheme =
                    DevelopmentAuthenticationDefaults.Scheme;
            })
            .AddScheme<
                AuthenticationSchemeOptions,
                DevelopmentAuthenticationHandler>(
                DevelopmentAuthenticationDefaults.Scheme,
                _ => { });
        return;
    }

    string clientSecret = OidcClientSecretResolver.Resolve(authSettings);
    if (string.IsNullOrWhiteSpace(authSettings.Authority) ||
        string.IsNullOrWhiteSpace(authSettings.ClientId) ||
        string.IsNullOrWhiteSpace(clientSecret))
    {
        throw new InvalidOperationException(
            "OIDC authentication requires Auth:Authority, Auth:ClientId, and " +
            "either Auth:ClientSecret or Auth:ClientSecretFile.");
    }

    builder.Services
        .AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme =
                CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultSignInScheme =
                CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme =
                OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(
            CookieAuthenticationDefaults.AuthenticationScheme,
            options =>
            {
                options.Cookie.Name = "__Host-AetherSdrWeb";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                options.AccessDeniedPath = "/access-denied";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
            })
        .AddOpenIdConnect(
            OpenIdConnectDefaults.AuthenticationScheme,
            options =>
            {
                options.Authority = authSettings.Authority.TrimEnd('/');
                options.ClientId = authSettings.ClientId;
                options.ClientSecret = clientSecret;
                options.CallbackPath = authSettings.CallbackPath;
                options.SignedOutCallbackPath =
                    authSettings.SignedOutCallbackPath;
                options.SignInScheme =
                    CookieAuthenticationDefaults.AuthenticationScheme;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.MapInboundClaims = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = authSettings.NameClaimType,
                    RoleClaimType = authSettings.RoleClaimType,
                    ValidateIssuer = true
                };
            });
}

static void ConfigureAuthorization(IServiceCollection services)
{
    AuthorizationPolicy authenticated =
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

    services.AddAuthorizationBuilder()
        .SetFallbackPolicy(authenticated)
        .AddPolicy(
            AetherPolicies.Observe,
            policy => policy.RequireRole(
                AetherRoles.Observe,
                AetherRoles.Control,
                AetherRoles.Transmit,
                AetherRoles.Admin))
        .AddPolicy(
            AetherPolicies.Control,
            policy => policy.RequireRole(AetherRoles.Control, AetherRoles.Admin))
        .AddPolicy(
            AetherPolicies.Transmit,
            policy => policy.RequireRole(AetherRoles.Transmit, AetherRoles.Admin))
        .AddPolicy(
            AetherPolicies.Admin,
            policy => policy.RequireRole(AetherRoles.Admin));
}

static void ConfigureReverseProxy(
    IServiceCollection services,
    ReverseProxySettings settings)
{
    if (!settings.Enabled)
    {
        return;
    }
    if (settings.KnownProxies.Length == 0)
    {
        throw new InvalidOperationException(
            "ReverseProxy:KnownProxies must contain at least one trusted IP address.");
    }

    List<IPAddress> knownProxies = [];
    foreach (string configuredAddress in settings.KnownProxies)
    {
        if (!IPAddress.TryParse(
                configuredAddress?.Trim(),
                out IPAddress? address))
        {
            throw new InvalidOperationException(
                $"ReverseProxy:KnownProxies contains an invalid IP address: " +
                $"'{configuredAddress}'.");
        }

        knownProxies.Add(address);
    }

    services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor |
            ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        foreach (IPAddress address in knownProxies)
        {
            options.KnownProxies.Add(address);
        }
    });
}

static string SafeReturnUrl(string? returnUrl)
{
    if (string.IsNullOrWhiteSpace(returnUrl) ||
        !Uri.TryCreate(returnUrl, UriKind.Relative, out Uri? parsed) ||
        !returnUrl.StartsWith("/", StringComparison.Ordinal) ||
        returnUrl.StartsWith("//", StringComparison.Ordinal))
    {
        return "/";
    }

    return parsed.ToString();
}

static IResult RadioAccessDeniedResult(
    RadioAccessDeniedException exception) =>
    Results.Conflict(new
    {
        code = "radio_access_denied",
        radioId = exception.RadioId,
        error = exception.Message
    });

static (string Id, string DisplayName) GetAdministrativeActor(
    ClaimsPrincipal user)
{
    string actorId =
        user.FindFirstValue("oid") ??
        user.FindFirstValue(ClaimTypes.NameIdentifier) ??
        user.FindFirstValue("sub") ??
        "unknown";
    string displayName =
        user.FindFirstValue("name") ??
        user.Identity?.Name ??
        user.FindFirstValue("preferred_username") ??
        actorId;
    return (actorId.Trim(), displayName.Trim());
}

public partial class Program;
