using Burbuja.Hosting;
using Example.Workbench.Host;

return await BurbujaApplication.RunAsync(new WebApplicationOptions
{
    Args = args,
    ApplicationName = typeof(WorkbenchHost).Assembly.GetName().Name,
    ContentRootPath = AppContext.BaseDirectory,
}, WorkbenchHost.Options());
