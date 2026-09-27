using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Model;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Arm;

namespace Burbuja.Agents.Handlers;

/// <summary>
/// The `agents` resource. PUT is synchronous and never runs a turn; the acting verbs are explicit
/// actions, each of which starts an operation and appends to the durable inbox.
/// </summary>
public sealed class AgentHandler(
    IResourceStore store,
    IClock clock,
    IOperationTracker ops,
    AgentActivationManager activation,
    ILeaseStore leases,
    // M14 R12 (request row X1) — `send` accepts an image, and an image named by ARTIFACT KEY is checked at the
    // door: an attachment that points at nothing is the kind of claim this kernel refuses to record. The
    // handler is resolved from DI (there is no hand-construction site in the tree), so this is a registration,
    // not a rewrite.
    IArtifactStore artifacts) : EnvelopeHandler<AgentProperties>(store, clock)
{
    protected override string TypeName => $"{AgentsConstants.Namespace}/agents";

    /// <summary>
    /// `state` is derived on read, never the source of truth: a held activation lease is Running, a
    /// pending approval or a non-empty inbox is Waiting, a finished session is Settled, and an agent
    /// that has never run is Idle.
    /// </summary>
    public override async ValueTask<ResourceEnvelope<AgentProperties>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        var envelope = await base.GetAsync(req, ct);
        if (envelope is null)
            return null;
        var props = envelope.Properties;
        if (await leases.GetAsync(AgentsConstants.LeaseId(StoreKey(req.Id)), ct) is not null)
            props.State = AgentState.Running;
        else if (props.PendingApprovalId is { Length: > 0 } || (props.Inbox?.Count ?? 0) > 0)
            props.State = AgentState.Waiting;
        else if (props.CurrentSessionId is { Length: > 0 })
            props.State = AgentState.Settled;
        else
            props.State = AgentState.Idle;
        return envelope;
    }

    public override async ValueTask<PutOutcome<AgentProperties>> PutAsync(ResourceRequest req, AgentProperties desired, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(desired.ModelId))
            desired.ModelId = AgentsConstants.DefaultAgentModelId;
        if (desired.Effort is not ("low" or "high" or "max"))
            return Reject("InvalidEffort", $"effort must be low|high|max (got '{desired.Effort}').");
        if (desired.Driver is not (AgentDrivers.Kernel or AgentDrivers.External))
            return Reject("InvalidDriver", $"driver must be {AgentDrivers.Kernel}|{AgentDrivers.External} (got '{desired.Driver}').");
        if (desired.Depth is < 0 or > AgentActivationManager.MaxAgentDepth)
            return Reject("DepthBudgetExceeded", $"depth must be 0..{AgentActivationManager.MaxAgentDepth}.");
        if (desired.ParentAgentId is not null
            && !AgentActivationManager.CanSpawn(desired.Depth - 1, 0)
            && desired.Depth > AgentActivationManager.MaxAgentDepth)
            return Reject("DepthBudgetExceeded", "the agent depth budget is exhausted.");

        // Creating an agent is inert: state is derived, and no session is started here.
        desired.State = AgentState.Idle;
        desired.Inbox ??= [];
        desired.OwnedChildren ??= [];
        desired.DeliveredDedupKeys ??= [];
        return await base.PutAsync(req, desired, ct);
    }

    public override async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var action = req.Action.ToLowerInvariant();
        return action switch
        {
            "send" => await SendAsync(req, ct),
            "interrupt" or "fork" or "resume" => await LifecycleAsync(req, action, ct),
            _ => await base.InvokeAsync(req, ct),
        };
    }

    private async ValueTask<ActionOutcome> SendAsync(ResourceActionRequest req, CancellationToken ct)
    {
        var raw = await Store.GetAsync(StoreKey(req.Id), ct);
        if (raw is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));
        var agent = Map(raw);

        var body = req.Body ?? new JsonObject();
        var mode = body["mode"]?.GetValue<string>()?.ToLowerInvariant() switch
        {
            "steer" => SendMode.Steer,
            "inject" => SendMode.Inject,
            _ => SendMode.Followup,
        };
        var text = body["text"]?.GetValue<string>() ?? "";
        var dedupKey = body["dedupKey"]?.GetValue<string>();

        // M14 R12 (request row X1) — the image, if the caller attached one. Validated here rather than at the
        // model call: a `send` that is accepted is a durable promise, and the promise "this message has an
        // image" must not be discovered to be false two turns later.
        var image = await ParseImageAsync(body["image"], ct);
        if (image.Error is not null)
            return new ActionOutcome.Rejected(ArmError.Validation(
                "InvalidImage",
                [new ArmError("InvalidImage", image.Error, "image", [], null, 400)]));

        // 2026-09-26 (lane CAPS) — `images`: further images of the same message, each validated like `image`.
        // Four in total, because a model call is priced by what it carries and a caller that attached forty
        // photos meant something no single turn should pay for; the refusal names the bound.
        var moreImages = new List<AgentImage>();
        if (body["images"] is JsonArray extra)
        {
            if (extra.Count + (image.Value is null ? 0 : 1) > MaxImagesPerSend)
                return new ActionOutcome.Rejected(ArmError.Validation(
                    "InvalidImage",
                    [new ArmError("InvalidImage", $"a message carries at most {MaxImagesPerSend} images (image + images)", "images", [], null, 400)]));
            foreach (var node in extra)
            {
                var parsed = await ParseImageAsync(node, ct);
                if (parsed.Error is not null || parsed.Value is null)
                    return new ActionOutcome.Rejected(ArmError.Validation(
                        "InvalidImage",
                        [new ArmError("InvalidImage", parsed.Error ?? "images holds an empty entry", "images", [], null, 400)]));
                moreImages.Add(parsed.Value);
            }
        }

        var streamId = agent.Properties.CurrentSessionId is { Length: > 0 } session
            ? AgentsConstants.SessionStreamId(session)
            : AgentsConstants.SessionStreamId(req.Id.Key);

        var delivery = activation.Deliver(agent.Properties, streamId, mode, text, dedupKey, image: image.Value, moreImages: moreImages);
        if (!delivery.Accepted)
            return new ActionOutcome.Ok(new JsonObject { ["accepted"] = false, ["reason"] = "duplicate" });

        await PersistAsync(agent, ct);
        await activation.EmitAsync(streamId, AgentInboxEvents.Enqueued, mode.ToString(), new JsonObject
        {
            ["itemId"] = delivery.Item!.Id,
            ["mode"] = mode.ToString(),
            ["action"] = delivery.Action.ToString(),
            // What KIND of attachment the message carries — never the URL (it can be a signed link) and never
            // the bytes. An operator reading the ring sees that an image arrived and where it came from.
            ["image"] = image.Value is null
                ? null
                : new JsonObject
                {
                    ["kind"] = image.Value.HasUrl ? "url" : "artifact",
                    ["mediaType"] = image.Value.MediaType,
                    ["artifactKey"] = image.Value.ArtifactKey,
                },
            ["moreImages"] = moreImages.Count == 0 ? null : moreImages.Count,
        }, ct);

        var opId = Guid.NewGuid().ToString("N");
        await ops.StartAsync(opId, req.Id.SubscriptionId, req.Id.Namespace, "local", agent.Id, null, ct);
        if (delivery.Action != DeliveryAction.InjectOnly)
        {
            var op = delivery.Action == DeliveryAction.ColdResumeThenEnqueue ? "resume" : "wake";
            await activation.EnqueueActivationAsync(StoreKey(req.Id), op, ct);
        }

        return new ActionOutcome.Accepted(opId, TimeSpan.FromSeconds(2), null);
    }

    private async ValueTask<ActionOutcome> LifecycleAsync(ResourceActionRequest req, string action, CancellationToken ct)
    {
        var raw = await Store.GetAsync(StoreKey(req.Id), ct);
        if (raw is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(ResourceIdentity(req.Id).Canonical, ResourceIdentity(req.Id).Canonical));

        var opId = Guid.NewGuid().ToString("N");
        await ops.StartAsync(opId, req.Id.SubscriptionId, req.Id.Namespace, "local", raw.Id, null, ct);
        await activation.EnqueueActivationAsync(StoreKey(req.Id), action, ct);
        return new ActionOutcome.Accepted(opId, TimeSpan.FromSeconds(2), null);
    }

    private async Task PersistAsync(ResourceEnvelope<AgentProperties> envelope, CancellationToken ct)
    {
        var json = new ResourceEnvelope<JsonObject>(
            envelope.Id,
            envelope.Name,
            envelope.Type,
            envelope.Location,
            envelope.Tags,
            JsonSerializer.SerializeToNode(envelope.Properties, JsonWire.Options)!.AsObject(),
            envelope.ProvisioningState,
            envelope.SystemData,
            envelope.ETag);
        await Store.UpsertAsync(json, envelope.ETag, ct);
    }

    /// <summary>
    /// M14 R12 (request row X1) — read `image` from a `send` body. Exactly one of <c>artifactKey</c> and
    /// <c>url</c>, an <c>image/*</c> media type, and — for an artifact — one that is really in the store. Every
    /// refusal names what was wrong with what, because the caller is a person who attached something.
    /// </summary>
    /// <summary>2026-09-26 (lane CAPS) — the most images one `send` may carry (<c>image</c> + <c>images</c>).</summary>
    public const int MaxImagesPerSend = 4;

    private async ValueTask<(AgentImage? Value, string? Error)> ParseImageAsync(JsonNode? node, CancellationToken ct)
    {
        if (node is null)
            return (null, null);
        if (node is not JsonObject obj)
            return (null, "image must be an object with artifactKey or url");

        var artifactKey = obj["artifactKey"]?.GetValue<string>();
        var url = obj["url"]?.GetValue<string>();
        var mediaType = obj["mediaType"]?.GetValue<string>() ?? "image/png";
        var alt = obj["alt"]?.GetValue<string>();

        var hasArtifact = !string.IsNullOrWhiteSpace(artifactKey);
        var hasUrl = !string.IsNullOrWhiteSpace(url);
        if (hasArtifact == hasUrl)
            return (null, "image needs exactly one of artifactKey or url");

        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return (null, $"image mediaType must be an image type (got '{mediaType}')");

        if (hasArtifact)
        {
            if (!await artifacts.ExistsAsync(artifactKey!, ct))
                return (null, $"no artifact '{artifactKey}' in the store — an attachment must point at something that exists");
        }
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            return (null, "image url must be an absolute http(s) URL");
        }

        return (new AgentImage(ArtifactKey: hasArtifact ? artifactKey : null, Url: hasUrl ? url : null, MediaType: mediaType, Alt: alt), null);
    }

    private static PutOutcome<AgentProperties> Reject(string code, string message) =>
        new PutOutcome<AgentProperties>.Rejected(ArmError.Validation(code, [new ArmError(code, message, null, [], null, 400)]));
}

