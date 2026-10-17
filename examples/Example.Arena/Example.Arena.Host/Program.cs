using Burbuja.Core.Plugin;
using Example.Arena.Host;

try
{
    await using var app = ArenaHost.Create(new WebApplicationOptions
    {
        Args = args,
        ApplicationName = typeof(ArenaHost).Assembly.GetName().Name,
        ContentRootPath = AppContext.BaseDirectory,
    });
    await app.RunAsync();
    return 0;
}
catch (PluginBootException ex)
{
    Console.Error.WriteLine(ex.Message);
    return PluginBootException.ExitCode;
}
