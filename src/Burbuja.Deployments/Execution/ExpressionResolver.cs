using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Deployments.Execution;

public static class ExpressionResolver
{
    private static readonly Regex Whole = new(@"^\$\{(steps\.[A-Za-z0-9-]+\.outputs\.[A-Za-z0-9_.-]+|parameters\.[A-Za-z0-9_.-]+)\}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Any = new(@"\$\$|\$\{(steps\.[A-Za-z0-9-]+\.outputs\.[A-Za-z0-9_.-]+|parameters\.[A-Za-z0-9_.-]+)\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static JsonNode? Resolve(JsonNode? node, JsonObject parameters, JsonObject outputs, string pointer, List<ArmError> errors)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue v when v.TryGetValue<string>(out var s):
                return ResolveString(s, parameters, outputs, pointer, errors);
            case JsonObject o:
            {
                var copy = new JsonObject();
                foreach (var kv in o)
                    copy[kv.Key] = Resolve(kv.Value?.DeepClone(), parameters, outputs, pointer + "/" + kv.Key, errors);
                return copy;
            }
            case JsonArray a:
            {
                var copy = new JsonArray();
                for (var i = 0; i < a.Count; i++)
                    copy.Add(Resolve(a[i]?.DeepClone(), parameters, outputs, pointer + "/" + i, errors));
                return copy;
            }
            default:
                return node.DeepClone();
        }
    }

    private static JsonNode? ResolveString(string s, JsonObject parameters, JsonObject outputs, string pointer, List<ArmError> errors)
    {
        if (Whole.IsMatch(s))
        {
            var inner = s[2..^1];
            if (TryLookup(inner, parameters, outputs, out var node))
                return node?.DeepClone();
            errors.Add(new ArmError("UnresolvedReference", $"Unresolved expression '{s}'.", pointer, [], null, 400));
            return JsonValue.Create(s);
        }

        var replaced = Any.Replace(s, m =>
        {
            if (m.Value == "$$")
                return "$";
            var inner = m.Value[2..^1];
            if (TryLookup(inner, parameters, outputs, out var node))
                return node is JsonValue jv && jv.TryGetValue<string>(out var sv) ? sv : node?.ToJsonString() ?? "";
            errors.Add(new ArmError("UnresolvedReference", $"Unresolved expression '${{{inner}}}'.", pointer, [], null, 400));
            return m.Value;
        });
        return JsonValue.Create(replaced);
    }

    private static bool TryLookup(string path, JsonObject parameters, JsonObject outputs, out JsonNode? node)
    {
        node = null;
        if (path.StartsWith("parameters.", StringComparison.Ordinal))
            return TryWalk(parameters, path["parameters.".Length..], out node);
        if (path.StartsWith("steps.", StringComparison.Ordinal) && path.Contains(".outputs.", StringComparison.Ordinal))
        {
            var rest = path["steps.".Length..];
            var idx = rest.IndexOf(".outputs.", StringComparison.Ordinal);
            var step = rest[..idx];
            var key = rest[(idx + ".outputs.".Length)..];
            if (outputs[step] is not JsonObject stepOut)
                return false;
            return TryWalk(stepOut, key, out node);
        }

        return false;
    }

    private static bool TryWalk(JsonObject root, string path, out JsonNode? node)
    {
        JsonNode? cur = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur is not JsonObject o || !o.TryGetPropertyValue(part, out cur))
            {
                node = null;
                return false;
            }
        }

        node = cur;
        return true;
    }
}
