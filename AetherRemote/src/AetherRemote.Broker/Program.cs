using AetherRemote.Broker;
using AetherRemote.Protocol;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services
    .AddOptions<StationLinkSettings>()
    .BindConfiguration(StationLinkSettings.SectionName)
    .Validate(
        settings =>
            settings.HeartbeatSeconds is >= 5 and <= 60,
        "StationLink:HeartbeatSeconds must be between 5 and 60.")
    .Validate(
        settings =>
            settings.DegradedAfterSeconds >
                settings.HeartbeatSeconds &&
            settings.DegradedAfterSeconds <= 300,
        "StationLink:DegradedAfterSeconds must be greater than the " +
        "heartbeat interval and no more than 300.")
    .Validate(
        settings =>
            settings.DisconnectAfterSeconds >
                settings.DegradedAfterSeconds &&
            settings.DisconnectAfterSeconds <= 600,
        "StationLink:DisconnectAfterSeconds must be greater than the " +
        "degraded interval and no more than 600.")
    .Validate(
        settings =>
            settings.LinkTokenSeconds is >= 15 and <= 300,
        "StationLink:LinkTokenSeconds must be between 15 and 300.")
    .Validate(
        settings =>
            settings.EnrollmentCodeMinutes is >= 5 and <= 60,
        "StationLink:EnrollmentCodeMinutes must be between 5 and 60.")
    .Validate(
        settings =>
            builder.Environment.IsDevelopment() ||
            !settings.Enabled ||
            !string.IsNullOrWhiteSpace(settings.EnrollmentRegistryPath),
        "Production station links require a durable enrollment registry.")
    .Validate(
        settings =>
            builder.Environment.IsDevelopment() ||
            !settings.Enabled ||
            settings.RequireForwardedHttps,
        "Production station links must require forwarded HTTPS.")
    .ValidateOnStart();
builder.Services.AddSingleton<StationEnrollmentRegistry>();
builder.Services.AddSingleton<StationCredentialVerifier>();
builder.Services.AddSingleton<StationLinkTokenService>();
builder.Services.AddSingleton<StationLinkTokenEndpoint>();
builder.Services.AddSingleton<StationRegistry>();
builder.Services.AddSingleton<RemoteReceiveSessionBroker>();
builder.Services.AddSingleton<RemoteReleaseServiceControlBroker>();
builder.Services.AddSingleton<RemoteReleaseUpdateBroker>();
builder.Services.AddSingleton<StationWebSocketEndpoint>();
builder.Services.AddSingleton<ReceiveProjectionWebSocketEndpoint>();
builder.Services.AddHostedService<StationLivenessMonitor>();

WebApplication app = builder.Build();
app.Use(async (context, next) =>
{
    bool developmentTestTransport =
        app.Environment.IsDevelopment() &&
        context.Connection.RemoteIpAddress is null;
    if (BrokerNetworkBoundary.RequiresLoopback(context.Request.Path) &&
        !developmentTestTransport &&
        !BrokerNetworkBoundary.IsLoopback(
            context.Connection.RemoteIpAddress))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15)
});

app.MapGet(
    "/healthz",
    (IOptions<StationLinkSettings> settings) => Results.Ok(new
    {
        status = "ok",
        protocol = StationProtocol.WebSocketSubprotocol,
        stationLinkEnabled = settings.Value.Enabled,
        transmitEnabled = false
    }));

app.MapGet(
    "/api/stations",
    (
        HttpContext context,
        StationCredentialVerifier credentials,
        StationRegistry registry) =>
    {
        string credential = ReadBearerCredential(context.Request);
        return credentials.VerifyRuntime(credential)
            ? Results.Ok(new { stations = registry.GetSnapshot() })
            : Results.Unauthorized();
    });

