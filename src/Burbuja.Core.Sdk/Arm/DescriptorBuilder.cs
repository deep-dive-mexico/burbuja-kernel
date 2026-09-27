using System.Text.Json;
using Burbuja.Core.Abstractions.Arm;
using Json.Schema;

namespace Burbuja.Core.Arm;

public static class SchemaBuilder
{
    public static JsonElement Object(bool additionalProperties = true) =>
        Parse(additionalProperties
            ? """{"type":"object","additionalProperties":true}"""
            : """{"type":"object","additionalProperties":false}""");

    public static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    public static JsonSchema Compile(JsonElement schema) =>
        JsonSchema.FromText(schema.GetRawText());
}

public sealed class DescriptorBuilder
{
    private readonly string _type;
    private IReadOnlyList<string> _apiVersions = [ArmConstants.ApiVersion];
    private ResourceCapabilities _capabilities;
    private bool _tracked = true;
    private bool _asyncPut;
    private bool _asyncDelete;
    private IReadOnlyList<string> _locations = ["local"];
    private IReadOnlyList<ResourceActionDescriptor> _actions = [];
    private IReadOnlyList<DataPlaneRouteDescriptor> _dataPlane = [];
    private IReadOnlyList<string> _children = [];
    private Type? _propertiesType;
    private JsonElement _schema = SchemaBuilder.Object();
    private IReadOnlyList<string> _rbac = [];

    private DescriptorBuilder(string type) => _type = type;

    public static DescriptorBuilder For(string type) => new(type);

    public DescriptorBuilder ApiVersions(params string[] versions)
    {
        _apiVersions = versions;
        return this;
    }

    public DescriptorBuilder Capabilities(ResourceCapabilities capabilities)
    {
        _capabilities = capabilities;
        return this;
    }

    public DescriptorBuilder Tracked(bool tracked = true)
    {
        _tracked = tracked;
        return this;
    }

    public DescriptorBuilder AsyncPut(bool value = true)
    {
        _asyncPut = value;
        return this;
    }

    public DescriptorBuilder AsyncDelete(bool value = true)
    {
        _asyncDelete = value;
        return this;
    }

    public DescriptorBuilder Locations(params string[] locations)
    {
        _locations = locations;
        return this;
    }

    public DescriptorBuilder Actions(params ResourceActionDescriptor[] actions)
    {
        _actions = actions;
        return this;
    }

    public DescriptorBuilder DataPlane(params DataPlaneRouteDescriptor[] routes)
    {
        _dataPlane = routes;
        return this;
    }

    public DescriptorBuilder Children(params string[] children)
    {
        _children = children;
        return this;
    }

    public DescriptorBuilder Properties<TProps>() where TProps : class
    {
        _propertiesType = typeof(TProps);
        return this;
    }

    public DescriptorBuilder PropertiesType(Type type)
    {
        _propertiesType = type;
        return this;
    }

    public DescriptorBuilder Schema(JsonElement schema)
    {
        _schema = schema;
        return this;
    }

    public DescriptorBuilder RbacActions(params string[] actions)
    {
        _rbac = actions;
        return this;
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(_type))
            errors.Add("type is required");
        if (_propertiesType is null)
            errors.Add($"{_type}: PropertiesType is required");
        if (_apiVersions.Count == 0 || _apiVersions.Any(string.IsNullOrWhiteSpace))
            errors.Add($"{_type}: apiVersions must be non-empty");
        if (_asyncPut && !_capabilities.HasFlag(ResourceCapabilities.Put))
            errors.Add($"{_type}: IsAsyncPut requires Put capability");
        if (_asyncDelete && !_capabilities.HasFlag(ResourceCapabilities.Delete))
            errors.Add($"{_type}: IsAsyncDelete requires Delete capability");
        if (_actions.Count > 0 && !_capabilities.HasFlag(ResourceCapabilities.Actions))
            errors.Add($"{_type}: actions require Actions capability");
        try
        {
            _ = SchemaBuilder.Compile(_schema);
        }
        catch (Exception ex)
        {
            errors.Add($"{_type}: schema does not compile: {ex.Message}");
        }

        var actionNames = _actions.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var childNames = _children.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var route in _dataPlane)
        {
            if (actionNames.Contains(route.Segment))
                errors.Add($"{_type}: data-plane segment '{route.Segment}' collides with an action");
            if (childNames.Contains(route.Segment))
                errors.Add($"{_type}: data-plane segment '{route.Segment}' collides with a child");
        }

        foreach (var child in _children)
        {
            if (actionNames.Contains(child))
                errors.Add($"{_type}: child '{child}' collides with an action");
        }

        return errors;
    }

    public ResourceTypeDescriptor Build()
    {
        var errors = Validate();
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join("; ", errors));
        return new ResourceTypeDescriptor(
            _type,
            _apiVersions,
            _capabilities,
            _tracked,
            _asyncPut,
            _asyncDelete,
            _locations,
            _actions,
            _dataPlane,
            _children,
            _propertiesType!,
            _schema,
            _rbac);
    }
}
