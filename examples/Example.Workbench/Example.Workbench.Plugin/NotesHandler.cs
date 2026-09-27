using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Arm;

namespace Example.Workbench.Plugin;

public sealed class NotesHandler(IResourceStore store, IClock clock, IEventLog events)
    : EnvelopeHandler<NoteProperties>(store, clock)
{
    protected override string TypeName => WorkbenchPlugin.Namespace + "/notes";

    public override async ValueTask<PutOutcome<NoteProperties>> PutAsync(
        ResourceRequest req, NoteProperties desired, CancellationToken ct)
    {
        var outcome = await base.PutAsync(req, desired, ct);
        await TraceAsync(req, "workbench.note.saved", ct);
        return outcome;
    }

    public override async ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct)
    {
        var outcome = await base.DeleteAsync(req, ct);
        if (outcome is DeleteOutcome.Deleted)
            await TraceAsync(req, "workbench.note.deleted", ct);
        return outcome;
    }

    // A small demonstration trace, not a transaction with the resource store. Never record note text.
    private ValueTask<long> TraceAsync(ResourceRequest req, string type, CancellationToken ct) => events.AppendAsync(new(
        NoteEventStreams.StreamId(req.Id), null, type, type,
        new JsonObject { ["resourceId"] = ResourceIdentity(req.Id).Canonical, ["principalId"] = req.Principal.PrincipalId },
        RenderLane.Log, ResourceIdentity(req.Id).Canonical, null, "workbench.note"), ct);
}

public sealed class NoteEventStreams : IEventStreamResolver
{
    public string Namespace => WorkbenchPlugin.Namespace;

    public static string StreamId(ResourceId id) => "workbench.note/" + EventStreamIds.Resource(
        (id with { Action = null, DataPlaneSegment = null, DataPlanePath = null }).Canonical);

    public bool TryResolve(ResourceId id, out string streamId)
    {
        streamId = StreamId(id);
        return id.Namespace.Equals(Namespace, StringComparison.OrdinalIgnoreCase)
            && id.FullyQualifiedType.Equals("notes", StringComparison.OrdinalIgnoreCase) && !id.IsCollection;
    }
}
