using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Mcp.Abstractions;
using Microsoft.Extensions.Options;

namespace Burbuja.Mcp;

/// <summary>
/// K1: what the kernel knows about one MCP connection.
///
/// <para><b>A transport session is not an agent session.</b> The agent's session is a durable conversation log
/// that outlives any connection and is where the tool calls are audited; this is the connection itself,
/// minted at <c>initialize</c>, echoed in <c>Mcp-Session-Id</c>, and gone when the client deletes it or stops
/// using it. Keeping them apart is what lets the kernel answer two different questions — "what did this agent
/// do" and "who is connected right now" — without one pretending to be the other.</para>
/// </summary>
/// <param name="SessionId">The transport's own id, minted by the SDK at <c>initialize</c>.</param>
/// <param name="PrincipalId">Who is on the other end. A key can serve many sessions, so the session names the
/// principal rather than the other way round.</param>
/// <param name="AgentName">The agent the client acts as, from <c>x-burbuja-agent</c>, when it named one.</param>
/// <param name="ProtocolVersion">The revision the client and server agreed, as the SDK reports it.</param>
/// <param name="CatalogueFingerprint">The tool catalogue this session was last shown; the comparison that
/// makes <c>tools/list_changed</c> a fact rather than a hope.</param>
public sealed record McpSessionRecord(
    string SessionId,
    string PrincipalId,
    string? AgentName,
    string? ProtocolVersion,
    string? CatalogueFingerprint,
    DateTimeOffset OpenedAt,
    DateTimeOffset LastSeenAt,
    int Requests);

/// <summary>What comparing a catalogue to the session's last one produced.</summary>
public enum CatalogueChange
{
    /// <summary>This session has no fingerprint yet: the first listing, and there is nothing to announce.</summary>
    First,

    /// <summary>Same tools, same descriptions.</summary>
    Unchanged,

    /// <summary>The catalogue moved: the client is holding a stale list.</summary>
    Changed,
}

/// <summary>
/// The kernel's own table of live MCP sessions, beside the SDK's.
///
/// <para><b>Why the kernel keeps its own.</b> The SDK owns the transport's sessions — it mints the id, routes
/// by header, expires idle ones and answers <c>DELETE</c>. What it cannot answer is the kernel's question:
/// *which agent is connected, since when, on what protocol revision, and has it been told that its tools
/// changed*. That needs a record with the caller on it, and it needs reaping rules the kernel can state:
/// an idle timeout and a ceiling, both configuration, both testable with a clock.</para>
///
/// <para><b>Bounded on purpose.</b> Past <see cref="McpServerOptions.MaxIdleSessions"/> the oldest session is
/// dropped even if it is not idle, mirroring the SDK's own rule for its table. A client that never says
/// goodbye must not be able to grow this without bound, and the failure mode of the cap (an old connection
/// losing its bookkeeping) is smaller than the failure mode of no cap.</para>
/// </summary>
public sealed class McpSessionRegistry(IClock clock, IOptions<McpServerOptions> options)
{
    private readonly ConcurrentDictionary<string, McpSessionRecord> _sessions = new(StringComparer.Ordinal);
    private McpServerOptions Options => options.Value;

    public int Count => _sessions.Count;

    public IReadOnlyList<McpSessionRecord> Snapshot() =>
        [.. _sessions.Values.OrderBy(s => s.OpenedAt).ThenBy(s => s.SessionId, StringComparer.Ordinal)];

    public bool TryGet(string sessionId, out McpSessionRecord record) => _sessions.TryGetValue(sessionId, out record!);

    /// <summary>
    /// The first request on a session opens its record; every later one moves <c>LastSeenAt</c> and counts.
    /// Returns the record as it was BEFORE this request, so a caller can tell a first sighting from a repeat
    /// without a second lookup.
    /// </summary>
    public (McpSessionRecord Record, bool Opened) Seen(string sessionId, McpCaller caller, string? protocolVersion = null)
    {
        var now = clock.UtcNow;
        var fresh = new McpSessionRecord(
            sessionId,
            caller.Principal.PrincipalId,
            caller.AgentName,
            protocolVersion,
            null,
            now,
            now,
            1);

        McpSessionRecord? previous = null;
        var record = _sessions.AddOrUpdate(
            sessionId,
            fresh,
            (_, existing) =>
            {
                previous = existing;
                return existing with { LastSeenAt = now, Requests = existing.Requests + 1 };
            });

        if (_sessions.Count > Options.MaxIdleSessions)
            Reap(force: true);

        return (record, previous is null);
    }

    /// <summary>Remember what this session was shown, and say whether it moved.</summary>
    public CatalogueChange RememberCatalogue(string sessionId, string fingerprint)
    {
        if (!_sessions.TryGetValue(sessionId, out var existing) || existing.CatalogueFingerprint is null)
        {
            if (existing is not null)
                _sessions[sessionId] = existing with { CatalogueFingerprint = fingerprint };
            return CatalogueChange.First;
        }

        if (string.Equals(existing.CatalogueFingerprint, fingerprint, StringComparison.Ordinal))
            return CatalogueChange.Unchanged;

        _sessions[sessionId] = existing with { CatalogueFingerprint = fingerprint };
        return CatalogueChange.Changed;
    }

    /// <summary>The SDK answered <c>DELETE</c>, or the kernel is dropping a session it no longer tracks.</summary>
    public bool Close(string sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>
    /// Drop what nobody is using: sessions idle past <see cref="McpServerOptions.SessionIdleTimeout"/> first,
    /// then — when <paramref name="force"/> is set, which is what the ceiling does — oldest first until the
    /// table is under <see cref="McpServerOptions.MaxIdleSessions"/>. Returns how many went.
    /// </summary>
    public int Reap(bool force = false)
    {
        var now = clock.UtcNow;
        var dropped = 0;

        foreach (var session in Snapshot())
        {
            if (now - session.LastSeenAt < Options.SessionIdleTimeout)
                continue;
            if (Close(session.SessionId))
                dropped++;
        }

        if (!force)
            return dropped;

        foreach (var session in Snapshot())
        {
            if (_sessions.Count <= Options.MaxIdleSessions)
                break;
            if (Close(session.SessionId))
                dropped++;
        }

        return dropped;
    }
}

/// <summary>
/// The fingerprint a session's catalogue is remembered by.
///
/// <para><b>Names AND descriptions.</b> A tool whose description changed is a tool an agent should re-read —
/// the description is what a model decides from, so a catalogue that only hashed names would call a
/// rewritten tool "unchanged". The schema is left out on purpose: it is large, it is stable in practice, and
/// hashing it would make the fingerprint expensive to compute on every listing while changing the answer
/// almost never.</para>
/// </summary>
public static class McpCatalogue
{
    public static string Fingerprint(IEnumerable<McpToolExport> tools)
    {
        var canonical = string.Join(
            "\n",
            tools
                .OrderBy(t => t.ToolName, StringComparer.Ordinal)
                .Select(t => $"{t.ToolName}\u001f{t.Description}"));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
