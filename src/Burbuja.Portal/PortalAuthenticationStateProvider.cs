using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace Burbuja.Portal;

/// <summary>Revokes every circuit using a signed-out session, and revalidates idle expiry without sliding it.</summary>
public sealed class PortalAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly PortalSessionStore _sessions;
    public PortalAuthenticationStateProvider(ILoggerFactory logger, PortalSessionStore sessions) : base(logger)
    {
        _sessions = sessions;
        sessions.SessionEnded += Ended;
    }
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(5);
    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState state, CancellationToken cancellationToken) =>
        Task.FromResult(_sessions.IsLive(state.User.FindFirst(PortalAuth.SessionClaim)?.Value));

    private void Ended(string id) => _ = RevokeAsync(id);
    private async Task RevokeAsync(string id)
    {
        try
        {
            var state = await GetAuthenticationStateAsync();
            if (state.User.FindFirst(PortalAuth.SessionClaim)?.Value == id)
                SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        }
        catch (InvalidOperationException)
        {
            // This circuit has not received its initial host authentication state yet, or was disposed.
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _sessions.SessionEnded -= Ended;
        base.Dispose(disposing);
    }
}
