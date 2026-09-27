using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Store;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Llm.Scripted;

public sealed class ScriptedLlmPlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Llm.Scripted",
        "Scripted LLM",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Llm.Scripted.dll",
        typeof(ScriptedLlmPlugin).FullName!,
        PluginLoadKind.Static,
        ["llm.provider"],
        ["llm.provider"],
        [PluginCapability.Network],
        ["all", "worker"],
        null,
        true,
        [],
        300);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton(sp => new ScriptedLlmProvider(
            sp.GetRequiredService<ICache>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetServices<IScriptedStructuredResponse>()));
        services.AddSingleton<ILlmProvider>(sp => Wrap(sp.GetRequiredService<ScriptedLlmProvider>()));
        services.AddSingleton<IStructuredLlm>(sp => sp.GetRequiredService<ScriptedLlmProvider>());
    }

    // Recording is process-wide opt-in: BURBUJA_LLM_RECORD=1 turns any run into a replayable
    // transcript on disk. Without it the provider is used directly, so the hot path is untouched.
    private static ILlmProvider Wrap(ILlmProvider provider) =>
        LlmRecorder.Enabled ? new LlmRecorder(provider) : provider;
}