app.MapGet(
    "/api/station-credentials",
    (
        HttpContext context,
        StationCredentialVerifier credentials,
        StationEnrollmentRegistry enrollments) =>
    {
        string credential = ReadBearerCredential(context.Request);
        return credentials.VerifyAdministration(credential)
            ? Results.Ok(new { stations = enrollments.GetSnapshot() })
            : Results.Unauthorized();
    });

app.MapPost(
    "/api/enrollment-codes",
    (
        HttpContext context,
        CreateStationEnrollmentRequest request,
        StationCredentialVerifier credentials,
        StationEnrollmentRegistry enrollments) =>
    {
        string credential = ReadBearerCredential(context.Request);
        if (!credentials.VerifyAdministration(credential))
        {
            return Results.Unauthorized();
        }
        try
        {
            return Results.Ok(
                enrollments.CreateEnrollmentCode(request.StationId));
        }
        catch (StationEnrollmentException exception)
        {
            return EnrollmentError(exception);
        }
    });

app.MapPost(
    "/api/enrollments/redeem",
    (
        RedeemStationEnrollmentRequest request,
        StationEnrollmentRegistry enrollments,
        StationRegistry stations) =>
    {
        try
        {
            StationEnrollmentResult result = enrollments.Redeem(
                request.EnrollmentCode,
                request.CredentialSha256);
            stations.Disconnect(result.StationId);
            return Results.Ok(result);
        }
        catch (StationEnrollmentException exception)
        {
            return EnrollmentError(exception);
        }
    });

app.MapPost(
    "/api/station-credentials/{stationId}/{action}",
    (
        HttpContext context,
        string stationId,
        string action,
        StationCredentialVerifier credentials,
        StationEnrollmentRegistry enrollments,
        StationRegistry stations,
        StationLinkTokenService tokens) =>
    {
        string credential = ReadBearerCredential(context.Request);
        if (!credentials.VerifyAdministration(credential))
        {
            return Results.Unauthorized();
        }
        string state = action switch
        {
            "enable" => StationCredentialStates.Enabled,
            "disable" => StationCredentialStates.Disabled,
            "revoke" => StationCredentialStates.Revoked,
            _ => string.Empty
        };
        try
        {
            StationCredentialSnapshot result =
                enrollments.SetState(stationId, state);
            if (state is StationCredentialStates.Disabled or
                StationCredentialStates.Revoked)
            {
                tokens.RevokeStation(stationId);
                stations.Disconnect(stationId);
            }
            return Results.Ok(result);
        }
        catch (StationEnrollmentException exception)
        {
            return EnrollmentError(exception);
        }
    });

app.MapPost(
    "/api/release-service-control",
    async (
        HttpContext context,
        RemoteReleaseServiceControlRequest request,
        StationCredentialVerifier credentials,
        RemoteReleaseServiceControlBroker control,
        CancellationToken cancellationToken) =>
    {
        string credential = ReadBearerCredential(context.Request);
        if (!credentials.VerifyAdministration(credential))
        {
            return Results.Unauthorized();
        }
        try
        {
            return Results.Ok(
                await control.ExecuteAsync(request, cancellationToken));
        }
        catch (RemoteReleaseServiceControlException exception)
        {
            int status = exception.Code switch
            {
                "invalid_station" or "invalid_request" =>
                    StatusCodes.Status400BadRequest,
                "station_offline" => StatusCodes.Status404NotFound,
                "station_capability" => StatusCodes.Status409Conflict,
                "station_timeout" or "station_disconnected" =>
                    StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status422UnprocessableEntity
            };
            return Results.Json(
                new { error = exception.Message, code = exception.Code },
                statusCode: status);
        }
    });

