using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Sdk.Authorization;

namespace Burbuja.Core.Arm.Rps;

public sealed class SubscriptionProperties
{
    public string State { get; set; } = "Enabled";
    public string DisplayName { get; set; } = "";
}

public sealed class ResourceGroupProperties
{
    public string ProvisioningState { get; set; } = "Succeeded";
}

public sealed class ProviderProperties
{
    public string Namespace { get; set; } = "";
    public string RegistrationState { get; set; } = nameof(ProviderRegistrationState.NotRegistered);
    public IReadOnlyList<ProviderResourceTypeManifest> ResourceTypes { get; set; } = [];
}

public sealed class OperationProperties
{
    public string Status { get; set; } = "Running";
}

public sealed class RoleDefinitionProperties
{
    public string RoleName { get; set; } = "";
    public List<string> Actions { get; set; } = [];
    public List<string> NotActions { get; set; } = [];
    public List<string> DataActions { get; set; } = [];
    public List<string> NotDataActions { get; set; } = [];
    public List<string> AssignableScopes { get; set; } = ["/"];
}

public sealed class RoleAssignmentProperties
{
    public string RoleDefinitionId { get; set; } = "";
    public string PrincipalId { get; set; } = "";
    public string PrincipalType { get; set; } = "User";
    public string? Condition { get; set; }
    public string? ConditionVersion { get; set; }
}

public sealed class LockProperties
{
    public string Level { get; set; } = "CanNotDelete";
    public string? Notes { get; set; }
}


public sealed class ResourcesProvider : IResourceProvider
{
    public string Namespace => ArmConstants.ResourcesNamespace;

    public IReadOnlyList<ResourceTypeDescriptor> ResourceTypes { get; } =
    [
        DescriptorBuilder.For("subscriptions").Properties<SubscriptionProperties>()
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.Put | ResourceCapabilities.List | ResourceCapabilities.ListBySubscription)
            .Build(),
        DescriptorBuilder.For("resourceGroups").Properties<ResourceGroupProperties>()
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.Put | ResourceCapabilities.Patch | ResourceCapabilities.Delete | ResourceCapabilities.ListBySubscription | ResourceCapabilities.ListByResourceGroup)
            .Build(),
        DescriptorBuilder.For("providers").Properties<ProviderProperties>()
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.List | ResourceCapabilities.Actions)
            .Actions(
                new ResourceActionDescriptor("register", true),
                new ResourceActionDescriptor(
                    "unregister",
                    false,
                    false,
                    SchemaBuilder.Parse("""{"type":"object","required":["reason"],"properties":{"reason":{"type":"string"}},"additionalProperties":false}""")))
            .Build(),
        DescriptorBuilder.For("operations").Properties<OperationProperties>()
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.List)
            .Build(),
        // K2: the recent requests, as a read-only projection over the ring. Tracked(false) because nothing
        // is stored: the ring is the state and a second copy would be a copy that can disagree with it.
        DescriptorBuilder.For("activity").Properties<ArmActivityRow>()
            .Tracked(false)
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.List | ResourceCapabilities.ListBySubscription | ResourceCapabilities.ListByResourceGroup)
            .Build(),
        DescriptorBuilder.For("approvals").Properties<ApprovalProperties>()
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.List | ResourceCapabilities.ListBySubscription | ResourceCapabilities.Actions)
            .Actions(
                new ResourceActionDescriptor("approve", false),
                new ResourceActionDescriptor("deny", false),
                new ResourceActionDescriptor("answer", false),
                new ResourceActionDescriptor("cancel", false))
            .Build(),
    ];

    public ValueTask OnSubscriptionStateChangedAsync(SubscriptionNotification notification, CancellationToken ct) => default;
}

public sealed class AuthorizationProvider : IResourceProvider
{
    public string Namespace => ArmConstants.AuthorizationNamespace;

