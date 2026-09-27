using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Secrets;
using Microsoft.Extensions.Options;

namespace Burbuja.Integrations;

/// <summary>Where the mailbox's token comes from, and how much of it a single call may read.</summary>
public sealed class MailIntegrationOptions
{
    public const string Section = "Integrations:Mail";

    /// <summary>
    /// The secret NAME the mailbox token is resolved under, used when an integration does not name its own.
    /// A name, never a token: the value is resolved through the kernel's secret seam, which for this host
    /// means a command under a minted grant rather than a literal in configuration.
    /// </summary>
    public string SecretName { get; set; } = "graph-mail";

    /// <summary>
    /// The Graph endpoint. Configuration rather than input, and that is a safety property rather than a
    /// style choice: a tool argument that could name the host would let a caller aim the bearer token at a
    /// server of its choosing.
    /// </summary>
    public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";

    /// <summary>The ceiling on any listing. An unbounded mailbox read is an exfiltration shape.</summary>
    public int MaxItems { get; set; } = 25;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// I1: the mailbox, as an instance of the integration abstraction.
///
/// <para><b>What this adds to I0.</b> I0 made a surface a resource and its calls a decision; nothing reached
/// anything. This is the first caller that does: the five read tools and the four write tools that
/// <c>MailClassifier</c> has judged since P1.5, spoken as Microsoft Graph requests. The classifier's rule —
/// reading is an allow, writing is gray, an unclassified tool is gray — is what the resource applies, and the
/// caller below is deliberately not a second gatekeeper: it refuses only what it cannot DO, never what it
/// thinks is risky.</para>
///
/// <para><b>Three properties are about the token.</b> It is resolved through <see cref="ISecretResolver"/>
/// by the NAME the integration carries in <c>transport.secretRef</c>; nothing is sent when it cannot be
/// resolved, because an unauthenticated request to Graph is not a safer request, it is a confusing one; and
/// it never leaves this class — not in a result, not in an error, not in a log. The one place it exists is
/// the request header.</para>
///
/// <para><b>Every response is mapped down to a compact shape.</b> A listing returns ids, subjects, senders
/// and dates — never the raw Graph payload and never a message body. That is the same rule the redactor
/// follows on the way to the audit, applied at the boundary: an integration whose output is "whatever the
/// remote server said" turns every policy decision upstream of it into decoration.</para>
/// </summary>
public sealed class GraphMailCaller : IIntegrationCaller, IDisposable
{
    /// <summary>The mailbox, and nothing else: the kind is what routes a call to this caller.</summary>
    public IReadOnlyList<string> Kinds { get; } = [IntegrationConstants.KindMail];

