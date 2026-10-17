using System.Text.Json.Serialization;

namespace Burbuja.Realtime.Abstractions;

/// <summary>Who a caller is, as an <see cref="IPlayerAuthenticator"/> established it.</summary>
/// <param name="PlayerId">Stable for this player across requests; the key for "one ticket, one lobby".</param>
/// <param name="Attributes">Server-asserted facts (a rating, a cosmetic tier). The kernel reads
/// <see cref="PlayerAttributes.Skill"/> for matchmaking; a client never sets these directly.</param>
public sealed record PlayerIdentity(
    string PlayerId,
    string DisplayName,
    IReadOnlyDictionary<string, string>? Attributes = null,
    bool IsGuest = false)
{
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> AttributesOrEmpty =>
        Attributes ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
}

/// <summary>A seated player as a session sees them. <paramref name="Slot"/> is stable for the seat's
/// lifetime and unique in the lobby (0-based), handy for colours or spawn points.</summary>
public sealed record RealtimePlayer(
    string PlayerId,
    string DisplayName,
    int Slot,
    IReadOnlyDictionary<string, string> Attributes);

public static class PlayerAttributes
{
    /// <summary>An integer matchmaking rating. Missing or unparsable means <see cref="DefaultSkill"/>.</summary>
    public const string Skill = "skill";

    public const int DefaultSkill = 1000;

    public static int SkillOf(IReadOnlyDictionary<string, string> attributes) =>
        attributes.TryGetValue(Skill, out var raw) && int.TryParse(raw, out var skill) ? skill : DefaultSkill;
}

/// <summary>
/// Turns a request into a player. The kernel asks every registered authenticator in ascending
/// <see cref="Order"/> and takes the first identity. Returning null means "not mine", not "refused": the
/// next authenticator still gets a turn, and nobody claiming it is a 401.
/// </summary>
public interface IPlayerAuthenticator
{
    int Order { get; }

    ValueTask<PlayerIdentity?> AuthenticateAsync(PlayerAuthRequest request, CancellationToken ct);
}

/// <summary>The transport-neutral part of a request an authenticator may read.</summary>
public sealed record PlayerAuthRequest(
    IReadOnlyDictionary<string, string> Headers,
    string? RemoteAddress)
{
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var value)
            ? value
            : Headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
