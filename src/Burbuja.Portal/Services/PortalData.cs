using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Portal.Services;

/// <summary>Where in the kernel's tenancy a portal page is looking.</summary>
public sealed record ArmScope(string SubscriptionId, string ResourceGroup)
{
    public string AgentsRoot =>
        $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/Burbuja.Agents";

    /// <summary>
    /// The pomnibot namespace: jobs live <em>directly</em> under it (no workspace segment), which is
    /// the one structural difference from <see cref="AgentsRoot"/> the projections have to respect.
    /// </summary>
    public string PomnibotRoot =>
        $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}/providers/Burbuja.Pomnibot";

    public string ResourcesRoot => $"/subscriptions/{SubscriptionId}/providers/Burbuja.Resources";

    /// <summary>
    /// The resource-group root, for a caller that wants an arbitrary namespace under it — the explorer,
    /// which builds `{RootPath}/providers/{ns}/{type}` from the type a provider declared.
    /// </summary>
    public string RootPath => $"/subscriptions/{SubscriptionId}/resourceGroups/{ResourceGroup}";

    public string Display => $"{SubscriptionId}/{ResourceGroup}";
}

public sealed record PortalRailItem(
    string Kind,
    string Name,
    string Href,
    string Status,
    string Age,
    string Cost,
    string Detail);

public sealed record PortalRailGroup(string Title, string Subtitle, IReadOnlyList<PortalRailItem> Items);

/// <summary>
/// M11 S2 — thrown by the dashboard's panel reads when the caller's roles are denied the namespace. It is not
/// an error and never reaches a page: it exists so ONE guard can skip a read without every panel growing its
/// own `if`.
/// </summary>
public sealed class NamespaceHidden : Exception;

public sealed class PortalDashboardModel
{
    public ArmScope? Scope { get; set; }
    public IReadOnlyList<PortalEnvelope<PortalSubscription>> Subscriptions { get; set; } = [];
    public PortalFailure? ScopeFailure { get; set; }

    /// <summary>Track 3a: the typed <c>/health/plugins</c> document; null when the probe could not be read.</summary>
    public PortalPluginHealth? Health { get; set; }

    /// <summary>Why the health probe failed (a 503 from an Unhealthy kernel, an unreachable host).</summary>
    public string? HealthError { get; set; }
    public IReadOnlyList<PortalProvider> Providers { get; set; } = [];
    public PortalLedgerDay? Today { get; set; }

    /// <summary>
    /// The pomnibot day ledger — a <em>separate</em> resource from the Agents one
    /// (<c>Burbuja.Pomnibot/ledgers</c> vs <c>Burbuja.Agents/ledgers</c>). The two are summed for
    /// the tile because neither books the other's jobs; the card names which is which.
    /// </summary>
    public PortalPomnibotLedgerDay? PomnibotToday { get; set; }

    public IReadOnlyList<string> LedgerDays { get; set; } = [];
    public IReadOnlyList<(string Kind, decimal Usd, int Tokens)> SpendByKind { get; set; } = [];
    public IReadOnlyList<(string Kind, decimal Usd)> PomnibotSpendByKind { get; set; } = [];
    public int ActiveJobs { get; set; }
    public int AwaitingJobs { get; set; }

    /// <summary>
    /// M11 S2 — the namespaces this caller may NOT read, so a page can say "not visible for your role" instead of
    /// painting the kernel's 403. The plan's rule: a panel whose resource type the role is denied is not
    /// requested and not rendered.
    /// </summary>
    public List<string> HiddenNamespaces { get; set; } = [];
    public int ActiveAgents { get; set; }
    public int PendingApprovals { get; set; }

    /// <summary>pomnibot job counts; false <see cref="PomnibotRegistered"/> means the RP is not mounted.</summary>
    public int ActivePomnibotJobs { get; set; }

    public int AwaitingPomnibotJobs { get; set; }
    public bool PomnibotRegistered { get; set; }
    public IReadOnlyList<PortalEnvelope<PortalPomnibotRepo>> Repos { get; set; } = [];
    public PortalChannelRead Channels { get; set; } = new([], false, null);
    public IReadOnlyList<PortalRailGroup> Rail { get; set; } = [];
    public List<PortalFailure> Failures { get; } = [];

    /// <summary>Agents + pomnibot, each ledger read from its own resource — no booking counted twice.</summary>
    public decimal SpendTodayUsd => (Today?.SpentUsd ?? 0m) + (PomnibotToday?.SpentUsd ?? 0m);

    public int SpendTodayJobs => (Today?.JobCount ?? 0) + (PomnibotToday?.JobCount ?? 0);

    /// <summary>
    /// The tile/probe line. A failed probe keeps the old "unavailable: CODE" wording; a live document
    /// reads "<c>Healthy · 8/8 plugins healthy</c>" so the tile is not a bare status word any more.
    /// </summary>
    public string? HealthText => HealthError ?? Health?.Summary;
}

