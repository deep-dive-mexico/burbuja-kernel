using Burbuja.Core.Abstractions.Arm;
using Burbuja.Portal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Burbuja.Portal;

/// <summary>Registers everything the portal needs inside the Host that mounts it.</summary>
public static class PortalServiceCollectionExtensions
{
    /// <summary>Register a product before portal boot. Registration does not create another cookie or circuit.</summary>
    public static IServiceCollection AddPortal(this IServiceCollection services, PortalScope definition)
    {
        services.AddSingleton(definition);
        services.TryAddSingleton<PortalRegistry>();
        return services;
    }

    /// <summary>
    /// Reads the <c>Portal</c> configuration section and falls back to the Host's own Kestrel URL
    /// for the base address, so the portal is an ARM client of the process it runs in.
    /// </summary>
    public static IServiceCollection AddBurbujaPortal(this IServiceCollection services, IConfiguration configuration)
    {
        var options = new PortalOptions();
        configuration.GetSection(PortalOptions.SectionName).Bind(options);
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            options.BaseUrl = Dialable(configuration["Kestrel:Endpoints:Http:Url"])
                ?? Dialable(configuration["urls"])
                ?? "http://127.0.0.1:18080";
        }

        return services.AddBurbujaPortal(options, configuration);
    }

    /// <summary>
    /// The Host's Kestrel URL is where the server <em>listens</em>, which is not always where a
    /// client may <em>connect</em>: the container is told <c>http://0.0.0.0:8080</c>, and dialling
    /// the wildcard from the portal's own HttpClient is how the Pi lane rendered an empty dashboard
    /// while every local run was green. A wildcard becomes the loopback it stands for.
    /// </summary>
    internal static string? Dialable(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        // `urls` may carry a semicolon-separated list; the first binding is the front door.
        var first = url.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first))
            return null;
        // The authority is the only `//` a base URL has, so replacing the wildcard host is safe.
        foreach (var wildcard in new[] { "//0.0.0.0", "//[::0]", "//[::]", "//+", "//*" })
            first = first.Replace(wildcard, "//127.0.0.1", StringComparison.Ordinal);

        return first;
    }

    public static IServiceCollection AddBurbujaPortal(
        this IServiceCollection services,
        PortalOptions options,
        IConfiguration configuration)
    {
        services.AddSingleton(options);
        services.TryAddSingleton<PortalRegistry>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(configuration);
        services.AddSingleton<PortalSessionStore>();
        // M12 R2: the user validator is what sign-in uses; it OWNS a key validator internally, because the
        // join reads Auth:ApiKeys. Both are registered — the key validator is still the thing that answers
        // "which principal does this key belong to", and the endpoint no longer calls it directly.
        services.AddSingleton(new PortalKeyValidator(configuration));
        services.AddSingleton(new PortalUserValidator(configuration));
        services.TryAddSingleton<PortalPermissions>(sp =>
            PortalPermissions.Create(sp.GetServices<IRoleContributor>()));
        services.AddHttpContextAccessor();
        services.AddHttpClient(PortalArm.ClientName, client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
            // An event stream is long-lived: the per-request timeout is the component's
            // CancellationToken, never a wall-clock limit that would kill a quiet stream.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        services.AddAuthentication(PortalAuth.Scheme)
            .AddScheme<AuthenticationSchemeOptions, PortalAuthenticationHandler>(PortalAuth.Scheme, _ => { });
        // Policies and routes consume the same registry; registration is complete before options resolve.
        services.AddAuthorization();
        services.AddOptions<Microsoft.AspNetCore.Authorization.AuthorizationOptions>().Configure<PortalRegistry>((authorization, registry) =>
        {
            foreach (var scope in registry.All)
            {
                authorization.AddPolicy(scope.Policy, policy => policy
                    .RequireAuthenticatedUser()
                    .RequireClaim(PortalAuth.PortalClaim, scope.Id));
            }
        });
        services.AddCascadingAuthenticationState();
        services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, PortalAuthenticationStateProvider>();

        services.AddScoped<PortalSessionAccessor>();
        services.AddScoped<IPortalArm, PortalArm>();
        services.AddScoped<PortalData>();
        // M14 R9 — the page's own "my reads landed" claim, reported by `PageHeader`/`ExecutionDetail` and
        // written as `data-loaded` on the page root by `PageRoot`. Scoped: it is about the page on screen in
        // THIS circuit, and `MainLayout` resets it on every navigation.
        services.AddScoped<PageLoadState>();
        // U2: which view a resource type gets. A singleton because it is a lookup table with no per-circuit
        // state, and the same table answers every blade in the app.
        services.AddSingleton<ResourceViewRegistry>();
        services.AddScoped<PortalStreamHub>();
        services.AddScoped<ExecutionView>();
        services.AddScoped<CircuitHandler, PortalCircuitHandler>();
        return services;
    }
}
