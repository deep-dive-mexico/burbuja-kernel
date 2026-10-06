using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Arm;

namespace Burbuja.Portal.Services;

/// <summary>
/// The slice of an ARM envelope the portal renders. The kernel's own
/// <see cref="Burbuja.Core.Abstractions.Arm.ResourceEnvelope{T}"/> is the authority; this mirror
/// exists so a page never depends on a contract field it does not show.
/// </summary>
public sealed record PortalEnvelope<T>
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public string? Location { get; init; }
    public IReadOnlyDictionary<string, string>? Tags { get; init; }
    public T? Properties { get; init; }
    public string? ETag { get; init; }
    public string? ProvisioningState { get; init; }

    /// <summary>The kernel's systemData block; the pomnibot projections carry their age here.</summary>
    public PortalSystemData? SystemData { get; init; }
}

/// <summary>The <c>systemData</c> block every kernel envelope carries.</summary>
public sealed class PortalSystemData
{
    public DateTimeOffset? CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastModifiedAt { get; set; }
    public string? LastModifiedBy { get; set; }
}

/// <summary>An ARM collection page: <c>{value, nextLink}</c>.</summary>
public sealed record PortalPage<T>
{
    public IReadOnlyList<T> Value { get; init; } = [];
    public string? NextLink { get; init; }
}

