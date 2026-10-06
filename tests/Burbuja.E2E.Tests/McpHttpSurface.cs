using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Burbuja.E2E.Tests;

internal sealed class McpHttpSurface : IAsyncDisposable
{
    private readonly WebApplication _app;
    public McpHttpSurface()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [new Tool
                {
                    Name = "echo", Description = "Echo a test input",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                }],
            }))
            .WithCallToolHandler((request, _) => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(request.Params!.Arguments) }],
                StructuredContent = JsonSerializer.SerializeToElement(new { transport = "http" }),
            }));
        _app = builder.Build();
        _app.MapMcp("/mcp");
    }

    public string Endpoint { get; private set; } = "";
    public async Task StartAsync()
    {
        await _app.StartAsync(KernelTestHost.Ct);
        Endpoint = _app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single() + "/mcp";
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
