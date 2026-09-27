using System.Collections.Immutable;
using System.Text;

namespace Burbuja.Core.Abstractions.Arm;

public interface IArmTypeResolver
{
    bool IsType(string ns, string fqType);
    bool IsAction(string ns, string fqType, string name);
    bool IsDataPlaneSegment(string ns, string fqType, string name);
}

public sealed record ResourceId
{
    public required string SubscriptionId { get; init; }
    public string? ResourceGroup { get; init; }
    public required string Namespace { get; init; }
    public ImmutableArray<string> TypeSegments { get; init; } = [];
    public ImmutableArray<string> NameSegments { get; init; } = [];
    public string? Action { get; init; }
    public string? DataPlaneSegment { get; init; }

    /// <summary>
    /// The sub-path after <see cref="DataPlaneSegment"/> (e.g. <c>patch.diff</c> in
    /// <c>…/jobs/j1/artifacts/patch.diff</c>). Never part of <see cref="Canonical"/>/<see cref="Key"/>.
    /// </summary>
    public string? DataPlanePath { get; init; }
    public bool IsCollection { get; init; }
    public ResourceId? Parent { get; init; }

    public string FullyQualifiedType => TypeSegments.Length == 0 ? string.Empty : string.Join('/', TypeSegments);

    public string Canonical
    {
        get
        {
            var sb = new StringBuilder();
            var parentIsDifferentHop = Parent is { Namespace.Length: > 0 } p
                && Namespace.Length > 0
                && !string.Equals(p.Namespace, Namespace, StringComparison.OrdinalIgnoreCase);
            if (parentIsDifferentHop)
            {
                sb.Append(Parent!.Canonical);
            }
            else
            {
                if (SubscriptionId.Length > 0)
                {
                    sb.Append("/subscriptions/").Append(SubscriptionId);
                    if (ResourceGroup is not null)
                        sb.Append("/resourceGroups/").Append(ResourceGroup);
                }
            }

            if (Namespace.Length > 0)
            {
                sb.Append("/providers/").Append(Namespace);
                for (var i = 0; i < TypeSegments.Length; i++)
                {
                    sb.Append('/').Append(TypeSegments[i]);
                    if (i < NameSegments.Length)
                        sb.Append('/').Append(NameSegments[i]);
                }
            }

            if (Action is not null)
                sb.Append('/').Append(Action);
            if (DataPlaneSegment is not null)
                sb.Append('/').Append(DataPlaneSegment);
            return sb.ToString();
        }
    }

    public string Key => Canonical.ToLowerInvariant();

    public static bool TryParse(ReadOnlySpan<char> path, out ResourceId id, out ArmError? error) =>
        TryParse(path, null, out id, out error);

    public static bool TryParse(ReadOnlySpan<char> path, IArmTypeResolver? resolver, out ResourceId id, out ArmError? error)
    {
        id = null!;
        error = null;
        if (path.IsEmpty || path[0] != '/')
        {
            error = ArmError.InvalidResourceUri("Resource URI must start with '/'.");
            return false;
        }

        var text = path.ToString().TrimEnd('/');
        if (text.Length == 0)
        {
            error = ArmError.InvalidResourceUri("Resource URI is empty.");
            return false;
        }

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = 0;
        var subscriptionId = "";
        string? resourceGroup = null;

        if (i < parts.Length && parts[i].Equals("subscriptions", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= parts.Length)
            {
                error = ArmError.InvalidResourceUri("Missing subscription id.");
                return false;
            }

            subscriptionId = parts[i + 1];
            i += 2;
            if (i < parts.Length && parts[i].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= parts.Length)
                {
                    id = new ResourceId
                    {
                        SubscriptionId = subscriptionId,
                        ResourceGroup = null,
                        Namespace = "",
                        TypeSegments = ["resourceGroups"],
                        NameSegments = [],
                        IsCollection = true,
                        Parent = new ResourceId
                        {
                            SubscriptionId = subscriptionId,
                            ResourceGroup = null,
                            Namespace = "",
                            TypeSegments = [],
                            NameSegments = [],
                            IsCollection = true,
                        },
                    };
                    return true;
                }

                resourceGroup = parts[i + 1];
                i += 2;
            }
        }

        ResourceId? scopeParent = null;
        if (subscriptionId.Length > 0)
        {
            var sub = new ResourceId
            {
                SubscriptionId = subscriptionId,
                ResourceGroup = null,
                Namespace = "",
                TypeSegments = [],
                NameSegments = [],
                IsCollection = false,
                Parent = null,
            };
            if (resourceGroup is not null)
            {
                scopeParent = new ResourceId
                {
                    SubscriptionId = subscriptionId,
                    ResourceGroup = resourceGroup,
                    Namespace = "",
                    TypeSegments = [],
                    NameSegments = [],
                    IsCollection = false,
                    Parent = sub,
                };
            }
            else
            {
                scopeParent = sub;
            }
        }

        if (i >= parts.Length)
        {
            if (subscriptionId.Length == 0)
            {
                error = ArmError.InvalidResourceUri("Expected /providers/{namespace}.");
                return false;
            }

            id = new ResourceId
            {
                SubscriptionId = subscriptionId,
                ResourceGroup = resourceGroup,
                Namespace = "",
                TypeSegments = [],
                NameSegments = [],
                IsCollection = resourceGroup is null,
                Parent = resourceGroup is not null ? scopeParent!.Parent : null,
            };
            return true;
        }

