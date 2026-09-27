using Burbuja.Agents;
using Burbuja.Compute;
using Burbuja.Integrations;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.DependencyInjection;
using Burbuja.Core.Diagnostics;
using Burbuja.Core.Options;
using Burbuja.Core.Plugin;
using Burbuja.Deployments;
using Burbuja.Host.Arm;
using Burbuja.Host.OpenApi;
using Burbuja.Host.Seed;
using Burbuja.Host.Worker;
using Burbuja.Mcp;
using Burbuja.Plugins.Llm.Scripted;
using Burbuja.Portal;
using Burbuja.Plugins.Policy.Core;
using Burbuja.Plugins.Supervisor;
using Burbuja.Plugins.Tools.Agents;
using Burbuja.Plugins.Tools.Http;
using Burbuja.Plugins.Tools.Workspace;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Burbuja.Hosting;

/// <summary>The shared ARM, portal, MCP, ingress and worker composition used by executable hosts.</summary>
public static class BurbujaApplication
{
    /// <summary>Runs a composed host, reporting plugin boot failures with the established exit code.</summary>
    public static async Task<int> RunAsync(WebApplicationOptions builderOptions, BurbujaHostOptions? options = null)
    {
        try
        {
            await using var app = CreateApp(builderOptions, options);
            await app.RunAsync();
            return 0;
        }
        catch (PluginBootException ex)
        {
            // A host that exits 78 with no explanation is undiagnosable from a LaunchAgent log.
            Console.Error.WriteLine(ex.Message);
            return PluginBootException.ExitCode;
        }
    }

    /// <summary>Creates a host using the entry assembly's application identity and normal ASP.NET configuration.</summary>
    public static WebApplication CreateApp(string[] args, BurbujaHostOptions? options = null) =>
        CreateApp(new WebApplicationOptions { Args = args }, options);

    /// <summary>
    /// Creates, but does not start, a composed host. A consumer may supply its application name and content
    /// root through <paramref name="builderOptions"/> so its own configuration and static assets are used.
    /// </summary>
    public static WebApplication CreateApp(WebApplicationOptions builderOptions, BurbujaHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builderOptions);
        options ??= new BurbujaHostOptions();
        var landingRoute = options.DefaultLandingRoute;
        if (landingRoute is not null && (landingRoute.Length < 2 || landingRoute[0] != '/'
            || landingRoute[1] is '/' or '\\' || landingRoute.Any(char.IsControl)))
            throw new ArgumentException("The default landing route must be a local path other than '/', or null.", nameof(options));

