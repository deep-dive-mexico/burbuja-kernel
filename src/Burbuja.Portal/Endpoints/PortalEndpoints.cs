using Burbuja.Portal.Services;

namespace Burbuja.Portal.Endpoints;

/// <summary>
/// The portal's own routes. Sign-in and sign-out are endpoint POSTs, never circuit callbacks: a
/// cookie can only be written on an HTTP response, and the sign-in form is a plain HTML form so it
/// works before any circuit exists.
///
/// <para><b>M16 — two doors, one handler.</b> Each <see cref="PortalScope"/> gets its own sign-in and
/// sign-out path and its own copy of the same handler: the password check is shared, and the only thing
/// that differs is which portal the door belongs to. After the credential is accepted, a user whose
/// record does not list that portal is refused — the check the two-door decision exists for.</para>
///
/// <para><b>The 301 map</b> is here, and only here, because a moved route without a redirect is a dead
/// link somebody bookmarked. It is anonymous on purpose: an old URL is answered before anybody signs in,
/// so the redirect works for a caller whose session has long expired.</para>
/// </summary>
public static class PortalEndpoints
{
    /// <summary>The kernel door's paths, kept as constants because Host tests and docs cite them.</summary>
    public const string SignIn = "/portal/signin";
    public const string SignOut = "/portal/signout";
    public const string Download = "/portal/download";

    public static void Map(WebApplication app)
    {
        // Antiforgery is deliberately disabled on these two: the session cookie is HttpOnly and
        // SameSite=Lax, and signing in mints a session for whoever presents the credential — the request
        // carries it itself, so there is no ambient authority to forge. Sign-out carries no authority
        // beyond dropping the caller's own session. Both are asserted by the Host tests, including the
        // "bad credentials re-render with the error" path.
        //
        // M12 R2: the credential is now a USERNAME AND PASSWORD from Auth:Users, not a raw API key. The
        // form no longer accepts a key at all — a key is a machine credential, and a machine does not type
        // into a login page. See PortalUserValidator for the join and why a Service key is refused.
        var registry = app.Services.GetRequiredService<PortalRegistry>();
        app.Use(async (http, next) =>
        {
            var scope = registry.Of(http.Request.Path);
            if (scope.Contains(http.Request.Path))
            {
                // Authenticated markup is never a reusable browser/back-cache response.
                http.Response.Headers.CacheControl = "no-store, no-cache";
                http.Response.Headers.Pragma = "no-cache";
            }
            if (HttpMethods.IsGet(http.Request.Method) && scope.IsSignIn(http.Request.Path))
            {
                var store = http.RequestServices.GetRequiredService<PortalSessionStore>();
                var session = store.Get(http.User.FindFirst(PortalAuth.SessionClaim)?.Value);
                var destination = PortalSignInNavigation.Destination(scope, session, http.Request.Query["returnUrl"], http.Request.Query["error"]);
                if (destination is not null)
                {
                    http.Response.Redirect(destination);
                    return;
                }
            }
            await next(http);
            if (HttpMethods.IsGet(http.Request.Method) && http.Response.StatusCode == 404 && !http.Response.HasStarted
                && scope.Contains(http.Request.Path) && !scope.IsSignIn(http.Request.Path)
                && !http.Request.Path.StartsWithSegments(scope.NotFoundPath))
            {
                http.Response.Redirect(scope.NotFoundPath + "?path=" + Uri.EscapeDataString(http.Request.Path));
            }
        });
        foreach (var scope in registry.All)
        {
            app.MapPost(scope.SignInPath, (HttpContext http, PortalUserValidator users, PortalSessionStore sessions,
                    PortalOptions options, ILoggerFactory logger) =>
                    SignInAsync(scope, http, users, sessions, options, logger))
                .DisableAntiforgery();
            app.MapPost(scope.SignOutPath, (HttpContext http, PortalSessionStore sessions, PortalOptions options) =>
                    SignOutAsync(scope, http, sessions, options))
                .DisableAntiforgery();
            // A bookmarked auth action is navigation, never an implicit logout.
            app.MapGet(scope.SignOutPath, () => Results.Redirect(scope.Home));
        }

        // The old URLs. `Results.Redirect(..., permanent: true)` is a 301, and HttpClient (which G43's
        // route check uses) follows it, so a bookmark and a test both land on the new page. The subtree
        // entries are catch-alls, including the old prefix itself as well as its descendants.
        foreach (var (old, moved, prefix) in registry.All.SelectMany(s => s.Redirects))
        {
            var destination = moved;
            if (prefix)
            {
                app.MapGet(old + "/{**rest}", (HttpContext http, string? rest) => Results.Redirect(
                    destination
                    + (string.IsNullOrEmpty(rest) ? string.Empty : "/" + rest)
                    + http.Request.QueryString,
                    permanent: true));
            }
            else
            {
                app.MapGet(old, (HttpContext http) => Results.Redirect(
                    destination + http.Request.QueryString, permanent: true));
            }
        }

        app.MapGet(Download, DownloadAsync).RequireAuthorization();
    }

