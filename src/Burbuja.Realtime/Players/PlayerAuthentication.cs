using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Burbuja.Core.Options;
using Burbuja.Realtime.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Burbuja.Realtime.Players;

/// <summary>Asks every <see cref="IPlayerAuthenticator"/> in order; the first identity wins.</summary>
public sealed class PlayerAuthentication(IEnumerable<IPlayerAuthenticator> authenticators)
{
    private readonly IReadOnlyList<IPlayerAuthenticator> _ordered = authenticators.OrderBy(a => a.Order).ToList();

    public async ValueTask<PlayerIdentity?> AuthenticateAsync(HttpContext http)
    {
        var request = new PlayerAuthRequest(
            http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            http.Connection.RemoteIpAddress?.ToString());
        foreach (var authenticator in _ordered)
        {
            if (await authenticator.AuthenticateAsync(request, http.RequestAborted) is { } identity)
                return identity;
        }

        return null;
    }
}

/// <summary>
/// A kernel API key (<c>x-api-key</c> or <c>Authorization: Bearer</c>, the same keys ARM accepts) is a player
/// named after its principal. This is how a trusted backend, a bot or an operator plays.
/// </summary>
public sealed class ApiKeyPlayerAuthenticator(IOptions<AuthOptions> auth) : IPlayerAuthenticator
{
    public int Order => 100;

    public ValueTask<PlayerIdentity?> AuthenticateAsync(PlayerAuthRequest request, CancellationToken ct) =>
        ValueTask.FromResult(Match(request) is { } entry ? new PlayerIdentity(entry.PrincipalId, entry.PrincipalId) : null);

    /// <summary>The configured key entry the request presents, compared in fixed time.</summary>
    public ApiKeyEntry? Match(PlayerAuthRequest request)
    {
        var key = request.Header("x-api-key");
        if (key is null && request.Header("Authorization") is { } authz
            && authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            key = authz["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(key))
            return null;

        var provided = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        ApiKeyEntry? match = null;
        foreach (var candidate in auth.Value.ApiKeys)
        {
            if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Key)), provided))
                match ??= candidate;
        }

        return match;
    }
}

/// <summary>Accepts the <c>x-player-token</c> a guest was issued by <c>POST /realtime/v1/guests</c>.</summary>
public sealed class GuestPlayerAuthenticator(GuestTokens tokens, IOptions<RealtimeOptions> options) : IPlayerAuthenticator
{
    public const string Header = "x-player-token";

    public int Order => 200;

    public ValueTask<PlayerIdentity?> AuthenticateAsync(PlayerAuthRequest request, CancellationToken ct) =>
        ValueTask.FromResult(options.Value.Guests.Enabled && request.Header(Header) is { } token ? tokens.Validate(token) : null);
}

/// <summary>
/// Signed guest identities: <c>base64url(payload).base64url(HMAC-SHA256)</c> over a player id, a display
/// name and an expiry. Stateless, so any node holding the same key accepts them.
/// </summary>
public sealed class GuestTokens
{
    public const int MaxDisplayNameLength = 24;

    private readonly byte[] _key;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _time;

    public GuestTokens(IOptions<RealtimeOptions> options, TimeProvider time)
    {
        var guests = options.Value.Guests;
        _key = string.IsNullOrEmpty(guests.SigningKey)
            ? RandomNumberGenerator.GetBytes(32)
            : SHA256.HashData(Encoding.UTF8.GetBytes(guests.SigningKey));
        _lifetime = TimeSpan.FromMinutes(Math.Max(1, guests.TokenLifetimeMinutes));
        _time = time;
    }

    /// <summary>A trimmed, printable display name of 1..24 characters, or null.</summary>
    public static string? CleanDisplayName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxDisplayNameLength
            || trimmed.Any(c => char.IsControl(c) || char.IsSurrogate(c)))
            return null;
        return trimmed;
    }

    public (PlayerIdentity Identity, string Token, DateTimeOffset ExpiresAt) Issue(string displayName)
    {
        var expires = _time.GetUtcNow() + _lifetime;
        var identity = new PlayerIdentity(
            "guest-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), displayName, null, IsGuest: true);
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new GuestClaims(identity.PlayerId, displayName, expires.ToUnixTimeSeconds())));
        return (identity, payload + "." + Encode(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload))), expires);
    }

    public PlayerIdentity? Validate(string token)
    {
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot == token.Length - 1 || token.Length > 1024)
            return null;
        var payload = token[..dot];
        try
        {
            var signature = Decode(token[(dot + 1)..]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes(payload))))
                return null;
            var claims = JsonSerializer.Deserialize<GuestClaims>(Decode(payload));
            if (claims is null || claims.E < _time.GetUtcNow().ToUnixTimeSeconds() || CleanDisplayName(claims.N) is null
                || string.IsNullOrEmpty(claims.P))
                return null;
            return new PlayerIdentity(claims.P, claims.N, null, IsGuest: true);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    private sealed record GuestClaims(string P, string N, long E);
}