        var builder = WebApplication.CreateBuilder(builderOptions);
        builder.Configuration.AddBurbujaSources(builderOptions.Args);
        // Plugin discovery and ConfigureServices run synchronously in AddBurbujaCore. Product services,
        // configuration providers and test transports must therefore be supplied BEFORE this call.
        options.ConfigureBuilder?.Invoke(builder);
        builder.Services.AddBurbujaCore(builder.Configuration, StaticPlugins(builder.Configuration, options.AdditionalStaticPlugins));
        // M5.2: the kernel MCP lane. Registered unconditionally (the role gate is the status quo of
        // every other subsystem) and mounted below only on the control plane.
        builder.Services.AddBurbujaMcp();
        // M8/I0: integrations as ARM resources. A module rather than a plugin: the ARM router discovers
        // providers from DI, so no trust list, manifest or staged assembly is involved.
        builder.Services.AddBurbujaIntegrations();
        // I1: the mailbox instance of it.
        builder.Services.AddBurbujaMailIntegration();
        builder.Services.AddBurbujaBrowserIntegration();
        // I3: the workspace instance of it — the report sink nightshift kept in its own process.
        builder.Services.AddBurbujaNotionIntegration();
        builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer<ArmDocumentTransformer>());
        builder.Services.AddHostedService<KernelSeedHostedService>();
        builder.Services.AddHostedService<NodeHeartbeatService>();
        builder.Services.AddHostedService<WorkerPlaceholderService>();

        // ─── M5.5 portal: services ────────────────────────────────────────────────────────────
        // Registered unconditionally (like every other subsystem) and mounted below only on the
        // control plane, where ARM itself lives: the portal is an ARM client and has nothing to
        // talk to on a worker.
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();
        builder.Services.AddBurbujaPortal(builder.Configuration);
        // ─── end M5.5 services ────────────────────────────────────────────────────────────────

        var app = builder.Build();
        var role = app.Services.GetRequiredService<IOptions<KernelOptions>>().Value.Role;
        var controlPlane = role.Equals("control-plane", StringComparison.OrdinalIgnoreCase)
            || role.Equals("all", StringComparison.OrdinalIgnoreCase);

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains("ready") });

        // M14 R10 (request row X5) — the plugin doors. Mapped for EVERY role and not only the control
        // plane: a third party calls the node that runs the channel plugin, and on a split deployment that
        // is the worker, not the node serving ARM. With no endpoints registered this maps nothing and the
        // boot check has nothing to check, which is the correct state for a node whose plugins ask for no
        // door. See `Ingress/IngressEndpoints.cs` for what the kernel does and does not do here.
        Burbuja.Host.Ingress.IngressEndpoints.Map(app);

        // Track 3a: /health/plugins answers one JSON document (status, host identity, one entry per
        // loaded plugin) instead of the old always-Healthy aggregate text. Unhealthy is the only
        // status that fails the probe (503) — Degraded still means "serving, with something to look
        // at". /health/live and /health/ready are deliberately untouched: compose's healthcheck and
        // the E2E readiness poll read their exact old shape.
        app.MapHealthChecks("/health/plugins", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("plugins"),
            ResponseWriter = WritePluginHealthAsync,
            ResultStatusCodes = new Dictionary<HealthStatus, int>
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        });

        if (controlPlane)
        {
            app.MapOpenApi("/openapi/v1.json");
            ArmEndpoints.Map(app);

            // ─── M5.5 portal: wiring ──────────────────────────────────────────────────────────
            // The portal replaces the three /portal/* 501 stubs M3 left behind. Static assets are
            // mapped first (the RCL publishes portal.css/portal.js under /portal), then routing and
            // authentication: the cookie ticket is the only way in, and every component endpoint
            // requires it — the sign-in page opts out with [AllowAnonymous]. Antiforgery must run
            // after routing (and after authentication, whose cookie it binds to).
            app.MapStaticAssets();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseAntiforgery();
            var portals = app.Services.GetRequiredService<PortalRegistry>();
            var razor = app.MapRazorComponents<Burbuja.Portal.Components.App>()
                .AddInteractiveServerRenderMode();
            var additionalAssemblies = options.AdditionalRazorAssemblies
                .Concat(portals.AdditionalAssemblies)
                .Concat(app.Services.GetServices<System.Reflection.Assembly>())
                .Where(assembly => assembly != typeof(Burbuja.Portal.Components.App).Assembly)
                .Distinct()
                .ToArray();
            if (additionalAssemblies.Length > 0)
                razor.AddAdditionalAssemblies(additionalAssemblies);
            // Razor component endpoints answer POST too (enhanced form handling), which would collide
            // with POST /portal/signin below. The portal has no [SupplyParameterFromForm] page, so its
            // component endpoints are GET-only and the auth POSTs own their paths.
            //
            // M7.0 (Track 3b): this used to be `.WithMetadata(new HttpMethodMetadata(["GET"]))` on the
            // builder — one convention over EVERY endpoint it owns, including the `/_blazor` hub that
            // AddInteractiveServerRenderMode registers. The hub negotiates with a POST, so GET-only
            // metadata answered 405 and the circuit could never start even once blazor.web.js was
            // served. The obvious discriminator, ComponentTypeMetadata, is internal to the framework,
            // so this keys off the hub's route instead — the one endpoint under this builder that must
            // keep its POST. It also keeps a plain `AuthorizeAttribute()`: either portal may open a
            // circuit, because the circuit's OWN page is what the route policy decides, and a hub that
            // required one portal's claim would refuse the other portal's console before it rendered.
            //
            // The portal's route scope decides its policy. Unknown component routes use the kernel
            // policy; sign-in pages opt out with [AllowAnonymous] in their own metadata.
            razor.Add(builder =>
            {
                var route = (builder as RouteEndpointBuilder)?.RoutePattern.RawText;
                if (route is not null && route.StartsWith("/_blazor", StringComparison.Ordinal))
                {
                    builder.Metadata.Add(new AuthorizeAttribute());
                    return;
                }

                builder.Metadata.Add(new HttpMethodMetadata(["GET"]));
                builder.Metadata.Add(new AuthorizeAttribute(portals.Of(route).Policy));
            });
            Burbuja.Portal.Endpoints.PortalEndpoints.Map(app);
            // ─── end M5.5 portal wiring ───────────────────────────────────────────────────────

            if (landingRoute is not null)
                app.MapGet("/", () => Results.Redirect(landingRoute));

            if (app.Services.GetRequiredService<IOptions<Burbuja.Mcp.McpServerOptions>>().Value.Enabled)
                McpEndpoints.Map(app);
        }

        return app;
    }

    /// <summary>
    /// The /health/plugins body. The health service has already run every `plugin:&lt;id&gt;` check by
    /// the time this writer runs (and set the status code), so the registry snapshot carries both the
    /// supervisor state and the folded contributor verdict.
    /// </summary>
    private static async Task WritePluginHealthAsync(HttpContext context, HealthReport report)
    {
        var registry = context.RequestServices.GetRequiredService<PluginHealthRegistry>();
        var host = context.RequestServices.GetRequiredService<KernelHostInfo>();
        var document = PluginHealthDocument.Build(report.Status, registry.Snapshot(), host);
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(document.ToJsonString(), context.RequestAborted);
    }

    internal static IReadOnlyList<IBurbujaPlugin> StaticPlugins(
        IConfiguration configuration,
        IEnumerable<StaticPluginRegistration>? additionalPlugins = null)
    {
        // A plugin that also exists in Kernel:PluginsDirectory must not be registered statically:
        // discovery would then see two manifests with the same id and the boot fails (duplicate
        // plugin id). Before M4 only Policy.Core was guarded, so staging the other built-ins
        // crashed in TopoSort with a raw "An item with the same key has already been added".
        var dir = configuration["Kernel:PluginsDirectory"];
        var list = new List<IBurbujaPlugin>();
        Add(list, dir, "Burbuja.Agents", "Burbuja.Agents.dll", () => new AgentsPlugin());
        Add(list, dir, "Burbuja.Deployments", "Burbuja.Deployments.dll", () => new DeploymentsPlugin());
        Add(list, dir, "Burbuja.Compute", "Burbuja.Compute.dll", () => new ComputePlugin());
        Add(list, dir, "Llm.Scripted", "Burbuja.Plugins.Llm.Scripted.dll", () => new ScriptedLlmPlugin());
        Add(list, dir, "Supervisor", "Burbuja.Plugins.Supervisor.dll", () => new SupervisorPlugin());
        Add(list, dir, "Tools.Workspace", "Burbuja.Plugins.Tools.Workspace.dll", () => new WorkspaceToolsPlugin());
        Add(list, dir, "Tools.Http", "Burbuja.Plugins.Tools.Http.dll", () => new HttpToolsPlugin());
        Add(list, dir, "Tools.Agents", "Burbuja.Plugins.Tools.Agents.dll", () => new AgentToolsPlugin());
        Add(list, dir, "Policy.Core", "Burbuja.Plugins.Policy.Core.dll", () => new PolicyCorePlugin());
        foreach (var plugin in additionalPlugins ?? [])
            Add(list, dir, plugin.Id, plugin.AssemblyFileName, plugin.Create);
        return list;
    }

    private static void Add(
        List<IBurbujaPlugin> list,
        string? pluginsDirectory,
        string id,
        string assembly,
        Func<IBurbujaPlugin> create)
    {
        if (list.Any(plugin => string.Equals(plugin.Manifest.Id, id, StringComparison.OrdinalIgnoreCase)))
            return;
        if (!PluginOnDisk(pluginsDirectory, id, assembly))
        {
            var plugin = create();
            if (!string.Equals(plugin.Manifest.Id, id, StringComparison.Ordinal)
                || !string.Equals(plugin.Manifest.Assembly, assembly, StringComparison.Ordinal))
                throw new PluginBootException([$"static plugin {id}: factory manifest must name id '{id}' and assembly '{assembly}'"]);
            list.Add(plugin);
        }
    }

    private static bool PluginOnDisk(string? pluginsDirectory, string id, string assembly)
    {
        if (string.IsNullOrWhiteSpace(pluginsDirectory) || !Directory.Exists(pluginsDirectory))
            return false;
        foreach (var dir in Directory.GetDirectories(pluginsDirectory))
        {
            var json = Path.Combine(dir, "plugin.json");
            if (!File.Exists(json) || !File.Exists(Path.Combine(dir, assembly)))
                continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("id", out var idElement)
                    && idElement.ValueKind == System.Text.Json.JsonValueKind.String
                    && string.Equals(idElement.GetString(), id, StringComparison.Ordinal))
                    return true;
            }
            catch (System.Text.Json.JsonException)
            {
                // A malformed plugin.json is reported by discovery; keep the static fallback.
            }
        }

        return false;
    }
}
