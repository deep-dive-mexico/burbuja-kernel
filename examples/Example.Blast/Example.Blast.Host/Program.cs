using Burbuja.Core.Plugin;
using Example.Blast.Host;

try
{
    await using var app = BlastHost.Create(new WebApplicationOptions
    {
        Args = args,
        ApplicationName = typeof(BlastHost).Assembly.GetName().Name,
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