    private static async Task<IResult> SignInAsync(
        PortalScope scope,
        HttpContext http,
        PortalUserValidator users,
        PortalSessionStore sessions,
        PortalOptions options,
        ILoggerFactory logger)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        var username = form["username"].ToString();
        var password = form["password"].ToString();
        var returnUrl = SafeReturn(scope, form["returnUrl"].ToString());
        var existingId = http.Request.Cookies[options.CookieName];
        if (PortalSignInNavigation.Destination(scope, sessions.Get(existingId), returnUrl, null) is { } existingDestination)
            return Results.Redirect(existingDestination);
        var user = users.Validate(username, password);
        if (user is null)
        {
            // The username and the remote address, never the password and never the hash. Warning rather
            // than Information because a run of these is the only signal a console gives that somebody is
            // guessing; there is deliberately no rate limiter (the browser lane performs 25+ serial
            // sign-ins and a limiter would make the lane unusable), so the log is the whole detection story.
            logger.CreateLogger("Burbuja.Portal.SignIn").LogWarning(
                "portal sign-in refused for '{Username}' from {RemoteIp}",
                username,
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

            var query = "?error=badcredentials";
            if (returnUrl is not null)
                query += "&returnUrl=" + Uri.EscapeDataString(returnUrl);
            return Results.Redirect(scope.SignInPath + query);
        }

        // M16 — the credential is good and the DOOR is still refused. A client-portal user has no business
        // in the operator console, and this is where that is enforced rather than merely hidden: the
        // session's `portal` claims are what the policies read, so without this check the sign-in would
        // mint a session for a console its user cannot open. Warning, not Information: a run of these is
        // somebody trying doors, and it is the only signal there is.
        if (!user.HasPortal(scope.Id))
        {
            logger.CreateLogger("Burbuja.Portal.SignIn").LogWarning(
                "portal sign-in refused for '{Username}' from {RemoteIp}: the account does not list the '{Portal}' portal",
                username,
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                scope.Id);
            return Results.Redirect(scope.SignInPath + "?error=noaccess");
        }

        sessions.Remove(existingId);
        var session = sessions.Create(user.Key, user.PrincipalId, user.Roles, user.Username, user.Portals);
        http.Response.Cookies.Append(options.CookieName, session.Id, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            // Local development runs over http; a TLS deployment gets Secure automatically.
            Secure = http.Request.IsHttps,
            // ONE cookie for both doors, at the root: a path-scoped cookie is not sent to the hub's own
            // endpoint, and two cookies at one path would give `/_blazor` two identities to choose from.
            Path = "/",
            MaxAge = options.SessionLifetime,
        });
        return Results.Redirect(returnUrl ?? scope.Prefix);
    }

    private static async Task<IResult> SignOutAsync(
        PortalScope scope,
        HttpContext http,
        PortalSessionStore sessions,
        PortalOptions options)
    {
        if (http.Request.Cookies.TryGetValue(options.CookieName, out var id))
            sessions.Remove(id);
        http.Response.Cookies.Delete(options.CookieName, new CookieOptions { Path = "/", Secure = http.Request.IsHttps });
        // Sign-out is per door and the session is one: ending it from either portal ends both, which is
        // the documented cost of one cookie and one person.
        return Results.Redirect(scope.SignInPath);
    }

    /// <summary>
    /// The browser cannot send <c>x-api-key</c>, so an artifact download is proxied through the
    /// session. The path is validated as a local ARM path: a request must never be able to point
    /// the Host's own client at another origin.
    /// </summary>
    private static async Task<IResult> DownloadAsync(
        HttpContext http,
        IPortalArm arm,
        string? path)
    {
        if (!IsLocalPath(path))
            return Results.BadRequest(new { error = new { code = "InvalidPath", message = "path must be a local ARM path." } });

        var download = await arm.DownloadAsync(path!, http.RequestAborted);
        if (download is null)
            return Results.NotFound(new { error = new { code = "NotFound", message = $"No data-plane content for '{path}'." } });
        if (download.Error is not null)
        {
            var status = download.Status is >= 400 and < 600 ? download.Status : 502;
            download.Dispose();
            return Results.Json(new { error = new { code = "UpstreamFailed", message = download.Error } }, statusCode: status);
        }

        return Results.Stream(download.Content!, download.ContentType ?? "application/octet-stream", download.FileName);
    }

    internal static bool IsLocalPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path.StartsWith('/')
        && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains("..", StringComparison.Ordinal)
        && !path.Contains("://", StringComparison.Ordinal);

    /// <summary>
    /// Only a return URL belonging to the door that was used is honoured; anything else falls back to that
    /// portal's home. Segment boundaries and local-path validation live in <see cref="PortalScope.AcceptsReturn"/>.
    /// </summary>
    internal static string? SafeReturn(PortalScope scope, string? value) =>
        scope.AcceptsReturn(value) ? value : null;
}
