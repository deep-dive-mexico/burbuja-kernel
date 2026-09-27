using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Arm;
using Microsoft.Extensions.Configuration;

namespace Burbuja.Integrations;

/// <summary>
/// I0: <c>integrations</c> — an external tool surface as a resource, with every call decided by the kernel's
/// own policy pipeline.
///
/// <para><b>This is the producer the browser and mail classifiers never had.</b> `BrowserClassifier` and
/// `MailClassifier` were written in P1.5 and judged 100 % against nightshift's ported corpus; nothing in the
/// host ever produced a `mcp__browser__*` or `outlook_*` action, so the rules were exercised by the replay
/// and by nobody else. A call through this resource is classified under the tool's own name, which is the
/// vocabulary those classifiers key on — the same pipeline, the same rules, no second gate.</para>
///
/// <para><b>The gate is not re-implemented here.</b> Intent and decision are appended to the integration's
/// stream in that order (the seq of the intent is strictly lower than the decision's), a `HardDeny` or a
/// forbidden tier is final, a `Gray` or an escalation opens an approval and returns 202 with its id, and a
/// `Clarify` — the tier that means "this cannot be judged" — refuses rather than proceeding. That is the
/// same sequence `compute` and the agent tool gate follow, deliberately: a second gate with its own
/// ordering would be a second answer to "may this happen".</para>
///
/// <para><b>The one difference from the agent path is the retry.</b> An agent's escalation PARKS the turn
/// and the run resumes with the approval id; a resource action cannot park anything, so it answers 202 and
/// the caller retries with the id it was handed. The retry is honoured only when the approval is approved,
/// carries the same risk class, and was opened for the same decision — a grant for one class never
/// authorises another.</para>
/// </summary>
public sealed class IntegrationHandler(
    IResourceStore store,
    IClock clock,
    IPolicyPipeline<ActionPolicySubject> policy,
    IApprovalService approvals,
    IEventLog events,
    IRedactor redactor,
    IConfiguration configuration) : EnvelopeHandler<IntegrationProperties>(store, clock)
{
    public const string Probe = "probe";
    public const string Call = "call";

    public const string IntentEvent = "integration.intent";
    public const string DecisionEvent = "integration.decision";
    public const string CalledEvent = "integration.called";
    public const string ProbedEvent = "integration.probed";

    protected override string TypeName => IntegrationConstants.ResourceType("integrations");

    public override async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        // `StoreKey` strips the action segment: an action's id carries the verb, so `Id.Key` names a resource
        // that does not exist. Every hand-built unit fact passes an id WITHOUT an Action and never saw this;
        // the first front-door action call did (a 404 naming the path with `/probe` on it).
        var existing = await Store.GetAsync(StoreKey(req.Id), ct).ConfigureAwait(false);
        if (existing is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(req.Id.Canonical, req.Id.Canonical));
        var properties = Map(existing).Properties;

        return req.Action.ToLowerInvariant() switch
        {
            Probe => await ProbeAsync(req, properties, ct).ConfigureAwait(false),
            Call => await CallAsync(req, existing, properties, ct).ConfigureAwait(false),
            _ => new ActionOutcome.Rejected(ArmError.Validation($"Unknown action '{req.Action}'.", [])),
        };
    }

    /// <summary>
    /// Ask the surface what it offers and record the answer. It changes no authority: the discovered list is
    /// a fact about the server, and what may be CALLED stays whatever a human declared in <c>tools</c>.
    /// </summary>
    private async ValueTask<ActionOutcome> ProbeAsync(
        ResourceActionRequest req,
        IntegrationProperties properties,
        CancellationToken ct)
    {
        if (Resolve(req, properties) is not { } caller)
            return NotConfigured(req, properties, "probe");

        IReadOnlyList<string> tools;
        try
        {
            tools = await caller.ListToolsAsync(properties, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A probe that threw is a fact about the surface, so it is recorded rather than raised: the
            // resource is where an operator looks to find out whether an integration is reachable.
            await WriteAsync(req, properties with { State = ex.Message, ProbedAt = Clock.UtcNow }, ct).ConfigureAwait(false);
            await EmitAsync(req, ProbedEvent, properties.State ?? "error", new JsonObject { ["error"] = ex.Message }, ct).ConfigureAwait(false);
            return new ActionOutcome.Rejected(ArmError.Validation($"The surface could not be listed: {ex.Message}", []));
        }

        await WriteAsync(
            req,
            properties with { DiscoveredTools = tools, State = "ok", ProbedAt = Clock.UtcNow },
            ct).ConfigureAwait(false);
        await EmitAsync(
            req,
            ProbedEvent,
            $"{tools.Count} tool(s)",
            new JsonObject { ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)t)]) },
            ct).ConfigureAwait(false);

        return new ActionOutcome.Ok(new JsonObject
        {
            ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)t)]),
            ["declared"] = new JsonArray([.. (properties.Tools ?? []).Select(t => (JsonNode)t)]),
            ["probedAt"] = Clock.UtcNow.ToString("O"),
        });
    }

    private async ValueTask<ActionOutcome> CallAsync(
        ResourceActionRequest req,
        ResourceEnvelope<JsonObject> existing,
        IntegrationProperties properties,
        CancellationToken ct)
    {
        var body = req.Body?["properties"] ?? req.Body;
        var tool = body?["tool"]?.GetValue<string>() ?? "";
        if (tool.Length == 0)
            return new ActionOutcome.Rejected(ArmError.Validation("call needs a `tool`.", []));

        // (1) Declaration is authority. A tool the surface reports but nobody declared is NOT callable —
        //     otherwise a server that grows a tool overnight gains a way to act, and "we have not read the
        //     new list" would be an authorisation.
        var declared = properties.Tools ?? [];
        if (!declared.Any(t => string.Equals(t, tool, StringComparison.OrdinalIgnoreCase)))
        {
            return new ActionOutcome.Rejected(ArmError.Forbidden(
                $"'{tool}' is not a tool this integration declares, so no policy has been written for it. "
                + "Add it to `tools` in a deliberate change."));
        }

        if (!properties.Enabled)
        {
            return new ActionOutcome.Rejected(ArmError.Forbidden(
                $"Integration '{req.Id.NameSegments[^1]}' is disabled; enable it before calling its tools."));
        }

        // (2) The surface has to EXIST in this host before anything is decided about reaching it. Resolving
        //     the caller after the policy would mean a gray tool answered 202 with an approval card for a call
        //     this host cannot make — a human decision that authorises nothing, and the same shape as an
        //     approval whose subject has moved on. "Cannot" is configuration; "may not" is policy; the two
        //     belong in that order.
        if (Resolve(req, properties) is not { } caller)
            return NotConfigured(req, properties, "call");

        var input = body?["input"] as JsonNode ?? new JsonObject();
        var streamId = IntegrationConstants.StreamId(req.Id.Canonical);

        // (3) Intent before decision, so the audit reads in the order a person acted.
        await events.AppendAsync(new EventAppend(
            streamId,
            req.Id.NameSegments[^1],
            IntentEvent,
            tool,
            new JsonObject
            {
                ["tool"] = tool,
                ["input"] = JsonNode.Parse(redactor.Redact(input.ToJsonString())),
                ["toolCallId"] = req.RequestId,
            },
            RenderLane.Log,
            tool,
            null,
            IntegrationConstants.Namespace), ct).ConfigureAwait(false);

        var verdict = policy.Classify(new ActionPolicySubject(tool, input, Context(req, properties)));

        await events.AppendAsync(new EventAppend(
            streamId,
            req.Id.NameSegments[^1],
            DecisionEvent,
            verdict.Tier.ToString(),
            new JsonObject
            {
                ["tier"] = verdict.Tier.ToString(),
                ["class"] = verdict.Finding.Class,
                ["reason"] = redactor.Redact(verdict.Finding.Reason),
                ["rule"] = verdict.Finding.RuleId,
            },
            RenderLane.Log,
            tool,
            null,
            IntegrationConstants.Namespace), ct).ConfigureAwait(false);

        // (4) A terminal tier is final, and so is Clarify: "this cannot be judged" is not a licence.
        if (verdict.Tier is RiskTier.Forbidden or RiskTier.HardDeny or RiskTier.Clarify)
        {
            await EmitAsync(req, "integration.refused", verdict.Finding.Class, new JsonObject
            {
                ["tool"] = tool,
                ["tier"] = verdict.Tier.ToString(),
                ["reason"] = verdict.Finding.Reason,
            }, ct).ConfigureAwait(false);
            return new ActionOutcome.Rejected(ArmError.Forbidden(
                $"{verdict.Tier} ({verdict.Finding.Class}): {verdict.Finding.Reason}"));
        }

        // (5) A gray or an escalation needs a human. The approval's decision key carries the tool AND the
        //     arguments, so a second, different call does not inherit the first one's answer.
        if (verdict.Tier is RiskTier.Gray or RiskTier.Escalate)
        {
            var approvalId = body?["approvalId"]?.GetValue<string>() ?? "";
            if (!await IsApprovedAsync(approvalId, verdict.Finding.Class, req, tool, input, ct).ConfigureAwait(false))
            {
                var opened = await approvals.OpenAsync(new ApprovalRequest(
                    StreamId: streamId,
                    SubStreamId: null,
                    Kind: ApprovalKind.Permission,
                    SubjectType: "integration",
                    SubjectId: req.Id.Canonical,
                    DecisionKey: DecisionKey(req, tool, input),
                    ShapeKey: verdict.Finding.Fingerprint,
                    RiskClass: verdict.Finding.Class,
                    Action: tool,
                    Preview: redactor.Redact(input.ToJsonString()),
                    Why: verdict.Finding.Reason,
                    Options: ["approve", "deny"],
                    Grantable: true,
                    Timeout: TimeSpan.FromMinutes(15)), ct).ConfigureAwait(false);

                return new ActionOutcome.Accepted(opened, TimeSpan.FromSeconds(5), new JsonObject
                {
                    ["outcome"] = "pending-approval",
                    ["approvalId"] = opened,
                    ["tier"] = verdict.Tier.ToString(),
                    ["class"] = verdict.Finding.Class,
                    ["reason"] = verdict.Finding.Reason,
                });
            }
        }

        // (6) Allowed — by the rules, or by a human who has since approved this exact call.
        var result = await caller.CallAsync(properties, tool, input, ct).ConfigureAwait(false);

        await WriteAsync(req, properties with { Calls = properties.Calls + 1 }, ct).ConfigureAwait(false);
        await EmitAsync(req, CalledEvent, tool, new JsonObject
        {
            ["tool"] = tool,
            ["ok"] = result.Ok,
            ["error"] = result.Error,
            ["tier"] = verdict.Tier.ToString(),
            ["class"] = verdict.Finding.Class,
        }, ct).ConfigureAwait(false);

        return new ActionOutcome.Ok(new JsonObject
        {
            ["ok"] = result.Ok,
            ["output"] = result.Output?.DeepClone(),
            ["error"] = result.Error,
            ["tier"] = verdict.Tier.ToString(),
            ["class"] = verdict.Finding.Class,
            ["calls"] = properties.Calls + 1,
        });
    }

    /// <summary>
    /// The key a decision is filed under: the integration, the tool, and a hash of the arguments. The
    /// arguments are part of it on purpose — an approval for sending one email is not an approval for
    /// sending another, and a key that ignored them would make every call of a tool one decision.
    /// </summary>
    internal static string DecisionKey(ResourceActionRequest req, string tool, JsonNode input) =>
        $"integration:call:{StoreKey(req.Id)}:{tool}:{Hash(input)}";

    private static string Hash(JsonNode input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input.ToJsonString()));
        return Convert.ToHexStringLower(bytes)[..16];
    }

    /// <summary>
    /// Whether the caller handed back an approval that authorises THIS call: approved, the same risk class,
    /// and the same decision key. Any one of the three missing and the answer is no — an approval for a
    /// different class is the same defect as a grant for a different transition.
    /// </summary>
    private async ValueTask<bool> IsApprovedAsync(
        string approvalId,
        string riskClass,
        ResourceActionRequest req,
        string tool,
        JsonNode input,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(approvalId))
            return false;

        var record = await approvals.GetAsync(approvalId, ct).ConfigureAwait(false);
        if (record is null || record.Outcome is not Approved)
            return false;
        if (!string.Equals(record.Request.RiskClass, riskClass, StringComparison.Ordinal))
            return false;
        return string.Equals(record.Request.DecisionKey, DecisionKey(req, tool, input), StringComparison.Ordinal);
    }

    /// <summary>
    /// The policy context for a resource-side call. Everything a rule might read comes from the integration
    /// itself or from configuration — never from the caller's body, because a caller that could declare its
    /// own dev hosts would be choosing the answer to the browser rule it is asking about.
    /// </summary>
    private PolicyContext Context(ResourceActionRequest req, IntegrationProperties properties)
    {
        var root = configuration["Kernel:WorkRoot"] ?? Path.GetTempPath();
        return new PolicyContext(
            StreamId: IntegrationConstants.StreamId(req.Id.Canonical),
            WorkRoot: root,
            ArtifactDir: root,
            // The profile default is the parser's own (RestrictedProd): an integration call with nothing
            // configured is a real-world action, and the conservative reading is the right default.
            Profile: PolicyProfileParser.Parse(configuration["Policy:Profile"]),
            TargetEnv: configuration["Policy:TargetEnv"] ?? "dev",
            HostRepoPath: root,
            VerifyCommands: [],
            DevHosts: properties.DevHosts ?? [],
            AllowedSecretIds: []);
    }

    /// <summary>
    /// The caller for THIS integration's kind, resolved per request.
    ///
    /// <para><b>The kind decides, and the answer is exactly one caller.</b> I0 registered a single caller and
    /// resolved it by type, which was fine while there was one; with a mailbox and a browser registered
    /// together, resolving "the" caller would hand a browser resource to the mailbox — a failure whose
    /// message ("this mailbox cannot serve 'mcp__browser__navigate'") would look like a policy answer. Two
    /// callers claiming one kind is refused rather than resolved by registration order, because that would
    /// make a host's behaviour depend on the order of two lines in Program.cs.</para>
    ///
    /// <para>A host with NO caller still refuses rather than silently doing nothing: "this host cannot reach
    /// the surface" and "the surface said no" are different facts, and a caller that confused them would
    /// report a broken integration as a policy decision.</para>
    /// </summary>
    private static IIntegrationCaller? Resolve(ResourceActionRequest req, IntegrationProperties properties)
    {
        var callers = (req.Services.GetService(typeof(IEnumerable<IIntegrationCaller>)) as IEnumerable<IIntegrationCaller>)
            ?.ToList() ?? [];
        var matches = callers
            .Where(c => c.Kinds.Contains(properties.Kind, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static ActionOutcome NotConfigured(ResourceActionRequest req, IntegrationProperties properties, string action)
    {
        var callers = (req.Services.GetService(typeof(IEnumerable<IIntegrationCaller>)) as IEnumerable<IIntegrationCaller>)
            ?.ToList() ?? [];
        var matches = callers.Count(c => c.Kinds.Contains(properties.Kind, StringComparer.OrdinalIgnoreCase));
        if (matches > 1)
        {
            return new ActionOutcome.Rejected(ArmError.Validation(
                $"{matches} integration callers claim kind '{properties.Kind}', so '{action}' has no single "
                + "answer to take. Register one caller per kind.",
                []));
        }

        var served = callers
            .SelectMany(c => c.Kinds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (served.Count == 0)
        {
            return new ActionOutcome.Rejected(ArmError.Validation(
                $"This host has no integration caller, so '{action}' cannot reach any surface. "
                + "Register an IIntegrationCaller (I1-I3 supply the mail, browser and Notion ones).",
                []));
        }

        return new ActionOutcome.Rejected(ArmError.Validation(
            $"This host has no integration caller for kind '{properties.Kind}', so '{action}' cannot reach "
            + $"that surface. It serves: {string.Join(", ", served)}.",
            []));
    }

    private async ValueTask WriteAsync(ResourceActionRequest req, IntegrationProperties desired, CancellationToken ct)
    {
        var stored = await Store.GetAsync(StoreKey(req.Id), ct).ConfigureAwait(false);
        if (stored is null)
            return;
        var now = Clock.UtcNow;
        await Store.UpsertAsync(
            stored with
            {
                Properties = System.Text.Json.JsonSerializer.SerializeToNode(
                    desired, Burbuja.Core.Abstractions.Json.JsonWire.Options)!.AsObject(),
                ProvisioningState = ProvisioningState.Succeeded,
                SystemData = stored.SystemData with
                {
                    LastModifiedAt = now,
                    LastModifiedBy = req.Principal.PrincipalId,
                    LastModifiedByType = req.Principal.PrincipalType,
                },
            },
            stored.ETag,
            ct).ConfigureAwait(false);
    }

    private async ValueTask EmitAsync(
        ResourceActionRequest req,
        string type,
        string summary,
        JsonObject payload,
        CancellationToken ct)
    {
        payload["integration"] = req.Id.NameSegments[^1];
        await events.AppendAsync(new EventAppend(
            IntegrationConstants.StreamId(req.Id.Canonical),
            req.Id.NameSegments[^1],
            type,
            summary,
            payload,
            RenderLane.Log | RenderLane.Monitor,
            req.Id.NameSegments[^1],
            null,
            IntegrationConstants.Namespace), ct).ConfigureAwait(false);
    }
}
