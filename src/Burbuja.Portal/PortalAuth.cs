using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Burbuja.Portal;

/// <summary>The wire facts of the portal's own authentication.</summary>
public static class PortalAuth
{
    /// <summary>The cookie scheme. It is not "Cookies": the portal owns its ticket and its store.</summary>
    public const string Scheme = "Burbuja.Portal";

    /// <summary>The only claim the cookie ticket carries: the session id.</summary>
    public const string SessionClaim = "portal_session";

    /// <summary>Role claim, rebuilt per request from the session store — never from the cookie.</summary>
    public const string RoleClaim = ClaimTypes.Role;

    /// <summary>
    /// M16 — one claim per portal the session may open, rebuilt from the store on every request exactly
    /// like the roles. The value is a registered <see cref="PortalScope.Id"/>; its route policy requires it.
    /// </summary>
    public const string PortalClaim = "portal";

    /// <summary>
    /// What a record with no <c>Portals</c> configured may open: the kernel portal only. This is why M16
    /// needs no migration — every <c>Auth:Users</c> row written before the field existed keeps working
    /// against the same console it always opened.
    /// </summary>
    public static readonly IReadOnlyList<string> KernelOnly = [PortalScope.Kernel.Id];

    /// <summary>
    /// The portals a configured list names, normalized: entries are trimmed, blank entries are dropped,
    /// duplicates collapse case-insensitively, and an absent or empty list means
    /// <see cref="KernelOnly"/>. Never returns null, so no caller has to decide what "no portals" means.
    /// </summary>
    public static IReadOnlyList<string> PortalsOf(IEnumerable<string?>? configured)
    {
        var portals = configured?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        return portals.Count == 0 ? KernelOnly : portals;
    }
}

/// <summary>
/// One live login: the API key stays here, in this process, for the lifetime of the session. The
/// cookie only ever carries <see cref="Id"/>.
/// </summary>
/// <param name="Username">
/// M12 R2 — the NAME the person typed, which is not the same thing as <see cref="PrincipalId"/>. The
/// principal is the kernel's audit identity (it is what <c>systemData.createdBy</c> and the activity ring
/// show, and a production deployment may point several logins at one principal); the username is how the
/// shell addresses the human. Both are carried because the shell needs one and the audit needs the other,
/// and deriving either from the other is how the two quietly stop matching.
/// </param>
public sealed record PortalSession(
    string Id,
    string ApiKey,
    string PrincipalId,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string Username = "")
{
    /// <summary>
    /// M16 — the portals this session may open, from the user record's <c>Portals</c>. An init-only
    /// property with a default rather than an eighth positional parameter: every construction site that
    /// predates M16 (and every unit fake) keeps its shape, and the default is the one the absent case
    /// needs — kernel only.
    /// </summary>
    public IReadOnlyList<string> Portals { get; init; } = PortalAuth.KernelOnly;

    /// <summary>Whether this session may open the given portal — the check a sign-in performs.</summary>
    public bool HasPortal(string id) => Portals.Contains(id, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A server-side, in-memory session table.
///
/// In-memory is deliberate for M5.5: the portal runs inside the single Host process, and the key
/// it holds is the key the browser presented, so persisting it would turn a browser cookie into a
/// long-lived credential on disk. The cost is that a Host restart signs everyone out (and the
/// session is not shared across replicas) — acceptable while the kernel is one process, and the
/// place to swap for a durable, encrypted store if the portal ever runs multi-instance.
/// </summary>
public sealed class PortalSessionStore(TimeProvider clock, PortalOptions options)
{
    private readonly ConcurrentDictionary<string, PortalSession> _sessions = new(StringComparer.Ordinal);

    public int Count => _sessions.Count;
    public event Action<string>? SessionEnded;
    /// <summary>Revalidation checks liveness without extending an idle session.</summary>
    public bool IsLive(string? id) => id is not null && _sessions.TryGetValue(id, out var session) && session.ExpiresAt > clock.GetUtcNow();

    public PortalSession Create(
        string apiKey,
        string principalId,
        IReadOnlyList<string> roles,
        string? username = null,
        IReadOnlyList<string>? portals = null)
    {
        var now = clock.GetUtcNow();
        var session = new PortalSession(
            Guid.NewGuid().ToString("N"),
            apiKey,
            principalId,
            roles,
            now,
            now + options.SessionLifetime,
            // M12 R2: an API-key sign-in has no name to show, so the principal stands in for it — which is
            // what the chip showed before usernames existed, and is still the right answer for a machine.
            username ?? principalId)
        {
            // M16: absent means kernel, which is what every caller that predates the portals opens.
            Portals = PortalAuth.PortalsOf(portals),
        };
        _sessions[session.Id] = session;
        Sweep();
        return session;
    }

    /// <summary>
    /// A cookie without a live session is anonymous: an expired or unknown id returns null and the
    /// caller never learns whether it once existed.
    /// </summary>
    public PortalSession? Get(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        while (_sessions.TryGetValue(id, out var session))
        {
            var now = clock.GetUtcNow();
            if (session.ExpiresAt <= now)
            {
                Remove(id);
                return null;
            }
            var slid = session with { ExpiresAt = now + options.SessionLifetime };
            // A concurrent logout must not be undone by this sliding read.
            if (_sessions.TryUpdate(id, slid, session)) return slid;
        }
        return null;
    }

    public bool Remove(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_sessions.TryRemove(id, out _)) return false;
        SessionEnded?.Invoke(id);
        return true;
    }

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (id, session) in _sessions)
        {
            if (session.ExpiresAt <= now)
                Remove(id);
        }
    }
}

