using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Options;
using Burbuja.Mcp.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Burbuja.Mcp;

/// <summary>
/// Mounts the kernel's MCP server on the control plane.
///
/// The endpoint is Streamable HTTP at <c>/mcp</c>, behind the same API keys the ARM plane uses —
/// but a *different* key is expected here, one holding only <see cref="McpRoles"/> (read an agent's
/// context, call its tools). The agent is named with <see cref="McpConstants.AgentHeader"/> and the
/// server authorises the caller against that agent's scope before anything runs; the header is a
/// claim to be checked, never a fact to be trusted.
/// </summary>
public static class McpEndpoints
{
    public static void Map(WebApplication app)
    {
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments(McpConstants.Path),
            branch => branch.Use(AuthenticateAsync));

        var mcp = app.MapMcp(McpConstants.Path);
        _ = mcp;
    }

    private static async Task AuthenticateAsync(HttpContext http, RequestDelegate next)
    {
        var options = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value;
        var server = http.RequestServices.GetRequiredService<IOptions<McpServerOptions>>().Value;

        string? key = null;
        if (http.Request.Headers.TryGetValue("x-api-key", out var header))
            key = header.ToString();
        else if (http.Request.Headers.TryGetValue("Authorization", out var authz)
                 && authz.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            key = authz.ToString()["Bearer ".Length..].Trim();

        ApiKeyEntry? entry = null;
        foreach (var candidate in options.ApiKeys)
        {
            if (FixedTimeEquals(candidate.Key, key) && entry is null)
                entry = candidate;
        }

        if (entry is null || string.IsNullOrEmpty(key))
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            http.Response.Headers.WWWAuthenticate = "Bearer, ApiKey";
            await WriteErrorAsync(http, "AuthenticationFailed", "Authentication failed.", StatusCodes.Status401Unauthorized);
            return;
        }

        var agent = http.Request.Headers.TryGetValue(McpConstants.AgentHeader, out var named)
            ? named.ToString().Trim()
            : null;
        var claims = new List<Claim>
        {
            new("principalId", entry.PrincipalId),
            new("principalType", entry.PrincipalType),
            new("roles", string.Join(',', entry.Roles)),
        };
        if (!string.IsNullOrEmpty(agent))
            claims.Add(new Claim(McpConstants.AgentHeader, agent));
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));

        await next(http);
    }

    internal static McpCaller ResolveCaller(HttpContext http, McpServerOptions options, string? sessionId = null)
    {
        var principal = http.User.FindFirst("principalId")?.Value ?? "";
        var type = http.User.FindFirst("principalType")?.Value ?? "User";
        var roles = (http.User.FindFirst("roles")?.Value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        var arm = new ArmPrincipal(
            principal,
            type,
            null,
            [],
            new Dictionary<string, string> { ["roles"] = string.Join(',', roles) });

        var named = http.User.FindFirst(McpConstants.AgentHeader)?.Value;
        var agentName = string.IsNullOrWhiteSpace(named) ? null : Bare(named);
        var scope = agentName is null
            ? $"/subscriptions/{options.SubscriptionId}/resourceGroups/{options.ResourceGroup}/providers/{AgentsConstants.Namespace}/agents"
            : KernelMcpDispatcher.ScopeFor(options, agentName);
        var key = agentName is null ? null : scope.ToLowerInvariant();
        return new McpCaller(arm, agentName, scope, key, sessionId);
    }

    private static string Bare(string value)
    {
        var trimmed = value.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static bool FixedTimeEquals(string configured, string? provided)
    {
        if (provided is null)
            return false;
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static async Task WriteErrorAsync(HttpContext http, string code, string message, int status)
    {
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(JsonSerializer.Serialize(new { error = new { code, message } }));
        await http.Response.CompleteAsync();
        _ = status;
    }
}

/// <summary>Maps kernel dispatcher results onto MCP payloads. The only place protocol types appear.</summary>
public static class McpProtocol
{
    public static Tool ToTool(McpToolExport export) => new()
    {
        Name = export.McpName,
        Description = export.Description,
        InputSchema = JsonDocument.Parse(export.InputSchema.ToJsonString()).RootElement.Clone(),
    };

    public static CallToolResult ToResult(McpToolCallOutcome outcome)
    {
        var result = new CallToolResult
        {
            IsError = outcome.IsError,
            Content = [new TextContentBlock { Text = outcome.Text }],
        };
        if (outcome.ApprovalPending && outcome.ApprovalId is { Length: > 0 } id)
        {
            result.StructuredContent = JsonSerializer.SerializeToElement(new
            {
                code = McpConstants.ApprovalPendingCode,
                approvalId = id,
            });
        }

        return result;
    }

    public static Resource ToResource(McpResourceDescriptor descriptor) => new()
    {
        Uri = descriptor.Uri,
        Name = descriptor.Name,
        Description = descriptor.Description,
        MimeType = descriptor.MimeType,
    };

    public static ReadResourceResult ToResourceResult(McpResourceDescriptor descriptor, string text) => new()
    {
        Contents =
        [
            new TextResourceContents
            {
                Uri = descriptor.Uri,
                MimeType = descriptor.MimeType,
                Text = text,
            },
        ],
    };
}

/// <summary>Registration for the MCP server. Called from the Host before the container is built.</summary>
public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddBurbujaMcp(this IServiceCollection services)
    {
        services.AddOptions<McpServerOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(McpServerOptions.Section).Bind(o));
        services.AddHttpContextAccessor();
        // K1: the kernel's own view of live sessions, beside the SDK's transport sessions.
        services.AddSingleton<McpSessionRegistry>();
        services.AddSingleton<IRoleContributor, McpRoles>();
        services.AddSingleton<McpRoles>(); // one instance, so the role contributor is a singleton either way
        services.AddSingleton<KernelToolExporter>();
        services.AddSingleton<IMcpToolExporter>(sp => sp.GetRequiredService<KernelToolExporter>());
        services.AddSingleton<KernelMcpDispatcher>();

        // The transport options are configured at registration and take no service provider, so the two
        // session settings are read once here from the configuration instance the host already registered —
        // NOT by building a throwaway container, which would be a second container with its own singletons.
        // Reading them once is also the semantics wanted: a session's idle timeout that could change between
        // two requests is a lifetime nobody can reason about.
        var sessionOptions = new McpServerOptions();
        (services.LastOrDefault(d => d.ServiceType == typeof(IConfiguration))?.ImplementationInstance as IConfiguration)
            ?.GetSection(McpServerOptions.Section)
            .Bind(sessionOptions);
        services.AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = McpConstants.ServerName, Version = "1.0.0" };
                o.ServerInstructions =
                    "The Burbuja kernel's own tools. Every call is decided by the kernel's policy gate: "
                    + "a hard-denied call is final, and a call that needs a human returns "
                    + $"code '{McpConstants.ApprovalPendingCode}' with an approvalId the caller retries with.";
                // K1: a client that can be told its catalogue changed has to be told the server may say so.
                // Claiming the capability and never sending the notification would be the other kind of
                // defect, which is why the sender below exists and a fact drives it.
                o.Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = true },
                };
            })
            // K1: sessions. `StatefulForInitializeClients` is the honest mode for this lane: a client using
            // the initialize handshake (every client this host has) gets a real session with
            // `Mcp-Session-Id` and the GET/DELETE endpoints, while a client on the 2026-07-28 revision —
            // which removed sessions — is still served per request instead of being refused. `Stateful`
            // alone would force those clients to downgrade by answering them -32022.
            // MCP9006 is the SDK telling NEW code to stay stateless: the 2026-07-28 revision removed
            // sessions (SEP-2567), and `Stateless` is its default. This lane is the other case the same SDK
            // documents — `StatefulForInitializeClients` exists so an application can adopt the revision
            // progressively — because every client this host has (Claude Code, pomnibot's engine, the E2E
            // lane) speaks the initialize handshake, and a client that cannot be given a session cannot be
            // told its catalogue changed. A 2026-07-28 client is served per request by the same endpoint and
            // never sees any of this. Suppressed deliberately, once, here.
