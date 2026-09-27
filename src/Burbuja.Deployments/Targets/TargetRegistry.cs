using Burbuja.Deployments.Abstractions;

namespace Burbuja.Deployments.Targets;

public sealed class TargetRegistry(IEnumerable<IDeploymentTarget> targets)
{
    private readonly Dictionary<string, IDeploymentTarget> _byKind =
        targets.ToDictionary(t => t.Kind, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IDeploymentTarget> All => _byKind.Values;

    public bool TryGet(string kind, out IDeploymentTarget target) =>
        _byKind.TryGetValue(kind, out target!);
}
