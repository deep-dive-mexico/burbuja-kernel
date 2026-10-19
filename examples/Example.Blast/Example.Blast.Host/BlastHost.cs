using Burbuja.Hosting;

namespace Example.Blast.Host;

public static class BlastHost
{
    /// <summary>
    /// The kernel host with realtime enabled, plus this executable's own browser client at <c>/blast</c>.
    /// The game itself is discovered solely from the installed plugin's plugin.json.
    /// </summary>
    public static WebApplication Create(WebApplicationOptions builderOptions, Action<WebApplicationBuilder>? configure = null)
    {
        var app = BurbujaApplication.CreateApp(builderOptions, new BurbujaHostOptions
        {
            DefaultLandingRoute = "/blast",
            ConfigureBuilder = builder =>
            {
                var directory = builder.Configuration["Kernel:PluginsDirectory"];
                if (!string.IsNullOrWhiteSpace(directory) && !Path.IsPathRooted(directory))
                    builder.Configuration["Kernel:PluginsDirectory"] = Path.GetFullPath(directory, builder.Environment.ContentRootPath);
                configure?.Invoke(builder);
            },
        });

        var client = Path.Combine(Path.GetDirectoryName(typeof(BlastHost).Assembly.Location)!, "client", "index.html");
        app.MapGet("/blast", () => Results.File(client, "text/html; charset=utf-8")).AllowAnonymous();
        return app;
    }
}
