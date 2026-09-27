using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Arm;
using Burbuja.Deployments.Abstractions;
using Burbuja.Deployments.Targets;
using Microsoft.Extensions.Options;

namespace Burbuja.Deployments.Handlers;

public sealed class EnvironmentHandler(
    IResourceStore store,
    IClock clock,
    TargetRegistry targets,
    IOptions<DeploymentsOptions> options) : EnvelopeHandler<EnvironmentProperties>(store, clock)
{
    protected override string TypeName => $"{DeploymentsConstants.Namespace}/environments";

    public override async ValueTask<PutOutcome<EnvironmentProperties>> PutAsync(ResourceRequest req, EnvironmentProperties desired, CancellationToken ct)
    {
        if (!targets.TryGet(desired.Target.Kind, out _))
            return new PutOutcome<EnvironmentProperties>.Rejected(ArmError.Validation("UnsupportedTarget", [new ArmError("UnsupportedTarget", $"Unknown target '{desired.Target.Kind}'.", "/properties/target/kind", [], null, 400)]));
        if (desired.Target.Kind.Equals("local-process", StringComparison.OrdinalIgnoreCase))
        {
            var root = Path.GetFullPath(options.Value.LocalProcess.Root);
            var work = Path.GetFullPath(desired.Target.WorkRoot ?? root);
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!work.StartsWith(prefix, StringComparison.Ordinal) && !string.Equals(work, root, StringComparison.Ordinal))
                return new PutOutcome<EnvironmentProperties>.Rejected(ArmError.Validation("WorkRoot escapes Deployments:LocalProcess:Root.", []));
            desired.Target.WorkRoot = work;
            Directory.CreateDirectory(work);
        }

        return await base.PutAsync(req, desired, ct);
    }

    public override async ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct)
    {
        if (!req.Action.Equals("probe", StringComparison.OrdinalIgnoreCase))
            return await base.InvokeAsync(req, ct);
        var env = await GetAsync(req, ct);
        if (env is null)
            return new ActionOutcome.Rejected(ArmError.NotFound(req.Id.Canonical, req.Id.Canonical));
        if (!targets.TryGet(env.Properties.Target.Kind, out var target))
            return new ActionOutcome.Rejected(ArmError.Validation("UnsupportedTarget", []));
        var probe = await target.ProbeAsync(req.Id.Canonical, ct);
        return new ActionOutcome.Ok(new JsonObject { ["ready"] = probe.Ready, ["detail"] = probe.Detail });
    }
}