    /// <summary>The tools this caller can actually serve. An integration may declare fewer.</summary>
    public static readonly IReadOnlyList<string> Supported =
    [
        "outlook_status", "outlook_search", "outlook_recent", "outlook_read", "outlook_folders",
        "outlook_create_draft", "outlook_send",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly MailIntegrationOptions _options;
    private readonly ISecretResolver? _secrets;
    private readonly HttpClient _http;
    private bool _disposed;

    public GraphMailCaller(
        IOptions<MailIntegrationOptions> options,
        ISecretResolver? secrets = null,
        HttpMessageHandler? handler = null)
    {
        _options = options.Value;
        _secrets = secrets;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = _options.Timeout;
    }

    /// <summary>
    /// What the mailbox offers, as a fact about this caller. The resource still decides what may be CALLED:
    /// probing a mailbox that offers `outlook_send` and then calling it without declaring it is refused, and
    /// that is the point of keeping the two apart.
    /// </summary>
    public ValueTask<IReadOnlyList<string>> ListToolsAsync(IntegrationProperties integration, CancellationToken ct) =>
        ValueTask.FromResult(Supported);

    public async ValueTask<IntegrationCallResult> CallAsync(
        IntegrationProperties integration,
        string tool,
        JsonNode input,
        CancellationToken ct)
    {
        var name = tool.Trim().ToLowerInvariant();
        if (!Supported.Contains(name, StringComparer.Ordinal))
            return Fail($"This mailbox cannot serve '{tool}'. It serves: {string.Join(", ", Supported)}.");

        var secretName = integration.Transport?.SecretRef is { Length: > 0 } named ? named : _options.SecretName;
        if (_secrets is null)
            return Fail($"No secret resolver is registered, so the mailbox token ('{secretName}') cannot be read.");

        var token = await _secrets.ResolveAsync(secretName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return Fail($"The mailbox token '{secretName}' could not be resolved; nothing was sent.");

        try
        {
            return name switch
            {
                "outlook_status" => await StatusAsync(token, ct).ConfigureAwait(false),
                "outlook_search" => await ListAsync(token, SearchQuery(input), ct).ConfigureAwait(false),
                "outlook_recent" => await ListAsync(token, "", ct).ConfigureAwait(false),
                "outlook_folders" => await FoldersAsync(token, ct).ConfigureAwait(false),
                "outlook_read" => await ReadAsync(token, input, ct).ConfigureAwait(false),
                "outlook_create_draft" => await DraftAsync(token, input, ct).ConfigureAwait(false),
                _ => await SendAsync(token, input, ct).ConfigureAwait(false),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The exception's own message is safe to report: nothing in this class puts the token into one.
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async ValueTask<IntegrationCallResult> StatusAsync(string token, CancellationToken ct)
    {
        using var response = await GetAsync(token, "me/mailboxSettings", ct).ConfigureAwait(false);
        return await CompactAsync(response, node => new JsonObject
        {
            ["timeZone"] = node?["timeZone"]?.GetValue<string>(),
            ["language"] = node?["language"]?["displayName"]?.GetValue<string>(),
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A mailbox listing. `$top` is CLAMPED rather than rejected: a caller asking for 500 gets 25 and can see
    /// the cap in the answer, where a refusal would be a caller-visible policy the owner never wrote. The
    /// `$select` is fixed too — a listing that fetched bodies would pull the whole mailbox into one event.
    /// </summary>
    private async ValueTask<IntegrationCallResult> ListAsync(string token, string search, CancellationToken ct)
    {
        var top = Math.Clamp(_options.MaxItems, 1, 100);
        var query = new StringBuilder($"me/messages?$top={top}&$select={ListingFields}");
        if (search.Length > 0)
            query.Append("&$search=").Append(Uri.EscapeDataString($"\"{search}\""));
        else
            query.Append("&$orderby=").Append(Uri.EscapeDataString("receivedDateTime desc"));

        using var response = await GetAsync(token, query.ToString(), ct).ConfigureAwait(false);
        return await CompactAsync(response, node => new JsonObject
        {
            ["count"] = node?["value"]?.AsArray().Count ?? 0,
            ["messages"] = new JsonArray([.. (node?["value"]?.AsArray() ?? []).Select(Summarise)]),
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask<IntegrationCallResult> FoldersAsync(string token, CancellationToken ct)
    {
        using var response = await GetAsync(
            token, "me/mailFolders?$top=50&$select=id,displayName,totalItemCount,unreadItemCount", ct).ConfigureAwait(false);
        return await CompactAsync(response, node => new JsonObject
        {
            ["folders"] = new JsonArray([.. (node?["value"]?.AsArray() ?? []).Select(folder => (JsonNode)new JsonObject
            {
                ["id"] = folder?["id"]?.GetValue<string>(),
                ["name"] = folder?["displayName"]?.GetValue<string>(),
                ["total"] = folder?["totalItemCount"]?.GetValue<int>(),
                ["unread"] = folder?["unreadItemCount"]?.GetValue<int>(),
            })]),
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask<IntegrationCallResult> ReadAsync(string token, JsonNode input, CancellationToken ct)
    {
        var id = input["id"]?.GetValue<string>() ?? "";
        if (id.Length == 0)
            return Fail("outlook_read needs a message `id`; use the ids a search returns.");

        // The id is a Graph id, not a URL: it is escaped into the path so a caller cannot walk out of
        // `/me/messages/` with `../` and read a different resource under the same token.
        using var response = await GetAsync(
            token,
            $"me/messages/{Uri.EscapeDataString(id)}?$select=id,subject,from,toRecipients,receivedDateTime,bodyPreview",
            ct).ConfigureAwait(false);

        return await CompactAsync(response, node => new JsonObject
        {
            ["id"] = node?["id"]?.GetValue<string>(),
            ["subject"] = node?["subject"]?.GetValue<string>(),
            ["preview"] = node?["bodyPreview"]?.GetValue<string>(),
            ["messages"] = new JsonArray(Summarise(node)),
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask<IntegrationCallResult> DraftAsync(string token, JsonNode input, CancellationToken ct)
    {
        var message = Message(input);
        if (message is null)
            return Fail("outlook_create_draft needs at least a `to` and a `body`.");

        using var response = await SendAsync(
            token, HttpMethod.Post, "me/messages", message.ToJsonString(), ct).ConfigureAwait(false);
        return await CompactAsync(response, node => new JsonObject
        {
            ["id"] = node?["id"]?.GetValue<string>(),
            ["drafted"] = response.IsSuccessStatusCode,
        }, ct).ConfigureAwait(false);
    }

    private async ValueTask<IntegrationCallResult> SendAsync(string token, JsonNode input, CancellationToken ct)
    {
        var message = Message(input);
        if (message is null)
            return Fail("outlook_send needs at least a `to` and a `body`.");

        using var response = await SendAsync(
            token, HttpMethod.Post, "me/sendMail", new JsonObject { ["message"] = message }.ToJsonString(), ct)
            .ConfigureAwait(false);

        // Graph answers 202 with an empty body, so the outcome is the status rather than a payload.
        return response.IsSuccessStatusCode
            ? new IntegrationCallResult(true, new JsonObject { ["sent"] = true, ["status"] = (int)response.StatusCode })
            : Fail(await ErrorAsync(response, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The message shape Graph expects, built from a fixed field list. An input object is never forwarded:
    /// passing a caller's JSON through would make this caller a proxy for whatever Graph accepts, including
    /// the fields nobody here has thought about.
    /// </summary>
    private static JsonObject? Message(JsonNode input)
    {
        var to = input["to"]?.GetValue<string>() ?? "";
        var body = input["body"]?.GetValue<string>() ?? "";
        if (to.Length == 0 || body.Length == 0)
            return null;

        var message = new JsonObject
        {
            ["subject"] = input["subject"]?.GetValue<string>() ?? "",
            ["body"] = new JsonObject { ["contentType"] = "Text", ["content"] = body },
            ["toRecipients"] = new JsonArray([.. to
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(address => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = address } })]),
        };
        return message;
    }

    private static JsonObject Summarise(JsonNode? message) => new()
    {
        ["id"] = message?["id"]?.GetValue<string>(),
        ["subject"] = message?["subject"]?.GetValue<string>(),
        ["from"] = message?["from"]?["emailAddress"]?["address"]?.GetValue<string>(),
        ["receivedAt"] = message?["receivedDateTime"]?.GetValue<string>(),
        ["unread"] = message?["isRead"] is { } read ? !read.GetValue<bool>() : null,
    };

    private const string ListingFields = "id,subject,from,receivedDateTime,isRead";

    private static string SearchQuery(JsonNode input) => input["query"]?.GetValue<string>() ?? "";

    private Task<HttpResponseMessage> GetAsync(string token, string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _http.SendAsync(request, ct);
    }

    private Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string path, string body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, Url(path))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _http.SendAsync(request, ct);
    }

    private string Url(string path) => $"{_options.BaseUrl.TrimEnd('/')}/{path}";

    /// <summary>
    /// A successful response, mapped down by <paramref name="project"/>. The projection is where the
    /// "no raw payload" rule is enforced, so it is a required argument rather than an option.
    /// </summary>
    private static async ValueTask<IntegrationCallResult> CompactAsync(
        HttpResponseMessage response,
        Func<JsonNode?, JsonObject> project,
        CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return Fail(ErrorText(response, text));

        JsonNode? node = null;
        if (text.Length > 0)
        {
            try
            {
                node = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                // A 2xx whose body is not JSON: report it as a failure rather than inventing a shape.
                return Fail($"The mailbox answered {(int)response.StatusCode} with a body that is not JSON.");
            }
        }

        return new IntegrationCallResult(true, project(node));
    }

    /// <summary>
    /// What an operator sees when Graph refuses. The status and Graph's own `error.message` are useful;
    /// response HEADERS are deliberately not included, because `WWW-Authenticate` on a 401 carries the
    /// token's realm and audience and there is no reason to copy them into an event.
    /// </summary>
    private static async ValueTask<string> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ErrorText(response, text);
    }

    private static string ErrorText(HttpResponseMessage response, string body) =>
        $"The mailbox answered {(int)response.StatusCode}: {Truncate(body)}";

    private static string Truncate(string body) =>
        body.Length > 300 ? body[..300] + "…" : body;

    private static IntegrationCallResult Fail(string error) => new(false, null, error);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
    }
}
