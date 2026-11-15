using Burbuja.Cluster;
using Burbuja.Node;
using Burbuja.Plugins.Cluster.Tailscale;
using Burbuja.Plugins.Cluster.Twingate;

// burbuja-node                         run this machine as a cluster node (configuration decides the role)
// burbuja-node ctl <verb> [arguments]  talk to a controller as its operator
if (args.Length > 0 && args[0] == "ctl") return await OperatorCommand.RunAsync(args[1..]);

var builder = WebApplication.CreateBuilder(args);

// Installing this host is asking for a node, so the module is on unless configuration says otherwise.
builder.Configuration.Sources.Insert(0, new Microsoft.Extensions.Configuration.Memory.MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?> { ["Cluster:Enabled"] = "true" },
});
var configFile = Array.IndexOf(args, "--config") is >= 0 and var index && index + 1 < args.Length ? args[index + 1] : null;
if (configFile is not null) builder.Configuration.AddJsonFile(Path.GetFullPath(configFile), optional: false, reloadOnChange: false);

var options = builder.Configuration.GetSection(ClusterOptions.Section).Get<ClusterOptions>() ?? new ClusterOptions();
builder.WebHost.UseUrls(options.Listen.Count > 0 ? [.. options.Listen] : [$"{options.Scheme}://127.0.0.1:{options.Port}"]);

// The networks this host ships with. Each reports itself unusable where its client is absent, and they are
// registered before the module so that they claim their own addresses ahead of the generic classification.
new TailscalePlugin().ConfigureServices(builder.Services);
new TwingatePlugin().ConfigureServices(builder.Services);
builder.Services.AddBurbujaCluster(builder.Configuration);

// Give the node time to stop what it must stop on the way out (work holding exclusive keys, bounded by
// Cluster:Agent:ShutdownGraceSeconds) before the host gives up on it.
builder.Services.Configure<HostOptions>(host =>
    host.ShutdownTimeout = TimeSpan.FromSeconds(options.Agent.ShutdownGraceSeconds + 15));

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapBurbujaCluster();
app.Run();
return 0;
