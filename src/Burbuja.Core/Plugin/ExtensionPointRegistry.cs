using Burbuja.Core.Abstractions.Plugin;

namespace Burbuja.Core.Plugin;

public sealed class ExtensionPointRegistry : IExtensionPointRegistry
{
    private readonly Dictionary<string, ExtensionPoint> _points = new(StringComparer.Ordinal);

    public IReadOnlyList<ExtensionPoint> All => _points.Values.ToArray();

    public void Declare<TContract>(string name, ExtensionCardinality cardinality, PluginCapability requiredCapabilities = PluginCapability.None)
        where TContract : class
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _points[name] = new ExtensionPoint(name, typeof(TContract), cardinality, requiredCapabilities);
    }

    public bool TryGet(string name, out ExtensionPoint point) =>
        _points.TryGetValue(name, out point!);
}