#pragma warning disable MCP9006
            .WithHttpTransport(o =>
            {
                o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
                o.IdleTimeout = sessionOptions.SessionIdleTimeout;
                o.MaxIdleSessionCount = sessionOptions.MaxIdleSessions;
                // NOT setting RunSessionHandler, and the difference is measured rather than assumed. The SDK
                // documents it as "for running new MCP sessions manually" and it is evaluation-only
                // (MCPEXP002), so I added it when the notification seemed to go nowhere — and then removed it
                // again to check, at which point the fact still passed: a server-to-client notification is
                // delivered on the SSE stream of the request that triggered it, so the session loop is not
                // what carries it. With this line the session's own GET stream would also pump; without it
                // that stream stays silent while everything the kernel sends still arrives. One experimental
                // API fewer, and the delivery path is the one the fact actually observes.
            })
#pragma warning restore MCP9006
            .WithListToolsHandler(ListToolsAsync)
            .WithCallToolHandler(CallToolAsync)
            .WithListResourcesHandler(ListResourcesAsync)
            .WithReadResourceHandler(ReadResourceAsync)
            .WithListPromptsHandler(ListPromptsAsync)
            .WithGetPromptHandler(GetPromptAsync);

        return services;
    }

    private static McpServerOptions OptionsOf<T>(RequestContext<T> ctx) =>
        ctx.Services!.GetRequiredService<IOptions<McpServerOptions>>().Value;

    private static HttpContext HttpOf<T>(RequestContext<T> ctx) =>
        ctx.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext
        ?? throw new InvalidOperationException("the MCP endpoint requires an HTTP request.");

    private static KernelMcpDispatcher DispatcherOf<T>(RequestContext<T> ctx) =>
        ctx.Services!.GetRequiredService<KernelMcpDispatcher>();

    private static ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> ctx, CancellationToken ct) =>
        ListToolsCoreAsync(ctx, ct);

    private static async ValueTask<ListToolsResult> ListToolsCoreAsync(RequestContext<ListToolsRequestParams> ctx, CancellationToken ct)
    {
        var caller = McpEndpoints.ResolveCaller(HttpOf(ctx), OptionsOf(ctx), ctx.Server.SessionId);
        var tools = await DispatcherOf(ctx).ListToolsAsync(caller, ct);
        await AnnounceCatalogueAsync(ctx, caller, tools, ct);
        return new ListToolsResult { Tools = tools.Select(McpProtocol.ToTool).ToList() };
    }

    /// <summary>
    /// K1: record the session and tell it when its catalogue moved.
    ///
    /// <para><b>This is where <c>notifications/tools/list_changed</c> finally has a producer.</b> A session's
    /// catalogue is not fixed: it is filtered by the agent's tool filter and gated by the caller's
    /// authorisation on the agent's scope, so a PUT that narrows the filter or an assignment that changes what
    /// the caller may read silently changes what the client sees. Without a notification the client keeps
    /// calling a list it was shown once — which is how an agent ends up attempting a tool it can no longer
    /// use, or ignoring one it just gained.</para>
    ///
    /// <para><b>Compared on listing, not polled.</b> A background watcher per session would poll an agent
    /// store for every connected client forever; comparing the fingerprint when the client asks costs one
    /// hash on a request that is already reading that catalogue, and it announces the change on the
    /// connection that is provably alive at that moment. The cost is that a client that never lists again is
    /// never told — which is not a client that needs telling.</para>
    /// </summary>
    private static async Task AnnounceCatalogueAsync(
        RequestContext<ListToolsRequestParams> ctx,
        McpCaller caller,
        IReadOnlyList<McpToolExport> tools,
        CancellationToken ct)
    {
        if (caller.SessionId is not { Length: > 0 } sessionId)
            return; // A stateless client (a 2026-07-28 revision, or no session): nothing to remember.

        await TouchAsync(ctx, caller, ct);
        var registry = ctx.Services!.GetRequiredService<McpSessionRegistry>();
        var change = registry.RememberCatalogue(sessionId, McpCatalogue.Fingerprint(tools));
        if (change is not CatalogueChange.Changed || !OptionsOf(ctx).AnnounceCatalogueChanges)
            return;

        await ctx.Server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, ct);
        await DispatcherOf(ctx).SessionCatalogueChangedAsync(caller, tools.Count, ct);
    }

    /// <summary>
    /// Record the connection this request arrived on, and say so once.
    ///
    /// <para>Any request touches the record — a client that only ever calls tools is still connected, and the
    /// kernel's answer to "who is connected" must not depend on which method a client happens to use. The
    /// opened event is written by whichever request is first, which is also the honest description: the
    /// connection began working with this agent when it first asked for something.</para>
    /// </summary>
    private static async Task TouchAsync<T>(RequestContext<T> ctx, McpCaller caller, CancellationToken ct)
    {
        if (caller.SessionId is not { Length: > 0 } sessionId)
            return;

        var registry = ctx.Services!.GetRequiredService<McpSessionRegistry>();
        var (record, opened) = registry.Seen(sessionId, caller, ctx.Server.NegotiatedProtocolVersion);
        if (opened)
            await DispatcherOf(ctx).SessionOpenedAsync(caller, record.OpenedAt, ct);
    }

    private static ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> ctx, CancellationToken ct) =>
        CallToolCoreAsync(ctx, ct);

    private static async ValueTask<CallToolResult> CallToolCoreAsync(RequestContext<CallToolRequestParams> ctx, CancellationToken ct)
    {
        var caller = McpEndpoints.ResolveCaller(HttpOf(ctx), OptionsOf(ctx), ctx.Server.SessionId);
        await TouchAsync(ctx, caller, ct);
        var arguments = ctx.Params.Arguments is { } args
            ? new JsonObject(args.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, JsonNode.Parse(JsonSerializer.Serialize(kv.Value)))))
            : new JsonObject();
        var outcome = await DispatcherOf(ctx).CallToolAsync(caller, ctx.Params.Name, arguments, ct);
        return McpProtocol.ToResult(outcome);
    }

    private static ValueTask<ListResourcesResult> ListResourcesAsync(RequestContext<ListResourcesRequestParams> ctx, CancellationToken ct) =>
        ListResourcesCoreAsync(ctx, ct);

    private static async ValueTask<ListResourcesResult> ListResourcesCoreAsync(RequestContext<ListResourcesRequestParams> ctx, CancellationToken ct)
    {
        var caller = McpEndpoints.ResolveCaller(HttpOf(ctx), OptionsOf(ctx), ctx.Server.SessionId);
        var resources = await DispatcherOf(ctx).ListResourcesAsync(caller, ct);
        return new ListResourcesResult { Resources = resources.Select(McpProtocol.ToResource).ToList() };
    }

    private static ValueTask<ReadResourceResult> ReadResourceAsync(RequestContext<ReadResourceRequestParams> ctx, CancellationToken ct) =>
        ReadResourceCoreAsync(ctx, ct);

    private static async ValueTask<ReadResourceResult> ReadResourceCoreAsync(RequestContext<ReadResourceRequestParams> ctx, CancellationToken ct)
    {
        var caller = McpEndpoints.ResolveCaller(HttpOf(ctx), OptionsOf(ctx), ctx.Server.SessionId);
        var uri = ctx.Params.Uri;
        var text = await DispatcherOf(ctx).ReadResourceAsync(caller, uri, ct)
                   ?? throw new McpException($"no such resource or not authorized: {uri}");
        var descriptor = new McpResourceDescriptor(uri, uri, uri, "application/json");
        return McpProtocol.ToResourceResult(descriptor, text);
    }

    private static ValueTask<ListPromptsResult> ListPromptsAsync(RequestContext<ListPromptsRequestParams> ctx, CancellationToken ct)
    {
        _ = ctx;
        _ = ct;
        return new ValueTask<ListPromptsResult>(new ListPromptsResult
        {
            Prompts =
            [
                new Prompt
                {
                    Name = "persona",
                    Description = "The persona this agent was created with.",
                },
            ],
        });
    }

    private static ValueTask<GetPromptResult> GetPromptAsync(RequestContext<GetPromptRequestParams> ctx, CancellationToken ct) =>
        GetPromptCoreAsync(ctx, ct);

    private static async ValueTask<GetPromptResult> GetPromptCoreAsync(RequestContext<GetPromptRequestParams> ctx, CancellationToken ct)
    {
        var caller = McpEndpoints.ResolveCaller(HttpOf(ctx), OptionsOf(ctx), ctx.Server.SessionId);
        var persona = await DispatcherOf(ctx).GetPersonaAsync(caller, ct)
                      ?? throw new McpException("this caller has no agent persona.");
        return new GetPromptResult
        {
            Description = "The persona this agent was created with.",
            Messages =
            [
                new PromptMessage
                {
                    Role = Role.User,
                    Content = new TextContentBlock { Text = persona },
                },
            ],
        };
    }
}
