using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Llm;

namespace Burbuja.Agents.Runtime;

public sealed class NullLlmProvider : ILlmProvider
{
    public string Id => "null";

    public ModelCapabilities Capabilities(string modelId) =>
        new(false, "disabled", true, true, 1024, 200_000);

    public ValueTask<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new(new LlmResponse(LlmStopReason.EndTurn, [new TextBlock("no llm provider")], new Usage(1, 1, 0, 0), "end_turn"));
    }
}
