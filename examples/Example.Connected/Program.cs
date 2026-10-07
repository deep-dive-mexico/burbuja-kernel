using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Applications;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Sdk.Applications;

namespace Example.Connected;

/// <summary>Independent HTTP application: SDK only, with no local runtime or database.</summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var config = builder.Configuration;
        var scope = new ApplicationScope(config["Application:Environment"] ?? "development",
            config["Application:Tenant"] ?? "example", config["Application:Name"] ?? throw new InvalidOperationException("Application name required."));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var client = new ConnectedKernelClient(http, new Uri(config["Kernel:Url"] ?? throw new InvalidOperationException("Kernel URL required.")),
            config["Kernel:Credential"] ?? throw new InvalidOperationException("Application credential required."), scope,
            actor: "workbench-operator", allowInsecureDevelopment: scope.Environment == "development");
        var handshake = await client.ConnectAsync();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (KernelUnavailableException error)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { code = "KernelUnavailable", error.RequestId }, context.RequestAborted);
            }
        });
        app.MapGet("/binding", () => new { handshake.Scope, handshake.Protocol, handshake.KernelVersion,
            processId = Environment.ProcessId, dependencies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => n?.StartsWith("Burbuja.") == true) });
        app.MapPut("/notes/{name}", async (string name, JsonObject desired, CancellationToken ct) =>
        {
            var id = "/subscriptions/local/resourceGroups/dev/providers/Example.Connected/notes/" + name;
            var envelope = new ResourceEnvelope<JsonObject>(id, name, "Example.Connected/notes", null,
                new Dictionary<string, string>(), desired, ProvisioningState.Succeeded,
                new SystemData(null, "workbench-operator", "User", null, null, null), "");
            return await client.UpsertAsync(envelope, null, ct);
        });
        app.MapGet("/notes/{name}", async (string name, CancellationToken ct) =>
        {
            var id = "/subscriptions/local/resourceGroups/dev/providers/Example.Connected/notes/" + name;
            var resource = await ((IResourceStore)client).GetAsync(id.ToLowerInvariant(), ct);
            return resource is null ? Results.NotFound() : Results.Json(resource);
        });
        await app.RunAsync();
    }
}