/// <summary>
/// Validates a presented key against <c>Auth:ApiKeys</c> in fixed time, exactly like
/// <c>Burbuja.Core/Arm/Middleware.cs</c>: every candidate is compared as a SHA-256 digest with
/// <see cref="CryptographicOperations.FixedTimeEquals"/>, and the loop never breaks early, so the
/// response time does not reveal which entry (if any) matched.
///
/// <para><b>M12 R2: this is no longer the portal's sign-in path.</b> A human signs in with a username and
/// a password through <see cref="PortalUserValidator"/>, which JOINS the user to one of these entries and
/// uses that entry's key. This class stays because the entry list is what the join reads — and because the
/// keys themselves are unchanged: they remain the machine credential for ARM and MCP.</para>
/// </summary>
public sealed class PortalKeyValidator
{
    private readonly IReadOnlyList<PortalKeyEntry> _keys;

    public PortalKeyValidator(IConfiguration configuration) =>
        _keys = Read(configuration);

    /// <summary>Every configured entry, in configuration order. Read by the user validator's join.</summary>
    internal IReadOnlyList<PortalKeyEntry> Entries => _keys;

    /// <summary>The matching entry, or null. Never throws and never logs the key.</summary>
    public PortalKeyEntry? Validate(string? provided)
    {
        PortalKeyEntry? match = null;
        foreach (var candidate in _keys)
        {
            if (FixedTimeKeyEquals(candidate.Key, provided) && match is null)
                match = candidate;
        }

        return string.IsNullOrEmpty(provided) ? null : match;
    }

    internal static bool FixedTimeKeyEquals(string configured, string? provided)
    {
        if (provided is null)
            return false;
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static IReadOnlyList<PortalKeyEntry> Read(IConfiguration configuration)
    {
        var entries = new List<PortalKeyEntry>();
        foreach (var section in configuration.GetSection("Auth:ApiKeys").GetChildren())
        {
            var key = section["Key"];
            if (string.IsNullOrEmpty(key))
                continue;
            var roles = new List<string>();
            foreach (var role in section.GetSection("Roles").GetChildren())
            {
                if (!string.IsNullOrEmpty(role.Value))
                    roles.Add(role.Value);
            }

            entries.Add(new PortalKeyEntry(
                key,
                section["PrincipalId"] ?? "unknown",
                roles,
                // M12 R2: absent means User, which is what every entry in this repo's configs was before
                // the field existed. A Service has to say so.
                section["PrincipalType"] ?? "User"));
        }

        return entries;
    }
}

/// <summary>
/// One entry of <c>Auth:ApiKeys</c>. <see cref="PrincipalType"/> is M12 R2's addition and it is
/// load-bearing: a password may never front a <c>Service</c> key (see <see cref="PortalUserValidator"/>).
/// </summary>
public sealed record PortalKeyEntry(string Key, string PrincipalId, IReadOnlyList<string> Roles, string PrincipalType = "User");

/// <summary>
/// M12 R2 — a human login, resolved. It is a <see cref="PortalKeyEntry"/> plus the name the person typed,
/// because the two identities are genuinely different: <see cref="PrincipalId"/> is what the kernel's audit
/// and RBAC see (the key's principal), and <see cref="Username"/> is what the shell shows the human.
/// </summary>
public sealed record PortalUser(string Username, string Key, string PrincipalId, IReadOnlyList<string> Roles)
{
    /// <summary>
    /// M16 — the portals this login may open, read from its <c>Auth:Users</c> row. The sign-in endpoint
    /// refuses a user whose list does not contain the door they came through; the session carries the list
    /// so the document and the shell can answer "which portal is this" without asking again.
    /// </summary>
    public IReadOnlyList<string> Portals { get; init; } = PortalAuth.KernelOnly;

