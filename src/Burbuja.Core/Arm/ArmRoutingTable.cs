using System.Collections.Frozen;
using System.Text.Json;
using Burbuja.Core.Abstractions.Arm;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Core.Arm;

public sealed record ArmTypeRegistration(
    IResourceProvider Provider,
    ResourceTypeDescriptor Descriptor,
    IResourceHandlerAdapter Handler,
    JsonSchema? PropertiesSchema);

public interface IResourceHandlerAdapter
{
    ValueTask<object?> GetAsync(ResourceRequest req, CancellationToken ct);
    ValueTask<object> PutAsync(ResourceRequest req, object desired, CancellationToken ct);
    ValueTask<object> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct);
    ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct);
    ValueTask<object> ListAsync(ResourceListRequest req, CancellationToken ct);
    ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct);
}

public sealed class ResourceHandlerAdapter<TProps>(IResourceHandler<TProps> inner) : IResourceHandlerAdapter
    where TProps : class
{
    public async ValueTask<object?> GetAsync(ResourceRequest req, CancellationToken ct) =>
        await inner.GetAsync(req, ct);

    public async ValueTask<object> PutAsync(ResourceRequest req, object desired, CancellationToken ct) =>
        await inner.PutAsync(req, (TProps)desired, ct);

    public async ValueTask<object> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct) =>
        await inner.PatchAsync(req, patch, ct);

    public ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct) =>
        inner.DeleteAsync(req, ct);

    public async ValueTask<object> ListAsync(ResourceListRequest req, CancellationToken ct) =>
        await inner.ListAsync(req, ct);

    public ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct) =>
        inner.InvokeAsync(req, ct);
}

public sealed class ArmRoutingTable : IArmTypeResolver
{
    private readonly FrozenDictionary<string, ArmTypeRegistration> _byType;
    private readonly FrozenDictionary<string, IResourceProvider> _byNs;

    public ArmRoutingTable(IEnumerable<ArmTypeRegistration> registrations)
    {
        var types = new Dictionary<string, ArmTypeRegistration>(StringComparer.OrdinalIgnoreCase);
        var ns = new Dictionary<string, IResourceProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in registrations)
        {
            var key = TypeKey(r.Provider.Namespace, r.Descriptor.Type);
            if (!types.TryAdd(key, r))
                throw new Plugin.PluginBootException([$"duplicate resource type '{r.Provider.Namespace}/{r.Descriptor.Type}'"]);
            ns[r.Provider.Namespace] = r.Provider;
        }

        _byType = types.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _byNs = ns.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IResourceProvider> Providers => _byNs.Values;

    public static string TypeKey(string ns, string type) => $"{ns}/{type}".ToLowerInvariant();

    public bool TryGetProvider(string ns, out IResourceProvider provider) =>
        _byNs.TryGetValue(ns, out provider!);

    public bool TryGet(string ns, string type, out ArmTypeRegistration registration) =>
        _byType.TryGetValue(TypeKey(ns, type), out registration!);

