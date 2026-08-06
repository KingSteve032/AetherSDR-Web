using Microsoft.AspNetCore.Antiforgery;

namespace AetherSDR.Web.Auth;

/// <summary>
/// Shared browser antiforgery contract for every authenticated HTTP mutation.
/// The request token is intentionally returned only to an authenticated,
/// same-origin caller and is never accepted as authorization evidence.
/// </summary>
public static class AetherAntiforgery
{
    public const string HeaderName = "X-Aether-CSRF";
    public const string FailureMessage =
        "The request antiforgery token is missing or invalid.";

    public static IResult IssueToken(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(antiforgery);

        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new
        {
            headerName = HeaderName,
            formFieldName = tokens.FormFieldName,
            requestToken = tokens.RequestToken ?? string.Empty
        });
    }

    public static IResult IssueLogoutConfirmation(
        HttpContext context,
        IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(antiforgery);

        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
        string fieldName = System.Net.WebUtility.HtmlEncode(
            tokens.FormFieldName);
        string requestToken = System.Net.WebUtility.HtmlEncode(
            tokens.RequestToken ?? string.Empty);
        const string prefix =
            "<!doctype html><html lang=\"en\"><head>" +
            "<meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<meta name=\"color-scheme\" content=\"dark\">" +
            "<title>Sign out · AetherSDR Web</title>" +
            "<link rel=\"stylesheet\" href=\"/portal.css\"></head>" +
            "<body class=\"portal-body centered-body\"><main class=\"message-card\">" +
            "<p class=\"eyebrow\">AETHERSDR WEB</p><h1>Sign out?</h1>" +
            "<p class=\"muted\">This ends your authenticated browser session.</p>" +
            "<form method=\"post\" action=\"/auth/logout\">";
        string body =
            prefix +
            $"<input type=\"hidden\" name=\"{fieldName}\" value=\"{requestToken}\">" +
            "<div class=\"button-row\">" +
            "<button class=\"primary-action\" type=\"submit\">Sign out</button>" +
            "<a class=\"secondary-action\" href=\"/\">Cancel</a>" +
            "</div></form></main></body></html>";
        return Results.Content(body, "text/html; charset=utf-8");
    }

    public static RouteHandlerBuilder RequireAetherAntiforgery(
        this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilter(
            async (invocationContext, next) =>
            {
                IAntiforgery antiforgery =
                    invocationContext.HttpContext.RequestServices
                        .GetRequiredService<IAntiforgery>();
                try
                {
                    await antiforgery.ValidateRequestAsync(
                        invocationContext.HttpContext);
                }
                catch (AntiforgeryValidationException)
                {
                    return Results.Json(
                        new { error = FailureMessage },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                return await next(invocationContext);
            });
    }
}
