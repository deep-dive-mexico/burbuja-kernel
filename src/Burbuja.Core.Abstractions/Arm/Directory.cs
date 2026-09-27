namespace Burbuja.Core.Abstractions.Arm;

public sealed class SubscriptionRecord
{
    public required string Id { get; init; }
    public SubscriptionState State { get; set; } = SubscriptionState.Enabled;
    public string DisplayName { get; set; } = "";
}

public sealed class ResourceGroupRecord
{
    public required string SubscriptionId { get; init; }
    public required string Name { get; init; }
    public string Location { get; set; } = "local";
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// N4: the tenancy catalog (subscriptions, resource groups, provider registrations) behind a
/// seam, so a durable store can rehydrate it instead of the kernel hard-coding a dictionary.
/// </summary>
public interface IArmDirectory
{
    bool TryGetSubscription(string subscriptionId, out SubscriptionRecord record);
    void UpsertSubscription(SubscriptionRecord record);
    bool RemoveSubscription(string subscriptionId);

    bool TryGetResourceGroup(string subscriptionId, string name, out ResourceGroupRecord record);
    void UpsertResourceGroup(ResourceGroupRecord record);
    bool RemoveResourceGroup(string subscriptionId, string name);
    void RemoveResourceGroupsFor(string subscriptionId);

    bool TryGetProviderRegistration(string subscriptionId, string ns, out ProviderRegistrationState state);
    void SetProviderRegistration(string subscriptionId, string ns, ProviderRegistrationState state);

    static string RgKey(string subscriptionId, string name) =>
        $"{subscriptionId}/{name}".ToLowerInvariant();

    static string ProviderKey(string subscriptionId, string ns) =>
        $"{subscriptionId}/{ns}".ToLowerInvariant();
}
