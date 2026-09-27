using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Llm.DeepSeek;

public sealed class DeepSeekPlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Llm.DeepSeek",
        "DeepSeek LLM",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Llm.DeepSeek.dll",
        typeof(DeepSeekPlugin).FullName!,
        PluginLoadKind.Static,
        ["llm.provider", "llm.pricebook"],
        ["llm.provider"],
        [PluginCapability.Network, PluginCapability.Secrets],
        ["all", "worker"],
        DeepSeekOptions.Section,
        true,
        [],
        305);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddOptions<DeepSeekOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(DeepSeekOptions.Section).Bind(o))
            .Validate(
                o => o.ModelId.Equals(DeepSeekLlmProvider.FlashModelId, StringComparison.OrdinalIgnoreCase),
                "DeepSeek:ModelId must name a priced model (deepseek-flash).")
            .ValidateOnStart();

        // M2: the provider is handed the ROOT configuration so it can read the money guard itself — the same
        // value the three guards read, from the same place, so a host cannot be "configured" for one and not
        // the other. `BuildServiceProvider` resolves IConfiguration for any plugin.
        services.AddSingleton(sp => new DeepSeekLlmProvider(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeepSeekOptions>>(),
            sp.GetService<ISecretResolver>(),
            handler: null,
            configuration: sp.GetRequiredService<IConfiguration>()));
        // …and the plugin says so in its own health when the guard is missing: Degraded, with the sentence.
        services.AddSingleton<IPluginHealthContributor, DeepSeekBudgetHealth>();
        services.AddSingleton<ILlmProvider>(sp => sp.GetRequiredService<DeepSeekLlmProvider>());
        services.AddSingleton<IStructuredLlm>(sp => sp.GetRequiredService<DeepSeekLlmProvider>());
        // Per-point cardinality counts this contribution (the point may also receive direct
        // ILlmProvider registrations from other plugins).
        services.AddSingleton(new ExtensionRegistration("llm.provider", typeof(ILlmProvider), typeof(DeepSeekLlmProvider)));

        services.AddSingleton<IModelPricebook>(sp => new DeepSeekPricebook(time: sp.GetService<TimeProvider>()));
        services.AddSingleton<ISecretResolver, KeychainSecretResolver>();
    }
}

/// <summary>
/// M2 — <b>the plugin reports the missing guard rather than serving into it.</b>
///
/// <para>Refusing to serve is half the rule; the other half is that an operator must be able to SEE why. This
/// contributes a <c>Degraded</c> health for <c>Llm.DeepSeek</c> with the sentence that names the setting, so
/// <c>/health/plugins</c> answers "this provider will refuse" instead of "Healthy" until the first call fails.
/// Degraded maps to HTTP 200 (<c>Program.cs</c>), so a lane's readiness check survives — the plugin is loaded
/// and honest, not broken.</para>
/// </summary>
public sealed class DeepSeekBudgetHealth(IConfiguration configuration) : IPluginHealthContributor
{
    public string PluginId => "Llm.DeepSeek";

    public ValueTask<PluginHealth> CheckAsync(CancellationToken ct)
    {
        var guard = DailyHardStop.Guard(configuration);
        return ValueTask.FromResult(guard.Kind is BudgetGuardKind.Unset
            ? new PluginHealth(PluginHealthStatus.Degraded, guard.Sentence)
            : new PluginHealth(PluginHealthStatus.Healthy, $"the day's guard is {guard.StopUsd:0.######} USD"));
    }
}
