using System.Reflection;

namespace Burbuja.Hosting;

/// <summary>Explicit, per-application contributions to the generic host.</summary>
public sealed class BurbujaHostOptions
{
    /// <summary>
    /// Runs after the normal ASP.NET and Burbuja configuration sources, before core/plugin boot or any
    /// portal options are read. Register product services, configuration sources or a test server here.
    /// Later configuration sources take precedence; a product adding env aliases should re-add command
    /// line arguments last if it wants command-line overrides to retain priority.
    /// </summary>
    public Action<WebApplicationBuilder>? ConfigureBuilder { get; init; }

    /// <summary>
    /// Extra plugins alongside the built-ins. A matching staged manifest and assembly takes precedence
    /// over the static factory. This does not grant trust: Kernel:TrustedPlugins still controls boot.
    /// Repeated ids use the first static registration, with built-ins first.
    /// </summary>
    public IReadOnlyList<StaticPluginRegistration> AdditionalStaticPlugins { get; init; } = [];

    /// <summary>
    /// Additional Razor endpoint assemblies. PortalRegistry.AdditionalAssemblies and Assembly services
    /// contributed before Build are included too, with duplicates removed. For interactive navigation,
    /// register the portal's scope through AddPortal in ConfigureBuilder; that supplies both the router's
    /// assembly and its policy/layout. The executable must reference its Razor libraries so the SDK
    /// publishes their static web assets.
    /// </summary>
    public IReadOnlyList<Assembly> AdditionalRazorAssemblies { get; init; } = [];

    /// <summary>The control-plane '/' redirect. Defaults to the kernel portal; null leaves '/' unmapped.</summary>
    public string? DefaultLandingRoute { get; init; } = "/portal";
}