    public IReadOnlyList<ResourceTypeDescriptor> ResourceTypes { get; } =
    [
        DescriptorBuilder.For("roleDefinitions").Properties<RoleDefinitionProperties>()
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.Put | ResourceCapabilities.Delete | ResourceCapabilities.ListBySubscription | ResourceCapabilities.ListByResourceGroup)
            .Tracked(false)
            .Build(),
        DescriptorBuilder.For("roleAssignments").Properties<RoleAssignmentProperties>()
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.Put | ResourceCapabilities.Delete | ResourceCapabilities.ListBySubscription | ResourceCapabilities.ListByResourceGroup)
            .Tracked(false)
            .Build(),
        DescriptorBuilder.For("locks").Properties<LockProperties>()
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.Put | ResourceCapabilities.Delete | ResourceCapabilities.ListBySubscription | ResourceCapabilities.ListByResourceGroup)
            .Tracked(true)
            .AsyncPut()
            .Build(),
    ];

    public ValueTask OnSubscriptionStateChangedAsync(SubscriptionNotification notification, CancellationToken ct) => default;
}

public sealed class SubscriptionHandler(IResourceStore store, IClock clock, IArmDirectory directory)
    : EnvelopeHandler<SubscriptionProperties>(store, clock)
{
    protected override string TypeName => $"{ArmConstants.ResourcesNamespace}/subscriptions";

    public override async ValueTask<PutOutcome<SubscriptionProperties>> PutAsync(ResourceRequest req, SubscriptionProperties desired, CancellationToken ct)
    {
        directory.UpsertSubscription(new SubscriptionRecord
        {
            Id = req.Id.SubscriptionId,
            DisplayName = desired.DisplayName,
            State = Enum.TryParse<SubscriptionState>(desired.State, true, out var st) ? st : SubscriptionState.Enabled,
        });
        return await base.PutAsync(req, desired, ct);
    }

    public override async ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct)
    {
        directory.RemoveSubscription(req.Id.SubscriptionId);
        directory.RemoveResourceGroupsFor(req.Id.SubscriptionId);
        foreach (var rp in req.Services.GetService(typeof(IEnumerable<IResourceProvider>)) as IEnumerable<IResourceProvider> ?? [])
            await rp.OnSubscriptionStateChangedAsync(new SubscriptionNotification(req.Id.SubscriptionId, SubscriptionState.Deleted), ct);
        return await base.DeleteAsync(req, ct);
    }
}

public sealed class ResourceGroupHandler(IResourceStore store, IClock clock, IArmDirectory directory)
    : EnvelopeHandler<ResourceGroupProperties>(store, clock)
{
    protected override string TypeName => $"{ArmConstants.ResourcesNamespace}/resourceGroups";

    public override async ValueTask<PutOutcome<ResourceGroupProperties>> PutAsync(ResourceRequest req, ResourceGroupProperties desired, CancellationToken ct)
    {
        var name = req.Id.ResourceGroup ?? req.Id.NameSegments.LastOrDefault() ?? "";
        directory.UpsertResourceGroup(new ResourceGroupRecord
        {
            SubscriptionId = req.Id.SubscriptionId,
            Name = name,
        });
        return await base.PutAsync(req, desired, ct);
    }

    public override async ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct)
    {
        var name = req.Id.ResourceGroup ?? req.Id.NameSegments.LastOrDefault() ?? "";
        directory.RemoveResourceGroup(req.Id.SubscriptionId, name);
        return await base.DeleteAsync(req, ct);
    }
}

public sealed class ProviderHandler(IResourceStore store, IClock clock, IArmDirectory directory) : EnvelopeHandler<ProviderProperties>(store, clock)
{
    protected override string TypeName => $"{ArmConstants.ResourcesNamespace}/providers";