        ResourceId? current = scopeParent;
        var firstHop = true;
        while (i < parts.Length)
        {
            if (!parts[i].Equals("providers", StringComparison.OrdinalIgnoreCase))
            {
                error = ArmError.InvalidResourceUri("Expected /providers/{namespace}.");
                return false;
            }

            if (i + 1 >= parts.Length)
            {
                error = ArmError.NoRegisteredProviderFound("Missing provider namespace.");
                return false;
            }

            var ns = parts[i + 1];
            i += 2;
            var hopParent = firstHop ? scopeParent : current;
            if (!TryParseHop(parts, ref i, subscriptionId, resourceGroup, ns, hopParent, resolver, firstHop, out var hop, out error))
                return false;
            current = hop;
            firstHop = false;
        }

        id = current!;
        return true;
    }

    private static bool TryParseHop(
        string[] parts,
        ref int i,
        string subscriptionId,
        string? resourceGroup,
        string ns,
        ResourceId? parent,
        IArmTypeResolver? resolver,
        bool firstHop,
        out ResourceId id,
        out ArmError? error)
    {
        error = null;
        var types = new List<string>();
        var names = new List<string>();
        string? action = null;
        string? dataPlane = null;
        string? dataPlanePath = null;
        var collection = false;
        _ = firstHop;

        if (i >= parts.Length)
        {
            id = new ResourceId
            {
                SubscriptionId = subscriptionId,
                ResourceGroup = resourceGroup,
                Namespace = ns,
                TypeSegments = [],
                NameSegments = [],
                IsCollection = false,
                Parent = parent,
            };
            return true;
        }

        while (i < parts.Length)
        {
            if (parts[i].Equals("providers", StringComparison.OrdinalIgnoreCase) && types.Count > 0 && types.Count == names.Count)
                break;

            var segment = parts[i];
            var fqSoFar = types.Count == 0 ? "" : string.Join('/', types);
            var nextFq = fqSoFar.Length == 0 ? segment : fqSoFar + "/" + segment;

            if (types.Count != names.Count)
            {
                names.Add(segment);
                collection = false;
                i++;
                continue;
            }

            if (resolver is not null)
            {
                if (resolver.IsType(ns, nextFq))
                {
                    types.Add(segment);
                    collection = true;
                    i++;
                    continue;
                }

                if (resolver.IsAction(ns, fqSoFar, segment))
                {
                    action = segment;
                    i++;
                    break;
                }

                if (resolver.IsDataPlaneSegment(ns, fqSoFar, segment))
                {
                    dataPlane = segment;
                    i++;
                    if (i < parts.Length)
                        dataPlanePath = string.Join('/', parts[i..]);
                    // The sub-path belongs to the data plane; consume it so the outer hop loop
                    // does not require another '/providers/...'.
                    i = parts.Length;
                    break;
                }

                if (types.Count == 0 && parts.Length - i == 1)
                {
                    action = segment;
                    i++;
                    break;
                }

                error = ArmError.InvalidResourceType($"'{segment}' is not a type, action, or data-plane segment of '{ns}/{fqSoFar}'.");
                id = null!;
                return false;
            }

            if (types.Count == 0 && parts.Length - i == 1 && subscriptionId.Length > 0 && IsLikelyAction(segment))
            {
                action = segment;
                i++;
                break;
            }

            types.Add(segment);
            collection = true;
            i++;
        }

        id = new ResourceId
        {
            SubscriptionId = subscriptionId,
            ResourceGroup = resourceGroup,
            Namespace = ns,
            TypeSegments = [.. types],
            NameSegments = [.. names],
            Action = action,
            DataPlaneSegment = dataPlane,
            DataPlanePath = dataPlanePath,
            IsCollection = collection && action is null && dataPlane is null,
            Parent = BuildNestedParent(subscriptionId, resourceGroup, ns, types, names, parent),
        };
        return true;
    }

    private static ResourceId? BuildNestedParent(
        string subscriptionId,
        string? resourceGroup,
        string ns,
        List<string> types,
        List<string> names,
        ResourceId? hopParent)
    {
        if (types.Count == 0)
            return hopParent;

        if (names.Count == 0)
            return hopParent;

        if (types.Count == names.Count && types.Count == 1)
            return hopParent;

        if (types.Count == names.Count + 1)
        {
            return new ResourceId
            {
                SubscriptionId = subscriptionId,
                ResourceGroup = resourceGroup,
                Namespace = ns,
                TypeSegments = [.. types.Take(types.Count - 1)],
                NameSegments = [.. names],
                IsCollection = false,
                Parent = hopParent,
            };
        }

        return new ResourceId
        {
            SubscriptionId = subscriptionId,
            ResourceGroup = resourceGroup,
            Namespace = ns,
            TypeSegments = [.. types.Take(types.Count - 1)],
            NameSegments = [.. names.Take(names.Count - 1)],
            IsCollection = false,
            Parent = BuildNestedParent(subscriptionId, resourceGroup, ns, types.Take(types.Count - 1).ToList(), names.Take(names.Count - 1).ToList(), hopParent),
        };
    }

    private static bool IsLikelyAction(string segment) =>
        segment.Equals("register", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("unregister", StringComparison.OrdinalIgnoreCase);
}
