using System.Collections.Concurrent;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents;
using Burbuja.Compute;
using Burbuja.Core.Abstractions.Applications;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Applications;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Burbuja.Core.Options;
using Burbuja.Core.Sdk.Authorization;
using Burbuja.Core.Sdk.Applications;
using Burbuja.Plugins.Llm.Scripted;
using Burbuja.Plugins.Policy.Core;
using Burbuja.Plugins.Supervisor;
using Burbuja.Plugins.Tools.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Burbuja.Hosting.Applications;

/// <summary>Kernel-owned workers capture a binding explicitly for their entire lifecycle.</summary>
public sealed class ScopedApplicationRuntime(ConnectedKernelOptions options) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<ApplicationScope, Lazy<Task<Node>>> _nodes = [];

    public async ValueTask<ArmClientResponse> SendAsync(ApplicationScope scope, ApplicationStores stores,
        ApplicationRuntimeCall call, CancellationToken ct)
    {
        var authority = Validate(scope, call.Principal);
        if (authority.Error is { } error) return new(error.HttpStatus, new Dictionary<string, string>(), null, error);
        var node = await GetNodeAsync(scope, stores, authority.Registration, ct);
        return await node.SendAsync(call with { Principal = Delegated(call.Principal) }, ct);
    }

    public async Task StreamAsync(ApplicationScope scope, ApplicationStores stores, ApplicationRuntimeCall call, HttpContext http)
    {
        var authority = Validate(scope, call.Principal);
        if (authority.Error is { } error)
        {
            http.Response.StatusCode = error.HttpStatus;
            await http.Response.WriteAsJsonAsync(new { error }, JsonWire.Options, http.RequestAborted);
            return;
        }
        if (call.Request.Method != "GET") { http.Response.StatusCode = 405; return; }
        var node = await GetNodeAsync(scope, stores, authority.Registration, http.RequestAborted);
        await node.StreamAsync(call with { Principal = Delegated(call.Principal) }, http);
    }

    private Task<Node> GetNodeAsync(ApplicationScope scope, ApplicationStores stores, ApplicationRegistration registration, CancellationToken ct) =>
        _nodes.GetOrAdd(scope, _ => new Lazy<Task<Node>>(() => Node.Start(scope, stores, registration, Path.Combine(Path.GetFullPath(options.StorageRoot),
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(scope)))), "compute")),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value.WaitAsync(ct);

    private (ApplicationRegistration Registration, ArmError? Error) Validate(ApplicationScope scope, ArmPrincipal principal)
    {
        var registration = options.Applications.Single(a => a.Tenant == scope.Tenant && a.Application == scope.Application);
        if (!registration.RuntimeEnabled || !registration.Writable)
            return (registration, ArmError.Forbidden("Runtime capability is not enabled for this application."));
        var roles = principal.Claims.GetValueOrDefault("roles", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (string.IsNullOrWhiteSpace(principal.PrincipalId) || roles.Length == 0
            || roles.Any(role => !registration.DelegatedRoles.Contains(role, StringComparer.Ordinal))
            || (principal.TenantId is { } tenant && tenant != scope.Tenant))
            return (registration, ArmError.Forbidden("Delegated identity is outside the registered application authority."));
        return (registration, null);
    }

    private static ArmPrincipal Delegated(ArmPrincipal principal) => principal with
    {
        Claims = new Dictionary<string, string>(principal.Claims) { ["kernel.delegated"] = "validated-application" }
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var node in _nodes.Values)
            if (node.IsValueCreated)
                await (await node.Value).DisposeAsync();
    }

    private sealed class Node : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly IReadOnlyList<IHostedService> _workers;
        private Node(ServiceProvider services, IReadOnlyList<IHostedService> workers) { _services = services; _workers = workers; }

        public static async Task<Node> Start(ApplicationScope scope, ApplicationStores stores, ApplicationRegistration registration, string computeRoot)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Kernel:Role"] = "all", ["Kernel:Seed"] = "false", ["Kernel:PluginsDirectory"] = "",
                ["Kernel:NodeId"] = "application-" + scope.Tenant + "-" + scope.Application,
                ["Compute:LocalDirectory:Root"] = computeRoot,
                ["Agents:ProviderId"] = "scripted", ["Budget:DailyHardStopUsd"] = registration.DailyHardStopUsd.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Auth:ApiKeys:0:Key"] = Guid.NewGuid().ToString("N"),
                ["Auth:ApiKeys:0:PrincipalId"] = "application-lifecycle", ["Auth:ApiKeys:0:PrincipalType"] = "Service",
                ["Auth:ApiKeys:0:Roles:0"] = "Owner",
                ["Kernel:TrustedPlugins:0"] = "Policy.Core", ["Kernel:TrustedPlugins:1"] = "Supervisor", ["Kernel:TrustedPlugins:2"] = "Burbuja.Compute",
            };
            settings["Agents:LifecycleKey"] = settings["Auth:ApiKeys:0:Key"];
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var services = new ServiceCollection();
            services.AddBurbujaCore(configuration,
                [new AgentsPlugin(), new ComputePlugin(), new ScriptedLlmPlugin(), new PolicyCorePlugin(), new SupervisorPlugin(), new AgentToolsPlugin()]);
            services.Replace(ServiceDescriptor.Singleton(stores.Resources));
            services.Replace(ServiceDescriptor.Singleton(stores.Events));
            services.Replace(ServiceDescriptor.Singleton(stores.Artifacts));
            services.Replace(ServiceDescriptor.Singleton(stores.Leases));
            services.Replace(ServiceDescriptor.Singleton(stores.Approvals));
            services.Replace(ServiceDescriptor.Singleton(stores.Operations));
            services.Replace(ServiceDescriptor.Singleton(stores.Idempotency));
            services.Replace(ServiceDescriptor.Singleton(stores.Outbox));
            services.Replace(ServiceDescriptor.Singleton<IOutbox>(new OutboxView(stores.Outbox)));
            services.Replace(ServiceDescriptor.Singleton(typeof(IDecisionCache<>), typeof(ScopedDecisions<>)));
            services.AddSingleton(stores);
            services.Replace(ServiceDescriptor.Singleton<IAuthorizationEvaluator>(sp =>
                new DelegatedAuthorization(sp.GetRequiredService<AuthorizationEvaluator>())));
            services.Replace(ServiceDescriptor.Singleton<IApprovalDecider>(sp => new ApprovalDecider(sp.GetRequiredService<IAuthorizationEvaluator>())));
            var authentication = services.Single(d => d.ServiceType == typeof(IArmMiddleware) && d.ImplementationType == typeof(AuthenticationMiddleware));
            services.Remove(authentication);
            services.AddSingleton<IArmMiddleware, DelegatedAuthentication>();
            var provider = services.BuildServiceProvider();
            var workers = provider.GetServices<IHostedService>().ToArray();
            try
            {
                foreach (var worker in workers) await worker.StartAsync(CancellationToken.None);
                return new Node(provider, workers);
            }
            catch { await provider.DisposeAsync(); throw; }
        }

        public async ValueTask<ArmClientResponse> SendAsync(ApplicationRuntimeCall call, CancellationToken ct)
        {
            using var scope = _services.CreateScope();
            var body = call.Request.Body?.ToJsonString(JsonWire.Options);
            await using var stream = body is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(body));
            var context = new ArmRequestContext
            {
                Method = call.Request.Method, Path = call.Request.Path,
                Query = call.Request.Query ?? new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion },
                Headers = call.Request.Headers ?? new Dictionary<string, string>(), Body = stream,
                Services = scope.ServiceProvider, Principal = call.Principal,
            };
            await scope.ServiceProvider.GetRequiredService<ArmPipeline>().RunAsync(context, ct);
            return new(context.StatusCode, context.ResponseHeaders,
                context.Result is null ? null : JsonSerializer.SerializeToNode(context.Result, JsonWire.Options), context.Error);
        }

        public async Task StreamAsync(ApplicationRuntimeCall call, HttpContext http)
        {
            using var scope = _services.CreateScope();
            var dispatch = new DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider, Response = { Body = http.Response.Body }, RequestAborted = http.RequestAborted,
            };
            dispatch.Request.Method = "GET";
            dispatch.Request.Path = call.Request.Path;
            dispatch.Request.QueryString = QueryString.Create((call.Request.Query ?? new Dictionary<string, string> { ["api-version"] = ArmConstants.ApiVersion }).Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));
            if (call.Request.Headers is not null)
                foreach (var (key, value) in call.Request.Headers) dispatch.Request.Headers[key] = value;
            dispatch.Request.Headers.Accept = "text/event-stream";
            var result = await Burbuja.Host.Arm.ArmEndpoints.HandleDelegatedAsync(dispatch, call.Principal);
            // Execute against the real HTTP response so streaming headers and cancellation reach the client.
            foreach (var (key, value) in dispatch.Response.Headers) http.Response.Headers[key] = value;
            await result.ExecuteAsync(http);
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var worker in _workers.Reverse()) await worker.StopAsync(timeout.Token);
            await _services.DisposeAsync();
        }
    }

    private sealed class DelegatedAuthentication(IOptions<AuthOptions> auth) : IArmMiddleware
    {
        public int Order => 200;
        public ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct) =>
            context.Principal is not null ? next(context, ct) : new AuthenticationMiddleware(auth).InvokeAsync(context, next, ct);
    }
    private sealed class DelegatedAuthorization(AuthorizationEvaluator inner) : IAuthorizationEvaluator
    {
        public IReadOnlyList<string> ActionCatalogue => inner.ActionCatalogue;
        public ValueTask<AuthorizationResult> AuthorizeAsync(ArmPrincipal principal, string scope, string action, bool isDataAction, CancellationToken ct) =>
            AuthorizeAsync(principal, scope, action, isDataAction, AuthorizationContext.Empty, ct);
        public ValueTask<AuthorizationResult> AuthorizeAsync(ArmPrincipal principal, string scope, string action, bool isDataAction,
            AuthorizationContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (principal.Claims.GetValueOrDefault("kernel.delegated") != "validated-application")
                return inner.AuthorizeAsync(principal, scope, action, isDataAction, context, ct);
            // Request-local role assertions were validated against this registration.
            // Do not persist/cache grants: an Owner assertion must not survive a later Reader request.
            var allowed = principal.Claims.GetValueOrDefault("roles", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(name => inner.TryGetDefinition(name, out var role)
                    && role.AssignableScopes.Any(s => ArmActionMatcher.AssignmentCoversRequest(s, scope))
                    && (isDataAction ? role.DataActions : role.Actions).Any(pattern => ArmActionMatcher.ActionMatches(pattern, action))
                    && !(isDataAction ? role.NotDataActions : role.NotActions).Any(pattern => ArmActionMatcher.ActionMatches(pattern, action)));
            return ValueTask.FromResult(new AuthorizationResult(allowed, action, scope,
                allowed ? null : "The delegated application identity is not authorized."));
        }
    }
    private sealed class OutboxView(IOutboxStore inner) : IOutbox
    {
        public ValueTask EnqueueAsync(OutboxMessage message, CancellationToken ct) => inner.EnqueueAsync(message, ct);
    }
    private sealed class ScopedDecisions<T>(ApplicationStores stores) : IDecisionCache<T>
    {
        public async ValueTask<T?> GetAsync(string streamId, string decisionKey, CancellationToken ct) =>
            (await stores.Decisions.GetAsync(streamId, decisionKey, ct)) is { } value ? value.Deserialize<T>(JsonWire.Options) : default;
        public ValueTask SetAsync(string streamId, string decisionKey, T value, CancellationToken ct) =>
            stores.Decisions.SetAsync(streamId, decisionKey, JsonSerializer.SerializeToNode(value, JsonWire.Options)!, ct);
    }
}