    public override ValueTask<ResourceEnvelope<ProviderProperties>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var table = req.Services.GetService(typeof(ArmRoutingTable)) as ArmRoutingTable;
        if (table is null)
            return default;
        var ns = req.Id.Namespace;
        if (!table.TryGetProvider(ns, out var provider))
            return default;
        return new(ToEnvelope(req, provider, LookupRegistration(req.Id.SubscriptionId, ns)));
    }

    public override ValueTask<PagedResult<ResourceEnvelope<ProviderProperties>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var table = req.Services.GetService(typeof(ArmRoutingTable)) as ArmRoutingTable;
        if (table is null)
            return new(new PagedResult<ResourceEnvelope<ProviderProperties>>([], null));

        if (req.Id.FullyQualifiedType.Equals("operations", StringComparison.OrdinalIgnoreCase))
        {
            var ns = req.Id.Namespace;
            var actions = table.ActionCatalogue
                .Where(a => a.StartsWith(ns + "/", StringComparison.OrdinalIgnoreCase)
                            || a.Equals($"{ns}/register/action", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .Select(a => new ResourceEnvelope<ProviderProperties>(
                    $"/providers/{ns}/operations/{a}",
                    a,
                    $"{ns}/operations",
                    "local",
                    new Dictionary<string, string>(),
                    new ProviderProperties { Namespace = ns, RegistrationState = LookupRegistration(req.Id.SubscriptionId, ns) },
                    ProvisioningState.Succeeded,
                    new SystemData(Clock.UtcNow, null, null, null, null, null),
                    "W/\"1\""))
                .ToList();
            return new(new PagedResult<ResourceEnvelope<ProviderProperties>>(actions, null));
        }

        var items = table.Providers
            .OrderBy(p => p.Namespace, StringComparer.OrdinalIgnoreCase)
            .Select(p => ToEnvelope(req, p, LookupRegistration(req.Id.SubscriptionId, p.Namespace)))
            .ToList();
        return new(new PagedResult<ResourceEnvelope<ProviderProperties>>(items, null));
    }

    public override async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var ns = req.Id.Namespace;
        if (string.Equals(req.Action, "register", StringComparison.OrdinalIgnoreCase))
        {
            directory.SetProviderRegistration(req.Id.SubscriptionId, ns, ProviderRegistrationState.Registered);
            var envelope = new ResourceEnvelope<JsonObject>(
                $"/subscriptions/{req.Id.SubscriptionId}/providers/{ns}",
                ns,
                TypeName,
                "local",
                new Dictionary<string, string>(),
                new JsonObject { ["namespace"] = ns, ["registrationState"] = nameof(ProviderRegistrationState.Registered) },
                ProvisioningState.Succeeded,
                new SystemData(Clock.UtcNow, req.Principal.PrincipalId, req.Principal.PrincipalType, Clock.UtcNow, req.Principal.PrincipalId, req.Principal.PrincipalType),
                "");
            await Store.UpsertAsync(envelope, null, ct);
            return new ActionOutcome.Ok(new JsonObject
            {
                ["namespace"] = ns,
                ["registrationState"] = nameof(ProviderRegistrationState.Registered),
            });
        }

        if (string.Equals(req.Action, "unregister", StringComparison.OrdinalIgnoreCase))
        {
            directory.SetProviderRegistration(req.Id.SubscriptionId, ns, ProviderRegistrationState.Unregistered);
            return new ActionOutcome.Ok(new JsonObject
            {
                ["namespace"] = ns,
                ["registrationState"] = nameof(ProviderRegistrationState.Unregistered),
            });
        }

        return await base.InvokeAsync(req, ct);
    }

    private string LookupRegistration(string subscriptionId, string ns)
    {
        if (subscriptionId.Length > 0
            && directory.TryGetProviderRegistration(subscriptionId, ns, out var state))
            return state.ToString();
        return nameof(ProviderRegistrationState.Registered);
    }

    private ResourceEnvelope<ProviderProperties> ToEnvelope(ResourceRequest req, IResourceProvider provider, string registrationState)
    {
        var id = req.Id.SubscriptionId.Length > 0
            ? $"/subscriptions/{req.Id.SubscriptionId}/providers/{provider.Namespace}"
            : $"/providers/{provider.Namespace}";
        var manifest = ProviderManifestMapper.From(provider, registrationState);
        return new ResourceEnvelope<ProviderProperties>(
            id,
            provider.Namespace,
            TypeName,
            "local",
            new Dictionary<string, string>(),
            new ProviderProperties
            {
                Namespace = manifest.Namespace,
                RegistrationState = manifest.RegistrationState,
                ResourceTypes = manifest.ResourceTypes,
            },
            ProvisioningState.Succeeded,
            new SystemData(Clock.UtcNow, req.Principal.PrincipalId, req.Principal.PrincipalType, Clock.UtcNow, req.Principal.PrincipalId, req.Principal.PrincipalType),
            "W/\"1\"");
    }
}

