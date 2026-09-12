namespace Geopolitics.Api;

/// <summary>
/// Response headers that constrain what a browser will do with this application's output.
/// <para>
/// The dashboard renders text fetched from public news feeds and text posted by anyone who can reach
/// the submission endpoint. Every render path escapes that text, and that is what actually prevents
/// injection; these headers are the layer that limits the damage if one of those paths is ever
/// written incorrectly. They are cheap, and the failure they guard against is the expensive kind.
/// </para>
/// <para>
/// The content policy itself is not here. It lives in a <c>meta</c> element in <c>index.html</c>,
/// because the published dashboard is served by GitHub Pages, which serves files and sets no headers
/// of its own — a policy expressed only here would protect the locally hosted page and leave the
/// public one unprotected. What remains below is the set that a <c>meta</c> element cannot express.
/// </para>
/// </summary>
public static class SecurityHeaders
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;

            // Nothing this application serves should ever be interpreted as a type other than the one
            // it declares. Chiefly this stops a JSON response full of feed text being sniffed as HTML.
            headers["X-Content-Type-Options"] = "nosniff";

            // frame-ancestors is ignored inside a meta element, so framing can only be refused here.
            // X-Frame-Options is sent alongside it for browsers that honour only the older header.
            headers["Content-Security-Policy"] = "frame-ancestors 'none'";
            headers["X-Frame-Options"] = "DENY";

            // Incident titles and place names end up in the URL as the dashboard is navigated, and
            // those should not be handed to the third-party hosts the globe fetches imagery from.
            headers["Referrer-Policy"] = "no-referrer";

            // This dashboard asks for none of these, so refusing them outright means a script that
            // somehow ran here still could not.
            headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=(), payment=()";

            await next(context);
        });
    }
}
