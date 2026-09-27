using System.Text.Json;
using System.Text.Json.Nodes;

namespace Burbuja.FakeMcp;

/// <summary>
/// A fake MCP server over stdio, and NOT a mock: it speaks the same JSON-RPC 2.0 the real servers do, on
/// the same two pipes, so a fact that spawns it exercises the transport the product actually uses rather
/// than a stand-in for it. The alternative — an in-process interface double — cannot show that the process
/// started, that the request was framed correctly on the wire, or that the answer was read from the child's
/// stdout; those are exactly the parts of a stdio transport that break.
///
/// <para><b>The switches exist because the interesting facts are about failure.</b> A server that exits at
/// once, one that writes a line that is not JSON, one that never answers, and one that writes noise to
/// stderr are the four shapes a real bridge takes when the extension is not running — and each of them has
/// to produce a message an operator can act on, not a hang.</para>
///
/// <para>Usage: <c>Burbuja.FakeMcp [--name fake] [--tools a,b] [--fail] [--garbage] [--hang] [--stderr text]
/// [--delay-ms 0]</c>. It reads one JSON-RPC request per line from stdin and writes one JSON object per
/// line to stdout.</para>
///
/// <para><c>--fail</c> exits on the FIRST REQUEST rather than at startup, on purpose: a child that dies
/// while nobody is writing to it is a race — the caller may meet a broken pipe or an early EOF depending on
/// which side the scheduler ran first — and a fact about it would be a fact about timing. Exiting when asked
/// is the same failure with one shape.</para>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var name = "fake";
        var tools = new List<string> { "echo", "read_page", "navigate" };
        var fail = false;
        var garbage = false;
        var hang = false;
        var delayMs = 0;
        var stderrText = "";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name" when i + 1 < args.Length:
                    name = args[++i];
                    break;
                case "--tools" when i + 1 < args.Length:
                    tools = [.. args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                    break;
                case "--fail":
                    fail = true;
                    break;
                case "--garbage":
                    garbage = true;
                    break;
                case "--hang":
                    hang = true;
                    break;
                case "--stderr" when i + 1 < args.Length:
                    stderrText = args[++i];
                    break;
                case "--delay-ms" when i + 1 < args.Length:
                    delayMs = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    break;
            }
        }

        if (stderrText.Length > 0)
        {
            await Console.Error.WriteLineAsync(stderrText).ConfigureAwait(false);
            await Console.Error.FlushAsync().ConfigureAwait(false);
        }

        if (hang)
        {
            // Never answers, so a caller without a timeout waits forever — which is the bug this switch
            // exists to make impossible to ship.
            await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
            return 0;
        }

        if (garbage)
        {
            await Console.Out.WriteLineAsync("this line is not JSON").ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
        }

        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0)
                continue;

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            // The shape of a bridge that is present but cannot serve: it fails once it is asked to.
            if (fail)
                return 3;

            var id = request?["id"];
            var method = request?["method"]?.GetValue<string>() ?? "";
            if (id is null)
                continue; // A notification: no reply, by the protocol.

            if (delayMs > 0)
                await Task.Delay(delayMs).ConfigureAwait(false);

            var result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = name, ["version"] = "0.0.1" },
                },
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t,
                        ["description"] = $"The fake '{t}' tool.",
                        ["inputSchema"] = new JsonObject { ["type"] = "object" },
                    })]),
                },
                "tools/call" => new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "text",
                        // The arguments are echoed back verbatim, so a fact can assert what reached the
                        // server rather than what the caller believed it sent. `env` answers with the one
                        // environment variable a transport sets on purpose, because "the permission mode
                        // reached the child" is otherwise a claim nobody can observe.
                        ["text"] = request?["params"]?["name"]?.GetValue<string>() == "env"
                            ? Environment.GetEnvironmentVariable("CLAUDE_CHROME_PERMISSION_MODE") ?? "(unset)"
                            : request?["params"]?["arguments"]?.ToJsonString() ?? "{}",
                    }),
                    ["isError"] = false,
                },
                _ => null,
            };

            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["result"] = result,
            };
            if (result is null)
            {
                response.Remove("result");
                response["error"] = new JsonObject
                {
                    ["code"] = -32601,
                    ["message"] = $"Method not found: {method}",
                };
            }

            await Console.Out.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
            await Console.Out.FlushAsync().ConfigureAwait(false);
        }

        return 0;
    }
}