public sealed class OperationHandler(IOperationTracker ops) : IResourceHandler<OperationProperties>
{
    public async ValueTask<ResourceEnvelope<OperationProperties>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        var name = req.Id.NameSegments.LastOrDefault() ?? "";
        var op = await ops.GetAsync(name, ct);
        if (op is null)
            return null;
        return new ResourceEnvelope<OperationProperties>(
            op.Id, op.Name, $"{ArmConstants.ResourcesNamespace}/operations", "local",
            new Dictionary<string, string>(),
            new OperationProperties { Status = op.Status.ToString() },
            op.Status == OperationStatus.Succeeded ? ProvisioningState.Succeeded : ProvisioningState.Accepted,
            new SystemData(op.StartTime, null, null, op.EndTime, null, null),
            $"W/\"1\"");
    }

    public ValueTask<PutOutcome<OperationProperties>> PutAsync(ResourceRequest req, OperationProperties desired, CancellationToken ct) =>
        new(new PutOutcome<OperationProperties>.Rejected(ArmError.Validation("operations are read-only.", [])));

    public ValueTask<PutOutcome<OperationProperties>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<OperationProperties>.Rejected(ArmError.Validation("operations are read-only.", [])));

    public ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct) =>
        new(new DeleteOutcome.Rejected(ArmError.Validation("operations are read-only.", [])));

    public async ValueTask<PagedResult<ResourceEnvelope<OperationProperties>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        var list = await ops.ListAsync(
            req.Id.SubscriptionId.Length == 0 ? null : req.Id.SubscriptionId,
            req.Id.Namespace.Length == 0 ? null : req.Id.Namespace,
            ct);
        var envs = list.Select(op => new ResourceEnvelope<OperationProperties>(
            op.Id, op.Name, $"{ArmConstants.ResourcesNamespace}/operations", "local",
            new Dictionary<string, string>(),
            new OperationProperties { Status = op.Status.ToString() },
            op.Status == OperationStatus.Succeeded ? ProvisioningState.Succeeded : ProvisioningState.Accepted,
            new SystemData(op.StartTime, null, null, op.EndTime, null, null),
            "W/\"1\"")).ToList();
        return new PagedResult<ResourceEnvelope<OperationProperties>>(envs, null);
    }

    public ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct) =>
        new(new ActionOutcome.Rejected(ArmError.Validation("unknown action", [])));
}

public sealed class RoleDefinitionHandler(IResourceStore store, IClock clock, AuthorizationEvaluator evaluator)
    : EnvelopeHandler<RoleDefinitionProperties>(store, clock)
{
    protected override string TypeName => $"{ArmConstants.AuthorizationNamespace}/roleDefinitions";

    public override async ValueTask<PutOutcome<RoleDefinitionProperties>> PutAsync(ResourceRequest req, RoleDefinitionProperties desired, CancellationToken ct)
    {
        var name = req.Id.NameSegments.LastOrDefault() ?? Guid.NewGuid().ToString("N");
        evaluator.UpsertDefinition(AuthorizationEvaluator.ToDefinition(name, desired));
        return await base.PutAsync(req, desired, ct);
    }

    public override async ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct)
    {
        var name = req.Id.NameSegments.LastOrDefault() ?? "";
        evaluator.RemoveDefinition(name);
        return await base.DeleteAsync(req, ct);
    }
}

public sealed class RoleAssignmentHandler : ChildEnvelopeHandler<RoleAssignmentProperties>
{
    private readonly AuthorizationEvaluator _evaluator;

    public RoleAssignmentHandler(IResourceStore store, IClock clock, AuthorizationEvaluator evaluator)
        : base(store, clock)
    {
        _evaluator = evaluator;
    }

    protected override string TypeName => $"{ArmConstants.AuthorizationNamespace}/roleAssignments";

    public override async ValueTask<PutOutcome<RoleAssignmentProperties>> PutAsync(ResourceRequest req, RoleAssignmentProperties desired, CancellationToken ct)
    {
        var name = req.Id.NameSegments.LastOrDefault() ?? Guid.NewGuid().ToString("N");
        var scope = ArmActionMatcher.ExtensionParentScope(req.Id.Canonical, ArmConstants.AuthorizationNamespace, "roleAssignments");
        var roleName = AuthorizationEvaluator.RoleNameFromId(desired.RoleDefinitionId);
        if (!_evaluator.TryGetDefinition(roleName, out var role))
            return new PutOutcome<RoleAssignmentProperties>.Rejected(ArmError.Validation($"Role definition '{desired.RoleDefinitionId}' was not found.", []));

        if (!role.AssignableScopes.Any(s => ArmActionMatcher.AssignmentCoversRequest(s, scope)))
            return new PutOutcome<RoleAssignmentProperties>.Rejected(ArmError.Forbidden($"Role '{roleName}' is not assignable at scope '{scope}'."));

        _evaluator.UpsertAssignment(new RoleAssignment(
            name,
            scope,
            desired.PrincipalId,
            roleName,
            Clock.UtcNow,
            req.Principal.PrincipalId,
            string.IsNullOrWhiteSpace(desired.Condition) ? null : desired.Condition,
            desired.ConditionVersion));
        return await base.PutAsync(req, desired, ct);
    }

