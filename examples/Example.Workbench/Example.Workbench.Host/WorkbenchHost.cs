using Burbuja.Hosting;
using Burbuja.Portal;

namespace Example.Workbench.Host;

public static class WorkbenchHost
{
    // The executable owns its portal. The resource provider is discovered solely from plugin.json.
    public static BurbujaHostOptions Options(Action<WebApplicationBuilder>? configure = null) => new()
    {
        DefaultLandingRoute = "/workbench",
        ConfigureBuilder = builder =>
        {
            builder.Services.AddPortal(new PortalScope("workbench", "/workbench", "en", "Example Workbench")
            {
                Assembly = typeof(WorkbenchHost).Assembly,
                Layout = typeof(Components.WorkbenchLayout),
                IsDefault = true,
                Stylesheets = [],
            });
            // Relative plugin paths belong to this executable's content root, not the caller's cwd.
            var directory = builder.Configuration["Kernel:PluginsDirectory"];
            if (!string.IsNullOrWhiteSpace(directory) && !Path.IsPathRooted(directory))
                builder.Configuration["Kernel:PluginsDirectory"] = Path.GetFullPath(directory, builder.Environment.ContentRootPath);
            configure?.Invoke(builder);
        },
    };
}
