using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Burbuja.Integrations;

/// <summary>How a stdio MCP server is talked to, and the three numbers that keep it bounded.</summary>
public sealed class McpClientOptions
{
    public const string Section = "Integrations:Mcp";

    /// <summary>
    /// How long ONE exchange may take. A real bridge answers in milliseconds; a bridge whose extension is
    /// asleep never answers at all, and a transport with no deadline turns "the extension is not connected"
    /// into a job that hangs until something else kills it.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The ceiling on a single response line. A server that writes an unbounded line is read into memory
    /// before any parsing happens, so the cap has to be on the read and not on the parse.
    /// </summary>
    public int MaxLineChars { get; set; } = 1_048_576;

    /// <summary>How much of the child's stderr is quoted back in an error. Enough to name the cause, never
    /// enough to become a channel of its own.</summary>
    public int MaxStderrChars { get; set; } = 2_000;
}

/// <summary>
/// I2: an MCP server over stdio, spoken from this side for the first time.
///
/// <para><b>The kernel has been an MCP SERVER since M2 and had no client at all.</b> <c>/mcp</c> answers other
/// people's tools; nothing here ever called anyone else's, so a resource could declare a transport and no
/// code could use one. This is that half: spawn the declared command, frame JSON-RPC on its stdin, read the
/// answers from its stdout, and report what happened in words an operator can act on.</para>
///
/// <para><b>Every failure has a different sentence, and that is the deliverable.</b> A command that cannot be
/// started, a child that exits before answering, a line that is not JSON, a JSON-RPC error, a result-less
/// answer and a deadline that passes are six different situations; a transport that reported them as
/// "call failed" would leave an operator with a broken integration and no way to tell a missing binary from
/// a sleepy extension. The stderr tail is quoted for the same reason — a native host that refuses to start
/// usually says why on the pipe nobody reads.</para>
///
/// <para><b>One process per exchange, deliberately.</b> A session that is kept alive would have to be owned,
/// keyed, invalidated and eventually reaped, and the stateful MCP surface is its own milestone (K1). What
/// this class must be is correct and bounded: initialize, ask, answer, exit. Nothing here holds a lock or a
/// socket between calls, so nothing here can leak one.</para>
///
/// <para><b>Never a shell.</b> The command and its arguments come from the resource and are passed as an
/// argument list, so a declared argument containing a space or a semicolon is one argument rather than a
/// second command. A transport that ran through a shell would make every integration a shell surface, which
/// is the one thing this host's own gate exists to supervise.</para>
/// </summary>
public sealed class McpStdioClient(IOptions<McpClientOptions>? options = null)
{
    private readonly McpClientOptions _options = options?.Value ?? new McpClientOptions();

