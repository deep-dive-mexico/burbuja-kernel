using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Portal.Services;

/// <summary>
/// M14 R5p — the pomnibot half of the portal's read model, split out by subject:
/// a lane reading and editing pomnibot's pages should open one file, not scroll past a thousand lines of
/// another subject's readers.
///
/// <para><b>What lives here.</b> The <c>Burbuja.Pomnibot</c> namespace's readers (jobs, runs, repos, its
/// own day ledger, its channels, its approvals), the artifact reader a run is opened with, and the three
/// static helpers that are ABOUT that resource: <c>CreatedAt</c>, the awaiting-approval rule, and the rail
/// row's projection. The genuinely shared helpers (<c>Age</c>, <c>IsTerminal</c>, <c>JobLink</c>) stay in
/// the primary file because the Agents readers use them too — moving one of those would file a shared rule
/// under one lane's name, which is the mistake this split exists to avoid.</para>
///
/// <para>The class is one class either way: a partial part is the same type, so no page changed and no
/// fact changed. The proof is that the build and the whole suite are green with nothing but the moves.</para>
/// </summary>
public partial class PortalData
{
    /// <summary>
    /// Every pomnibot job of the scope. Unlike <see cref="JobsAsync"/> this is a single list call:
    /// pomnibot jobs hang directly off the namespace, so there is no workspace to enumerate first.
    /// </summary>
    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalPomnibotJob>>> PomnibotJobsAsync(
        ArmScope scope, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var jobs = new List<PortalEnvelope<PortalPomnibotJob>>();
        await foreach (var job in client.ListAsync<PortalEnvelope<PortalPomnibotJob>>($"{scope.PomnibotRoot}/jobs", ct: ct))
            jobs.Add(job);
        return jobs;
    }

    public async ValueTask<PortalEnvelope<PortalPomnibotJob>?> PomnibotJobAsync(
        ArmScope scope, string name, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        return await client.GetAsync<PortalEnvelope<PortalPomnibotJob>>($"{scope.PomnibotRoot}/jobs/{name}", ct: ct);
    }

    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalPomnibotRun>>> PomnibotRunsAsync(
        ArmScope scope, string name, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var runs = new List<PortalEnvelope<PortalPomnibotRun>>();
        await foreach (var run in client.ListAsync<PortalEnvelope<PortalPomnibotRun>>(
                           $"{scope.PomnibotRoot}/jobs/{name}/runs", ct: ct))
            runs.Add(run);
        return runs;
    }

    /// <summary>The pomnibot repos the scope binds; empty when the type is not mounted.</summary>
    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalPomnibotRepo>>> PomnibotReposAsync(
        ArmScope scope, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var repos = new List<PortalEnvelope<PortalPomnibotRepo>>();
        await foreach (var repo in client.ListAsync<PortalEnvelope<PortalPomnibotRepo>>($"{scope.PomnibotRoot}/repos", ct: ct))
            repos.Add(repo);
        return repos;
    }

