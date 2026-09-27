using System.Text.RegularExpressions;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Deployments.Abstractions;

namespace Burbuja.Deployments.Execution;

public sealed record PlanResult(
    IReadOnlyList<IReadOnlyList<string>> Waves,
    IReadOnlyList<ArmError> Errors);

public static class DeploymentPlanner
{
    private static readonly Regex NamePattern = new("^[a-z][a-z0-9-]{1,38}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex StepRef = new(@"\$\{steps\.([A-Za-z0-9-]+)\.outputs\.[^}]+\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static PlanResult Validate(
        DeploymentTemplate template,
        IReadOnlyCollection<DeploymentResourceKind> supported,
        DeploymentMode mode)
    {
        var errors = new List<ArmError>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var resources = template.Resources;
        for (var i = 0; i < resources.Count; i++)
        {
            var r = resources[i];
            var ptr = $"/properties/template/resources/{i}";
            if (!NamePattern.IsMatch(r.Name))
                errors.Add(Detail(ptr + "/name", $"Resource name '{r.Name}' is invalid."));
            if (!names.Add(r.Name))
                errors.Add(Detail(ptr + "/name", $"Duplicate resource name '{r.Name}'."));
            if (!supported.Contains(r.Kind))
                errors.Add(Detail(ptr + "/kind", $"Kind '{r.Kind}' is not supported by the target."));
            foreach (var dep in r.DependsOn)
            {
                if (!resources.Any(x => x.Name.Equals(dep, StringComparison.Ordinal)))
                    errors.Add(Detail(ptr + "/dependsOn", $"Unknown dependsOn '{dep}'."));
            }

            foreach (Match m in StepRef.Matches(r.Spec.ToJsonString()))
            {
                var dep = m.Groups[1].Value;
                if (!r.DependsOn.Contains(dep, StringComparer.Ordinal))
                    r.DependsOn.Add(dep);
                if (!resources.Any(x => x.Name.Equals(dep, StringComparison.Ordinal)))
                    errors.Add(Detail(ptr + "/spec", $"Unresolved reference to steps.{dep}."));
            }
        }

        if (errors.Count > 0)
            return new PlanResult([], errors);

        try
        {
            var waves = Waves(resources);
            _ = mode;
            return new PlanResult(waves, []);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(Detail("/properties/template/resources", ex.Message));
            return new PlanResult([], errors);
        }
    }

    public static IReadOnlyList<IReadOnlyList<string>> Waves(IReadOnlyList<DeploymentResource> resources)
    {
        var byName = resources.ToDictionary(r => r.Name, StringComparer.Ordinal);
        var indegree = resources.ToDictionary(r => r.Name, _ => 0, StringComparer.Ordinal);
        var edges = resources.ToDictionary(r => r.Name, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var r in resources)
        {
            foreach (var dep in r.DependsOn.Distinct(StringComparer.Ordinal))
            {
                if (!byName.ContainsKey(dep))
                    throw new InvalidOperationException($"Unknown dependsOn '{dep}'.");
                if (dep.Equals(r.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Cycle involving '{r.Name}'.");
                edges[dep].Add(r.Name);
                indegree[r.Name]++;
            }
        }

        var remaining = resources.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var waves = new List<IReadOnlyList<string>>();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(n => indegree[n] == 0).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (ready.Count == 0)
            {
                var cycle = string.Join(", ", remaining.OrderBy(n => n, StringComparer.Ordinal));
                throw new InvalidOperationException($"Cycle involving {cycle}.");
            }

            waves.Add(ready);
            foreach (var n in ready)
            {
                remaining.Remove(n);
                foreach (var next in edges[n])
                    indegree[next]--;
            }
        }

        return waves;
    }

    private static ArmError Detail(string target, string message) =>
        new("InvalidTemplate", message, target, [], null, 400);
}
