using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Core.Arm;

/// <summary>
/// K2: <c>Burbuja.Resources/activity</c> — the recent requests, as a resource.
///
/// <para><b>A projection, not a store.</b> The rows live in the ring and the ring is the truth; a second
/// copy in the resource store would be a copy that can disagree with it, and the question this surface
/// answers ("what has this host been asked to do") is exactly the one a stale copy would get wrong.</para>
///
/// <para><b>It implements the handler interface directly rather than deriving from the envelope handler,
/// because it has no store.</b> The envelope base class takes an <see cref="IResourceStore"/> and this
/// surface must never read or write one — deriving would have meant injecting a store that nothing calls,
/// which is the "wired but unused" shape this repository keeps removing. Six members is the price of an
/// honest dependency list.</para>
///
/// <para><b>Read-only in both directions.</b> A PUT is refused because activity is a fact about what
/// happened, not a thing to file; a DELETE is refused for the same reason a log entry is not editable. The
/// refusals carry the reason, because "method not allowed" tells a caller nothing about why.</para>
///
/// <para><b>A read never contains itself.</b> The list is snapshotted on the way IN, while the row for this
/// very request is appended on the way OUT by <see cref="ArmActivityMiddleware"/> — so the newest row a
/// caller sees is always the request before it. That is a property worth asserting, because a surface whose
/// own traffic appears in its own output is one where a reader cannot tell what they are looking at.</para>
/// </summary>
public sealed class ActivityHandler(IArmActivityLog log) : IResourceHandler<ArmActivityRow>
{
    private const string TypeName = ArmConstants.ResourcesNamespace + "/activity";

    /// <summary>The list, filtered by the two questions an operator actually asks: whose, and since when.</summary>
    public ValueTask<PagedResult<ResourceEnvelope<ArmActivityRow>>> ListAsync(ResourceListRequest req, CancellationToken ct)
    {
        var principal = req.Query.TryGetValue("principal", out var p) && p.Length > 0 ? p : null;
        var since = req.Query.TryGetValue("since", out var s) && DateTimeOffset.TryParse(s, out var at)
            ? at
            : (DateTimeOffset?)null;

        var rows = log.Recent(principal, since, req.Top <= 0 ? 200 : req.Top);
        var tags = RingTags();
        return ValueTask.FromResult(new PagedResult<ResourceEnvelope<ArmActivityRow>>(
            [.. rows.Select(row => Envelope(row, tags))],
            null));
    }

    /// <summary>
    /// One row by the REQUEST it recorded.
    ///
    /// <para>The identity is the request id, not a position: a row addressed by its index in a sliding
    /// window would change what it means every time the ring drops something, and a portal that kept a link
    /// would open somebody else's request. A row for a request that failed before it had an id falls back to
    /// its timestamp, which is the only other thing unique about it.</para>
    /// </summary>
    public ValueTask<ResourceEnvelope<ArmActivityRow>?> GetAsync(ResourceRequest req, CancellationToken ct)
    {
        var name = req.Id.NameSegments.LastOrDefault() ?? "";
        if (name.Length == 0)
            return ValueTask.FromResult<ResourceEnvelope<ArmActivityRow>?>(null);

        var match = log.Recent(null, null, log.Capacity).FirstOrDefault(r => Key(r) == name);
        return ValueTask.FromResult(match is null ? null : Envelope(match, RingTags()));
    }

    /// <summary>What a row is called: its request, or its instant when the request never got an id.</summary>
    private static string Key(ArmActivityRow row) =>
        row.RequestId is { Length: > 0 } id ? id : row.At.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public ValueTask<PutOutcome<ArmActivityRow>> PutAsync(ResourceRequest req, ArmActivityRow desired, CancellationToken ct) =>
        new(new PutOutcome<ArmActivityRow>.Rejected(ArmError.MethodNotAllowed(
            "Activity is a record of what this host was asked to do; it is not a thing to file.")));

    public ValueTask<PutOutcome<ArmActivityRow>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        PutAsync(req, null!, ct);

    public ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct) =>
        new(new DeleteOutcome.Rejected(ArmError.MethodNotAllowed(
            "Activity rows expire with the ring, not by deletion. The durable record is each resource's own event stream.")));

    public ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct) =>
        new(new ActionOutcome.Rejected(ArmError.Validation(
            $"Unknown action '{req.Action}'. Activity is read-only.", [])));

    /// <summary>
    /// The envelope carries the ROW's time, not the read's: a projection that stamped "now" would make every
    /// request in the list look like it happened just as somebody looked.
    /// </summary>
    /// <summary>
    /// How full the ring is, on every row.
    ///
    /// <para><b>K2 claimed this and the surface did not have it.</b> The changelog said "`Total` is on the
    /// surface so a full ring and a quiet host cannot look alike", and `Total`/`Capacity` were only on the
    /// in-process <see cref="IArmActivityLog"/>: a reader of the resource saw the rows and could not tell 200
    /// held of 200 served from 200 held of two million. The counts ride in the envelope's TAGS — metadata
    /// that travels with every row, survives paging, and needs no second resource to ask.</para>
    /// </summary>
    private Dictionary<string, string> RingTags() => new(StringComparer.Ordinal)
    {
        ["ring.total"] = log.Total.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ring.capacity"] = log.Capacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ring.held"] = log.Recent(null, null, log.Capacity).Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static ResourceEnvelope<ArmActivityRow> Envelope(ArmActivityRow row, IReadOnlyDictionary<string, string>? tags = null)
    {
        var name = Key(row);
        return new ResourceEnvelope<ArmActivityRow>(
            $"{ArmConstants.ResourcesNamespace}/activity/{name}",
            name,
            TypeName,
            "local",
            tags ?? new Dictionary<string, string>(),
            row,
            ProvisioningState.Succeeded,
            new SystemData(
                row.At,
                row.PrincipalId ?? "unknown",
                row.PrincipalType ?? "System",
                row.At,
                row.PrincipalId ?? "unknown",
                row.PrincipalType ?? "System"),
            "");
    }
}