    public override async ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct)
    {
        var name = req.Id.NameSegments.LastOrDefault() ?? "";
        _evaluator.RemoveAssignment(name);
        return await base.DeleteAsync(req, ct);
    }
}

public sealed class LockHandler(IResourceStore store, IClock clock, IOperationTracker ops) : ChildEnvelopeHandler<LockProperties>(store, clock)
{
    protected override string TypeName => $"{ArmConstants.AuthorizationNamespace}/locks";

    public override async ValueTask<PutOutcome<LockProperties>> PutAsync(ResourceRequest req, LockProperties desired, CancellationToken ct)
    {
        if (desired.Level is not "CanNotDelete" and not "ReadOnly")
            return new PutOutcome<LockProperties>.Rejected(ArmError.Validation("Level must be CanNotDelete or ReadOnly.", []));

        var created = await base.PutAsync(req, desired, ct);
        var env = created switch
        {
            PutOutcome<LockProperties>.Created c => c.Resource,
            PutOutcome<LockProperties>.Updated u => u.Resource,
            _ => null,
        };
        if (env is null)
            return created;
        var opId = Guid.NewGuid().ToString("N");
        await ops.StartAsync(opId, req.Id.SubscriptionId, req.Id.Namespace.Length > 0 ? req.Id.Namespace : ArmConstants.AuthorizationNamespace, "local", env.Id, null, ct);
        await ops.SucceedAsync(opId, null, ct);
        return new PutOutcome<LockProperties>.Accepted(env with { ProvisioningState = ProvisioningState.Succeeded }, opId, TimeSpan.FromSeconds(1));
    }
}

public sealed class ApprovalProperties
{
    public ApprovalRequest? Request { get; set; }
    public ApprovalOutcome? Outcome { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }

    /// <summary>
    /// M11 S1 — whether the CALLER of this read may decide this card, answered by the kernel with the same
    /// rule the decision verbs apply. It is on the projection because the alternative is a console that
    /// re-implements the rule, and a console that re-implements an authorisation rule is a second answer to a
    /// question that has one — wrong exactly where scope matters.
    ///
    /// <para>A pending card the caller cannot decide is not hidden from a READER (the record is public inside
    /// the team), but the inbox groups and labels by it: the plan's rule is that the inbox hides the DECIDE
    /// controls it would refuse, and says how many it did.</para>
    /// </summary>
    public bool Decidable { get; set; }
}

