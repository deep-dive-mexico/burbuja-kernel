using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Llm;

namespace Burbuja.Plugins.Llm.Scripted;

/// <summary>A consumer's deterministic structured-response fixture, without registering another model provider.</summary>
/// <remarks>
/// Contributions run in DI registration order before the built-in structured responses. The first that returns
/// true wins, including a handled null response; false leaves the request to the next contribution or built-in
/// fallback. Return a fresh node for each call and support concurrent calls. Exceptions propagate to the caller.
/// The provider checks cancellation before dispatch and supplies its normal fixture usage (100 in, 50 out).
/// </remarks>
public interface IScriptedStructuredResponse
{
    bool TryRespond(StructuredLlmRequest request, out JsonNode? response);
}