public sealed class PortalSubscription
{
    public string State { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class PortalWorkspace
{
    public string DisplayName { get; set; } = "";
    public string? RepoId { get; set; }
    public string? Remote { get; set; }
    public string BaseBranch { get; set; } = "";
    public string DefaultStrategy { get; set; } = "";
    public string Profile { get; set; } = "";
    public decimal? DefaultMaxUsd { get; set; }
    public int Revision { get; set; }

    /// <summary>
    /// K3: what this workspace holds — job counts, spend, schedules and the most recent jobs. The KERNEL
    /// fills it when one workspace is read and leaves it null on every list row, so a page that renders a
    /// list must not expect it (and the hub page asks for the workspace by name).
    /// </summary>
    public PortalWorkspaceHub? Hub { get; set; }

    // ---------------------------------------------------------------- W1: the lifecycle (M10 U7)

    /// <summary><c>Provisioning | Ready | Failed</c>, or null on a workspace nobody asked to initialize.</summary>
    public string? Lifecycle { get; set; }

    /// <summary>Why the last initialize failed. Cleared when one succeeds, and shown verbatim on the page.</summary>
    public string? LifecycleError { get; set; }

    /// <summary>The <c>Burbuja.Compute/workspaces</c> resource this workspace runs on, once it is bound.</summary>
    public string? ComputeWorkspaceId { get; set; }

    /// <summary>
    /// W1: what makes "re-initialize reconciles" checkable — a second initialize of an unchanged workspace
    /// leaves the generation where it is.
    /// </summary>
    public int? Generation { get; set; }
}

public sealed class PortalJobSpec
{
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Brief { get; set; } = "";
    public string? Kind { get; set; }
    public string? RepoId { get; set; }
    public string? Strategy { get; set; }
    public string? Language { get; set; }
}

public sealed class PortalJobCost
{
    public decimal TotalUsd { get; set; }
}

public sealed class PortalJob
{
    public PortalJobSpec Spec { get; set; } = new();
    public string Status { get; set; } = "";
    public PortalJobCost Cost { get; set; } = new();
    public bool CostIsPartial { get; set; }
    public string? PendingApprovalId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? AgentId { get; set; }
    public string? TeamId { get; set; }
    public string? ModelId { get; set; }
    public int RunId { get; set; }
    public List<string> Notes { get; set; } = [];
}

public sealed class PortalAgent
{
    public string DisplayName { get; set; } = "";
    public string Driver { get; set; } = "kernel";
    public string ModelId { get; set; } = "";
    public string? WorkspaceId { get; set; }
    public string? ComputeWorkspaceId { get; set; }
    public string? TeamId { get; set; }
    public string? ParentAgentId { get; set; }
    public string? PendingApprovalId { get; set; }
    public string? CurrentSessionId { get; set; }
    public decimal CostUsd { get; set; }
    public string State { get; set; } = "";
    public List<JsonObject> Inbox { get; set; } = [];
}

/// <summary>
/// One <c>Burbuja.Pomnibot/jobs</c> resource as the portal reads it.
///
/// Deliberately not <see cref="PortalJob"/>: the pomnibot job is a different document (there is no
/// workspace segment, the status field is <c>executionStatus</c>, cost is split worker/supervisor
/// and the lifecycle lives in <c>outcome</c>). Forcing the agents shape onto it would have meant
/// three aliases and a silent mis-read of every field the two do not share.
/// </summary>
public sealed class PortalPomnibotJob
{
    public PortalPomnibotSpec Spec { get; set; } = new();
    public string ExecutionStatus { get; set; } = "";
    public PortalPomnibotCost Cost { get; set; } = new();
    public PortalPomnibotSession? Session { get; set; }
    public PortalPomnibotWorkspace? Workspace { get; set; }
    public PortalPomnibotOutcome? Outcome { get; set; }
    public string? PendingApprovalId { get; set; }

    public string? RequesterId { get; set; }
    public string? OperationId { get; set; }
    public int SchemaVersion { get; set; }
    public string? SpecHash { get; set; }
}

public sealed class PortalPomnibotSpec
{
    public string? Kind { get; set; }
    public string? RepoId { get; set; }
    public string? TargetEnv { get; set; }
    public string Title { get; set; } = "";
    public string? Slug { get; set; }
    public string Brief { get; set; } = "";
    public decimal? MaxBudgetUsd { get; set; }
    public int? MaxTurns { get; set; }
    public string? Effort { get; set; }
    public bool Quiet { get; set; }
    public string? Language { get; set; }
    public bool AutoAccept { get; set; }
    public string? Engine { get; set; }
    public string? Model { get; set; }
    public string? Profile { get; set; }
}

public sealed class PortalPomnibotCost
{
    public decimal WorkerUsd { get; set; }
    public decimal SupervisorUsd { get; set; }
    public int? Turns { get; set; }
    public int Extensions { get; set; }

    /// <summary>Both halves of the job's own cost; the ledger is the authority, this is the envelope.</summary>
    public decimal TotalUsd => WorkerUsd + SupervisorUsd;
}

public sealed class PortalPomnibotSession
{
    public string? Engine { get; set; }
    public string? SessionId { get; set; }
    public int ResumeCount { get; set; }
    public string? Model { get; set; }
    public string? ClaudeCodeVersion { get; set; }
    public string? Cwd { get; set; }
    public string? AgentId { get; set; }
}

public sealed class PortalPomnibotWorkspace
{
    public string? Path { get; set; }
    public string? Branch { get; set; }
    public string? BaseSha { get; set; }
}

public sealed class PortalPomnibotOutcome
{
    public string? ResultText { get; set; }
    public string? Error { get; set; }
    public string? CommitSha { get; set; }
    public int Commits { get; set; }
    public JsonNode? Diffstat { get; set; }
    public string? PrUrl { get; set; }
    public PortalPomnibotDelivery? Delivery { get; set; }
    public PortalPomnibotReview? Review { get; set; }
}

public sealed class PortalPomnibotDelivery
{
    public string? State { get; set; }
    public string? Base { get; set; }
    public DateTimeOffset? MergedAt { get; set; }
    public string? OpenedBy { get; set; }
}

public sealed class PortalPomnibotReview
{
    public string? Verdict { get; set; }
    public List<string> ResidualRisks { get; set; } = [];
}

/// <summary>One <c>jobs/{name}/runs</c> record: the engine session a run opened.</summary>
public sealed class PortalPomnibotRun
{
    public string? SessionId { get; set; }
    public int Attempt { get; set; }
    public string? Model { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public decimal? CostUsd { get; set; }
}

/// <summary>The body of the <c>getArtifact</c> action: <c>{name, contentType, base64}</c>.</summary>
public sealed class PortalPomnibotArtifactBlob
{
    public string? Name { get; set; }
    public string? ContentType { get; set; }
    public string? Base64 { get; set; }
}

/// <summary>
/// One row of a pomnibot job's own <c>approvals</c> child surface: a filtered view over the core
/// approvals by stream, flat where <see cref="PortalApproval"/> is nested. It carries the decisions
/// that never reached the kernel inbox (a deferred escalation, a denied tool call).
/// </summary>
public sealed class PortalPomnibotApproval
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public string Action { get; set; } = "";
    public string RiskClass { get; set; } = "";
    public string? Preview { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>One booking of the <c>Burbuja.Pomnibot/ledgers/{day}</c> resource.</summary>
public sealed class PortalPomnibotLedgerBooking
{
    public string? Watermark { get; set; }
    public string? JobKey { get; set; }
    public string Kind { get; set; } = "";
    public decimal Usd { get; set; }
    public DateTimeOffset? At { get; set; }
}

/// <summary>
/// The pomnibot ledger day. A separate resource from the Agents ledger (<c>spentUsd</c>,
/// <c>jobCount</c>, bookings keyed by <c>jobKey</c>, no token counts), so it gets its own record
/// rather than borrowing <see cref="PortalLedgerDay"/> and reading zeros for every missing field.
/// </summary>
public sealed class PortalPomnibotLedgerDay
{
    public string Day { get; set; } = "";
    public decimal SpentUsd { get; set; }
    public int JobCount { get; set; }
    public List<PortalPomnibotLedgerBooking> Bookings { get; set; } = [];

    /// <summary>
    /// M10 U8 — the caps this spend is measured against, read off the resource.
    ///
    /// <para>pomnibot's caps (P1.3: warn and auto-extend at $200, stop at $400 by default) are a DIFFERENT
    /// ledger from the kernel's `Budget:DailyHardStopUsd`, and the ledger carries them so a card cannot describe
    /// a job running against a ceiling the host is not using. Zero means no cap, which the resource's own doc
    /// says is a legitimate choice and must not be what you get by accident.</para>
    /// </summary>
    public decimal? SoftCapUsd { get; set; }

    /// <inheritdoc cref="SoftCapUsd"/>
    public decimal? HardStopUsd { get; set; }
}

/// <summary>One <c>Burbuja.Pomnibot/channels/{name}</c> binding (the Discord surface).</summary>
public sealed class PortalPomnibotChannel
{
    public string Provider { get; set; } = "discord";
    public string? GuildId { get; set; }
    public string? JobsChannelId { get; set; }
    public string? MonitorChannelId { get; set; }
    public List<string>? AllowedUserIds { get; set; }

    /// <summary>The human the bot serves. On the card because "who can approve here" is the first question.</summary>
    public string? OwnerId { get; set; }
    public bool Enabled { get; set; }
    public string State { get; set; } = "";
    public string? BotUser { get; set; }
    public string? LogThreadId { get; set; }
    public long Cursor { get; set; }
    public DateTimeOffset? LastPollAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>One <c>Burbuja.Pomnibot/repos</c> binding; only the fields the portal shows are mapped.</summary>
public sealed class PortalPomnibotRepo
{
    public string? HostPath { get; set; }
    public string? Remote { get; set; }
    public string? BaseBranch { get; set; }
    public JsonObject? Envs { get; set; }
    public PortalPomnibotRepoDefaults? Defaults { get; set; }
}

public sealed class PortalPomnibotRepoDefaults
{
    public decimal? BudgetUsd { get; set; }
    public int? Turns { get; set; }
    public string? Effort { get; set; }
}

/// <summary>
/// The pomnibot channel list as a page renders it: the bindings, and whether the resource type
/// exists on this kernel at all. A namespace without <c>channels</c> (the type is newer than the
/// host) is "none registered", never a failed dashboard.
/// </summary>
/// <param name="Hidden">
/// M11 S2 — the caller's roles are denied <c>Burbuja.Pomnibot</c>, so this was NOT requested. A panel that
/// painted the 403 instead is the wall of refusals the round exists to remove, and a panel that painted
/// "absent" would be a lie about the host — which mounts the namespace perfectly well.
/// </param>
public sealed record PortalChannelRead(
    IReadOnlyList<PortalEnvelope<PortalPomnibotChannel>> Channels,
    bool TypeAbsent,
    PortalFailure? Failure,
    bool Hidden = false);

/// <summary>
/// Track 3a: the <c>/health/plugins</c> document, typed. The endpoint used to answer the bare word
/// "Healthy" (one always-Healthy aggregate), so the dashboard read it as a string and could not tell
/// an operator which plugin was in trouble. One entry per loaded plugin now.
/// </summary>
public sealed class PortalPluginHealth
{
    public string Status { get; set; } = "";
    public PortalHostIdentity? Host { get; set; }

    /// <summary>Always false: PluginLoadContext is non-collectible, so a plugin can never be reloaded in place.</summary>
    public bool Reloadable { get; set; }

    public List<PortalPluginHealthEntry> Plugins { get; set; } = [];

    public int HealthyPlugins => Plugins.Count(p => p.IsHealthy);

    /// <summary>One line for the health tile and the probe row.
    ///
    /// M16 K-R2 — the middot between the status word and its count is gone (§7.2: no `·` meta
    /// strings), and `1/2` became words: a slash between two figures is read as a fraction of the
    /// HOST when it is a count of plugins, and the tile's own label already says "Plugin health".</summary>
    public string Summary => Plugins.Count == 0
        ? Status
        : $"{Status}, {HealthyPlugins} of {Plugins.Count} plugins healthy";
}

/// <summary>The kernel process a probe answered from (Track 3a: nodeId/role/pid/build identity).</summary>
public sealed class PortalHostIdentity
{
    public string NodeId { get; set; } = "";
    public string Role { get; set; } = "";
    public int Pid { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string MachineName { get; set; } = "";
    public string OS { get; set; } = "";
    public string Arch { get; set; } = "";
    public string Framework { get; set; } = "";
    public string Version { get; set; } = "";
    public long WorkingSetBytes { get; set; }
    public string? PluginsDirectory { get; set; }
}

public sealed class PortalPluginHealthEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Load { get; set; } = "";
    public bool Trusted { get; set; }
    public List<string> Provides { get; set; } = [];
    public List<string> Requires { get; set; } = [];
    public List<string> HostedServices { get; set; } = [];
    public string Status { get; set; } = "";

    /// <summary>The supervisor state: Registered, Started, StartFailed, Faulted or Stopped.</summary>
    public string State { get; set; } = "";

    public string? Description { get; set; }
    public JsonObject? Data { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? Error { get; set; }

    public bool IsHealthy => string.Equals(Status, "Healthy", StringComparison.OrdinalIgnoreCase);

    /// <summary>The detail column: what the check said, or the last error the supervisor recorded.</summary>
    public string Detail => Description ?? Error ?? "—";
}

public sealed class PortalAgentSession
{
    public string AgentId { get; set; } = "";
    public string StreamId { get; set; } = "";
    public long LastSeq { get; set; }
    public int Turn { get; set; }
    public string Status { get; set; } = "";
    public decimal CostUsd { get; set; }
}

/// <summary>M11 S2 — one seeded <c>roleDefinitions</c> row, as a page reads it.</summary>
public sealed class PortalRoleDefinition
{
    public string RoleName { get; set; } = "";
    public List<string> Actions { get; set; } = [];
    public List<string> NotActions { get; set; } = [];
    public List<string> DataActions { get; set; } = [];
    public List<string> NotDataActions { get; set; } = [];
    public List<string> AssignableScopes { get; set; } = ["/"];
}

public sealed class PortalApproval
{
    public ApprovalRequest? Request { get; set; }
    public JsonObject? Outcome { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }

    /// <summary>
    /// M11 S1 — the KERNEL's answer to "may the caller of this read decide this card", computed with the same
    /// rule the decision verbs apply (the card's subject decides: it needs <c>{ns}/{type}/write</c> at a scope
    /// covering it). The inbox renders the decide controls from this rather than from its own reading of the
    /// caller's roles, because scope is half the rule and the portal's role mirror is not scope-aware.
    /// </summary>
    public bool Decidable { get; set; }
}

public sealed class PortalLedgerBooking
{
    public Guid JobId { get; set; }
    public Guid RunId { get; set; }
    public string Kind { get; set; } = "";
    public decimal Usd { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}

public sealed class PortalLedgerDay
{
    public string Day { get; set; } = "";
    public decimal SpentUsd { get; set; }
    public int JobCount { get; set; }
    public List<PortalLedgerBooking> Bookings { get; set; } = [];

    /// <summary>M10 H3 — the host's daily stop, or null when it has none. Read off the ledger resource, so
    /// the console shows `spent / stop` from the kernel's own field rather than a second configuration copy.</summary>
    public decimal? HardStopUsd { get; set; }

    /// <summary>At or above the stop: the host refuses new spending for this UTC day.</summary>
    public bool HardStopReached { get; set; }
}

/// <summary>
/// One provider namespace as <c>GET /providers</c> lists it.
///
/// <para>The kernel answers the standard ARM registration envelope — <c>name</c> at the top, and
/// <c>namespace</c>/<c>registrationState</c>/<c>resourceTypes</c> under <c>properties</c>. This model
/// used to expect those four keys flattened onto the envelope, so every namespace deserialised to an
/// empty string and the dashboard rendered "N provider namespace(s)" followed by blank chips: the
/// count came from the array, the names from nowhere. Group G's tile fact is what caught it.</para>
/// </summary>
public sealed class PortalProvider
{
    /// <summary>The envelope's own <c>name</c>; the same value as <see cref="PortalProviderProperties.Namespace"/>.</summary>
    public string Name { get; set; } = "";

    public PortalProviderProperties Properties { get; set; } = new();

    /// <summary>The namespace to display: the envelope name, falling back to the properties copy.</summary>
    public string DisplayNamespace => Name is { Length: > 0 } ? Name : Properties.Namespace;

    public IReadOnlyList<PortalProviderType> ResourceTypes => Properties.ResourceTypes;
}

public sealed class PortalProviderProperties
{
    public string Namespace { get; set; } = "";

    public string RegistrationState { get; set; } = "";

    public List<PortalProviderType> ResourceTypes { get; set; } = [];
}

public sealed class PortalProviderType
{
    public string ResourceType { get; set; } = "";

    public bool IsTracked { get; set; }

    public bool IsAsyncPut { get; set; }

    public List<string> ApiVersions { get; set; } = [];

    public List<PortalProviderAction> Actions { get; set; } = [];

    /// <summary>
    /// M11 U12 — the type's DATA-PLANE routes, which is what the Actions tab needs in order to hide the right
    /// thing. The tab used to filter on <c>!IsDataAction</c>, a descriptor field that <b>nothing reads when
    /// authorizing</b> (M10 H2's finding) — but the tab was reading it as "is this a data-plane route", and for
    /// <c>workspaces/initialize</c> the answer to that is NO: it is an ordinary POST verb that happens to be
    /// declared as a data action, so the one action that deploys a workspace was the one the tab hid. What the
    /// tab must actually hide is a route that streams or downloads (<c>attach</c>, <c>download</c>), because a
    /// POST to those is not a button.
    /// </summary>
    public List<PortalProviderDataPlaneRoute> DataPlaneRoutes { get; set; } = [];

    /// <summary>
    /// The CHILD types the descriptor declares (<c>workspaces/jobs</c> for a workspace, <c>verdicts</c> for a
    /// mandate). U2's Children tab renders exactly this list, which is why it is the descriptor's and not the
    /// portal's: a type that gains a child gains the tab with no front-door change.
    /// </summary>
    public List<string> Children { get; set; } = [];

    /// <summary>
    /// What the type supports, as the kernel's descriptor spells it: <c>Get</c>, <c>Put</c>,
    /// <c>ListByResourceGroup</c>, <c>ListBySubscription</c>, <c>Actions</c>.
    ///
    /// <para><b>This is what tells a generic explorer WHERE to list a type.</b> U2's first version listed
    /// everything under the current resource group, which is right for most types and wrong for every
    /// subscription-level one — `Burbuja.Resources/subscriptions` lives at
    /// <c>/subscriptions/{sub}/providers/…</c> and has no resource group at all, so the namespace page
    /// rendered an empty grid for the kernel's own RP. The capability was already on the wire; the portal
    /// simply was not reading it.</para>
    /// </summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Whether a listing for this type belongs under a resource group.</summary>
    public bool ListsByResourceGroup => Capabilities.Contains("ListByResourceGroup", StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a listing for this type belongs under the subscription itself.</summary>
    public bool ListsBySubscription => Capabilities.Contains("ListBySubscription", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where a listing for this type lives, given the current scope. A type that supports BOTH is listed
    /// under the resource group when there is one: the narrower scope is the one a reader is standing in.
    /// A type that supports neither is <c>null</c>, which the page reports rather than calling a path the
    /// kernel would refuse — an explorer that guesses is an explorer whose empty grid means two things.
    /// </summary>
    public string? ListRoot(ArmScope scope) =>
        IsChildType ? null
        : ListsByResourceGroup ? scope.RootPath
        : ListsBySubscription ? $"/subscriptions/{scope.SubscriptionId}"
        : null;

    /// <summary>
    /// Whether this type is a CHILD of another one — <c>workspaces/jobs</c>, <c>mandates/proposals</c>.
    ///
    /// <para><b>Why the capability alone is not enough.</b> A child type declares <c>ListByResourceGroup</c>
    /// too, and it means "you may list instances of this type inside a parent instance", not "there is a
    /// collection of them under the resource group". Reading the capability as the second thing is what made
    /// the search page ask for <c>…/providers/Burbuja.Agents/workspaces/jobs</c> and collect a
    /// <c>ResourceNotFound</c> for a type that is perfectly listable one segment deeper. The explorer would
    /// have done the same thing on that type's page, and no fact had opened one.</para>
    /// </summary>
    public bool IsChildType => ResourceType.Contains('/', StringComparison.Ordinal);

    /// <summary>
    /// The parent type's name for a child (<c>workspaces/jobs</c> → <c>workspaces</c>, and
    /// <c>workspaces/jobs/runs</c> → <c>workspaces/jobs</c>).
    ///
    /// <para><b>The LAST slash, not the first.</b> The first version used <c>IndexOf</c> and <c>LeafType</c>
    /// used <c>LastIndexOf</c>, so the pair disagreed for a GRANDCHILD: <c>workspaces/jobs/runs</c> reported
    /// its parent as <c>workspaces</c> and its leaf as <c>runs</c>, and the search asked a workspace for its
    /// runs — a request the router answers 400 and the page reported as a failure. A helper whose two halves
    /// use different delimiters is a helper that is right for one depth only, and the browser lane found it
    /// on the live host's deeper type tree.</para>
    /// </summary>
    public string? ParentType => IsChildType
        ? ResourceType[..ResourceType.LastIndexOf('/')]
        : null;

    /// <summary>The child's own last segment (<c>workspaces/jobs</c> → <c>jobs</c>).</summary>
    public string LeafType => IsChildType
        ? ResourceType[(ResourceType.LastIndexOf('/') + 1)..]
        : ResourceType;
}

public sealed class PortalProviderAction
{
    public string Name { get; set; } = "";
    public bool IsDataAction { get; set; }
}

/// <summary>One declared data-plane route: its path segment and its kind (<c>Stream</c>, <c>Download</c>, …).</summary>
public sealed class PortalProviderDataPlaneRoute
{
    public string Segment { get; set; } = "";
    public string Kind { get; set; } = "";
}

/// <summary>
/// One cron schedule, as <c>Burbuja.Pomnibot/schedules</c> holds it.
///
/// <para><b>The two timestamps are the point of the row.</b> <c>lastFiredAt</c> and <c>nextRunAt</c> are what
/// the kernel's sweeper actually wrote, so a page that shows them can answer "has this ever run" — which is
/// the question a schedule that has silently never fired makes impossible to answer from the cron alone.</para>
/// </summary>
public sealed class PortalSchedule
{
    public string Cron { get; set; } = "";

    public string? RepoId { get; set; }

    public bool Paused { get; set; }

    public string? TimeZone { get; set; }

    public DateTimeOffset? LastFiredAt { get; set; }

    public DateTimeOffset? NextRunAt { get; set; }

    public string? LastJobName { get; set; }

    /// <summary>What the schedule calls itself, for a reader with more than one.</summary>
    public string? Title { get; set; }
}

/// <summary>
/// One external tool surface, as <c>Burbuja.Integrations/integrations</c> holds it.
///
/// <para><b><c>Tools</c> and <c>DiscoveredTools</c> are deliberately both here and deliberately different.</b>
/// The first is what a human declared and is therefore what may be CALLED; the second is what the surface
/// last reported. A page that showed only one of them would hide the drift between "what we allow" and "what
/// exists", which is the drift an operator opens this page to see.</para>
/// </summary>
public sealed class PortalIntegration
{
    public string Kind { get; set; } = "";

    public bool Enabled { get; set; }

    public List<string> Tools { get; set; } = [];

    public List<string> DevHosts { get; set; } = [];

    public string? State { get; set; }

    public DateTimeOffset? ProbedAt { get; set; }

    public List<string> DiscoveredTools { get; set; } = [];

    public int Calls { get; set; }

    public PortalIntegrationTransport? Transport { get; set; }
}

/// <summary>
/// How the kernel reaches a surface. <c>SecretRef</c> is the NAME of a secret and never its value — the
/// resource has never held anything else, which is why a page may show it.
/// </summary>
public sealed class PortalIntegrationTransport
{
    public string Mode { get; set; } = "";

    public string? Url { get; set; }

    public string? Command { get; set; }

    public string? SecretRef { get; set; }
}

/// <summary>Why a portal call could not complete, in a shape a page can render.</summary>
public sealed record PortalFailure(string Code, string Message, string? Target, int? Status)
{
    public static PortalFailure From(ArmException ex) =>
        new(ex.Error.Code, ex.Error.Message, ex.Error.Target, ex.Error.HttpStatus);

    public static PortalFailure Local(string code, string message) => new(code, message, null, null);
}

/// <summary>Where a page sits in its load/render cycle.</summary>
public enum PortalLoadState
{
    Idle,
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// The kernel answers <c>null</c> nextFrom explicitly (its page record ignores WhenWritingNull),
/// so a hand-written page record keeps it nullable too.
/// </summary>
public sealed record PortalEventPage
{
    public IReadOnlyList<Burbuja.Core.Abstractions.Events.LedgerEvent> Value { get; init; } = [];
    public int Count { get; init; }
    [JsonPropertyName("nextFrom")]
    public long? NextFrom { get; init; }
}

/// <summary>
/// M13 R5 — one list read, in the only three shapes the console prints.
///
/// <para><b>Why the distinction is a type and not a comment.</b> The plan's rule is that <em>ausente</em> and
/// <em>vacío</em> are never the same word: a type this host does not serve is a fact about the HOST, and a
/// type it serves with no rows is a fact about the WORK. A console that printed "nothing here" for both would
/// answer "has this host ever run this workload" with a shrug — and the two empty states ask a reader to do completely
/// different things (mount the plugin, versus wait for a turn).</para>
/// </summary>
public sealed record PortalRead<T>(IReadOnlyList<T> Rows, bool TypeAbsent)
{
    /// <summary>The host serves the type and the collection holds nothing.</summary>
    public bool Empty => !TypeAbsent && Rows.Count == 0;

    /// <summary>How many rows the RP returned. The ONLY arithmetic the console performs.</summary>
    public int Count => Rows.Count;
}

/// <summary>
/// T4: the answer to a trajectory search. <see cref="Scanned"/> is how many events the kernel read to
/// find <see cref="Events"/>, and <see cref="Complete"/> says whether that was the whole stream — the
/// two facts that separate "nothing matched" from "nothing matched in the part I looked at".
/// </summary>
public sealed record PortalEventSearch(
    IReadOnlyList<LedgerEvent> Events,
    long Scanned,
    bool Complete,
    IReadOnlyList<string> Terms);
