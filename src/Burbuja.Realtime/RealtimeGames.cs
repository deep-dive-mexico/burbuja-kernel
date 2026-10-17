using Burbuja.Realtime.Abstractions;

namespace Burbuja.Realtime;

/// <summary>Every registered <see cref="IRealtimeGame"/>, validated once: ids, uniqueness and rules.</summary>
public sealed class RealtimeGames
{
    private readonly Dictionary<string, IRealtimeGame> _byId = new(StringComparer.Ordinal);

    public RealtimeGames(IEnumerable<IRealtimeGame> games)
    {
        var errors = new List<string>();
        foreach (var game in games)
        {
            if (!RealtimeIds.IsValidGameId(game.Id))
            {
                errors.Add($"game '{game.Id}' ({game.GetType().FullName}): id must be one lowercase segment");
                continue;
            }

            if (!_byId.TryAdd(game.Id, game))
                errors.Add($"game '{game.Id}': registered twice ({_byId[game.Id].GetType().FullName}, {game.GetType().FullName})");
            errors.AddRange(game.Rules.Validate().Select(e => $"game '{game.Id}': {e}"));
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("Realtime game registrations are invalid: " + string.Join("; ", errors));
        All = _byId.Values.OrderBy(g => g.Id, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<IRealtimeGame> All { get; }

    public IRealtimeGame? Find(string? id) => id is not null && _byId.TryGetValue(id, out var game) ? game : null;
}
