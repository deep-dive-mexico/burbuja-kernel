using Burbuja.Hosting;

namespace Example.Arena.Host;

public static class ArenaHost
{
    /// <summary>
    /// The kernel host with realtime enabled, plus this executable's own browser client at <c>/arena</c>.
    /// The game itself is discovered solely from the installed plugin's plugin.json.
    /// </summary>
    public static WebApplication Create(WebApplicationOptions builderOptions, Action<WebApplicationBuilder>? configure = null)
    {
        var app = BurbujaApplication.CreateApp(builderOptions, new BurbujaHostOptions
        {
            DefaultLandingRoute = "/arena",
            ConfigureBuilder = builder =>
            {
                // Relative plugin paths belong to this executable's content root, not the caller's cwd.
                var directory = builder.Configuration["Kernel:PluginsDirectory"];
                if (!string.IsNullOrWhiteSpace(directory) && !Path.IsPathRooted(directory))
                    builder.Configuration["Kernel:PluginsDirectory"] = Path.GetFullPath(directory, builder.Environment.ContentRootPath);
                configure?.Invoke(builder);
            },
        });

        var client = Path.Combine(Path.GetDirectoryName(typeof(ArenaHost).Assembly.Location)!, "client", "index.html");
        app.MapGet("/arena", () => Results.File(client, "text/html; charset=utf-8")).AllowAnonymous();
        return app;
    }
}
