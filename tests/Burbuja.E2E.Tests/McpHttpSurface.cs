using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;

namespace Burbuja.E2E.Tests;

internal sealed class McpHttpSurface : IAsyncDisposable
{
    private readonly WebApplication _app;
    public McpHttpSurface(string? credential = null, bool paginated = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithListToolsHandler((request, _) => ValueTask.FromResult(new ListToolsResult
            {
                NextCursor = paginated && request.Params?.Cursor is null ? "next" : null,
                Tools = [new Tool
                {
                    Name = "echo", Description = "Echo a test input",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                }],
            }))
            .WithCallToolHandler((request, _) => ValueTask.FromResult(new CallToolResult
            {
                Content = [new TextContentBlock { Text = credential ?? JsonSerializer.Serialize(request.Params!.Arguments) }],
                StructuredContent = JsonSerializer.SerializeToElement(new { transport = "http", credential }),
            }));
        _app = builder.Build();
        if (credential is not null)
            _app.Use(async (context, next) =>
            {
                if (context.Request.Headers.Authorization != "Bearer " + credential)
                {
                    context.Response.StatusCode = 401;
                    return;
                }
                await next();
            });
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
