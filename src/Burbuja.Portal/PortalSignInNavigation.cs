using Microsoft.AspNetCore.WebUtilities;

namespace Burbuja.Portal;

/// <summary>The shared HTTP/circuit rule for entering a sign-in door with a live session.</summary>
public static class PortalSignInNavigation
{
    public static string? Destination(PortalScope scope, PortalSession? session, string? returnUrl, string? error)
    {
        if (session?.HasPortal(scope.Id) != true) return null;
        var destination = scope.AcceptsReturn(returnUrl) ? returnUrl! : scope.Home;
        // A wrong-door refusal remains explicit after leaving the stale form. Other stale errors do not
        // override a valid session. The destination is always an ordinary page in this portal.
        return error == "noaccess" ? QueryHelpers.AddQueryString(destination, "error", "noaccess") : destination;
    }
}
