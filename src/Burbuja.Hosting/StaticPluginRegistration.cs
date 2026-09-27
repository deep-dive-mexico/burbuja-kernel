using Burbuja.Core.Abstractions.Plugin;

namespace Burbuja.Hosting;

/// <summary>
/// A lazy static fallback for a plugin. The id and assembly filename are the staged plugin.json values;
/// the factory is only called when discovery will not load that plugin from disk.
/// </summary>
public sealed record StaticPluginRegistration(string Id, string AssemblyFileName, Func<IBurbujaPlugin> Create);
