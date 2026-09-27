using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Hosting;

namespace Burbuja.Host;

/// <summary>The generic executable; product hosts compose the same library with their own contributions.</summary>
public partial class Program
{
    public static Task<int> Main(string[] args) => BurbujaApplication.RunAsync(BuilderOptions(args));

    public static WebApplication CreateApp(string[] args) => BurbujaApplication.CreateApp(BuilderOptions(args));

    internal static IReadOnlyList<IBurbujaPlugin> StaticPlugins(IConfiguration configuration) =>
        BurbujaApplication.StaticPlugins(configuration);

    private static WebApplicationOptions BuilderOptions(string[] args) => new()
    {
        Args = args,
        ApplicationName = typeof(Program).Assembly.GetName().Name,
    };
}
