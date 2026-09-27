using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Core.Abstractions.Plugin;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Tools.Agents;

/// <summary>
/// The shared-context tools. Every one of them is an <see cref="IAgentTool"/>, so a call can only
/// happen through the policy gate: intent event, classification, decision, then a <c>GatePass</c>.
/// </summary>
public sealed class AgentToolsPlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Tools.Agents",
        "Agent context tools",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Tools.Agents.dll",
        typeof(AgentToolsPlugin).FullName!,
        PluginLoadKind.Static,
        ["tools.agents"],
        ["agents.tool"],
        [],
        ["all", "worker"],
        null,
        true,
        [],
        340);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAgentTool, ListAgentsTool>();
        services.AddSingleton<IAgentTool, ReadAgentContextTool>();
        services.AddSingleton<IAgentTool, SessionSearchTool>();
        services.AddSingleton<IAgentTool, PostContextTool>();
        services.AddSingleton<IAgentTool, SendMessageTool>();
    }
}
