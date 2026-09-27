using Burbuja.Compute.Abstractions;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Arm;

namespace Burbuja.Compute.Runtime;

/// <summary>
/// How other RPs (the Agents workspace bridge) attach to and use a workspace. Every exec here
/// still mints an execution grant before it touches <see cref="IProcessRunner"/>.
/// </summary>
public sealed class ComputeWorkspaceAccess(
    ComputeStateStore states,
    BackendRegistry backends,
    IExecutionGrantAuthority grants,
    IClock clock) : IComputeWorkspaceAccess
{
    public async ValueTask<ComputeProvisionResult> AttachAsync(string armId, string consumerId, CancellationToken ct)
    {
        var env = await states.GetAsync(armId.ToLowerInvariant(), ct)
                  ?? throw new ArmException(ArmError.NotFound($"Compute workspace '{armId}' was not found.", armId));
        var backend = Resolve(env.Properties.Backend);
        var root = env.Properties.RootPath ?? backend.ResolveRootPath(armId);
        if (!Directory.Exists(root))
            Directory.CreateDirectory(root);
        await TouchAsync(armId, ct);
        _ = consumerId;
        return new ComputeProvisionResult(env.Properties.BackendHandle ?? $"compute:{root}", root, env.Properties.Branch, env.Properties.BaseSha);
    }

    public async ValueTask<ComputeExecResult> ExecAsync(string armId, ComputeExecRequest request, CancellationToken ct)
    {
        var env = await states.GetAsync(armId.ToLowerInvariant(), ct)
                  ?? throw new ArmException(ArmError.NotFound($"Compute workspace '{armId}' was not found.", armId));
        var backend = Resolve(env.Properties.Backend);
        var grant = grants.Mint(armId, "compute:access-exec", TimeSpan.FromMinutes(10));
        await TouchAsync(armId, ct);
        return await backend.ExecAsync(request, grant, ct);
    }

    public async ValueTask TouchAsync(string armId, CancellationToken ct)
    {
        var key = armId.ToLowerInvariant();
        if (await states.GetAsync(key, ct) is null)
            return;
        await states.MutateAsync(key, p => p.LastActivityAt = clock.UtcNow, ct);
    }

    private IComputeBackend Resolve(string kind) =>
        backends.TryGet(kind, out var backend)
            ? backend
            : throw new ArmException(ArmError.Validation("UnsupportedBackend", [new ArmError("UnsupportedBackend", $"Unknown backend '{kind}'.", null, [], null, 400)]));
}
