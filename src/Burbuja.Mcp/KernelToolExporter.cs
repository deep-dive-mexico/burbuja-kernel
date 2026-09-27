using Burbuja.Agents.Abstractions.Agents;
using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Agents.Model;
using Burbuja.Mcp.Abstractions;

namespace Burbuja.Mcp;

/// <summary>
/// The kernel's MCP tool catalogue: every <see cref="IAgentTool"/> the host has registered, under
/// the name the kernel gate already classifies. The export carries no MCP SDK type, so the same
/// shape can be asserted by a test that never speaks the protocol.
/// </summary>
public sealed class KernelToolExporter(IEnumerable<IAgentTool> tools) : IMcpToolExporter
{
    public IReadOnlyList<McpToolExport> Export() =>
        tools
            .Select(t => new McpToolExport(
                t.Descriptor.Name,
                t.Descriptor.Name,
                t.Descriptor.Description,
                t.Descriptor.InputSchema,
                t.Descriptor.RiskHint))
            .OrderBy(t => t.McpName, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The tools one agent may see: its filter when it declares one, the whole catalogue otherwise.
    /// Only an <c>external</c> agent is ever filtered — a kernel-driven agent's catalogue is chosen
    /// by the same rule in the activation worker, so the two lanes cannot disagree.
    /// </summary>
    public IReadOnlyList<McpToolExport> ExportFor(AgentProperties? agent)
    {
        var all = Export();
        if (agent is null || agent.ToolFilter.Count == 0)
            return all;
        return all.Where(t => agent.ToolFilter.Contains(t.ToolName, StringComparer.OrdinalIgnoreCase)).ToArray();
    }
}
