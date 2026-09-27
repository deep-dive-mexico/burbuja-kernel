using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Portal;

/// <summary>
/// The portal's cookie handler. It authenticates by reading the session id out of the cookie and
/// asking <see cref="PortalSessionStore"/> for the live session; the principal — including its
/// roles — is rebuilt from that store on every request, so revoking a session (or a Host restart)
/// immediately makes the cookie anonymous and an expired session can never keep stale roles.
///
/// <para><b>M16 — one cookie, two doors.</b> The session carries the portals its user may open and the
/// handler emits one <c>portal</c> claim per entry, rebuilt from the store like the roles. Both doors
/// write the same cookie at <c>Path=/</c>, because two path-scoped cookies are never sent to
/// <c>/_blazor/negotiate</c> (the M7.0 failure class), and two cookies at one path would give the hub two
/// identities to choose between. One person, one session: signing out from either door ends both, which
/// is documented rather than hidden.</para>
/// </summary>
public sealed class PortalAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemes,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PortalSessionStore sessions,
    PortalOptions portal,
    PortalRegistry registry)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemes, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(portal.CookieName, out var id) || string.IsNullOrEmpty(id))
            return Task.FromResult(AuthenticateResult.NoResult());

        var session = sessions.Get(id);
        if (session is null)
        {
            // A cookie without a live session is treated as anonymous — not as an error, and not
            // as a "stale principal" that could still carry roles.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(PortalAuth.SessionClaim, session.Id),
            // M12 R2: the NAME is the username the person typed, not the principal. The principal is still
            // carried — by the session, which is what every ARM call and the audit read — but a claim named
            // "Name" that renders "owner" for somebody who signed in as "santiago" is a shell telling the
            // wrong person who they are.
            new(ClaimTypes.Name, session.Username),
        };
        foreach (var role in session.Roles)
            claims.Add(new Claim(PortalAuth.RoleClaim, role));
        // M16: one claim per portal. The policies require this claim, so a session whose user record lists
        // no portals (or the wrong one) is authenticated and still refused the page — which is the whole
        // point of gating two consoles with one cookie.
        foreach (var portalId in session.Portals)
        {
            if (registry.ById(portalId) is { } scope)
                claims.Add(new Claim(PortalAuth.PortalClaim, scope.Id));
        }

        var identity = new ClaimsIdentity(claims, PortalAuth.Scheme, ClaimTypes.Name, PortalAuth.RoleClaim);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), PortalAuth.Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>
    /// An anonymous caller goes to the sign-in door of the portal they were trying to open, carrying where
    /// they were going. The return URL is the request minus the sign-in page itself, so a bounce from the
    /// form does not come back with a return URL pointing at the form.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var scope = registry.Of(Request.Path);
        Response.Redirect(scope.SignInPath + Return(scope.SignInPath, scope));
        return Task.CompletedTask;
    }

    /// <summary>
    /// A user without kernel access is sent to an available registered door they can open. Other refusals
    /// stay at the requested door. Return URLs survive only when that door owns the requested path.
    /// </summary>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        var requested = registry.Of(Request.Path);
        var target = requested;
        if (requested.Id == PortalScope.Kernel.Id && !Context.User.HasClaim(PortalAuth.PortalClaim, PortalScope.Kernel.Id))
            target = registry.All.FirstOrDefault(s => Context.User.HasClaim(PortalAuth.PortalClaim, s.Id)) ?? requested;

        var url = target.SignInPath + "?error=noaccess";
        if (target == requested)
            url += Return(target.SignInPath, requested, first: false);
        Response.Redirect(url);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The <c>returnUrl</c> query for a request that was refused or challenged, or empty. It is skipped
    /// when the path IS the sign-in page (a bounce from the form asking for itself) and the whole path and
    /// query are escaped as one value so a return URL cannot inject a second parameter.
    /// </summary>
    private string Return(string signInPath, PortalScope scope, bool first = true)
    {
        var path = Request.Path + Request.QueryString;
        if (!Request.Path.HasValue || Request.Path.Value.Equals(signInPath, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (!scope.AcceptsReturn(Request.Path))
            return string.Empty;
        return (first ? "?" : "&") + "returnUrl=" + Uri.EscapeDataString(path);
    }
}