/// <summary>
/// The portal's read model: every method is one or more ARM calls through
/// <see cref="IPortalArm"/>, so a Reader login is refused by the kernel where a Reader is refused,
/// and a failure is a rendered envelope rather than an exception that blanks the page.
/// </summary>
public partial class PortalData(
    IPortalArm arm,
    PortalOptions options,
    TimeProvider clock,
    /// <summary>
    /// M11 S2 — the mirror a page asks "would the kernel refuse this" with, plus the roles this host has
    /// SEEDED (which are ARM resources and therefore invisible to the mirror alone). Optional so a fact that
    /// constructs this service by hand keeps compiling; null means "answer nothing", which hides MORE rather
    /// than less, and every page that matters resolves the real one from DI.
    /// </summary>
    PortalPermissions? permissions = null)
{
    // M14 R5p — PARTIAL, and the injected services are captured as fields rather than used as primary
    // constructor parameters from other files. A partial type's parts cannot see each other's primary
    // constructor parameters: `arm` and `options` are in scope only in THIS file, so a part that used them
    // would not compile, and the failure is a CS0103 naming a parameter the reader can see three files
    // away. Every part uses these fields, and this file is the only place the constructor exists.
    // Product readers reuse the same scoped transport/session and operation semantics.
    protected readonly IPortalArm arm = arm;
    protected readonly PortalOptions options = options;
    protected readonly TimeProvider clock = clock;

    private PortalPermissions? _effective;

    public async ValueTask<IArmClient> ClientAsync(CancellationToken ct) => await arm.RequireClientAsync(ct);

    /// <summary>
    /// M11 S2 — the caller's effective permissions, read ONCE per circuit: the built-in/contributed mirror plus
    /// the kernel's own seeded <c>roleDefinitions</c> for the roles this session holds.
    /// </summary>
    public async ValueTask<PortalPermissions?> PermissionsAsync(CancellationToken ct)
    {
        if (permissions is null)
            return null;
        if (_effective is not null)
            return _effective;

        var session = await arm.SessionAsync();
        var roles = session?.Roles ?? [];
        var (scope, _) = await ResolveScopeAsync(ct);
        var seeded = scope is null ? [] : await SeededRolesAsync(scope, roles, ct);
        _effective = permissions.With(seeded);
        return _effective;
    }

    /// <summary>Whether the signed-in caller may do <paramref name="action"/> anywhere. True when unknown.</summary>
    public async ValueTask<bool> MayAsync(string action, CancellationToken ct)
    {
        var effective = await PermissionsAsync(ct);
        if (effective is null)
            return true;
        var session = await arm.SessionAsync();
        return effective.Can(session?.Roles ?? [], action);
    }

    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalSubscription>>> SubscriptionsAsync(CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var items = new List<PortalEnvelope<PortalSubscription>>();
        await foreach (var item in client.ListAsync<PortalEnvelope<PortalSubscription>>("/subscriptions", ct: ct))
            items.Add(item);
        return items;
    }

    /// <summary>
    /// The tenancy a page renders: configured subscription and group must both appear in the caller's
    /// ARM listings. Without configuration, discovery succeeds only for a single visible choice at
    /// each level. A missing configured scope or an ambiguous listing returns a failure, never a
    /// different tenant picked by enumeration order. A kernel with no seed returns null.
    ///
    /// <para><b>M16 M-R2 — a REFUSED read is a failure the page renders, never a 500.</b> The two list calls
    /// below go through the ARM client, which throws on a non-2xx; an <c>ArmException</c> therefore left
    /// <c>OnInitializedAsync</c> and endpoint routing answered the whole page with a 500 body. That is
    /// reachable for any login with portal access whose key's roles grant nothing, such as a deployment
    /// where a resource-defined role has not been seeded. The refusal is the page's own
    /// <see cref="PortalFailure"/>, which every page already
    /// renders: the read is refused, the page says so, and the status is 200 with a sentence instead of a
    /// stack trace with none.</para>
    /// </summary>
    public async ValueTask<(ArmScope? Scope, PortalFailure? Failure)> ResolveScopeAsync(CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var subscriptions = new List<PortalEnvelope<PortalSubscription>>();
        try
        {
            await foreach (var item in client.ListAsync<PortalEnvelope<PortalSubscription>>("/subscriptions", ct: ct))
                subscriptions.Add(item);
        }
        catch (ArmException ex)
        {
            return (null, PortalFailure.From(ex));
        }

        var visibleSubscriptions = subscriptions.Select(s => s.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (visibleSubscriptions.Length == 0)
            return (null, PortalFailure.Local("NoSubscription", "The kernel has no subscription the caller can read."));

        var configuredSubscription = options.SubscriptionId.Trim();
        var subscription = configuredSubscription.Length > 0
            ? visibleSubscriptions.FirstOrDefault(name => string.Equals(name, configuredSubscription, StringComparison.OrdinalIgnoreCase))
            : visibleSubscriptions.Length == 1 ? visibleSubscriptions[0] : null;
        if (subscription is null)
            return (null, configuredSubscription.Length > 0
                ? PortalFailure.Local("ConfiguredSubscriptionNotVisible", $"Configured subscription '{configuredSubscription}' is not visible to this caller.")
                : PortalFailure.Local("AmbiguousSubscription", "More than one subscription is visible; configure Portal:SubscriptionId."));

        var groups = new List<PortalEnvelope<JsonObject>>();
        try
        {
            await foreach (var item in client.ListAsync<PortalEnvelope<JsonObject>>(
                                $"/subscriptions/{subscription}/resourceGroups", ct: ct))
                groups.Add(item);
        }
        catch (ArmException ex)
        {
            // The subscription was readable and its groups are not: the same honest answer, about the
            // narrower scope.
            return (null, PortalFailure.From(ex));
        }

        var visibleGroups = groups.Select(g => g.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (visibleGroups.Length == 0)
            return (null, PortalFailure.Local("NoResourceGroup", $"Subscription '{subscription}' has no resource group the caller can read."));

        var configuredGroup = options.ResourceGroup.Trim();
        var group = configuredGroup.Length > 0
            ? visibleGroups.FirstOrDefault(name => string.Equals(name, configuredGroup, StringComparison.OrdinalIgnoreCase))
            : visibleGroups.Length == 1 ? visibleGroups[0] : null;
        if (group is null)
            return (null, configuredGroup.Length > 0
                ? PortalFailure.Local("ConfiguredResourceGroupNotVisible", $"Configured resource group '{configuredGroup}' is not visible in subscription '{subscription}' to this caller.")
                : PortalFailure.Local("AmbiguousResourceGroup", $"More than one resource group is visible in subscription '{subscription}'; configure Portal:ResourceGroup."));

        return (new ArmScope(subscription, group), null);
    }

    public async ValueTask<PortalDashboardModel> DashboardAsync(CancellationToken ct)
    {
        var model = new PortalDashboardModel();
        var (scope, failure) = await ResolveScopeAsync(ct);
        model.Scope = scope;
        model.ScopeFailure = failure;
        if (scope is null)
            return model;

        model.Failures.AddRange(await PanelsAsync(model, scope, ct));
        return model;
    }

    private async ValueTask<List<PortalFailure>> PanelsAsync(PortalDashboardModel model, ArmScope scope, CancellationToken ct)
    {
        var failures = new List<PortalFailure>();
        var client = await ClientAsync(ct);

        // M11 S2 — what this caller may not read is not requested. The panels below are the exact five the
        // restricted key rendered as `AuthorizationFailed`, and the rule the plan states is that a panel whose
        // resource type the role is denied is neither fetched nor painted. The answer comes from the KERNEL's
        // own role definitions for this session's roles, because the release's roles are ARM resources and a
        // mirror that only knew the built-ins would have answered "no" for the one key users are handed.
        var mayPomnibot = await MayAsync("Burbuja.Pomnibot/jobs/read", ct);
        if (!mayPomnibot)
            model.HiddenNamespaces.Add("Burbuja.Pomnibot");

        try
        {
            model.Subscriptions = await SubscriptionsAsync(ct);
        }
        catch (ArmException ex)
        {
            failures.Add(PortalFailure.From(ex));
        }

        try
        {
            // Track 3a: the endpoint answers JSON (one entry per loaded plugin), not the old
            // always-Healthy word; an Unhealthy kernel answers 503 and lands in the failure path.
            model.Health = await client.GetAsync<PortalPluginHealth>("/health/plugins", ct: ct);
        }
        catch (ArmException ex)
        {
            model.HealthError = $"unavailable: {ex.Error.Code}";
            failures.Add(PortalFailure.From(ex));
        }

        try
        {
            var providers = new List<PortalProvider>();
            await foreach (var item in client.ListAsync<PortalProvider>("/providers", ct: ct))
                providers.Add(item);
            model.Providers = providers;
        }
        catch (ArmException ex)
        {
            failures.Add(PortalFailure.From(ex));
        }

        try
        {
            var day = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            try
            {
                var envelope = await client.GetAsync<PortalEnvelope<PortalLedgerDay>>(
                    $"/providers/Burbuja.Agents/ledgers/{day}", ct: ct);
                model.Today = envelope?.Properties;
            }
            catch (ArmException ex) when (ex.Error.HttpStatus == 404)
            {
                // A day nobody has booked against yet has no resource; zero, not a failure.
                model.Today = new PortalLedgerDay { Day = day };
            }

            if (model.Today is { } today)
            {
                model.SpendByKind = today.Bookings
                    .GroupBy(b => b.Kind)
                    .Select(g => (Kind: g.Key, Usd: g.Sum(b => b.Usd), Tokens: g.Sum(b => b.InputTokens + b.OutputTokens)))
                    .OrderByDescending(x => x.Usd)
                    .ToList();
            }

            var days = new List<string>();
            await foreach (var item in client.ListAsync<PortalEnvelope<PortalLedgerDay>>("/providers/Burbuja.Agents/ledgers", ct: ct))
            {
                if (item.Name is { Length: > 0 })
                    days.Add(item.Name);
            }

            model.LedgerDays = days;
        }
        catch (ArmException ex)
        {
            failures.Add(PortalFailure.From(ex));
        }

        try
        {
            var jobs = await JobsAsync(scope, ct);
            model.ActiveJobs = jobs.Count(j => !IsTerminal(j.Properties?.Status));
            model.AwaitingJobs = jobs.Count(j => string.Equals(j.Properties?.Status, "awaitingApproval", StringComparison.OrdinalIgnoreCase));
        }
        catch (ArmException ex)
        {
            failures.Add(PortalFailure.From(ex));
        }

        try
        {
            if (!mayPomnibot)
                throw new NamespaceHidden();
            var pomnibot = await PomnibotJobsAsync(scope, ct);
            model.PomnibotRegistered = true;
            model.ActivePomnibotJobs = pomnibot.Count(j => !IsTerminal(j.Properties?.ExecutionStatus));
            model.AwaitingPomnibotJobs = pomnibot.Count(j => IsPomnibotAwaitingApproval(j.Properties?.ExecutionStatus));
        }
        catch (NamespaceHidden)
        {
            // The panel is not this caller's; `HiddenNamespaces` says so and the tile prints it.
            model.PomnibotRegistered = false;
        }
        catch (ArmException ex) when (IsTypeAbsent(ex))
        {
            // The Burbuja.Pomnibot namespace is not mounted on this host (an Agents-only build):
            // the panels say "not registered" instead of painting the dashboard red.
            model.PomnibotRegistered = false;
        }
        catch (ArmException ex)
        {
            failures.Add(PortalFailure.From(ex));
        }

        try
        {
            if (!mayPomnibot)
                throw new NamespaceHidden();
            var day = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var pomnibotDay = await PomnibotLedgerAsync(scope, day, ct);
            model.PomnibotToday = pomnibotDay;
            if (pomnibotDay is not null)
            {
                model.PomnibotSpendByKind = pomnibotDay.Bookings
                    .GroupBy(b => b.Kind)
                    .Select(g => (Kind: g.Key, Usd: g.Sum(b => b.Usd)))
                    .OrderByDescending(x => x.Usd)
                    .ToList();
            }
        }
        catch (NamespaceHidden)
        {
            // Same as above: not requested, not painted.
        }
        catch (ArmException ex)
        {
            failures.Add(PortalFailure.From(ex));
        }

        try
        {
            if (!mayPomnibot)
                throw new NamespaceHidden();
            model.Repos = await PomnibotReposAsync(scope, ct);
        }
        catch (ArmException ex) when (IsTypeAbsent(ex))
        {
            model.Repos = [];
        }
        catch (NamespaceHidden)
        {
            model.Repos = [];
        }
        catch (ArmException ex)
        {
            failures.Add(PortalFailure.From(ex));
        }

        try
        {
            if (!mayPomnibot)
                throw new NamespaceHidden();
            model.Channels = await PomnibotChannelsAsync(scope, ct);
            if (model.Channels.Failure is { } channelFailure)
                failures.Add(channelFailure);
        }
        catch (NamespaceHidden)
        {
            model.Channels = new PortalChannelRead([], false, null, Hidden: true);
        }
        catch (ArmException ex)
        {
            failures.Add(PortalFailure.From(ex));
        }

        try
        {
            var agents = await AgentsAsync(scope, ct);
            model.ActiveAgents = agents.Count(a => !string.Equals(a.Properties?.State, "settled", StringComparison.OrdinalIgnoreCase));
        }
        catch (ArmException ex)
        {
            failures.Add(PortalFailure.From(ex));
        }

        try
        {
            model.PendingApprovals = (await ApprovalsAsync(scope, pendingOnly: true, ct)).Count;
        }
        catch (ArmException ex)
        {
            failures.Add(PortalFailure.From(ex));
        }

        try
        {
            model.Rail = await RailAsync(scope, ct);
        }
        catch (ArmException ex)
        {
            failures.Add(PortalFailure.From(ex));
        }

        return failures;
    }

    /// <summary>
    /// The provider catalogue: what this host serves, as the front door describes it. The explorer is built
    /// on this and nothing else — a provider registered in DI appears without a portal change.
    /// </summary>
    public async ValueTask<IReadOnlyList<PortalProvider>> ProvidersAsync(CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var providers = new List<PortalProvider>();
        await foreach (var item in client.ListAsync<PortalProvider>("/providers", ct: ct))
            providers.Add(item);
        return providers;
    }

    /// <summary>
    /// The cron schedules the kernel's sweeper fires. Scoped to the resource group, because a schedule that
    /// runs in somebody else's scope is not this page's business.
    /// </summary>
    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalSchedule>>> SchedulesAsync(ArmScope scope, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var items = new List<PortalEnvelope<PortalSchedule>>();
        await foreach (var row in client.ListAsync<PortalEnvelope<PortalSchedule>>(
            $"{scope.PomnibotRoot}/schedules", ct: ct))
        {
            items.Add(row);
        }

        return items;
    }

    /// <summary>
    /// Every resource of one type at the scope a descriptor declares, built the way the explorer builds it:
    /// from the type's OWN namespace and type name, at the root its capabilities name. A page that rebuilt
    /// that path from something else invented 404s for the kernel's own subscription.
    /// </summary>
    public async IAsyncEnumerable<PortalEnvelope<JsonNode>> ResourcesAsync(
        ArmScope scope,
        PortalProvider provider,
        PortalProviderType type,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (type.ListRoot(scope) is not { } root)
            yield break;

        var client = await ClientAsync(ct);
        var path = $"{root}/providers/{provider.DisplayNamespace}/{type.ResourceType}";
        await foreach (var row in client.ListAsync<PortalEnvelope<JsonNode>>(path, ct: ct))
            yield return row;
    }

    /// <summary>
    /// The activity ring: the last N requests this host served, newest first, with the ring's own counts
    /// beside them (`ring.total`, `ring.capacity`, `ring.held` on every row's tags) so a page can say whether
    /// it is looking at a full ring or a quiet host.
    ///
    /// <para><b>Filtered where the kernel filters.</b> <c>principal</c> and <c>since</c> are the resource's
    /// own query parameters; everything else the page does is client-side over the page it was given, which
    /// is why the page says how many rows it is looking at.</para>
    /// </summary>
    public async ValueTask<(IReadOnlyList<PortalEnvelope<PortalActivityRow>> Rows, PortalActivityRing Ring)> ActivityAsync(
        ArmScope scope,
        int top,
        string? principal,
        TimeSpan? since,
        CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var query = new List<string> { $"$top={top}" };
        if (principal is { Length: > 0 })
            query.Add($"principal={Uri.EscapeDataString(principal)}");
        if (since is { } window)
            query.Add($"since={Uri.EscapeDataString(DateTimeOffset.UtcNow.Subtract(window).ToString("O"))}");

        var items = new List<PortalEnvelope<PortalActivityRow>>();
        var ring = new PortalActivityRing();
        await foreach (var row in client.ListAsync<PortalEnvelope<PortalActivityRow>>(
            $"{scope.ResourcesRoot}/activity?{string.Join('&', query)}", ct: ct))
        {
            items.Add(row);

            // The counts ride on every row and are the same on all of them; the first one that has them wins,
            // and a host that answered without them leaves the ring at zero rather than invented.
            if (row.Tags is { Count: > 0 } tags && ring.Total == 0)
            {
                ring = new PortalActivityRing
                {
                    Total = Parse(tags, "ring.total"),
                    Capacity = (int)Parse(tags, "ring.capacity"),
                    Held = (int)Parse(tags, "ring.held"),
                };
            }
        }

        // Newest first: the question this page answers is about what just happened.
        items.Reverse();
        return (items, ring);
    }

    private static long Parse(IReadOnlyDictionary<string, string> tags, string key) =>
        tags.TryGetValue(key, out var raw) && long.TryParse(raw, out var value) ? value : 0;

    /// <summary>
    /// M12 R3 — the last few things that happened, for Home's "Recent" list.
    ///
    /// <para><b>Deduplicated by PATH, not by row.</b> The activity ring is a request log, so one screen
    /// refresh writes several rows for the same resource and a list of them says "the portal read this
    /// page" rather than "somebody did something". Collapsing to the newest row per path is what makes the
    /// list read as work rather than as traffic, and it is why this is a separate read from
    /// <see cref="ActivityAsync"/> rather than a call to it with a smaller <c>$top</c>.</para>
    ///
    /// <para><b>M13 R0 — the portal's own reads are excluded (F25).</b> `G35-home-dark.png` showed twelve
    /// `GET … 0s ago` rows, every one of them a read THIS page had just made to render itself. The ring is
    /// per-process and the portal is its own client, so rendering the page wrote the rows that described
    /// rendering the page: zero information, and a panel that looks broken rather than empty. A row is
    /// dropped when it is a read (<c>GET</c>/<c>HEAD</c>) by the principal this session signs in as —
    /// writes and actions are kept whoever made them, and reads by OTHER principals are kept because those
    /// are somebody else looking at something. When the ring holds nothing else, the page says so.</para>
    ///
    /// <para>Reads a superset (the window is four times what is shown, floored at <paramref name="take"/>
    /// times four) and then trims, because the dedupe AND the self-read filter both happen after the fetch:
    /// asking for exactly 12 rows and collapsing them can return three.</para>
    /// </summary>
    public async ValueTask<IReadOnlyList<PortalActivityRow>> RecentAsync(
        ArmScope scope,
        int take,
        CancellationToken ct,
        string? selfPrincipal = null)
    {
        var (rows, _) = await ActivityAsync(scope, top: Math.Max(take * 4, 60), principal: null, since: null, ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var recent = new List<PortalActivityRow>();
        foreach (var row in rows)
        {
            var properties = row.Properties;
            if (properties is null)
                continue;

            // A row with no path cannot be deduplicated and cannot be linked, so it is a read of nothing —
            // checked BEFORE it reaches the HashSet, which is where the first version of this put it. A
            // HashSet<string> rejects a null VALUE with ArgumentNullException rather than a false, so the
            // order cost a 500 on any host whose ring carried a row without a path, and the page that pays
            // for it is Home — the one a person lands on when something is already wrong.
            var path = properties.Path;
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (IsSelfRead(properties, selfPrincipal))
                continue;

            if (!seen.Add(path))
                continue;

            recent.Add(properties);
            if (recent.Count >= take)
                break;
        }

        return recent;
    }

    /// <summary>
    /// M13 R0 — whether an activity row is this console reading its own page. Public and static so the rule
    /// is assertable without a browser: it is the whole of F25, and a fact that had to render Home to check
    /// it would be a fact about Home rather than about the filter.
    /// </summary>
    public static bool IsSelfRead(PortalActivityRow row, string? selfPrincipal) =>
        selfPrincipal is { Length: > 0 }
        && (row.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            || row.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        && string.Equals(row.PrincipalId, selfPrincipal, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The fleet as this host sees it: the kernel process's own identity and one row per loaded plugin, with
    /// what it provides, what it requires, whether it is trusted and what the supervisor says about it.
    ///
    /// <para><b>Why a page and not a chip.</b> The dashboard chips the namespace names, which is enough to
    /// notice that something is missing and not enough to find out what — and the trust list is the thing
    /// this repository has paid for twice (a missing entry is exit 78 at boot, and a wrong role field made a
    /// plugin load nowhere at all). A roster an operator can read is the surface those two defects argue
    /// for.</para>
    /// </summary>
    public async ValueTask<PortalPluginHealth?> PluginHealthAsync(CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        try
        {
            return await client.GetAsync<PortalPluginHealth>("/health/plugins", ct: ct);
        }
        catch (ArmException)
        {
            // An Unhealthy kernel answers 503, and a page that turned that into a stack trace would hide the
            // roster behind the very failure an operator is looking for.
            return null;
        }
    }

    /// <summary>One workspace WITH its hub, which is what the workspaces blade renders. The LIST does not
    /// carry it — the kernel fills the hub for a single read only, so a list of fifty workspaces does not
    /// scan fifty job sets.</summary>
    public async ValueTask<PortalEnvelope<PortalWorkspace>?> WorkspaceAsync(ArmScope scope, string name, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        return await client.GetAsync<PortalEnvelope<PortalWorkspace>>($"{scope.AgentsRoot}/workspaces/{name}", ct: ct);
    }

    /// <summary>The external tool surfaces this host serves, declared and probed.</summary>
    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalIntegration>>> IntegrationsAsync(ArmScope scope, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var items = new List<PortalEnvelope<PortalIntegration>>();
        await foreach (var row in client.ListAsync<PortalEnvelope<PortalIntegration>>(
            $"{scope.RootPath}/providers/Burbuja.Integrations/integrations", ct: ct))
        {
            items.Add(row);
        }

        return items;
    }

    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalWorkspace>>> WorkspacesAsync(ArmScope scope, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var items = new List<PortalEnvelope<PortalWorkspace>>();
        await foreach (var item in client.ListAsync<PortalEnvelope<PortalWorkspace>>($"{scope.AgentsRoot}/workspaces", ct: ct))
            items.Add(item);
        return items;
    }

    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalJob>>> JobsAsync(ArmScope scope, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var jobs = new List<PortalEnvelope<PortalJob>>();
        foreach (var workspace in await WorkspacesAsync(scope, ct))
        {
            if (workspace.Name is null)
                continue;
            await foreach (var job in client.ListAsync<PortalEnvelope<PortalJob>>(
                               $"{scope.AgentsRoot}/workspaces/{workspace.Name}/jobs", ct: ct))
                jobs.Add(job);
        }

        return jobs;
    }

    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalAgent>>> AgentsAsync(ArmScope scope, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var agents = new List<PortalEnvelope<PortalAgent>>();
        await foreach (var agent in client.ListAsync<PortalEnvelope<PortalAgent>>($"{scope.AgentsRoot}/agents", ct: ct))
            agents.Add(agent);
        return agents;
    }

    public async ValueTask<PortalEnvelope<PortalJob>?> JobAsync(ArmScope scope, string workspace, string name, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        return await client.GetAsync<PortalEnvelope<PortalJob>>($"{scope.AgentsRoot}/workspaces/{workspace}/jobs/{name}", ct: ct);
    }

    public async ValueTask<PortalEnvelope<PortalAgent>?> AgentAsync(ArmScope scope, string name, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        return await client.GetAsync<PortalEnvelope<PortalAgent>>($"{scope.AgentsRoot}/agents/{name}", ct: ct);
    }

    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalAgentSession>>> AgentSessionsAsync(
        ArmScope scope, string agent, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var sessions = new List<PortalEnvelope<PortalAgentSession>>();
        await foreach (var session in client.ListAsync<PortalEnvelope<PortalAgentSession>>(
                           $"{scope.AgentsRoot}/agents/{agent}/sessions", ct: ct))
            sessions.Add(session);
        return sessions;
    }

    /// <summary>
    /// Why a type-absent error is not a page failure: a host that does not mount
    /// <c>Burbuja.Pomnibot</c> — or mounts a version older than a resource type — answers the route
    /// with a client error. The kernel says <c>NoRegisteredProviderFound</c> for a namespace it does
    /// not know and <c>InvalidResourceType</c> for a type a registered namespace does not declare; a
    /// data-plane probe of a route that is not there answers 404; the live pomnibot RP answers
    /// <c>ActionNotSupported</c> for a resource type it has not shipped yet. All of those are "not
    /// registered here", which the portal renders as such.
    /// </summary>
    public static bool IsTypeAbsent(ArmException ex) =>
        ex.Error.HttpStatus is 404 or 405 or 501
        || ex.Error.HttpStatus == 400
        && (ex.Error.Code is "NoRegisteredProviderFound" or "InvalidResourceType" or "UnknownResourceType"
            or "ResourceTypeNotFound" or "ActionNotSupported");

    /// <summary>
    /// Where an approval's subject lives in the portal, or null when the subject is not a job this
    /// portal has a page for. The kernel inbox is namespace-agnostic, so the link has to be derived
    /// from the subject id — and a pomnibot subject must never be sent to the Agents route.
    /// </summary>
    public static string? JobLink(string? subjectId, string? streamId)
    {
        var id = subjectId is { Length: > 0 } ? subjectId : streamId;
        if (id is null)
            return null;

        var path = id.Split('?')[0].TrimEnd('/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var provider = Array.FindIndex(segments, s => s.Equals("providers", StringComparison.OrdinalIgnoreCase));
        if (provider < 0 || provider + 1 >= segments.Length)
            return null;
        var ns = segments[provider + 1];
        var rest = segments[(provider + 2)..];

        if (ns.Equals("Burbuja.Pomnibot", StringComparison.OrdinalIgnoreCase))
            return rest.Length >= 2 && rest[0].Equals("jobs", StringComparison.OrdinalIgnoreCase)
                ? $"/portal/pomnibot/jobs/{Uri.EscapeDataString(rest[1])}"
                : null;

        if (ns.Equals("Burbuja.Agents", StringComparison.OrdinalIgnoreCase))
        {
            var workspace = Array.FindIndex(rest, s => s.Equals("workspaces", StringComparison.OrdinalIgnoreCase));
            if (workspace >= 0 && workspace + 1 < rest.Length && rest.Length >= workspace + 4
                && rest[workspace + 2].Equals("jobs", StringComparison.OrdinalIgnoreCase))
            {
                return $"/portal/jobs/{Uri.EscapeDataString(rest[workspace + 1])}/{Uri.EscapeDataString(rest[workspace + 3])}";
            }
        }

        return null;
    }

    /// <summary>
    /// M11 S2 — the caller's roles as the KERNEL defines them.
    ///
    /// <para>The built-in three and the plugin-contributed ones are mirrored in <c>PortalPermissions</c>; a
    /// SEEDED role is an ARM resource and was invisible to that mirror, so
    /// every permission question about the one key users hold answered "no" — a permission-driven console
    /// would have hidden everything from exactly the person it is for.</para>
    ///
    /// <para>This reads the kernel's own <c>roleDefinitions</c> — the evaluator's source — at the root and at
    /// the subscription, and keeps the rows the session's roles name. Both spellings are matched: the profile
    /// may use a display name while the seeder writes an identifier. A scope that cannot be read contributes
    /// nothing, which hides more and never less, and a page must not paint a failure for it.</para>
    /// </summary>
    public async ValueTask<IReadOnlyList<RoleDefinition>> SeededRolesAsync(
        ArmScope scope, IReadOnlyList<string> roles, CancellationToken ct)
    {
        if (roles.Count == 0)
            return [];

        var client = await ClientAsync(ct);
        var wanted = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, RoleDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var prefix in new[] { "/providers", $"/subscriptions/{scope.SubscriptionId}/providers" })
        {
            try
            {
                await foreach (var row in client.ListAsync<PortalEnvelope<PortalRoleDefinition>>(
                                   $"{prefix}/{ArmConstants.AuthorizationNamespace}/roleDefinitions", ct: ct))
                {
                    var properties = row.Properties;
                    if (properties is null)
                        continue;
                    if (!wanted.Contains(row.Name ?? "") && !wanted.Contains(properties.RoleName))
                        continue;
                    if (row.Name is not { Length: > 0 } name)
                        continue;

                    found[name] = new RoleDefinition(
                        name,
                        properties.RoleName,
                        properties.Actions,
                        properties.NotActions,
                        properties.DataActions,
                        properties.NotDataActions,
                        properties.AssignableScopes,
                        IsBuiltIn: false);
                }
            }
            catch (ArmException)
            {
                // Not readable at this scope: contributes nothing.
            }
        }

        return [.. found.Values];
    }

    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalApproval>>> ApprovalsAsync(
        ArmScope scope, bool pendingOnly, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var query = pendingOnly
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pendingOnly"] = "true" }
            : null;
        var approvals = new List<PortalEnvelope<PortalApproval>>();
        await foreach (var approval in client.ListAsync<PortalEnvelope<PortalApproval>>(
                           $"{scope.ResourcesRoot}/approvals", query, ct))
            approvals.Add(approval);
        return approvals;
    }

    /// <summary>
    /// M12 R1 — how many decisions are parked, for the top bar's bell. It is its own read rather than a
    /// field off <see cref="DashboardAsync"/> because the bell is on EVERY page: the shell cannot afford
    /// the dashboard's dozen reads to draw one badge, and a badge that only worked on the dashboard would
    /// be a badge nobody sees when they are deep in a turn trace.
    ///
    /// <para>Null means "could not ask" — a denied namespace, a host that is down — and the shell renders
    /// NO badge rather than a zero. A zero is a claim that nothing needs a decision; an absent badge is
    /// the truth that nobody knows.</para>
    /// </summary>
    public async ValueTask<int?> PendingApprovalCountAsync(CancellationToken ct)
    {
        try
        {
            var (scope, _) = await ResolveScopeAsync(ct);
            if (scope is null)
                return null;
            return (await ApprovalsAsync(scope, pendingOnly: true, ct)).Count;
        }
        catch (Exception ex) when (ex is ArmException or NamespaceHidden)
        {
            return null;
        }
    }

    /// <summary>
    /// The approval plus the ETag the kernel serves for it. The body carries an etag too, but the
    /// response header is the one an <c>If-Match</c> has to echo, so both are read through the same
    /// typed façade (<see cref="ArmClientExtensions.Body{T}"/>).
    /// </summary>
    public async ValueTask<(PortalEnvelope<PortalApproval>? Envelope, string? ETag)> ApprovalWithTagAsync(
        ArmScope scope, string id, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var response = await client.SendAsync(new ArmClientRequest("GET", $"{scope.ResourcesRoot}/approvals/{id}"), ct);
        response.EnsureSuccess();
        var envelope = response.Body<PortalEnvelope<PortalApproval>>();
        var tag = response.Headers.TryGetValue("ETag", out var value) && value.Length > 0 ? value : envelope?.ETag;
        return (envelope, tag);
    }

    public async ValueTask<PortalEnvelope<PortalApproval>?> ApprovalAsync(ArmScope scope, string id, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        return await client.GetAsync<PortalEnvelope<PortalApproval>>($"{scope.ResourcesRoot}/approvals/{id}", ct: ct);
    }

    /// <summary>
    /// M10 A7/U7 — run an ARM ACTION the way a console does: send it, follow the operation when the kernel
    /// answers 202, and hand back both the outcome and what changed.
    ///
    /// <para><b>Why this is here and not in each page.</b> It was written inside `ResourceBlade.RunAsync`, and
    /// product actions and the workspace form need exactly the same four things: an `If-Match` from the
    /// envelope the page rendered, the parse of an optional JSON body, the 412 that is surfaced rather than
    /// retried, and the poll of `Azure-AsyncOperation` when the kernel accepts rather than answers. Two copies
    /// of that is how one page ends up retrying a 412 that the other reports — which is the class of defect the
    /// blade's own comment says it exists to prevent.</para>
    ///
    /// <para><b>The 412 is never retried.</b> A resource that moved under the page is a human's decision, and
    /// the reason is returned as the message rather than swallowed.</para>
    ///
    /// <para><b>M13 R0 — <paramref name="followOperation"/>: the 202 is not always a provisioning step.</b>
    /// The poll below was written for the workspace's <c>initialize</c>, whose operation SUCCEEDS in seconds.
    /// A job PUT is the other shape: the kernel accepts it, stores the job, and the operation is the WHOLE RUN
    /// — which, for a job created <c>Paused</c> (auto-accept off), never terminates until a human Accepts it.
    /// So `CreateJob` waited the full 60 s on an operation that could not end, `_busy` stayed true, the
    /// `finally` never ran and the form never navigated: the symptom the M12 report called "the job redirect
    /// needs a debugger". It was never a Blazor bug. A caller that has somewhere to GO on the 202 passes
    /// <c>false</c> and reads the operation from <see cref="PortalActionResult.Operation"/>. A magic
    /// <c>TimeSpan.Zero</c> meaning "do not poll" was considered and rejected: zero already means "the first
    /// poll throws", and the next caller would get it wrong.</para>
    /// </summary>
    public async ValueTask<PortalActionResult> RunActionAsync(
        string path,
        string? action,
        string? body,
        string? ifMatch,
        TimeSpan? pollTimeout = null,
        CancellationToken ct = default,
        // M10 A7: the verb. A POST is an ACTION on an existing resource (`…/advance`); a PUT is how a resource
        // is CREATED (`…/turns/{name}`), and the first version of this helper sent a POST for both — the kernel
        // answered `'' is not an action of a turn`, which is a true sentence about a request that named no
        // action, and the form read as a 404. Default POST so every action call site is unchanged.
        string method = "POST",
        // M13 R0: true keeps every existing call site's behaviour (the operation IS the answer). A caller that
        // navigates on the 202 passes false — see the note above.
        bool followOperation = true)
    {
        JsonNode? parsed = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                parsed = JsonNode.Parse(body);
            }
            catch (JsonException ex)
            {
                return new PortalActionResult(0, null, null, null, "InvalidJson", "The body is not JSON: " + ex.Message);
            }
        }

        var client = await ClientAsync(ct);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ifMatch is { Length: > 0 } etag)
            headers["If-Match"] = etag;

        var target = string.IsNullOrWhiteSpace(action) ? path : $"{path}/{action}";
        var response = await client.SendAsync(new ArmClientRequest(method, target, Headers: headers, Body: parsed), ct);

        if (response.StatusCode == 412)
        {
            return new PortalActionResult(
                412,
                null,
                null,
                response.Headers.TryGetValue("ETag", out var current) ? current : null,
                "PreconditionFailed",
                "This resource changed since the page rendered it. Reload and act again — the kernel refused the stale write rather than merging it.");
        }

        if (response.StatusCode is < 200 or > 299)
        {
            return new PortalActionResult(
                response.StatusCode,
                null,
                null,
                null,
                response.Error?.Code ?? $"HTTP {response.StatusCode}",
                response.Error?.Message ?? $"The kernel refused '{target}'.");
        }

        if (response.StatusCode == 202)
        {
            // M13 R0 — a caller that has a page to reach reads the operation off the result and goes. See the
            // note on `followOperation` above for the job the 60 s poll left stuck.
            if (!followOperation)
                return new PortalActionResult(202, response.Body, response.Headers, null, null, null);

            // Long-running: the operation is the answer until it reaches a terminal state. Polling HERE is what
            // makes the console a console rather than a log the reader has to refresh.
            var finished = await client.PollOperationAsync<JsonNode>(
                response, pollTimeout ?? TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(250), ct);
            return new PortalActionResult(202, finished ?? response.Body, response.Headers, null, null, null);
        }

        return new PortalActionResult(response.StatusCode, response.Body, response.Headers, null, null, null);
    }

    /// <summary>What an action answered: the status, the body, the response headers, and the reason when it
    /// did not answer 2xx. A record rather than a thrown exception because a refusal is a RESULT a page
    /// renders — a 403 panel, a 412 reload prompt — not an error that should blank the page.</summary>
    public sealed record PortalActionResult(
        int StatusCode,
        JsonNode? Body,
        IReadOnlyDictionary<string, string>? Headers,
        string? CurrentETag,
        string? ErrorCode,
        string? ErrorMessage,
        // M13 R0 — whether the 202's operation was polled to a terminal state. A page that navigated on the
        // 202 reports `accepted` rather than claiming the work finished; that distinction is the whole reason
        // the job form could hang silently.
        bool FollowedOperation = true)
    {
        public bool Ok => StatusCode is >= 200 and < 300;

        /// <summary>The `Azure-AsyncOperation` the kernel returned, when it accepted rather than answered.</summary>
        public string? Operation => Headers is not null && Headers.TryGetValue("Azure-AsyncOperation", out var value) ? value : null;

        /// <summary>The kernel accepted the request and the work continues — nothing here says it finished.</summary>
        public bool Accepted => StatusCode == 202;
    }

    public async ValueTask<IReadOnlyList<PortalRailGroup>> RailAsync(ArmScope scope, CancellationToken ct)
    {
        var groups = new List<PortalRailGroup>();
        var workspaces = await WorkspacesAsync(scope, ct);
        var jobs = await JobsAsync(scope, ct);
        var agents = await AgentsAsync(scope, ct);
        var now = clock.GetUtcNow();

        foreach (var workspace in workspaces)
        {
            if (workspace.Name is null)
                continue;
            var workspaceJobs = jobs
                .Where(j => j.Id is not null && j.Id.Contains($"/workspaces/{workspace.Name}/", StringComparison.OrdinalIgnoreCase))
                .Select(j => JobItem(workspace.Name, j, now))
                .OrderByDescending(i => i.Age)
                .ToList();
            groups.Add(new PortalRailGroup(
                workspace.Properties?.DisplayName is { Length: > 0 } display ? display : workspace.Name,
                workspace.Properties?.RepoId ?? workspace.Properties?.Remote ?? "no repo bound",
                workspaceJobs));
        }

        // pomnibot jobs group by repo, not by workspace: the resource has no workspace segment.
        IReadOnlyList<PortalEnvelope<PortalPomnibotJob>> pomnibotJobs;
        try
        {
            pomnibotJobs = await PomnibotJobsAsync(scope, ct);
        }
        catch (ArmException ex) when (IsTypeAbsent(ex))
        {
            pomnibotJobs = [];
        }

        foreach (var group in pomnibotJobs
                     .GroupBy(j => j.Properties?.Spec.RepoId is { Length: > 0 } repo ? repo : "(no repo)")
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            groups.Add(new PortalRailGroup(
                $"pomnibot, {group.Key}",
                $"{Text.Plural(group.Count(), "pomnibot job", "pomnibot jobs")}",
                group.Select(j => PomnibotJobItem(j, now)).OrderByDescending(i => i.Age).ToList()));
        }

        var byWorkspace = agents
            .GroupBy(a => a.Properties?.WorkspaceId ?? a.Properties?.ComputeWorkspaceId ?? "(unbound)")
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in byWorkspace)
        {
            groups.Add(new PortalRailGroup(
                $"agents, {group.Key}",
                $"{Text.Plural(group.Count(), "kernel agent", "kernel agents")}",
                group.Select(a => AgentItem(a, now)).OrderByDescending(i => i.Age).ToList()));
        }

        return groups;
    }

    private static PortalRailItem JobItem(string workspace, PortalEnvelope<PortalJob> job, DateTimeOffset now)
    {
        var name = job.Name ?? "(unnamed)";
        var status = job.Properties?.Status ?? job.ProvisioningState ?? "unknown";
        var started = job.Properties?.StartedAt;
        return new PortalRailItem(
            "job",
            job.Properties?.Spec.Title is { Length: > 0 } title ? title : name,
            $"/portal/jobs/{Uri.EscapeDataString(workspace)}/{Uri.EscapeDataString(name)}",
            status,
            Age(started, now),
            Money(job.Properties?.Cost.TotalUsd),
            name);
    }

    private static PortalRailItem AgentItem(PortalEnvelope<PortalAgent> agent, DateTimeOffset now)
    {
        var name = agent.Name ?? "(unnamed)";
        return new PortalRailItem(
            "agent",
            agent.Properties?.DisplayName is { Length: > 0 } display ? display : name,
            $"/portal/agents/{Uri.EscapeDataString(name)}",
            agent.Properties?.State ?? agent.ProvisioningState ?? "unknown",
            Age(null, now),
            Money(agent.Properties?.CostUsd),
            agent.Properties?.Driver is { Length: > 0 } driver ? $"{driver}, {agent.Properties.ModelId}" : name);
    }

    public static bool IsTerminal(string? status) =>
        status is not null
        && (status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("budgetExceeded", StringComparison.OrdinalIgnoreCase)
            || status.Equals("turnsExceeded", StringComparison.OrdinalIgnoreCase)
            // M10 H3: a job parked at the day's hard stop is done RUNNING. It is not finished work — the
            // status is a refusal, not a result — but counting it as active would show a job holding a slot
            // that no run will ever take until the limit moves.
            || status.Equals("blocked", StringComparison.OrdinalIgnoreCase));

    public static string Age(DateTimeOffset? started, DateTimeOffset now)
    {
        if (started is null)
            return "—";
        var delta = now - started.Value;
        if (delta < TimeSpan.Zero)
            delta = TimeSpan.Zero;
        return delta.TotalDays >= 1
            ? $"{(int)delta.TotalDays}d {delta.Hours}h"
            : delta.TotalHours >= 1
                ? $"{(int)delta.TotalHours}h {delta.Minutes}m"
                : delta.TotalMinutes >= 1
                    ? $"{(int)delta.TotalMinutes}m {delta.Seconds}s"
                    : $"{delta.TotalSeconds:F0}s";
    }

    public static string Money(decimal? value) => value is null ? "—" : "$" + value.Value.ToString("0.0000", CultureInfo.InvariantCulture);
}
