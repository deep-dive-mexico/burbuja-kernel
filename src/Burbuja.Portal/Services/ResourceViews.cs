namespace Burbuja.Portal.Services;

/// <summary>
/// How a resource type is presented in the explorer.
///
/// <para><b>The generic view is the default, not the fallback.</b> Every ARM type already describes itself:
/// <c>/providers</c> carries the resource types, the descriptor names the actions and the data-plane routes,
/// and the envelope carries the properties. So the explorer renders ANY type without knowing anything about
/// it, and a <see cref="ResourceViewDescriptor"/> exists to say where that is not good enough — a job has a
/// cost and an agent, a mandate has a latch. A registry that made the generic path the exception would need
/// an entry per type and would present the next type nobody registered as broken.</para>
/// </summary>
/// <param name="Type">
/// The fully qualified type as ARM spells it (<c>Burbuja.Agents/jobs</c>). Matched case-insensitively,
/// because the router lowercases keys and a portal should not care.
/// </param>
/// <param name="Title">What the blade calls this type. Falls back to the type's own last segment.</param>
/// <param name="Tabs">
/// The blade's tabs, in order. The three every resource has — overview, json, events — are also the three
/// the kernel can always answer: the envelope, and <c>…/events</c> for anything on a stream.
/// </param>
public sealed record ResourceViewDescriptor(
    string Type,
    string Title,
    IReadOnlyList<string>? Tabs = null)
{
    /// <summary>The tabs this type gets. <c>events</c> is omitted for a type the descriptor marks untracked.</summary>
    public IReadOnlyList<string> EffectiveTabs => Tabs is { Count: > 0 } given ? given : ResourceViewRegistry.DefaultTabs;
}

/// <summary>
/// Which view a type gets, and what the blade calls it.
///
/// <para><b>Why a registry rather than a switch in the page.</b> A page that named its types would need
/// editing for every new resource provider — and the kernel's whole point is that a provider is discovered
/// from DI and served with no front-door change. The registry is the same idea one layer up: the explorer
/// asks it, and a provider that ships a view registers one.</para>
///
/// <para><b>What is deliberately NOT here.</b> No component type per entry, yet. A bespoke properties panel
/// is a Razor component and the portal's existing blades are pages rather than embeddable views, so a
/// descriptor that named a component would either force that refactor or ship a field nothing reads. What
/// the registry does today is what the blade actually varies on — title and tabs — and the generic panel
/// renders the properties either way. Naming a component here before one exists would be the "declared with
/// no caller" shape this project keeps removing.</para>
/// </summary>
public sealed class ResourceViewRegistry
{
    /// <summary>
    /// The five tabs every resource has, in the order a reader wants them: what it is, the envelope as the
    /// kernel returned it, what it can DO, who can reach it, and what it has done.
    ///
    /// <para><b>M9 U2: `actions` and `access` live here rather than in a per-type descriptor</b>, because the
    /// kernel answers both for every type — the action catalogue comes from the provider descriptor, and a
    /// role assignment is an ARM EXTENSION resource, addressable at any scope including a single resource.
    /// A tab only some types could open would be a per-type table nobody keeps up to date; these two are the
    /// same code for a job, a mandate and a compute workspace.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultTabs =
    [
        "overview", "json", "actions", "access", "events", "children", "operations", "tags", "locks",
    ];

    private readonly Dictionary<string, ResourceViewDescriptor> _byType = new(StringComparer.OrdinalIgnoreCase);

    public ResourceViewRegistry(IEnumerable<ResourceViewDescriptor>? contributions = null)
    {
        // The types a reader most often arrives at, each with the tab order that answers its question.
        // A resource whose stream is the only interesting part leads with events; a mandate leads with its
        // properties because that is what a human is checking.
        Register(new ResourceViewDescriptor("Burbuja.Agents/jobs", "Job"));
        Register(new ResourceViewDescriptor("Burbuja.Agents/agents", "Agent"));
        Register(new ResourceViewDescriptor("Burbuja.Agents/workspaces", "Workspace"));
        Register(new ResourceViewDescriptor("Burbuja.Integrations/integrations", "Integration"));
        Register(new ResourceViewDescriptor("Burbuja.Resources/approvals", "Approval"));
        Register(new ResourceViewDescriptor("Burbuja.Compute/workspaces", "Compute workspace"));
        foreach (var descriptor in contributions ?? [])
            Register(descriptor);
    }

    /// <summary>Every registered descriptor, for a page that wants to list what has a view.</summary>
    public IReadOnlyList<ResourceViewDescriptor> All => [.. _byType.Values.OrderBy(d => d.Type, StringComparer.Ordinal)];

    public ResourceViewRegistry Register(ResourceViewDescriptor descriptor)
    {
        _byType[descriptor.Type] = descriptor;
        return this;
    }

    /// <summary>
    /// The descriptor for a type, or the GENERIC one — never null, because every type is renderable. The
    /// title falls back to the type's last segment (`Burbuja.HostFacts/machines` → `machines`), which is
    /// what the kernel called it.
    /// </summary>
    public ResourceViewDescriptor Find(string? type)
    {
        if (type is not { Length: > 0 })
            return Generic("");
        return _byType.TryGetValue(type, out var registered) ? registered : Generic(type);
    }

    /// <summary>
    /// The generic descriptor for a type: the type's own name, and the three tabs. Public because a page
    /// showing "this type has no registered view" should be able to say what it uses instead.
    /// </summary>
    public static ResourceViewDescriptor Generic(string type) =>
        new(type, type.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "resource");

    /// <summary>Whether the tab is one the blade knows how to render.</summary>
    public static bool IsKnownTab(string? tab) =>
        tab is not null && DefaultTabs.Contains(tab, StringComparer.OrdinalIgnoreCase);
}
