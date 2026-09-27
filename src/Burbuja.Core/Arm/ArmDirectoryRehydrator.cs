using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Store;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Burbuja.Core.Arm;

/// <summary>
/// N4: rebuilds the tenancy catalog from the resource store on boot. Durability is by
/// rehydration — the directory is a cache of the store, not a second source of truth, so a
/// durable <see cref="IResourceStore"/> needs no extra tables. Registered inside
/// <c>AddBurbujaCore</c> so it runs before any host-added seed hosted service.
/// </summary>
public sealed class ArmDirectoryRehydrator(
    IResourceStore store,
    IArmDirectory directory,
    ILogger<ArmDirectoryRehydrator> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await RehydrateAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task RehydrateAsync(CancellationToken ct)
    {
        var subscriptions = 0;
        var resourceGroups = 0;
        var registrations = 0;

        foreach (var env in await ListAllAsync(ArmConstants.ResourcesNamespace, "subscriptions", ct))
        {
            var subscriptionId = SegmentAfter(env.Id, "subscriptions") ?? env.Name;
            if (string.IsNullOrWhiteSpace(subscriptionId))
                continue;
            directory.UpsertSubscription(new SubscriptionRecord
            {
                Id = subscriptionId,
                DisplayName = env.Properties["displayName"]?.GetValue<string>() ?? "",
                State = Enum.TryParse<SubscriptionState>(env.Properties["state"]?.GetValue<string>(), ignoreCase: true, out var state)
                    ? state
                    : SubscriptionState.Enabled,
            });
            subscriptions++;
        }

        foreach (var env in await ListAllAsync(ArmConstants.ResourcesNamespace, "resourceGroups", ct))
        {
            var subscriptionId = SegmentAfter(env.Id, "subscriptions");
            if (string.IsNullOrWhiteSpace(subscriptionId))
                continue;
            directory.UpsertResourceGroup(new ResourceGroupRecord
            {
                SubscriptionId = subscriptionId,
                Name = env.Name,
            });
            resourceGroups++;
        }

        foreach (var env in await ListAllAsync(ArmConstants.ResourcesNamespace, "providers", ct))
        {
            var subscriptionId = SegmentAfter(env.Id, "subscriptions");
            var ns = env.Properties["namespace"]?.GetValue<string>() ?? env.Name;
            if (string.IsNullOrWhiteSpace(subscriptionId) || string.IsNullOrWhiteSpace(ns))
                continue;
            var state = Enum.TryParse<ProviderRegistrationState>(env.Properties["registrationState"]?.GetValue<string>(), ignoreCase: true, out var parsed)
                ? parsed
                : ProviderRegistrationState.Registered;
            directory.SetProviderRegistration(subscriptionId, ns, state);
            registrations++;
        }

        log.LogInformation(
            "rehydrated ARM directory: {Subscriptions} subscriptions, {ResourceGroups} resource groups, {Registrations} provider registrations",
            subscriptions,
            resourceGroups,
            registrations);
    }

    private async Task<List<ResourceEnvelope<JsonObject>>> ListAllAsync(string ns, string type, CancellationToken ct)
    {
        var all = new List<ResourceEnvelope<JsonObject>>();
        string? token = null;
        do
        {
            var page = await store.ListAsync(new ResourceStoreQuery(null, null, ns, type, 1000, token), ct);
            all.AddRange(page.Value);
            token = page.NextLink;
        }
        while (!string.IsNullOrEmpty(token));

        return all;
    }

    private static string? SegmentAfter(string id, string segment)
    {
        var parts = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i].Equals(segment, StringComparison.OrdinalIgnoreCase))
                return parts[i + 1];
        }

        return null;
    }
}
