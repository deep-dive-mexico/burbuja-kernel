using Burbuja.Agents.Abstractions.Llm;
using Microsoft.Extensions.Options;

namespace Burbuja.Agents.Runtime;

/// <summary>
/// Picks the one <see cref="ILlmProvider"/> the agent runtime uses. "Last registration wins" is
/// fragile — staging a second provider (the E2E lane stages both Scripted and DeepSeek) silently
/// changes who serves every turn — so the choice is explicit and configurable, and an unknown id
/// is a boot failure rather than a surprise network call.
/// </summary>
public sealed class LlmProviderRouter
{
    public LlmProviderRouter(IEnumerable<ILlmProvider> providers, IOptions<AgentsOptions> options)
    {
        var installed = providers.ToList();
        var wanted = options.Value.ProviderId;
        if (string.IsNullOrWhiteSpace(wanted))
        {
            Installed = installed.Select(p => p.Id).ToList();
            Provider = installed.LastOrDefault() ?? new NullLlmProvider();
            return;
        }

        var match = installed.FirstOrDefault(p => p.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"Agents:ProviderId '{wanted}' is not installed (installed: {string.Join(", ", installed.Select(p => p.Id))}).");
        Installed = installed.Select(p => p.Id).ToList();
        Provider = match;
    }

    public ILlmProvider Provider { get; }

    /// <summary>The chosen provider when it can also answer structured calls, else null.</summary>
    public IStructuredLlm? Structured => Provider as IStructuredLlm;

    /// <summary>Every provider id the kernel actually resolved, for boot logging and tests.</summary>
    public IReadOnlyList<string> Installed { get; }
}