    public ArmTypeRegistration? Find(ResourceId id)
    {
        if (id.TypeSegments.Length == 2
            && id.TypeSegments[0].Equals("locations", StringComparison.OrdinalIgnoreCase)
            && (id.TypeSegments[1].Equals("operations", StringComparison.OrdinalIgnoreCase)
                || id.TypeSegments[1].Equals("operationResults", StringComparison.OrdinalIgnoreCase)))
        {
            return TryGet(ArmConstants.ResourcesNamespace, "operations", out var operations) ? operations : null;
        }

        if (id.TypeSegments.Length == 1
            && id.TypeSegments[0].Equals("operations", StringComparison.OrdinalIgnoreCase))
        {
            if (id.SubscriptionId.Length == 0)
                return TryGet(ArmConstants.ResourcesNamespace, "providers", out var catalogue) ? catalogue : null;
            return TryGet(ArmConstants.ResourcesNamespace, "operations", out var tracked) ? tracked : null;
        }

        if (id.Namespace.Length == 0)
        {
            if (id.TypeSegments.Length == 1
                && id.TypeSegments[0].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
                return TryGet(ArmConstants.ResourcesNamespace, "resourceGroups", out var rgs) ? rgs : null;
            if (id.ResourceGroup is not null && id.TypeSegments.Length == 0)
                return TryGet(ArmConstants.ResourcesNamespace, "resourceGroups", out var rg) ? rg : null;
            if (id.TypeSegments.Length == 0)
                return TryGet(ArmConstants.ResourcesNamespace, "subscriptions", out var sub) ? sub : null;
            return null;
        }

        if (id.TypeSegments.Length == 0)
            return TryGet(ArmConstants.ResourcesNamespace, "providers", out var providers) ? providers : null;

        return TryGet(id.Namespace, id.FullyQualifiedType, out var reg) ? reg : null;
    }

    public bool IsType(string ns, string fqType) =>
        fqType.Equals("locations", StringComparison.OrdinalIgnoreCase)
        || fqType.Equals("locations/operations", StringComparison.OrdinalIgnoreCase)
        || fqType.Equals("locations/operationResults", StringComparison.OrdinalIgnoreCase)
        || fqType.Equals("operations", StringComparison.OrdinalIgnoreCase)
        || TryGet(ns, fqType, out _);

    public bool IsAction(string ns, string fqType, string name)
    {
        if (fqType.Length == 0
            && (name.Equals("register", StringComparison.OrdinalIgnoreCase)
                || name.Equals("unregister", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (!TryGet(ns, fqType, out var reg))
            return false;
        return reg.Descriptor.Actions.Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsDataPlaneSegment(string ns, string fqType, string name)
    {
        if (!TryGet(ns, fqType, out var reg))
            return false;
        return reg.Descriptor.DataPlaneRoutes.Any(r => r.Segment.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> ActionCatalogue { get; init; } = [];
}

public static class ArmRoutingTableBuilder
{
    public static ArmRoutingTable Build(IServiceProvider services)
    {
        var providers = services.GetService(typeof(IEnumerable<IResourceProvider>)) as IEnumerable<IResourceProvider>
            ?? [];
        var errors = new List<string>();
        var regs = new List<ArmTypeRegistration>();
        var catalogue = new List<string>();
        var seenNs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rp in providers)
        {
            if (!seenNs.Add(rp.Namespace))
                errors.Add($"duplicate provider namespace '{rp.Namespace}'");
            if (rp.Namespace.Length == 0 || !rp.Namespace.Contains('.', StringComparison.Ordinal))
                errors.Add($"provider namespace '{rp.Namespace}' is not well-formed");

            var typeNames = rp.ResourceTypes.Select(t => t.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var type in rp.ResourceTypes)
            {
                var segments = type.Type.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length is < 1 or > 3)
                    errors.Add($"{rp.Namespace}/{type.Type}: type must have 1-3 segments");
                foreach (var v in type.ApiVersions)
                {
                    if (string.IsNullOrWhiteSpace(v))
                        errors.Add($"{rp.Namespace}/{type.Type}: empty api-version");
                }

                errors.AddRange(DescriptorBuilder.For(type.Type)
                    .ApiVersions([.. type.ApiVersions])
                    .Capabilities(type.Capabilities)
                    .Tracked(type.IsTracked)
                    .AsyncPut(type.IsAsyncPut)
                    .AsyncDelete(type.IsAsyncDelete)
                    .Locations([.. type.Locations])
                    .Actions([.. type.Actions])
                    .DataPlane([.. type.DataPlaneRoutes])
                    .Children([.. type.Children])
                    .PropertiesType(type.PropertiesType)
                    .Schema(type.PropertiesSchema)
                    .RbacActions([.. type.RbacActions])
                    .Validate()
                    .Select(e => $"{rp.Namespace}/{e}"));

                foreach (var child in type.Children)
                {
                    var fq = type.Type + "/" + child;
                    if (!typeNames.Contains(child) && !typeNames.Contains(fq))
                        errors.Add($"{rp.Namespace}/{type.Type}: child '{child}' does not exist");
                }

                var handlerType = typeof(IResourceHandler<>).MakeGenericType(type.PropertiesType);
                var key = ArmRoutingTable.TypeKey(rp.Namespace, type.Type);
                var handler = services.GetKeyedService(handlerType, key) ?? services.GetService(handlerType);
                if (handler is null)
                {
                    errors.Add($"missing IResourceHandler<{type.PropertiesType.Name}> for {rp.Namespace}/{type.Type}");
                    continue;
                }

                var adapterType = typeof(ResourceHandlerAdapter<>).MakeGenericType(type.PropertiesType);
                var adapter = (IResourceHandlerAdapter)Activator.CreateInstance(adapterType, handler)!;
                JsonSchema? schema = null;
                if (type.PropertiesSchema.ValueKind is JsonValueKind.Object)
                    schema = JsonSchema.FromText(type.PropertiesSchema.GetRawText());

                regs.Add(new ArmTypeRegistration(rp, type, adapter, schema));
                catalogue.AddRange(type.RbacActions);
                catalogue.Add($"{rp.Namespace}/{type.Type}/read");
                catalogue.Add($"{rp.Namespace}/{type.Type}/write");
                catalogue.Add($"{rp.Namespace}/{type.Type}/delete");
                foreach (var action in type.Actions)
                    catalogue.Add($"{rp.Namespace}/{type.Type}/{action.Name}/action");
            }
        }

        if (errors.Count > 0)
            throw new Plugin.PluginBootException(errors);

        var distinct = catalogue.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var table = new ArmRoutingTable(regs) { ActionCatalogue = distinct };
        if (services.GetService(typeof(AuthorizationEvaluator)) is AuthorizationEvaluator evaluator)
            evaluator.BindCatalogueAndValidate(distinct);
        return table;
    }
}

public sealed class ArmRoutingTableStartup(ArmRoutingTable table) : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = table.Providers;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
