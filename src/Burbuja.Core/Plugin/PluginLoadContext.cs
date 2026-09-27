using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Burbuja.Core.Plugin;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginPath)
        : base(isCollectible: false)
    {
        _pluginDirectory = Path.GetDirectoryName(Path.GetFullPath(pluginPath)) ?? ".";
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var n = assemblyName.Name ?? "";

        // Hard identity rule: the kernel's contract assemblies are always unified to the
        // default ALC. Never resolve a plugin-local copy of these, or IBurbujaPlugin type
        // identity breaks. This is intentionally *not* a fallback path.
        if (n.StartsWith("Burbuja.", StringComparison.Ordinal) && n.Contains(".Abstractions", StringComparison.Ordinal))
            return null;
        if (n == "Burbuja.Core.Sdk")
            return null;

        // Framework assemblies stay in the default ALC.
        if (n.StartsWith("System.", StringComparison.Ordinal) || n == "System")
            return null;

        // Shared-prefix Microsoft.Extensions.* names: prefer the default ALC (one copy shared
        // with the kernel) but fall back to the plugin folder when the host does not ship the
        // assembly. Without the fallback a dynamic plugin that needs e.g.
        // Microsoft.Extensions.AI.Abstractions throws FileNotFoundException even though the
        // assembly ships in the plugin's deps.json.
        if (n.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
        {
            if (TryLoadFromDefault(assemblyName, out var fromDefault))
                return fromDefault;

            var sharedPath = _resolver.ResolveAssemblyToPath(assemblyName);
            return sharedPath is null ? null : LoadFromAssemblyPath(sharedPath);
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) ?? ProbeNative(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private static bool TryLoadFromDefault(AssemblyName assemblyName, out Assembly? assembly)
    {
        try
        {
            assembly = Default.LoadFromAssemblyName(assemblyName);
            return true;
        }
        catch (FileNotFoundException)
        {
            assembly = null;
            return false;
        }
        catch (FileLoadException)
        {
            assembly = null;
            return false;
        }
    }

    // AssemblyDependencyResolver only resolves native assets declared as NuGet native assets in
    // deps.json. Plugins (and test fixtures) that ship a project-local dylib/so/dll under
    // runtimes/<rid>/native are not covered, so probe the conventional locations ourselves.
    private string? ProbeNative(string unmanagedDllName)
    {
        var candidates = new List<string> { unmanagedDllName };
        if (!unmanagedDllName.EndsWith(".dylib", StringComparison.Ordinal)
            && !unmanagedDllName.EndsWith(".so", StringComparison.Ordinal)
            && !unmanagedDllName.EndsWith(".dll", StringComparison.Ordinal))
        {
            candidates.Add($"lib{unmanagedDllName}.dylib");
            candidates.Add($"lib{unmanagedDllName}.so");
            candidates.Add($"{unmanagedDllName}.dll");
            candidates.Add($"lib{unmanagedDllName}");
        }

        var directories = new List<string>();
        var nativeRoot = Path.Combine(_pluginDirectory, "runtimes");
        if (Directory.Exists(nativeRoot))
        {
            var rid = RuntimeInformation.RuntimeIdentifier;
            var exact = Path.Combine(nativeRoot, rid, "native");
            if (Directory.Exists(exact))
                directories.Add(exact);

            try
            {
                foreach (var runtimes in Directory.GetDirectories(nativeRoot, "*", SearchOption.AllDirectories))
                {
                    if (string.Equals(Path.GetFileName(runtimes), "native", StringComparison.Ordinal))
                        directories.Add(runtimes);
                }
            }
            catch (IOException)
            {
                // best-effort probe only
            }
        }

        directories.Add(Path.Combine(nativeRoot, "native"));
        directories.Add(_pluginDirectory);

        foreach (var dir in directories)
        {
            foreach (var candidate in candidates)
            {
                var path = Path.Combine(dir, candidate);
                if (File.Exists(path))
                    return path;
            }
        }

        return null;
    }
}