    /// <summary>Whether this login may open the given portal.</summary>
    public bool HasPortal(string id) => Portals.Contains(id, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// M12 R2 — the portal's sign-in: a username and a password from <c>Auth:Users</c>, joined to the
/// <c>Auth:ApiKeys</c> entry that carries the same principal.
///
/// <para><b>Why a JOIN rather than roles on the user record.</b> Every portal read and write goes to the
/// kernel as the session's KEY, and the kernel decides with that KEY's roles. If <c>Auth:Users</c> carried
/// its own roles, the shell would render controls the kernel then refuses — a page that looks like it can
/// do something and cannot, which is the exact defect M11 S2 spent a round removing. Joining on
/// <c>PrincipalId</c> means RBAC, the kernel's <c>roleDefinitions</c> merge, <c>systemData.createdBy</c>
/// and the activity ring all see ONE identity, because there is only one.</para>
///
/// <para><b>A password can never front a <c>Service</c> key.</b> The keys in <c>Auth:ApiKeys</c> are not
/// all human: pipelines, workspace initializers and protocol adapters can be Services, and one of them
/// holds <c>Owner</c>. Without the <see cref="PortalKeyEntry.PrincipalType"/> check, adding an
/// <c>Auth:Users</c> row pointing at a service principal would turn a password into the service's
/// own credential — a privilege escalation written as a configuration line, which is the kind nobody
/// reviews. Services keep using keys.</para>
///
/// <para><b>The portal reads <see cref="IConfiguration"/>, not the kernel's options type.</b>
/// Burbuja.Portal references only the <c>*.Abstractions</c> projects and <c>Core.Sdk</c>, so
/// <c>AuthOptions</c> is not on its compile path — the same reason <see cref="PortalKeyValidator"/> reads
/// configuration directly.</para>
/// </summary>
public sealed class PortalUserValidator
{
    private readonly PortalKeyValidator _keys;
    private readonly IReadOnlyList<PortalUserRecord> _users;

    public PortalUserValidator(IConfiguration configuration)
        : this(new PortalKeyValidator(configuration), Read(configuration))
    {
    }

    internal PortalUserValidator(PortalKeyValidator keys, IReadOnlyList<PortalUserRecord> users)
    {
        _keys = keys;
        _users = users;
    }

    /// <summary>
    /// The user this username and password authenticate, or null.
    ///
    /// <para>Null for: no such username, a wrong password, a user whose principal has no key, and a user
    /// whose principal's key is a Service. The caller gets ONE answer for all four, which is deliberate —
    /// "no such user" and "wrong password" are the same fact to somebody guessing, and telling them apart
    /// is how a login form becomes a username oracle.</para>
    /// </summary>
    public PortalUser? Validate(string? username, string? password)
    {
        // Exactly one PBKDF2 runs on every path: an unknown username verifies against DummyHash, which is
        // real work with a known-false answer, so the time taken says nothing about whether the user exists.
        // The lookup is ordinal and case-sensitive — a username is an identifier, and "Owner" quietly
        // resolving to "owner" is how two accounts become one.
        var record = _users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.Ordinal));
        var matches = PortalPasswordHasher.Verify(record?.PasswordHash ?? PortalPasswordHasher.DummyHash, password);
        if (record is null || !matches)
            return null;

        var principal = record.PrincipalId ?? record.Username;
        var key = _keys.Entries.FirstOrDefault(k =>
            string.Equals(k.PrincipalId, principal, StringComparison.Ordinal));

        // A Service key is refused even when the principal matches exactly: a password is a human
        // credential, and the pipeline's key is not.
        if (key is null || !string.Equals(key.PrincipalType, "User", StringComparison.OrdinalIgnoreCase))
            return null;

        return new PortalUser(record.Username, key.Key, key.PrincipalId, key.Roles)
        {
            Portals = record.Portals,
        };
    }

    private static IReadOnlyList<PortalUserRecord> Read(IConfiguration configuration)
    {
        var users = new List<PortalUserRecord>();
        foreach (var section in configuration.GetSection("Auth:Users").GetChildren())
        {
            var username = section["Username"];
            var hash = section["PasswordHash"];
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(hash))
                continue;
            users.Add(new PortalUserRecord(username, hash, section["PrincipalId"])
            {
                // M16: absent (or empty) means kernel, so every row written before the field existed keeps
                // opening the console it always did. The seeds that need both portals say so explicitly.
                Portals = PortalAuth.PortalsOf(section.GetSection("Portals").GetChildren().Select(p => p.Value)),
            });
        }

        return users;
    }
}

/// <summary>One <c>Auth:Users</c> row, as configuration declares it.</summary>
public sealed record PortalUserRecord(string Username, string PasswordHash, string? PrincipalId)
{
    /// <summary>
    /// M16 — the portals this row may open, from <c>Auth:Users:N:Portals:*</c>. Absent means
    /// <see cref="PortalAuth.KernelOnly"/>, which is the whole migration story for this field.
    /// </summary>
    public IReadOnlyList<string> Portals { get; init; } = PortalAuth.KernelOnly;
}