app.MapPost(
    "/api/release-updates",
    async (
        HttpContext context,
        RemoteReleaseUpdateRequest request,
        StationCredentialVerifier credentials,
        RemoteReleaseUpdateBroker updates,
        CancellationToken cancellationToken) =>
    {
        string credential = ReadBearerCredential(context.Request);
        if (!credentials.VerifyAdministration(credential))
        {
            return Results.Unauthorized();
        }
        try
        {
            return Results.Ok(
                await updates.ExecuteAsync(request, cancellationToken));
        }
        catch (RemoteReleaseUpdateException exception)
        {
            int status = exception.Code switch
            {
                "invalid_station" or "invalid_request" =>
                    StatusCodes.Status400BadRequest,
                "station_offline" => StatusCodes.Status404NotFound,
                "station_capability" or "station_busy" =>
                    StatusCodes.Status409Conflict,
                "station_timeout" or "station_disconnected" =>
                    StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status422UnprocessableEntity
            };
            return Results.Json(
                new { error = exception.Message, code = exception.Code },
                statusCode: status);
        }
    });

app.MapGet(
    "/api/receive-sessions",
    (
        HttpContext context,
        StationCredentialVerifier credentials,
        RemoteReceiveSessionBroker sessions) =>
    {
        string credential = ReadBearerCredential(context.Request);
        return credentials.VerifyRuntime(credential)
            ? Results.Ok(new { sessions = sessions.GetSnapshot() })
            : Results.Unauthorized();
    });

app.MapPost(
    "/api/receive-sessions",
    async (
        HttpContext context,
        OpenRemoteReceiveSessionRequest request,
        StationCredentialVerifier credentials,
        RemoteReceiveSessionBroker sessions) =>
    {
        string credential = ReadBearerCredential(context.Request);
        if (!credentials.VerifyRuntime(credential))
        {
            return Results.Unauthorized();
        }

        try
        {
            RemoteReceiveSessionSnapshot opened =
                await sessions.OpenAsync(
                    request,
                    context.RequestAborted);
            return Results.Ok(opened);
        }
        catch (RemoteReceiveSessionException exception)
        {
            int status = exception.Code switch
            {
                "invalid_request" => StatusCodes.Status400BadRequest,
                "station_capacity" or "station_capability" =>
                    StatusCodes.Status409Conflict,
                "station_offline" or "station_timeout" =>
                    StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status409Conflict
            };
            return Results.Json(
                new
                {
                    error = exception.Message,
                    code = exception.Code
                },
                statusCode: status);
        }
    });

app.MapDelete(
    "/api/receive-sessions/{sessionId}",
    async (
        HttpContext context,
        string sessionId,
        StationCredentialVerifier credentials,
        RemoteReceiveSessionBroker sessions) =>
    {
        string credential = ReadBearerCredential(context.Request);
        if (!credentials.VerifyRuntime(credential))
        {
            return Results.Unauthorized();
        }
        return await sessions.CloseAsync(
            sessionId,
            context.RequestAborted)
            ? Results.NoContent()
            : Results.NotFound();
    });

app.MapPost(
    "/station/v1/token",
    (HttpContext context, StationLinkTokenEndpoint endpoint) =>
        endpoint.HandleAsync(context));

app.Map(
    "/station/v1",
    (HttpContext context, StationWebSocketEndpoint endpoint) =>
        endpoint.HandleAsync(context));

app.Map(
    "/receive/v1",
    (HttpContext context, ReceiveProjectionWebSocketEndpoint endpoint) =>
        endpoint.HandleAsync(context));

app.Run();

static string ReadBearerCredential(HttpRequest request)
{
    string authorization =
        request.Headers.Authorization.FirstOrDefault() ?? string.Empty;
    const string prefix = "Bearer ";
    return authorization.StartsWith(prefix, StringComparison.Ordinal)
        ? authorization[prefix.Length..].Trim()
        : string.Empty;
}

static IResult EnrollmentError(StationEnrollmentException exception)
{
    int status = exception.Code switch
    {
        "station_not_found" => StatusCodes.Status404NotFound,
        "enrollment_capacity" => StatusCodes.Status409Conflict,
        "station_revoked" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    };
    return Results.Json(
        new { error = exception.Message, code = exception.Code },
        statusCode: status);
}

public partial class Program;