/// <summary>A session is append-only and log-derived: it is created by the runtime, never by a PUT.</summary>
public sealed class SessionHandler(IResourceStore store, IClock clock) : ChildEnvelopeHandler<SessionProperties>(store, clock)
{
    protected override string TypeName => $"{AgentsConstants.Namespace}/agents/sessions";

    public override ValueTask<PutOutcome<SessionProperties>> PutAsync(ResourceRequest req, SessionProperties desired, CancellationToken ct) =>
        new(new PutOutcome<SessionProperties>.Rejected(ArmError.MethodNotAllowed("sessions are created by the runtime.")));

    public override ValueTask<PutOutcome<SessionProperties>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        new(new PutOutcome<SessionProperties>.Rejected(ArmError.MethodNotAllowed("sessions are immutable.")));

    public override ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct) =>
        new(new DeleteOutcome.Rejected(ArmError.MethodNotAllowed("sessions are ended by the runtime.")));
}

/// <summary>`agents/contexts`: bounded, redacted, versioned cards.</summary>
public sealed class ContextHandler(IResourceStore store, IClock clock) : ChildEnvelopeHandler<ContextCardProperties>(store, clock)
{
    protected override string TypeName => $"{AgentsConstants.Namespace}/agents/contexts";

    public override async ValueTask<PutOutcome<ContextCardProperties>> PutAsync(ResourceRequest req, ContextCardProperties desired, CancellationToken ct)
    {
        if (desired.Label.Length == 0)
            return new PutOutcome<ContextCardProperties>.Rejected(ArmError.Validation("label is required.", []));
        desired.Bytes = System.Text.Encoding.UTF8.GetByteCount(desired.Body);
        if (desired.Bytes > Abstractions.Context.ContextBudget.MaxCardBytes)
            return new PutOutcome<ContextCardProperties>.Rejected(ArmError.Validation(
                "ContextCardTooLarge",
                [new ArmError("ContextCardTooLarge", $"a card is at most {Abstractions.Context.ContextBudget.MaxCardBytes} bytes.", null, [], null, 400)]));
        return await base.PutAsync(req, desired, ct);
    }
}
