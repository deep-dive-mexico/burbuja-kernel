using Burbuja.Realtime.Abstractions;

namespace Burbuja.Realtime.Matchmaking;

/// <summary>
/// The default grouping: oldest ticket first, each with a skill window that widens the longer it waits.
/// A ticket first backfills the fullest compatible lobby (so matches fill and start), otherwise it anchors a
/// new group of compatible tickets, which becomes a lobby once it reaches the game's minimum. Regions are
/// compatible when equal or when either side has no preference.
/// </summary>
public sealed class SkillWindowMatchmaker(MatchmakingOptions options) : IMatchmakingStrategy
{
    public IReadOnlyList<MatchDecision> Decide(MatchmakingPool pool)
    {
        var decisions = new List<MatchDecision>();
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var open = pool.Backfill.ToDictionary(c => c.LobbyId, c => c.OpenSeats, StringComparer.Ordinal);
        var backfills = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var ordered = pool.Tickets.OrderBy(t => t.EnqueuedAt).ThenBy(t => t.TicketId, StringComparer.Ordinal).ToList();

        foreach (var ticket in ordered)
        {
            if (assigned.Contains(ticket.TicketId))
                continue;
            var window = Window(ticket, pool.Now);

            var lobby = pool.Backfill
                .Where(c => open[c.LobbyId] > 0 && Compatible(ticket.Region, c.Region)
                    && Math.Abs(c.AverageSkill - ticket.Skill) <= window)
                .OrderBy(c => open[c.LobbyId])
                .ThenBy(c => Math.Abs(c.AverageSkill - ticket.Skill))
                .FirstOrDefault();
            if (lobby is not null)
            {
                open[lobby.LobbyId]--;
                assigned.Add(ticket.TicketId);
                if (!backfills.TryGetValue(lobby.LobbyId, out var list))
                    backfills[lobby.LobbyId] = list = [];
                list.Add(ticket.TicketId);
                continue;
            }

            var region = ticket.Region;
            var group = new List<MatchTicket> { ticket };
            foreach (var other in ordered)
            {
                if (group.Count >= pool.Rules.MaxPlayers)
                    break;
                if (other == ticket || assigned.Contains(other.TicketId) || !Compatible(region, other.Region)
                    || Math.Abs(other.Skill - ticket.Skill) > Math.Min(window, Window(other, pool.Now)))
                    continue;
                region ??= other.Region;
                group.Add(other);
            }

            if (group.Count < pool.Rules.MinPlayers)
                continue;
            foreach (var member in group)
                assigned.Add(member.TicketId);
            decisions.Add(new NewLobbyDecision(region, group.Select(t => t.TicketId).ToList()));
        }

        decisions.InsertRange(0, backfills.Select(b => new BackfillDecision(b.Key, b.Value)));
        return decisions;
    }

    private double Window(MatchTicket ticket, DateTimeOffset now)
    {
        var waited = Math.Max(0, (now - ticket.EnqueuedAt).TotalSeconds);
        return Math.Min(options.MaxSkillWindow, options.SkillWindow + options.SkillWindowGrowthPerSecond * waited);
    }

    private static bool Compatible(string? a, string? b) =>
        a is null || b is null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
