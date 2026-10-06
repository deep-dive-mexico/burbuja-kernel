using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;
using static Burbuja.E2E.Tests.KernelTestHost;

namespace Burbuja.E2E.Tests;

[Trait("Category", "E2E")]
public sealed class OutboundMcpE2ETests
{
    [Fact]
    public async Task A_paginated_stdio_catalogue_is_read_within_the_same_owned_connection()
    {
        await using var host = new KernelTestHost();
        await host.StartAsync();
        using var client = host.Client();
        var path = IntegrationRoot + "/pages";
        await DeclareAsync(client, path, "--tools", "one,two,three", "--page-size", "1");
        var probe = await ExpectAsync(await client.PostAsJsonAsync(path + "/probe" + Api, new { }, Ct), HttpStatusCode.OK);
        Assert.Equal(new[] { "mcp__fixture__one", "mcp__fixture__two", "mcp__fixture__three" },
            probe["tools"]!.AsArray().Select(tool => tool!.GetValue<string>()));
    }

    [Fact]
    public async Task Changing_a_declared_transport_invalidates_an_approval_for_the_original_server()
    {
        await using var host = new KernelTestHost();
        await host.StartAsync();
        using var client = host.Client();
        var path = IntegrationRoot + "/changed";
        await DeclareAsync(client, path);
        var pending = await ExpectAsync(await client.PostAsJsonAsync(path + "/call" + Api,
            new { tool = "mcp__fixture__echo", input = new { text = "original" } }, Ct), HttpStatusCode.Accepted);
        var id = pending["approvalId"]!.GetValue<string>();
        await ExpectAsync(await client.PostAsJsonAsync("/providers/Burbuja.Resources/approvals/" + id + "/approve" + Api,
            new { note = "authorize the original server" }, Ct), HttpStatusCode.OK);
        await ExpectAsync(await client.PutAsJsonAsync(path + Api, new
        {
            properties = new
            {
                kind = "mcp", serverName = "fixture", enabled = true, tools = new[] { "mcp__fixture__echo" },
                transport = new { mode = "stdio", command = "/not/the/approved/server" },
            },
        }, Ct), HttpStatusCode.OK);
        var refused = await ExpectAsync(await client.PostAsJsonAsync(path + "/call" + Api,
            new { tool = "mcp__fixture__echo", input = new { text = "original" }, approvalId = id }, Ct), HttpStatusCode.Accepted);
        Assert.NotEqual(id, refused["approvalId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("--garbage", "JSON")]
    [InlineData("--fail", "exited")]
    [InlineData("--hang", "deadline")]
    public async Task Broken_generic_servers_are_reported_without_hanging_the_host(string fault, string message)
    {
        await using var host = new KernelTestHost(new() { ["Integrations:Mcp:Timeout"] = "00:00:00.500" });
        await host.StartAsync();
        using var client = host.Client();
        var path = IntegrationRoot + "/broken";
        await DeclareAsync(client, path, fault);
        var failure = await ExpectAsync(await client.PostAsJsonAsync(path + "/probe" + Api, new { }, Ct), HttpStatusCode.BadRequest);
        Assert.Contains(message, failure["error"]!["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        using var healthy = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a__b")]
    [InlineData("../server")]
    public async Task Invalid_server_names_are_refused_before_launching_a_command(string serverName)
    {
        await using var host = new KernelTestHost();
        await host.StartAsync();
        using var client = host.Client();
        var path = IntegrationRoot + "/invalid";
        await ExpectAsync(await client.PutAsJsonAsync(path + Api, new
        {
            properties = new { kind = "mcp", serverName, enabled = true, transport = new { mode = "stdio", command = "/not/a/command" } },
        }, Ct), HttpStatusCode.Created);
        var failure = await ExpectAsync(await client.PostAsJsonAsync(path + "/probe" + Api, new { }, Ct), HttpStatusCode.BadRequest);
        Assert.Contains("serverName", failure["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Disabled_integrations_and_readers_cannot_execute_generic_tools()
    {
        await using var host = new KernelTestHost();
        await host.StartAsync();
        using var client = host.Client();
        var path = IntegrationRoot + "/disabled";
        await ExpectAsync(await client.PutAsJsonAsync(path + Api, new
        {
            properties = new
            {
                kind = "mcp", serverName = "fixture", enabled = false, tools = new[] { "mcp__fixture__echo" },
                transport = new { mode = "stdio", command = "/not/a/command" },
            },
        }, Ct), HttpStatusCode.Created);
        var refused = await ExpectAsync(await client.PostAsJsonAsync(path + "/call" + Api,
            new { tool = "mcp__fixture__echo", input = new { } }, Ct), HttpStatusCode.Forbidden);
        Assert.Contains("disabled", refused["error"]!["message"]!.GetValue<string>());
        using var reader = host.Client(Reader);
        await ExpectAsync(await reader.PostAsJsonAsync(path + "/call" + Api,
            new { tool = "mcp__fixture__echo", input = new { } }, Ct), HttpStatusCode.Forbidden);
    }

    private static async Task<JsonObject> DeclareAsync(HttpClient client, string path, params string[] arguments) =>
        await ExpectAsync(await client.PutAsJsonAsync(path + Api, new
        {
            properties = new
            {
                kind = "mcp", serverName = "fixture", enabled = true, tools = new[] { "mcp__fixture__echo" },
                transport = new { mode = "stdio", command = "dotnet",
                    args = new[] { Path.Combine(Repository, "tools/Burbuja.FakeMcp/bin/Release/net10.0/Burbuja.FakeMcp.dll") }.Concat(arguments).ToArray() },
            },
        }, Ct), HttpStatusCode.Created);

    [Fact]
    public async Task Generic_streamable_http_tools_are_discovered_and_called_with_structured_content()
    {
        await using var remote = new McpHttpSurface();
        await remote.StartAsync();
        await using var host = new KernelTestHost();
        await host.StartAsync();
        using var client = host.Client();
        var path = IntegrationRoot + "/http";
        await ExpectAsync(await client.PutAsJsonAsync(path + Api, new
        {
            properties = new
            {
                kind = "mcp", serverName = "http", enabled = true, tools = new[] { "mcp__http__echo" },
                transport = new { mode = "http", url = remote.Endpoint },
            },
        }, Ct), HttpStatusCode.Created);
        var probe = await ExpectAsync(await client.PostAsJsonAsync(path + "/probe" + Api, new { }, Ct), HttpStatusCode.OK);
        Assert.Equal("mcp__http__echo", Assert.Single(probe["tools"]!.AsArray())!.GetValue<string>());
        var pending = await ExpectAsync(await client.PostAsJsonAsync(path + "/call" + Api,
            new { tool = "mcp__http__echo", input = new { note = "through the public host" } }, Ct), HttpStatusCode.Accepted);
        var id = pending["approvalId"]!.GetValue<string>();
        await ExpectAsync(await client.PostAsJsonAsync("/providers/Burbuja.Resources/approvals/" + id + "/approve" + Api,
            new { note = "approve the HTTP MCP call" }, Ct), HttpStatusCode.OK);
        var result = await ExpectAsync(await client.PostAsJsonAsync(path + "/call" + Api,
            new { tool = "mcp__http__echo", input = new { note = "through the public host" }, approvalId = id }, Ct), HttpStatusCode.OK);
        Assert.True(result["ok"]!.GetValue<bool>(), result.ToJsonString());
        Assert.Equal("http", result["output"]!["structuredContent"]!["transport"]!.GetValue<string>());
        Assert.Contains("through the public host", result["output"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_declared_generic_tool_runs_after_approval_and_altered_arguments_need_their_own_decision()
    {
        await using var host = new KernelTestHost();
        await host.StartAsync();
        using var client = host.Client();
        var path = IntegrationRoot + "/approved";
        await ExpectAsync(await client.PutAsJsonAsync(path + Api, new
        {
            properties = new
            {
                kind = "mcp", serverName = "fixture", enabled = true, tools = new[] { "mcp__fixture__echo" },
                transport = new
                {
                    mode = "stdio", command = "dotnet",
                    args = new[] { Path.Combine(Repository, "tools/Burbuja.FakeMcp/bin/Release/net10.0/Burbuja.FakeMcp.dll"), "--tools", "echo" },
                },
            },
        }, Ct), HttpStatusCode.Created);
        var pending = await ExpectAsync(await client.PostAsJsonAsync(path + "/call" + Api,
            new { tool = "mcp__fixture__echo", input = new { text = "approved" } }, Ct), HttpStatusCode.Accepted);
        var id = pending["approvalId"]!.GetValue<string>();
        await ExpectAsync(await client.PostAsJsonAsync("/providers/Burbuja.Resources/approvals/" + id + "/approve" + Api,
            new { note = "approve the original input" }, Ct), HttpStatusCode.OK);
        var different = await ExpectAsync(await client.PostAsJsonAsync(path + "/call" + Api,
            new { tool = "mcp__fixture__echo", input = new { text = "changed" }, approvalId = id }, Ct), HttpStatusCode.Accepted);
        Assert.NotEqual(id, different["approvalId"]!.GetValue<string>());
        var called = await ExpectAsync(await client.PostAsJsonAsync(path + "/call" + Api,
            new { tool = "mcp__fixture__echo", input = new { text = "approved" }, approvalId = id }, Ct), HttpStatusCode.OK);
        Assert.True(called["ok"]!.GetValue<bool>(), called.ToJsonString());
        Assert.Equal("approved", JsonNode.Parse(called["output"]!["content"]![0]!["text"]!.GetValue<string>())!["text"]!.GetValue<string>());
        Assert.Equal(1, (await GetAsync(client, path))["properties"]!["calls"]!.GetValue<int>());
    }

    [Fact]
    public async Task Generic_stdio_probe_reports_server_qualified_tools_without_granting_them()
    {
        await using var host = new KernelTestHost();
        await host.StartAsync();
        using var client = host.Client();
        var path = IntegrationRoot + "/fixture";
        await ExpectAsync(await client.PutAsJsonAsync(path + Api, new
        {
            properties = new
            {
                kind = "mcp", serverName = "fixture", enabled = true, tools = Array.Empty<string>(),
                transport = new
                {
                    mode = "stdio", command = "dotnet",
                    args = new[] { Path.Combine(Repository, "tools/Burbuja.FakeMcp/bin/Release/net10.0/Burbuja.FakeMcp.dll"), "--tools", "echo,read_page" },
                },
            },
        }, Ct), HttpStatusCode.Created);
        var probe = await ExpectAsync(await client.PostAsJsonAsync(path + "/probe" + Api,
            new { }, Ct), HttpStatusCode.OK);
        Assert.Equal(new[] { "mcp__fixture__echo", "mcp__fixture__read_page" },
            probe["tools"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Empty(probe["declared"]!.AsArray());
        var refused = await client.PostAsJsonAsync(path + "/call" + Api,
            new { tool = "mcp__fixture__echo", input = new { text = "unapproved" } }, Ct);
        await ExpectAsync(refused, HttpStatusCode.Forbidden);
    }
}