public sealed class ApprovalHandler(IApprovalService approvals, IApprovalDecider decider) : IResourceHandler<ApprovalProperties>
{
    public async ValueTask<ResourceEnvelope<ApprovalProperties>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        var id = req.Id.NameSegments.LastOrDefault() ?? "";
        var rec = await approvals.GetAsync(id, ct);
        if (rec is null)
            return null;
        // A card with no outcome is the only one a decision applies to; a decided one is a record, and
        // "decidable" on a record would invite a second decision on something already settled.
        var decidable = rec.Outcome is null && await decider.RefuseAsync(rec, req.Principal, ct).ConfigureAwait(false) is null;
        return Map(rec, req.Id.Canonical, decidable);
    }

    public ValueTask<PutOutcome<ApprovalProperties>> PutAsync(ResourceRequest req, ApprovalProperties desired, CancellationToken ct) =>
        new(new PutOutcome<ApprovalProperties>.Rejected(ArmError.Validation("approvals are opened by the kernel, not PUT.", [])));

    public ValueTask<PutOutcome<ApprovalProperties>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<ApprovalProperties>.Rejected(ArmError.Validation("approvals are immutable.", [])));

    public ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct) =>
        new(new DeleteOutcome.Rejected(ArmError.Validation("delete an approval with the cancel action.", [])));

    public async ValueTask<PagedResult<ResourceEnvelope<ApprovalProperties>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        var page = await approvals.ListAsync(new ApprovalQuery(
            req.Id.SubscriptionId.Length == 0 ? null : req.Id.SubscriptionId,
            null,
            null,
            req.Query.TryGetValue("streamId", out var stream) ? stream : null,
            req.Query.TryGetValue("pendingOnly", out var pending) && pending.Equals("true", StringComparison.OrdinalIgnoreCase),
            req.Top,
            req.ContinuationToken), ct);
        var idPrefix = req.Id.SubscriptionId.Length == 0
            ? "/providers/Burbuja.Resources/approvals"
            : $"/subscriptions/{req.Id.SubscriptionId}/providers/Burbuja.Resources/approvals";
        // One evaluation per DISTINCT subject: a page of cards opened by the same work shares a subject, and
        // authorising is the expensive part of this read.
        var decidable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ResourceEnvelope<ApprovalProperties>>();
        foreach (var r in page.Value)
        {
            if (!decidable.TryGetValue(r.Request.SubjectId ?? "", out var can))
            {
                can = r.Outcome is null
                    && await decider.RefuseAsync(r, req.Principal, ct).ConfigureAwait(false) is null;
                decidable[r.Request.SubjectId ?? ""] = can;
            }

            rows.Add(Map(r, $"{idPrefix}/{r.Id}", can));
        }

        return new PagedResult<ResourceEnvelope<ApprovalProperties>>(rows, page.NextLink);
    }

    public async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var id = req.Id.NameSegments.LastOrDefault() ?? "";
        var note = req.Body?["note"]?.GetValue<string>();

        // An action on an approval nobody opened is a 404, never a cheerful 200: the store returns
        // the requested outcome unchanged for a missing id, so silently reporting "approved" would
        // tell the caller a decision was recorded when nothing happened.
        var record = await approvals.GetAsync(id, ct);
        if (record is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(req.Id.Canonical, id));

        // M11 S1 — DECIDE ONLY WHAT YOU COULD HAVE ASKED FOR, and record who decided. The verbs used to be
        // authorised as unscoped actions on a kernel-wide resource, so a demo user could settle the owner's
        // mandate transitions, a pomnibot card, or its own persona's parked tool call. Now the card's SUBJECT
        // decides: the request must hold `{ns}/{type}/write` at the subject's scope, which is the permission
        // that would have let the decider open the thing in the first place.
        if (await decider.RefuseAsync(record, req.Principal, ct).ConfigureAwait(false) is { } refusal)
            return new ActionOutcome.Rejected(refusal);

        // `cancel` is deliberately NOT gated: withdrawing your own question is not deciding somebody else's.
        var actor = req.Principal.PrincipalId;
        var outcome = req.Action.ToLowerInvariant() switch
        {
            "approve" => await approvals.ApproveAsync(
                id,
                note,
                req.Body?["grantScope"]?.GetValue<string>(),
                req.Body?["grantHours"] is { } hoursNode ? hoursNode.GetValue<double>() : null,
                ct,
                actor),
            "deny" => await approvals.DenyAsync(id, note, ct, actor),
            "answer" => await approvals.AnswerAsync(id, req.Body?["answer"]?.GetValue<string>() ?? "", ct, actor),
            "cancel" => await approvals.CancelAsync(id, ct, actor),
            _ => (ApprovalOutcome?)null,
        };
        if (outcome is null)
            return new ActionOutcome.Rejected(ArmError.ActionNotSupported($"Unknown approval action '{req.Action}'."));
        return new ActionOutcome.Ok(new JsonObject { ["status"] = outcome.GetType().Name.ToLowerInvariant() });
    }

    private static ResourceEnvelope<ApprovalProperties> Map(ApprovalRecord rec, string canonical, bool decidable) =>
        new(
            canonical,
            rec.Id,
            $"{ArmConstants.ResourcesNamespace}/approvals",
            "local",
            new Dictionary<string, string>(),
            new ApprovalProperties
            {
                Request = rec.Request,
                Outcome = rec.Outcome,
                OpenedAt = rec.OpenedAt,
                ExpiresAt = rec.ExpiresAt,
                DecidedAt = rec.DecidedAt,
                DecidedBy = rec.DecidedBy,
                Decidable = decidable,
            },
            rec.Outcome is null ? ProvisioningState.Accepted : ProvisioningState.Succeeded,
            new SystemData(rec.OpenedAt, rec.DecidedBy, null, rec.DecidedAt, rec.DecidedBy, null),
            "W/\"1\"");
}