    /// <summary>
    /// The pomnibot day ledger, or null when the day has no resource yet (zero, not a failure), or
    /// when the whole namespace is absent. A <c>{day}</c> that the host answers with anything else
    /// is rethrown so the dashboard can render it.
    /// </summary>
    public async ValueTask<PortalPomnibotLedgerDay?> PomnibotLedgerAsync(ArmScope scope, string day, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        try
        {
            var envelope = await client.GetAsync<PortalEnvelope<PortalPomnibotLedgerDay>>(
                $"{scope.PomnibotRoot}/ledgers/{day}", ct: ct);
            if (envelope?.Properties is not { } properties)
                return null;
            // The resource name is the day; the envelope may or may not repeat it.
            properties.Day = day;
            return properties;
        }
        catch (ArmException ex) when (ex.Error.HttpStatus == 404 || IsTypeAbsent(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// The channel bindings, tolerant of the <c>channels</c> resource type being newer than the
    /// host that answers: an absent type is "none registered", an absent binding is an empty list.
    /// </summary>
    public async ValueTask<PortalChannelRead> PomnibotChannelsAsync(ArmScope scope, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var channels = new List<PortalEnvelope<PortalPomnibotChannel>>();
        try
        {
            await foreach (var channel in client.ListAsync<PortalEnvelope<PortalPomnibotChannel>>($"{scope.PomnibotRoot}/channels", ct: ct))
                channels.Add(channel);
        }
        catch (ArmException ex) when (IsTypeAbsent(ex))
        {
            return new PortalChannelRead([], true, null);
        }
        catch (ArmException ex)
        {
            return new PortalChannelRead([], false, PortalFailure.From(ex));
        }

        return new PortalChannelRead(channels, false, null);
    }

    /// <summary>
    /// The job's own approvals (the pomnibot child surface, which also carries the decisions that
    /// never reached the kernel inbox — a deferred escalation, a denied tool call).
    /// </summary>
    public async ValueTask<IReadOnlyList<PortalEnvelope<PortalPomnibotApproval>>> PomnibotApprovalsAsync(
        ArmScope scope, string name, bool pendingOnly, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var query = pendingOnly
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["pendingOnly"] = "true" }
            : null;
        var approvals = new List<PortalEnvelope<PortalPomnibotApproval>>();
        await foreach (var approval in client.ListAsync<PortalEnvelope<PortalPomnibotApproval>>(
                           $"{scope.PomnibotRoot}/jobs/{name}/approvals", query, ct))
            approvals.Add(approval);
        return approvals;
    }

    /// <summary>
    /// One pomnibot artifact through the <c>getArtifact</c> action (POST, base64). There is no
    /// data-plane artifact route on a pomnibot job — a GET of <c>jobs/{n}/artifacts/patch.diff</c>
    /// answers 400 InvalidResourceType — so the action is the only honest way to read one, and a
    /// 404 means "the run never wrote this", which the ring renders as absent.
    /// </summary>
    public async ValueTask<PortalArtifact> PomnibotArtifactAsync(
        ArmScope scope, string name, string artifact, CancellationToken ct)
    {
        var path = $"{scope.PomnibotRoot}/jobs/{name}/getArtifact";
        var client = await ClientAsync(ct);
        try
        {
            var body = new JsonObject { ["properties"] = new JsonObject { ["name"] = artifact } };
            var blob = await client.PostActionAsync<PortalPomnibotArtifactBlob>(path, body, ct);
            if (blob?.Base64 is not { Length: > 0 } encoded)
                return new PortalArtifact(path, 204, blob?.ContentType, 0) { Name = artifact };

            var bytes = Convert.FromBase64String(encoded);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            return new PortalArtifact(path, 200, blob.ContentType ?? ContentTypeOf(artifact), bytes.LongLength)
            {
                Name = artifact,
                Text = Cap(text, options.ArtifactInlineLimit),
            };
        }
        catch (ArmException ex) when (ex.Error.HttpStatus == 404)
        {
            return new PortalArtifact(path, 404, null, null) { Name = artifact };
        }
    }

    private static string ContentTypeOf(string artifact) =>
        artifact.EndsWith(".diff", StringComparison.OrdinalIgnoreCase) ? "text/x-diff"
            : artifact.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "text/markdown"
                : "text/plain";

    private static string Cap(string text, int limit) =>
        limit <= 0 || text.Length <= limit ? text : text[..limit] + $"\n… truncated at {limit} bytes";

    /// <summary>
    /// A job's lifecycle is not the agents job's: pomnibot uses <c>executionStatus</c> with an
    /// underscore between words. Both spellings are accepted because the wire has carried both.
    /// </summary>
    public static bool IsPomnibotAwaitingApproval(string? status) =>
        status is not null
        && (status.Equals("awaiting_approval", StringComparison.OrdinalIgnoreCase)
            || status.Equals("awaitingApproval", StringComparison.OrdinalIgnoreCase)
            || status.Equals("awaiting_confirm", StringComparison.OrdinalIgnoreCase));

    private static PortalRailItem PomnibotJobItem(PortalEnvelope<PortalPomnibotJob> job, DateTimeOffset now)
    {
        var name = job.Name ?? "(unnamed)";
        var status = job.Properties?.ExecutionStatus ?? job.ProvisioningState ?? "unknown";
        return new PortalRailItem(
            "job",
            job.Properties?.Spec.Title is { Length: > 0 } title ? title : name,
            $"/portal/pomnibot/jobs/{Uri.EscapeDataString(name)}",
            status,
            Age(CreatedAt(job), now),
            Money(job.Properties?.Cost.TotalUsd),
            job.Properties?.Spec.Model ?? name);
    }

    /// <summary>
    /// A pomnibot job's age anchor: it has no <c>startedAt</c> of its own, so the envelope's own
    /// systemData is what the kernel recorded.
    /// </summary>
    public static DateTimeOffset? CreatedAt(PortalEnvelope<PortalPomnibotJob> job) =>
        job.SystemData?.CreatedAt ?? job.SystemData?.LastModifiedAt;
}