    /// <summary>
    /// Ask the server which tools it offers. Throws <see cref="McpTransportException"/> when the surface
    /// could not be reached, because a listing IS the probe: the handler records the message on the resource,
    /// which is where an operator looks to find out whether an integration is alive.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListToolsAsync(
        IntegrationTransport transport,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct)
    {
        var result = await ExchangeAsync(transport, environment, "tools/list", null, ct).ConfigureAwait(false);
        var tools = result?["tools"] as JsonArray;
        if (tools is null)
            throw new McpTransportException($"{Describe(transport)} answered tools/list without a 'tools' array.");

        return [.. tools
            .Select(t => t?["name"]?.GetValue<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)];
    }

    /// <summary>
    /// Invoke one tool. A failure is a RESULT here rather than a throw, because this is an act: the caller
    /// gets the outcome of the call, and the handler records it as the call's own answer.
    /// </summary>
    public async Task<IntegrationCallResult> CallAsync(
        IntegrationTransport transport,
        IReadOnlyDictionary<string, string>? environment,
        string tool,
        JsonNode input,
        CancellationToken ct)
    {
        JsonNode? result;
        try
        {
            result = await ExchangeAsync(
                transport,
                environment,
                "tools/call",
                new JsonObject { ["name"] = tool, ["arguments"] = input.DeepClone() },
                ct).ConfigureAwait(false);
        }
        catch (McpTransportException ex)
        {
            return new IntegrationCallResult(false, null, ex.Message);
        }

        var content = result?["content"] as JsonArray;
        if (content is null)
            return new IntegrationCallResult(false, null, $"{Describe(transport)} answered tools/call without content.");

        // Mapped down rather than forwarded: the text is what a caller can act on, and a bridge's raw
        // envelope is a shape this host would then be promising to keep. The cap is named in the answer,
        // the same way the mailbox's listing clamp is.
        var text = string.Join("\n", content
            .Select(c => c?["text"]?.GetValue<string>())
            .Where(t => !string.IsNullOrEmpty(t)));
        var isError = result?["isError"]?.GetValue<bool>() ?? false;

        const int MaxOutputChars = 20_000;
        var truncated = text.Length > MaxOutputChars;
        return new IntegrationCallResult(
            !isError,
            new JsonObject
            {
                ["tool"] = tool,
                ["text"] = truncated ? text[..MaxOutputChars] : text,
                ["truncated"] = truncated,
                ["isError"] = isError,
            },
            isError ? $"The browser reported the call as failed: {Trim(text, 500)}" : null);
    }

    /// <summary>
    /// One request, one answer, on a process of its own: start it, initialize it, ask it, stop it.
    ///
    /// <para><c>initialize</c> is not optional and the handshake's shape is not decorative — a server that
    /// never agreed a protocol version may answer anything, so the answer is checked before it is used.</para>
    /// </summary>
    private async Task<JsonNode?> ExchangeAsync(
        IntegrationTransport transport,
        IReadOnlyDictionary<string, string>? environment,
        string method,
        JsonNode? parameters,
        CancellationToken ct)
    {
        var command = transport.Command;
        if (string.IsNullOrWhiteSpace(command))
            throw new McpTransportException(
                "This integration declares no transport.command, so there is no MCP server to start. "
                + "A browser integration names the bridge command it runs.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);
        var token = timeout.Token;

        var start = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in transport.Args ?? [])
            start.ArgumentList.Add(arg);
        foreach (var pair in environment ?? new Dictionary<string, string>())
            start.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = start };
        var stderr = new StringBuilder();

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new McpTransportException(
                $"The MCP server '{command}' could not be started: {ex.Message} "
                + $"(args: {string.Join(' ', transport.Args ?? [])}).");
        }

        // The child's stderr is drained by an explicit pump rather than by the event API, because a message
        // has to be able to WAIT for it: the child in this transport's failure cases exits immediately, and
        // reading "whatever has arrived so far" is a race that makes an error message depend on the
        // scheduler. The pump is bounded by the same ceiling the message is.
        var pump = PumpAsync(process.StandardError, stderr);

        try
        {
            await WriteAsync(process, 1, "initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "burbuja-kernel", ["version"] = "1.0" },
            }, transport, stderr, token).ConfigureAwait(false);

            var hello = await ReadAsync(process, 1, "initialize", transport, stderr, pump, ct, token).ConfigureAwait(false);
            if (hello?["serverInfo"] is null && hello?["protocolVersion"] is null)
                throw new McpTransportException(
                    $"{Describe(transport)} answered initialize without a serverInfo or protocolVersion.");

            // The notification the protocol asks for, and the only message here with no answer.
            await WriteAsync(process, null, "notifications/initialized", null, transport, stderr, token).ConfigureAwait(false);

            await WriteAsync(process, 2, method, parameters, transport, stderr, token).ConfigureAwait(false);
            return await ReadAsync(process, 2, method, transport, stderr, pump, ct, token).ConfigureAwait(false);
        }
        finally
        {
            Stop(process);
        }
    }

    private async Task WriteAsync(
        Process process,
        int? id,
        string method,
        JsonNode? parameters,
        IntegrationTransport transport,
        StringBuilder stderr,
        CancellationToken ct)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is { } value)
            request["id"] = value;
        if (parameters is not null)
            request["params"] = parameters;

        try
        {
            await process.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A child that died between the write and the flush meets a broken pipe here rather than an EOF
            // on the read, and the sentence has to be just as useful: the exit code when there is one, and
            // whatever the child said on the pipe nobody reads.
            var code = process.HasExited ? $"(code {process.ExitCode})" : "";
            throw new McpTransportException(
                $"{Describe(transport)} stopped before {method} could be sent{code}: {ex.Message}{Stderr(stderr)}");
        }
    }

    /// <summary>
    /// Read until the answer to <paramref name="id"/> arrives. A notification (an object with no id) is
    /// skipped, because a real server sends them; anything else that cannot be read as a JSON-RPC answer is a
    /// sentence naming what arrived, since "the bridge wrote something that is not JSON" and "the bridge
    /// refused" are different problems with different fixes.
    /// </summary>
    private async Task<JsonNode?> ReadAsync(
        Process process,
        int id,
        string method,
        IntegrationTransport transport,
        StringBuilder stderr,
        Task pump,
        CancellationToken callerCt,
        CancellationToken ct)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
            }
            // The deadline is a transport failure with a sentence; a CALLER's cancellation is the caller's
            // business and must travel as a cancellation, or a job that was stopped would be recorded as a
            // bridge that broke.
            catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
            {
                throw new McpTransportException(
                    $"{Describe(transport)} did not answer {method} within {_options.Timeout.TotalSeconds:0.#}s"
                    + $"{Stderr(stderr)}.");
            }

            if (line is null)
            {
                // The child is gone, so its stderr is complete: settle the pump before quoting it.
                await SettleAsync(pump).ConfigureAwait(false);

                // EOF on the child's stdout can arrive a moment BEFORE the OS marks it exited, and the code
                // is part of the sentence an operator reads — "exited" without a code is a weaker fact than
                // the transport can honestly report. A bounded wait, because the pipe is already closed.
                if (!process.HasExited)
                    process.WaitForExit(500);

                var code = process.HasExited ? process.ExitCode : (int?)null;
                throw new McpTransportException(
                    $"{Describe(transport)} exited{(code is { } c ? $" (code {c})" : "")} before answering {method}"
                    + $"{Stderr(stderr)}.");
            }

            if (line.Length == 0)
                continue;

            if (line.Length > _options.MaxLineChars)
                throw new McpTransportException(
                    $"{Describe(transport)} wrote a {line.Length}-character line answering {method}, above the "
                    + $"{_options.MaxLineChars}-character ceiling; the answer was not read.");

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                throw new McpTransportException(
                    $"{Describe(transport)} wrote a line that is not JSON: {Trim(line, 200)}{Stderr(stderr)}.");
            }

            if (node is not JsonObject answer)
                throw new McpTransportException(
                    $"{Describe(transport)} answered {method} with a {node?.GetType().Name ?? "null"} rather than "
                    + "a JSON-RPC object.");

            if (answer["id"] is null)
                continue; // A notification, which the protocol says has no answer.

            if (answer["id"]!.GetValue<int>() != id)
                throw new McpTransportException(
                    $"{Describe(transport)} answered id {answer["id"]} while {id} was outstanding.");

            if (answer["error"] is { } error)
                throw new McpTransportException(
                    $"{Describe(transport)} answered {method} with error {error["code"]}: {error["message"]}");

            return answer["result"];
        }
    }

    /// <summary>
    /// Copy the child's stderr into <paramref name="sink"/> until the pipe closes, capped so a chatty server
    /// cannot grow this without bound.
    /// </summary>
    private async Task PumpAsync(StreamReader reader, StringBuilder sink)
    {
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                lock (sink)
                {
                    if (sink.Length < _options.MaxStderrChars)
                        sink.Append(buffer, 0, Math.Min(read, _options.MaxStderrChars - sink.Length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe closing is how this ends; there is nothing to report and nowhere to report it.
        }
    }

    /// <summary>
    /// Give the stderr pump a moment to finish, bounded. It returns at once when the child has exited — which
    /// is the only case this is called in — and the bound exists so a stuck read cannot turn an error message
    /// into a hang.
    /// </summary>
    private static async Task SettleAsync(Task pump) =>
        await Task.WhenAny(pump, Task.Delay(TimeSpan.FromMilliseconds(250))).ConfigureAwait(false);

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
            // A process that already exited, or one the OS will not let us signal: nothing left to do, and
            // throwing here would replace a useful error with a useless one.
        }
    }

    private static string Describe(IntegrationTransport transport) =>
        $"The MCP server '{transport.Command}'";

    private string Stderr(StringBuilder? stderr)
    {
        if (stderr is null)
            return "";
        lock (stderr)
        {
            var text = stderr.ToString().Trim();
            return text.Length == 0 ? "" : $". Its stderr said: {Trim(text, _options.MaxStderrChars)}";
        }
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

/// <summary>
/// The surface could not be reached, and the message says which of the six ways. It is an exception because a
/// LISTING is a probe whose whole answer is the message; a CALL turns it into a result instead.
/// </summary>
public sealed class McpTransportException(string message) : Exception(message);
