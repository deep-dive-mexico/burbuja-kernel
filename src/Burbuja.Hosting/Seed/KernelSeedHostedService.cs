using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Burbuja.Core.Abstractions.Host;
using Burbuja.Core.Options;
using Microsoft.Extensions.Options;

namespace Burbuja.Host.Seed;

public sealed class KernelSeedHostedService(
    IServiceScopeFactory scopes,
    IOptions<KernelOptions> kernel,
    IOptions<AuthOptions> auth,
    ILogger<KernelSeedHostedService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var role = kernel.Value.Role;
        if (!kernel.Value.Seed)
            return;
        if (!role.Equals("control-plane", StringComparison.OrdinalIgnoreCase)
            && !role.Equals("all", StringComparison.OrdinalIgnoreCase))
            return;

        using var scope = scopes.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<ArmPipeline>();
        var key = auth.Value.ApiKeys.Count > 0 ? auth.Value.ApiKeys[0].Key : "local-dev-api-key";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-api-key"] = key,
        };

        await Put(pipeline, scope.ServiceProvider, "/subscriptions/local", new JsonObject { ["displayName"] = "local", ["state"] = "Enabled" }, headers, cancellationToken);
        await Put(pipeline, scope.ServiceProvider, "/subscriptions/local/resourceGroups/dev", new JsonObject { ["location"] = "local" }, headers, cancellationToken);
        await Put(
            pipeline,
            scope.ServiceProvider,
            "/subscriptions/local/resourceGroups/dev/providers/Burbuja.Agents/workspaces/demo",
            Envelope("Burbuja.Agents/workspaces", "demo", new JsonObject { ["displayName"] = "demo" }),
            headers,
            cancellationToken);
        await Put(
            pipeline,
            scope.ServiceProvider,
            "/subscriptions/local/resourceGroups/dev/providers/Burbuja.Deployments/environments/demo",
            Envelope("Burbuja.Deployments/environments", "demo", new JsonObject { ["displayName"] = "demo" }),
            headers,
            cancellationToken);
        log.LogInformation("Seeded subscription/local, RG/dev, workspace/demo, environment/demo");

        // M14 R10 (request row X2) — and then whoever asked to add to it. Same switch, same scope, after the
        // kernel's own rows: a contributor that needs the scope its rows live in finds it there, and a host
        // that was not asked to seed runs none of this.
        var contributors = scope.ServiceProvider.GetServices<IKernelSeedContributor>().ToList();
        foreach (var contributor in contributors)
        {
            try
            {
                await contributor.SeedAsync(cancellationToken);
                log.LogInformation("Seed contributor {Name} finished", contributor.Name);
            }
            catch (Exception ex)
            {
                // Loud and fatal, with the name in the message: the operator asked for a seed and half of one
                // is a host whose pages lie about what exists. `ausente ≠ vacío`, at boot.
                throw new InvalidOperationException($"kernel seed contributor '{contributor.Name}' failed", ex);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static JsonObject Envelope(string type, string name, JsonObject properties) => new()
    {
        ["name"] = name,
        ["type"] = type,
        ["properties"] = properties,
    };

    private static async Task Put(
        ArmPipeline pipeline,
        IServiceProvider services,
        string path,
        JsonNode body,
        Dictionary<string, string> headers,
        CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        var ctx = new ArmRequestContext
        {
            Method = "PUT",
            Path = path,
            Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["api-version"] = ArmConstants.ApiVersion },
            Headers = headers,
            Body = new MemoryStream(bytes),
            Services = services,
        };
        await pipeline.RunAsync(ctx, ct);
        if (ctx.Error is not null || ctx.StatusCode is < 200 or >= 300)
            throw new InvalidOperationException($"seed PUT {path} returned {ctx.StatusCode} {ctx.Error?.Code}");
    }
}
