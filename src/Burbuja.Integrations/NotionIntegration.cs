using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Secrets;
using Microsoft.Extensions.Options;

namespace Burbuja.Integrations;

/// <summary>Where the workspace's token comes from, and the three numbers that keep a page bounded.</summary>
public sealed class NotionIntegrationOptions
{
    public const string Section = "Integrations:Notion";

    /// <summary>
    /// The secret NAME the workspace token is resolved under, used when an integration does not name its own.
    /// A name, never a token: the value comes from the kernel's secret seam, so the token exists in a request
    /// header and nowhere else.
    /// </summary>
    public string SecretName { get; set; } = "notion";

    /// <summary>
    /// The API endpoint. Configuration rather than input, for the reason the mailbox's is: an argument that
    /// could name the host would let a caller aim the bearer token at a server of its choosing.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.notion.com/v1";

    /// <summary>The version header the reference pins, and Notion requires on every request.</summary>
    public string Version { get; set; } = "2022-06-28";

    /// <summary>The ceiling on any listing or page of results. An unbounded workspace read is an
    /// exfiltration shape, the same one the mailbox's cap answers.</summary>
    public int MaxItems { get; set; } = 25;

    /// <summary>
    /// How many blocks one request may carry. Notion's own ceiling is 100 and the reference uses 90; the
    /// number is configuration so a caller can be more conservative than the server, never less.
    /// </summary>
    public int MaxBlocksPerCall { get; set; } = 90;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// I3: the Notion workspace, as the third instance of the integration abstraction — and the one the reference
/// built a report SINK for.
///
/// <para><b>What the abstraction buys, three times over.</b> The mailbox proved a caller can speak a protocol
/// without becoming a proxy for it; the browser proved a transport can be spawned and bounded; this proves the
/// same resource, the same gate and the same retry carry a surface that is neither an inbox nor a page under
/// a cursor. Nightshift's sink publishes one page per finished job — a status callout with the facts, the
/// summary, the still-pending items as tickable checkboxes, toggles per section, the brief as a quote, the
/// diffstat as code and a bookmark for the pull request — and that presentation is what
/// <c>notion_create_page</c> builds here, from a FIXED list of fields.</para>
///
/// <para><b>The fixed field list is the boundary, not the formatting.</b> A caller hands over a report and
/// gets the reference's page; it does not hand over a Notion block tree, so <c>icon</c>, <c>cover</c>,
/// <c>parent.workspace</c> and every other field the API accepts cannot be reached through this tool. The
/// same rule the mailbox follows for a message it sends: build the request from what was understood, never
/// forward what was sent.</para>
///
/// <para><b>An id is validated before it is put in a path.</b> Notion ids are hex with dashes; anything else
/// is refused before a request is built, so <c>../users</c> cannot walk the bearer token to another resource.
/// That is the mailbox's message-id escaping with a stricter rule, because a page id has a known shape.</para>
/// </summary>
public sealed class NotionCaller : IIntegrationCaller, IDisposable
{
    /// <summary>The tools this caller can actually serve. An integration may declare fewer.</summary>
    public static readonly IReadOnlyList<string> Supported =
    [
        "notion_status", "notion_search", "notion_read_page", "notion_read_block", "notion_read_database",
        "notion_create_page",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NotionIntegrationOptions _options;
    private readonly ISecretResolver? _secrets;
    private readonly HttpClient _http;
    private bool _disposed;

    public NotionCaller(
        IOptions<NotionIntegrationOptions> options,
        ISecretResolver? secrets = null,
        HttpMessageHandler? handler = null)
    {
        _options = options.Value;
        _secrets = secrets;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = _options.Timeout;
    }

    public IReadOnlyList<string> Kinds { get; } = [IntegrationConstants.KindNotion];

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
            return Fail($"This workspace cannot serve '{tool}'. It serves: {string.Join(", ", Supported)}.");

        var secretName = integration.Transport?.SecretRef is { Length: > 0 } named ? named : _options.SecretName;
        if (_secrets is null)
            return Fail($"No secret resolver is registered, so the workspace token ('{secretName}') cannot be read.");

        var token = await _secrets.ResolveAsync(secretName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return Fail($"The workspace token '{secretName}' could not be resolved; nothing was sent.");

        try
        {
            return name switch
            {
                "notion_status" => await StatusAsync(token, ct).ConfigureAwait(false),
                "notion_search" => await SearchAsync(token, input, ct).ConfigureAwait(false),
                "notion_read_page" => await ReadPageAsync(token, input, ct).ConfigureAwait(false),
                "notion_read_block" => await ReadBlockAsync(token, input, ct).ConfigureAwait(false),
                "notion_read_database" => await QueryDatabaseAsync(token, input, ct).ConfigureAwait(false),
                "notion_create_page" => await CreatePageAsync(token, input, ct).ConfigureAwait(false),
                _ => Fail($"This workspace cannot serve '{tool}'."),
            };
        }
        catch (NotionRefused ex)
        {
            return Fail(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // A refusal is a RESULT, not an exception: the handler records it as the call's own answer, and
            // the message is the status and the server's words — never the response's headers, which on a
            // 401 describe the token's realm.
            return Fail($"The workspace call failed: {ex.Message}");
        }
    }

    // ---- reads ------------------------------------------------------------------------------------

    /// <summary>The token's own identity, which is what makes it the probe-friendly read: it answers whether
    /// the secret is a token this workspace accepts without touching any page.</summary>
    private async Task<IntegrationCallResult> StatusAsync(string token, CancellationToken ct)
    {
        var (status, body) = await SendAsync(token, HttpMethod.Get, "/users/me", null, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
            return Fail($"The workspace refused the token ({(int)status}): {Trim(body, 300)}");

        var user = JsonNode.Parse(body);
        return Ok(new JsonObject
        {
            ["ok"] = true,
            ["name"] = user?["name"]?.GetValue<string>(),
            ["type"] = user?["type"]?.GetValue<string>(),
            ["bot"] = user?["bot"]?["owner"]?["type"]?.GetValue<string>(),
            ["version"] = _options.Version,
        });
    }

    /// <summary>A search, clamped and projected: ids, titles, types and urls — never the payload.</summary>
    private async Task<IntegrationCallResult> SearchAsync(string token, JsonNode input, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["query"] = Text(input, "query") ?? "",
            ["page_size"] = _options.MaxItems,
        };
        if (Text(input, "type") is { Length: > 0 } type)
            payload["filter"] = new JsonObject { ["property"] = "object", ["value"] = type };

        var (status, body) = await SendAsync(token, HttpMethod.Post, "/search", payload, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
            return Fail($"The workspace refused the search ({(int)status}): {Trim(body, 300)}");

        return Ok(ProjectResults(body, "results"));
    }

    private async Task<IntegrationCallResult> ReadPageAsync(string token, JsonNode input, CancellationToken ct)
    {
        var id = RequireId(input, "id", "page");
        var (status, body) = await SendAsync(token, HttpMethod.Get, $"/pages/{id}", null, ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
            return Fail($"The workspace could not read page '{id}' ({(int)status}): {Trim(body, 300)}");

        var page = JsonNode.Parse(body);
        return Ok(new JsonObject
        {
            ["id"] = page?["id"]?.GetValue<string>(),
            ["url"] = page?["url"]?.GetValue<string>(),
            ["archived"] = page?["archived"]?.GetValue<bool>() ?? false,
            ["createdTime"] = page?["created_time"]?.GetValue<string>(),
            ["lastEditedTime"] = page?["last_edited_time"]?.GetValue<string>(),
            ["title"] = Title(page?["properties"]),
        });
    }

    private async Task<IntegrationCallResult> ReadBlockAsync(string token, JsonNode input, CancellationToken ct)
    {
        var id = RequireId(input, "id", "block");
        var (status, body) = await SendAsync(
            token,
            HttpMethod.Get,
            $"/blocks/{id}/children?page_size={_options.MaxItems}",
            null,
            ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
            return Fail($"The workspace could not read block '{id}' ({(int)status}): {Trim(body, 300)}");

        var node = JsonNode.Parse(body);
        var blocks = node?["results"] as JsonArray ?? [];
        var projected = new JsonArray([.. blocks.Take(_options.MaxItems).Select(b => (JsonNode)new JsonObject
        {
            ["id"] = b?["id"]?.GetValue<string>(),
            ["type"] = b?["type"]?.GetValue<string>(),
            ["text"] = BlockText(b),
        })]);

        return Ok(new JsonObject
        {
            ["blocks"] = projected,
            ["count"] = projected.Count,
            ["truncated"] = node?["has_more"]?.GetValue<bool>() ?? false,
        });
    }

    private async Task<IntegrationCallResult> QueryDatabaseAsync(string token, JsonNode input, CancellationToken ct)
    {
        var id = RequireId(input, "id", "database");
        var payload = new JsonObject { ["page_size"] = _options.MaxItems };
        var (status, body) = await SendAsync(token, HttpMethod.Post, $"/databases/{id}/query", payload, ct)
            .ConfigureAwait(false);
        if (status != HttpStatusCode.OK)
            return Fail($"The workspace could not query database '{id}' ({(int)status}): {Trim(body, 300)}");

        return Ok(ProjectResults(body, "results"));
    }

    // ---- the write --------------------------------------------------------------------------------

    /// <summary>
    /// Publish a report as one page.
    ///
    /// <para><b>The parent is a database or a page, and the caller says which.</b> The reference probes the
    /// target once and caches the answer; here the choice is an argument, because a probe is a second request
    /// per page and the caller already knows what it shared. Both shapes exist in the API and both are
    /// legitimate: rows get properties, and a page under a page is what somebody who shared the page they had
    /// open gets.</para>
    ///
    /// <para><b>Blocks are batched at the configured ceiling.</b> The first request carries as many as fit and
    /// the rest are appended, so a long report cannot be lost to one oversized body.</para>
    /// </summary>
    private async Task<IntegrationCallResult> CreatePageAsync(string token, JsonNode input, CancellationToken ct)
    {
        var parent = RequireParent(input);
        var title = Text(input, "title") ?? "(untitled report)";
        var blocks = ReportBlocks(input);
        var first = blocks.Take(_options.MaxBlocksPerCall).ToList();

        var payload = new JsonObject
        {
            ["parent"] = parent,
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject
                {
                    ["title"] = new JsonArray(new JsonObject { ["text"] = new JsonObject { ["content"] = title } }),
                },
            },
            ["children"] = new JsonArray([.. first.Select(b => b.DeepClone())]),
        };

        var (status, body) = await SendAsync(token, HttpMethod.Post, "/pages", payload, ct).ConfigureAwait(false);
        if (status is not (HttpStatusCode.OK or HttpStatusCode.Created))
            return Fail($"The workspace refused the page ({(int)status}): {Trim(body, 300)}");

        var page = JsonNode.Parse(body);
        var pageId = page?["id"]?.GetValue<string>() ?? "";
        var appended = 0;

        for (var i = _options.MaxBlocksPerCall; i < blocks.Count; i += _options.MaxBlocksPerCall)
        {
            var batch = new JsonObject
            {
                ["children"] = new JsonArray([.. blocks.Skip(i).Take(_options.MaxBlocksPerCall).Select(b => b.DeepClone())]),
            };
            var (appendStatus, appendBody) = await SendAsync(
                token,
                HttpMethod.Patch,
                $"/blocks/{pageId}/children",
                batch,
                ct).ConfigureAwait(false);
            if (appendStatus != HttpStatusCode.OK)
            {
                // The page exists and part of the report is on it: the answer says so rather than pretending
                // the publish failed, because a caller that retried would create a second page.
                return Fail(
                    $"The page was created ({page?["url"]?.GetValue<string>()}) but appending blocks "
                    + $"{i}..{i + _options.MaxBlocksPerCall} failed ({(int)appendStatus}): {Trim(appendBody, 300)}");
            }

            appended += Math.Min(_options.MaxBlocksPerCall, blocks.Count - i);
        }

        return Ok(new JsonObject
        {
            ["id"] = pageId,
            ["url"] = page?["url"]?.GetValue<string>(),
            ["blocks"] = first.Count + appended,
            ["appendedInBatches"] = appended > 0,
        });
    }

    /// <summary>
    /// The reference's page, built from a FIXED field list.
    ///
    /// <para>A status callout, the summary, the pending items as tickable checkboxes, one toggle per section,
    /// the brief as a quote, the diffstat as code and the link as a bookmark. Anything else the caller sends
    /// is ignored — a caller cannot reach the API's other fields through this tool, which is the difference
    /// between publishing a report and having write access to a workspace.</para>
    /// </summary>
    private static List<JsonObject> ReportBlocks(JsonNode input)
    {
        var blocks = new List<JsonObject>();

        var status = Text(input, "status");
        var facts = Strings(input, "facts");
        if (status is { Length: > 0 } || facts.Count > 0)
        {
            blocks.Add(new JsonObject
            {
                ["object"] = "block",
                ["type"] = "callout",
                ["callout"] = new JsonObject
                {
                    ["rich_text"] = RichText(string.Join(" · ", new[] { status ?? "" }.Concat(facts)).Trim(' ', '·')),
                    ["icon"] = new JsonObject { ["emoji"] = "🟢" },
                },
            });
        }

        if (Text(input, "summary") is { Length: > 0 } summary)
            blocks.Add(Paragraph(summary));

        foreach (var pending in Strings(input, "pending"))
        {
            blocks.Add(new JsonObject
            {
                ["object"] = "block",
                ["type"] = "to_do",
                ["to_do"] = new JsonObject
                {
                    ["rich_text"] = RichText(pending),
                    ["checked"] = false,
                },
            });
        }

        foreach (var section in (input["sections"] as JsonArray) ?? [])
        {
            var name = Text(section, "name");
            var text = Text(section, "text");
            if (name is not { Length: > 0 })
                continue;
            blocks.Add(new JsonObject
            {
                ["object"] = "block",
                ["type"] = "toggle",
                ["toggle"] = new JsonObject
                {
                    ["rich_text"] = RichText(name),
                    ["children"] = text is { Length: > 0 } ? new JsonArray(Paragraph(text)) : new JsonArray(),
                },
            });
        }

        if (Text(input, "brief") is { Length: > 0 } brief)
        {
            blocks.Add(new JsonObject
            {
                ["object"] = "block",
                ["type"] = "quote",
                ["quote"] = new JsonObject { ["rich_text"] = RichText(brief) },
            });
        }

        if (Text(input, "diffstat") is { Length: > 0 } diffstat)
        {
            blocks.Add(new JsonObject
            {
                ["object"] = "block",
                ["type"] = "code",
                ["code"] = new JsonObject { ["rich_text"] = RichText(diffstat), ["language"] = "diff" },
            });
        }

        var link = Text(input, "link");
        if (link is { Length: > 0 } && Uri.TryCreate(link, UriKind.Absolute, out var url)
            && url.Scheme is "http" or "https")
        {
            blocks.Add(new JsonObject
            {
                ["object"] = "block",
                ["type"] = "bookmark",
                ["bookmark"] = new JsonObject { ["url"] = url.ToString() },
            });
        }

        return blocks;
    }

    private static JsonObject Paragraph(string text) => new()
    {
        ["object"] = "block",
        ["type"] = "paragraph",
        ["paragraph"] = new JsonObject { ["rich_text"] = RichText(text) },
    };

    private static JsonArray RichText(string text) =>
        new(new JsonObject { ["type"] = "text", ["text"] = new JsonObject { ["content"] = text } });

    // ---- the wire ---------------------------------------------------------------------------------

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string token,
        HttpMethod method,
        string path,
        JsonObject? payload,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, _options.BaseUrl.TrimEnd('/') + path);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("Notion-Version", _options.Version);
        if (payload is not null)
            request.Content = new StringContent(payload.ToJsonString(Json), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    /// <summary>Ids, titles, types and urls, clamped — the projection a search and a query share.</summary>
    private JsonObject ProjectResults(string body, string property)
    {
        var node = JsonNode.Parse(body);
        var rows = node?[property] as JsonArray ?? [];
        var projected = new JsonArray([.. rows.Take(_options.MaxItems).Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r?["id"]?.GetValue<string>(),
            ["type"] = r?["object"]?.GetValue<string>(),
            ["title"] = Title(r?["properties"]) ?? r?["title"]?.GetValue<string>(),
            ["url"] = r?["url"]?.GetValue<string>(),
            ["lastEditedTime"] = r?["last_edited_time"]?.GetValue<string>(),
        })]);

        return new JsonObject
        {
            ["results"] = projected,
            ["count"] = projected.Count,
            ["truncated"] = node?["has_more"]?.GetValue<bool>() ?? false,
            ["cappedAt"] = _options.MaxItems,
        };
    }

    /// <summary>The page's title, out of whichever property is the title one — the API does not name it.</summary>
    private static string? Title(JsonNode? properties)
    {
        if (properties is not JsonObject map)
            return null;

        foreach (var (_, value) in map)
        {
            if (value?["type"]?.GetValue<string>() != "title")
                continue;
            var parts = value["title"] as JsonArray;
            var text = string.Concat((parts ?? []).Select(p => p?["plain_text"]?.GetValue<string>() ?? ""));
            if (text.Length > 0)
                return text;
        }

        return null;
    }

    private static string? BlockText(JsonNode? block)
    {
        var type = block?["type"]?.GetValue<string>();
        if (type is null)
            return null;
        var parts = block?[type]?["rich_text"] as JsonArray;
        var text = string.Concat((parts ?? []).Select(p => p?["plain_text"]?.GetValue<string>() ?? ""));
        return text.Length > 0 ? text : null;
    }

    /// <summary>
    /// A Notion id, or a refusal. Hex and dashes only, which is what an id is: a path built from anything else
    /// is a path this caller had no business building, and validating here is what keeps <c>../users</c> from
    /// turning the bearer token towards another resource.
    /// </summary>
    private static string RequireId(JsonNode input, string field, string what)
    {
        var id = Text(input, field) ?? "";
        var ok = id.Length is >= 8 and <= 64 && id.All(c => char.IsAsciiHexDigit(c) || c == '-');
        if (!ok)
        {
            throw new NotionRefused(
                $"'{what}' needs an id of hex digits and dashes in `{field}`; '{Trim(id, 60)}' is not one. "
                + "This caller does not build a path out of anything else.");
        }

        return id;
    }

    private static JsonObject RequireParent(JsonNode input)
    {
        var parent = input["parent"];
        var database = Text(parent, "databaseId");
        var page = Text(parent, "pageId");
        if (database is { Length: > 0 })
            return new JsonObject { ["database_id"] = RequireId(parent!, "databaseId", "database") };
        if (page is { Length: > 0 })
            return new JsonObject { ["page_id"] = RequireId(parent!, "pageId", "page") };

        throw new NotionRefused(
            "A report needs a `parent` naming the database or the page to publish under "
            + "(`parent.databaseId` or `parent.pageId`).");
    }

    private static string? Text(JsonNode? node, string field)
    {
        var value = node?[field];
        return value is null ? null : value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    private static IReadOnlyList<string> Strings(JsonNode? node, string field) =>
        node?[field] is JsonArray array
            ? [.. array.Select(v => v?.GetValue<string>()).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!)]
            : [];

    private IntegrationCallResult Ok(JsonObject output) => new(true, output, null);

    private static IntegrationCallResult Fail(string message) => new(false, null, message);

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
    }
}

/// <summary>A refusal this caller decided, as opposed to one the workspace returned.</summary>
public sealed class NotionRefused(string message) : Exception(message);
