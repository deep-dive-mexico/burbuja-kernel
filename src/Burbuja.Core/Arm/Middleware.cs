using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Options;
using Json.Schema;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.Arm;

public sealed class RequestIdentityMiddleware : IArmMiddleware
{
    public int Order => 100;

    public ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        context.RequestId = Guid.NewGuid().ToString("D");
        context.ResponseHeaders["x-ms-request-id"] = context.RequestId;
        if (context.Headers.TryGetValue("x-ms-client-request-id", out var client))
        {
            context.ClientRequestId = client;
            context.ResponseHeaders["x-ms-client-request-id"] = client;
        }

        context.CorrelationId = context.Headers.TryGetValue("x-ms-correlation-request-id", out var corr)
            ? corr
            : Guid.NewGuid().ToString("D");
        context.ResponseHeaders["x-ms-correlation-request-id"] = context.CorrelationId!;
        Activity.Current?.SetTag("x-ms-request-id", context.RequestId);
        return next(context, ct);
    }
}

public sealed class BodyBufferingMiddleware : IArmMiddleware
{
    public int Order => 150;

    public async ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        if (context.Body is not null)
        {
            using var ms = new MemoryStream();
            await context.Body.CopyToAsync(ms, ct);
            context.BufferedBody = ms.ToArray();
        }

        await next(context, ct);
    }
}

public sealed class AuthenticationMiddleware(IOptions<AuthOptions> auth) : IArmMiddleware
{
    public int Order => 200;

    public ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        string? key = null;
        if (context.Headers.TryGetValue("x-api-key", out var header))
            key = header;
        else if (context.Headers.TryGetValue("Authorization", out var authz)
                 && authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            key = authz["Bearer ".Length..].Trim();

        ApiKeyEntry? entry = null;
        foreach (var candidate in auth.Value.ApiKeys)
        {
            if (FixedTimeKeyEquals(candidate.Key, key) && entry is null)
                entry = candidate;
        }

        if (entry is null || string.IsNullOrEmpty(key))
        {
            context.Error = ArmError.AuthenticationFailed("Authentication failed.");
            context.ResponseHeaders["WWW-Authenticate"] = "Bearer, ApiKey";
            return default;
        }

        context.Principal = new ArmPrincipal(
            entry.PrincipalId,
            entry.PrincipalType,
            null,
            entry.Groups,
            new Dictionary<string, string>
            {
                ["roles"] = string.Join(',', entry.Roles),
            });
        return next(context, ct);
    }

    internal static bool FixedTimeKeyEquals(string configured, string? provided)
    {
        if (provided is null)
            return false;
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(configured));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}

public sealed class ResourceIdParseMiddleware(ArmRoutingTable table) : IArmMiddleware
{
    public int Order => 300;

    public ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        if (context.Path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
            return next(context, ct);

        if (string.Equals(context.Path, "/subscriptions", StringComparison.OrdinalIgnoreCase)
            || string.Equals(context.Path, "/providers", StringComparison.OrdinalIgnoreCase))
        {
            context.ResourceId = new ResourceId
            {
                SubscriptionId = "",
                Namespace = ArmConstants.ResourcesNamespace,
                TypeSegments = string.Equals(context.Path, "/providers", StringComparison.OrdinalIgnoreCase)
                    ? ["providers"]
                    : ["subscriptions"],
                NameSegments = [],
                IsCollection = true,
            };
            return RejectCollectionPost(context) ?? next(context, ct);
        }

        var trimmed = context.Path.TrimEnd('/');
        var segs = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 3
            && segs[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase)
            && segs[2].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase))
        {
            context.ResourceId = new ResourceId
            {
                SubscriptionId = segs[1],
                Namespace = ArmConstants.ResourcesNamespace,
                TypeSegments = ["resourceGroups"],
                NameSegments = [],
                IsCollection = true,
                Parent = new ResourceId
                {
                    SubscriptionId = segs[1],
                    Namespace = "",
                    TypeSegments = [],
                    NameSegments = [],
                    IsCollection = true,
                },
            };
            return RejectCollectionPost(context) ?? next(context, ct);
        }

        if (segs.Length == 3
            && segs[0].Equals("subscriptions", StringComparison.OrdinalIgnoreCase)
            && segs[2].Equals("providers", StringComparison.OrdinalIgnoreCase))
        {
            context.ResourceId = new ResourceId
            {
                SubscriptionId = segs[1],
                Namespace = ArmConstants.ResourcesNamespace,
                TypeSegments = ["providers"],
                NameSegments = [],
                IsCollection = true,
                Parent = new ResourceId
                {
                    SubscriptionId = segs[1],
                    Namespace = "",
                    TypeSegments = [],
                    NameSegments = [],
                    IsCollection = true,
                },
            };
            return RejectCollectionPost(context) ?? next(context, ct);
        }

        if (!ResourceId.TryParse(context.Path, table, out var id, out var error))
        {
            context.Error = error ?? ArmError.InvalidResourceUri("Unable to parse resource URI.");
            return default;
        }

        context.ResourceId = id;
        context.IsDataPlane = id.DataPlaneSegment is not null;
        // M11 P6 — resolve the verb's idempotency declaration HERE, because this is the only middleware
        // before the seam (Order 800) that holds the routing table. A verb that says `Idempotent: false`
        // is a REPORT rather than a create — its whole point is that the second call is a new call — and
        // the answer for it must never come out of a 24-hour replay cache. An unparseable or unknown
        // action leaves the default, which is the behaviour every existing verb has today.
        if (id.Action is { Length: > 0 } verb && table.Find(id) is { } verbReg)
        {
            var declared = verbReg.Descriptor.Actions
                .FirstOrDefault(a => a.Name.Equals(verb, StringComparison.OrdinalIgnoreCase));
            if (declared is not null)
                context.ActionIdempotent = declared.Idempotent;
        }

        var rejected = RejectCollectionPost(context);
        if (rejected is not null)
            return rejected.Value;
        if (id.Namespace.Length > 0 && !table.TryGetProvider(id.Namespace, out _))
        {
            context.Error = ArmError.NoRegisteredProviderFound($"No registered resource provider found for namespace '{id.Namespace}'.");
            return default;
        }

        var reg = table.Find(id);
        if (id.Namespace.Length > 0 && id.FullyQualifiedType.Length > 0 && reg is null
            && id.Action is null)
        {
            context.Error = ArmError.InvalidResourceType($"Resource type '{id.FullyQualifiedType}' is not valid for namespace '{id.Namespace}'.");
            return default;
        }

        return next(context, ct);
    }

    private static ValueTask? RejectCollectionPost(ArmRequestContext context)
    {
        var id = context.ResourceId;
        if (id is null)
            return null;
        if (string.Equals(context.Method, "POST", StringComparison.OrdinalIgnoreCase)
            && id.IsCollection
            && id.Action is null
            && id.DataPlaneSegment is null)
        {
            context.Error = ArmError.MethodNotAllowed("POST is not supported on a collection; create with PUT on a named resource.");
            return ValueTask.CompletedTask;
        }

        return null;
    }
}

public sealed class ApiVersionMiddleware(ArmRoutingTable table) : IArmMiddleware
{
    public int Order => 400;

    public ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        if (!TryGetApiVersion(context, out var version) || string.IsNullOrEmpty(version))
        {
            context.Error = ArmError.MissingApiVersion();
            return default;
        }

        context.ApiVersion = version;
        var id = context.ResourceId;
        var supported = new[] { ArmConstants.ApiVersion };
        if (id is not null)
        {
            var reg = table.Find(id);
            if (reg is not null)
                supported = [.. reg.Descriptor.ApiVersions];
        }

        if (!supported.Contains(version, StringComparer.OrdinalIgnoreCase))
        {
            context.Error = ArmError.InvalidApiVersion(
                $"The api-version '{version}' is invalid. Supported: {string.Join(", ", supported)}.",
                "api-version");
            return default;
        }

        return next(context, ct);
    }

    internal static bool TryGetApiVersion(ArmRequestContext context, out string version)
    {
        foreach (var (k, v) in context.Query)
        {
            if (k.Equals("api-version", StringComparison.OrdinalIgnoreCase))
            {
                version = v;
                return true;
            }
        }

        version = "";
        return false;
    }
}

public sealed class TenancyMiddleware(IArmDirectory directory) : IArmMiddleware
{
    public int Order => 500;

    public ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        var id = context.ResourceId;
        if (id is null || id.SubscriptionId.Length == 0)
            return next(context, ct);

        var creatingSubscription = context.Method is "PUT"
            && id.ResourceGroup is null
            && id.Namespace.Length == 0
            && id.TypeSegments.Length == 0;
        if (!directory.TryGetSubscription(id.SubscriptionId, out var sub))
        {
            if (!creatingSubscription)
            {
                context.Error = ArmError.SubscriptionNotFound(id.SubscriptionId);
                return default;
            }

            sub = new SubscriptionRecord { Id = id.SubscriptionId, State = SubscriptionState.Enabled };
        }

        if (sub.State is SubscriptionState.Warned or SubscriptionState.Suspended
            && context.Method is not "GET" and not "HEAD" and not "DELETE")
        {
            context.Error = ArmError.Conflict("SubscriptionNotEnabled", $"Subscription '{id.SubscriptionId}' is {sub.State}.");
            return default;
        }

        if (id.ResourceGroup is not null
            && !string.Equals(id.FullyQualifiedType, "resourceGroups", StringComparison.OrdinalIgnoreCase)
            && (id.Namespace.Length > 0 || id.TypeSegments.Length > 0)
            && !directory.TryGetResourceGroup(id.SubscriptionId, id.ResourceGroup, out _))
        {
            // creating the RG itself is allowed
            if (!(id.Namespace.Length == 0 && context.Method is "PUT"))
            {
                context.Error = ArmError.ResourceGroupNotFound(id.ResourceGroup);
                return default;
            }
        }

        return next(context, ct);
    }
}

public sealed class AuthorizationMiddleware(IAuthorizationEvaluator evaluator, ArmRoutingTable table) : IArmMiddleware
{
    public int Order => 600;

    public async ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        if (context.Principal is null || context.ResourceId is null)
        {
            context.Error = ArmError.Forbidden("No principal.");
            return;
        }

        var action = ActionString(context.Method, context.ResourceId, table);
        var scope = context.ResourceId.Canonical;
        var result = await evaluator.AuthorizeAsync(context.Principal, scope, action, context.IsDataPlane, BuildAuthorizationContext(context), ct);
        if (!result.Allowed)
        {
            context.Error = ArmError.Forbidden(
                result.DeniedBecause ?? $"The client does not have authorization to perform action '{action}' over scope '{scope}'.");
            return;
        }

        await next(context, ct);
    }

    internal static AuthorizationContext BuildAuthorizationContext(ArmRequestContext context)
    {
        var id = context.ResourceId;
        var resourceName = id is { NameSegments.Length: > 0 } ? id.NameSegments[^1] : null;

        IReadOnlyDictionary<string, string>? resourceTags = null;
        if (!context.BufferedBody.IsEmpty)
        {
            try
            {
                if (ArmBody.ParseObject(context)?["tags"] is JsonObject tags)
                {
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (key, value) in tags)
                    {
                        if (value is JsonValue v && v.TryGetValue<string>(out var text))
                            map[key] = text;
                    }

                    resourceTags = map;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // a malformed body is rejected elsewhere; conditions simply see no resource tags
            }
        }

        Dictionary<string, string>? requestTags = null;
        foreach (var (key, value) in context.Query)
        {
            if (!key.StartsWith("tags.", StringComparison.OrdinalIgnoreCase))
                continue;
            requestTags ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            requestTags[key["tags.".Length..]] = value;
        }

        return new AuthorizationContext(resourceName, resourceTags, requestTags);
    }

    internal static string ActionString(string method, ResourceId id, ArmRoutingTable table)
    {
        _ = table;
        var ns = id.Namespace.Length > 0 ? id.Namespace : ArmConstants.ResourcesNamespace;
        if (id.TypeSegments.Length >= 2
            && id.TypeSegments[0].Equals("locations", StringComparison.OrdinalIgnoreCase)
            && (id.TypeSegments[1].Equals("operations", StringComparison.OrdinalIgnoreCase)
                || id.TypeSegments[1].Equals("operationResults", StringComparison.OrdinalIgnoreCase)))
        {
            return $"{ns}/operations/read";
        }

        var type = id.FullyQualifiedType.Length > 0 ? id.FullyQualifiedType : (id.ResourceGroup is null ? "subscriptions" : "resourceGroups");
        if (id.Action is not null)
        {
            if (id.TypeSegments.Length == 0)
                return $"{ns}/{id.Action}/action";
            return $"{ns}/{type}/{id.Action}/action";
        }
        return method.ToUpperInvariant() switch
        {
            "GET" or "HEAD" => $"{ns}/{type}/read",
            "PUT" or "PATCH" => $"{ns}/{type}/write",
            "DELETE" => $"{ns}/{type}/delete",
            "POST" => $"{ns}/{type}/write",
            _ => $"{ns}/{type}/read",
        };
    }
}

public sealed class LockMiddleware(IResourceStore store) : IArmMiddleware
{
    public int Order => 650;

    public async ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        var id = context.ResourceId;
        if (id is null || context.Method is "GET" or "HEAD"
            || (id.Namespace.Equals(ArmConstants.AuthorizationNamespace, StringComparison.OrdinalIgnoreCase)
                && id.FullyQualifiedType.Equals("locks", StringComparison.OrdinalIgnoreCase)))
        {
            await next(context, ct);
            return;
        }

        var page = await store.ListAsync(new ResourceStoreQuery(
            id.SubscriptionId.Length == 0 ? null : id.SubscriptionId,
            null,
            ArmConstants.AuthorizationNamespace,
            $"{ArmConstants.AuthorizationNamespace}/locks",
            1000,
            null), ct);

        var requestScope = id.Canonical;
        foreach (var lockEnv in page.Value)
        {
            var lockScope = Burbuja.Core.Sdk.Authorization.ArmActionMatcher.ExtensionParentScope(
                lockEnv.Id, ArmConstants.AuthorizationNamespace, "locks");
            if (!Burbuja.Core.Sdk.Authorization.ArmActionMatcher.AssignmentCoversRequest(lockScope, requestScope)
                && !string.Equals(lockScope, requestScope, StringComparison.OrdinalIgnoreCase))
                continue;

            var level = lockEnv.Properties["level"]?.GetValue<string>()
                        ?? lockEnv.Properties["Level"]?.GetValue<string>()
                        ?? "";
            if (level.Equals("ReadOnly", StringComparison.OrdinalIgnoreCase)
                && context.Method is "PUT" or "PATCH" or "POST" or "DELETE")
            {
                context.Error = ArmError.ScopeLocked($"Scope '{lockScope}' is ReadOnly.");
                return;
            }

            if (level.Equals("CanNotDelete", StringComparison.OrdinalIgnoreCase) && context.Method == "DELETE")
            {
                context.Error = ArmError.ScopeLocked($"Scope '{lockScope}' is CanNotDelete.");
                return;
            }
        }

        await next(context, ct);
    }
}

public sealed class ThrottlingMiddleware(IClock clock, IOptions<ArmThrottleOptions> options) : IArmMiddleware
{
    public int Order => 700;
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Remaining, DateTimeOffset Window)> _buckets = new(StringComparer.Ordinal);

    public ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        var sub = context.ResourceId?.SubscriptionId ?? "global";
        var kind = context.Method.ToUpperInvariant() switch
        {
            "DELETE" => "deletes",
            "PUT" or "PATCH" or "POST" => "writes",
            _ => "reads",
        };
        var limit = kind switch
        {
            "deletes" => Math.Max(1, options.Value.DeletesPerMinute),
            "writes" => Math.Max(1, options.Value.WritesPerMinute),
            _ => Math.Max(1, options.Value.ReadsPerMinute),
        };
        int remaining;
        lock (_gate)
        {
            var key = $"{sub}:{kind}";
            var now = clock.UtcNow;
            if (!_buckets.TryGetValue(key, out var b) || now - b.Window >= TimeSpan.FromMinutes(1))
                b = (limit, now);
            if (b.Remaining <= 0)
            {
                context.Error = ArmError.Throttled("Too many requests.", 60);
                context.ResponseHeaders[$"x-ms-ratelimit-remaining-subscription-{kind}"] = "0";
                return default;
            }

            remaining = b.Remaining - 1;
            _buckets[key] = (remaining, b.Window);
        }

        context.ResponseHeaders[$"x-ms-ratelimit-remaining-subscription-{kind}"] = remaining.ToString();
        return next(context, ct);
    }
}

public sealed class IdempotencyMiddleware(IIdempotencyStore store, IClock clock) : IArmMiddleware
{
    public int Order => 800;

    public async ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        // M11 P6 — a verb that declared `Idempotent: false` (a probe, a heartbeat: a state REPORT) is
        // neither replayed nor stored. Skipping the lookup is as load-bearing as skipping the store: a
        // report whose body happens to repeat — and a heartbeat's does, by construction — would otherwise
        // be answered from the first response for 24 hours and the resource would stop moving while every
        // caller saw a 200. Skipping the store is what stops a per-second reporter from writing a row a
        // second: 36 268 rows in ten hours on the M10 release host.
        if (context.IsDataPlane || !context.ActionIdempotent || context.Method is "GET" or "HEAD" or "DELETE")
        {
            await next(context, ct);
            return;
        }

        var bodyHash = await HashBodyAsync(context, ct);
        var client = context.ClientRequestId;
        var principal = context.Principal?.PrincipalId ?? "";
        var id = context.ResourceId?.Canonical ?? context.Path;
        var material = string.IsNullOrEmpty(client)
            ? $"{principal}|{context.Method}|{id}|{context.ApiVersion}|{bodyHash}"
            : $"{principal}|{context.Method}|{id}|{context.ApiVersion}|{client}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();

        var existing = store.Get(key);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, bodyHash, StringComparison.Ordinal))
            {
                context.Error = ArmError.ConflictingClientRequestId("The x-ms-client-request-id has already been used with a different payload.");
                return;
            }

            context.StatusCode = existing.StatusCode;
            context.Result = CloneResult(existing.Response);
            foreach (var (h, v) in existing.Headers)
            {
                if (h.Equals("x-ms-request-id", StringComparison.OrdinalIgnoreCase)
                    || h.Equals("x-ms-correlation-request-id", StringComparison.OrdinalIgnoreCase)
                    || h.Equals("x-ms-client-request-id", StringComparison.OrdinalIgnoreCase))
                    continue;
                context.ResponseHeaders[h] = v;
            }
            return;
        }

        if (!store.TryBegin(key))
        {
            context.Error = ArmError.OperationInProgress("A request with this idempotency key is already in progress.");
            return;
        }

        try
        {
            await next(context, ct);
            if (context.Error is null && context.Result is not null)
            {
                var clone = CloneResult(context.Result);
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (h, v) in context.ResponseHeaders)
                {
                    if (h.Equals("x-ms-request-id", StringComparison.OrdinalIgnoreCase)
                        || h.Equals("x-ms-correlation-request-id", StringComparison.OrdinalIgnoreCase)
                        || h.Equals("x-ms-client-request-id", StringComparison.OrdinalIgnoreCase))
                        continue;
                    headers[h] = v;
                }

                store.Put(key, new IdempotencyRecord(
                    bodyHash,
                    clone,
                    context.StatusCode,
                    headers,
                    clock.UtcNow.AddHours(24)));
            }
        }
        finally
        {
            store.End(key);
        }
    }

    private static JsonNode? CloneResult(object? result) => result switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        _ => JsonSerializer.SerializeToNode(result, JsonWire.Options)?.DeepClone(),
    };

    private static ValueTask<string> HashBodyAsync(ArmRequestContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new(ArmBody.Sha256Hex(context));
    }
}

public sealed class ConcurrencyMiddleware(IResourceStore store) : IArmMiddleware
{
    public int Order => 900;

    public async ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        var id = context.ResourceId;
        if (id is null || id.IsCollection)
        {
            await next(context, ct);
            return;
        }

        var ifMatch = Header(context, "If-Match");
        var ifNoneMatch = Header(context, "If-None-Match");

        // The identity, not the action's key: `…/schedules/nightly/run` names an ACTION on a resource, and
        // reading the store at that key finds nothing — so a precondition on an action would answer
        // `If-Match * requires the resource to exist` about a resource that is right there.
        var identity = id.Action is null && id.DataPlaneSegment is null
            ? id
            : id with { Action = null, DataPlaneSegment = null };
        var existing = await store.GetAsync(identity.Key, ct);

        // ACTIONS ARE INCLUDED, and that is the 2026-09-13 fix. The gate used to cover PUT/PATCH/DELETE
        // only, so an `If-Match` on a POST action was accepted, passed to the handler, and ignored —
        // while the portal's stale-row guard sent exactly that header and told the human it had protected
        // them. Found by group P's fact: the page rendered a row, the schedule moved underneath it, and
        // `run` fired anyway. A header that means "only if this is still what I read" cannot mean that for
        // some verbs and nothing for others; a caller that does not want the check simply does not send it,
        // and the store read above is one the middleware already performed.
        if (context.Method is "PUT" or "PATCH" or "DELETE" or "POST" || id.Action is not null)
        {
            if (ifNoneMatch == "*" && existing is not null)
            {
                context.Error = ArmError.PreconditionFailed("If-None-Match * does not allow overwrite of an existing resource.");
                return;
            }

            if (ifMatch == "*")
            {
                if (existing is null)
                {
                    context.Error = ArmError.PreconditionFailed("If-Match * requires the resource to exist.");
                    return;
                }
            }
            else if (!string.IsNullOrEmpty(ifMatch) && existing is not null
                && !InMemoryResourceStore.ETagMatches(existing.ETag, ifMatch))
            {
                context.Error = ArmError.PreconditionFailed("The provided ETag does not match the current resource.");
                return;
            }
        }

        await next(context, ct);

        if (context.Result is IResourceEnvelope env)
            context.ResponseHeaders["ETag"] = env.ETag;
    }

    private static string? Header(ArmRequestContext context, string name) =>
        context.Headers.TryGetValue(name, out var v) ? v : null;
}

public sealed class ValidationMiddleware(ArmRoutingTable table) : IArmMiddleware
{
    public int Order => 1000;

    public async ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        if (context.Method is not "PUT" and not "PATCH" and not "POST")
        {
            await next(context, ct);
            return;
        }

        var id = context.ResourceId;
        if (id is null)
        {
            await next(context, ct);
            return;
        }

        var reg = table.Find(id);
        JsonNode? node;
        try
        {
            node = ArmBody.Parse(context);
        }
        catch
        {
            context.Error = ArmError.Validation("Request body is not valid JSON.", []);
            return;
        }

        if (id.Action is not null || (context.Method == "POST" && !id.IsCollection))
        {
            var actionName = id.Action ?? "";
            var action = reg?.Descriptor.Actions.FirstOrDefault(a => a.Name.Equals(actionName, StringComparison.OrdinalIgnoreCase));
            if (action?.RequestSchema is { } schemaEl && node is not null)
            {
                var schema = JsonSchema.FromText(schemaEl.GetRawText());
                var result = schema.Evaluate(JsonSerializer.SerializeToElement(node), new EvaluationOptions { OutputFormat = OutputFormat.List });
                if (!result.IsValid)
                {
                    context.Error = ArmError.Validation("The request content was invalid.", ToDetails(result, prefix: null));
                    return;
                }
            }

            await next(context, ct);
            return;
        }

        if (reg?.PropertiesSchema is null)
        {
            await next(context, ct);
            return;
        }

        var props = node?["properties"] ?? node;
        if (props is null)
        {
            await next(context, ct);
            return;
        }

        var eval = reg.PropertiesSchema.Evaluate(JsonSerializer.SerializeToElement(props), new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!eval.IsValid)
        {
            context.Error = ArmError.Validation("The request content was invalid.", ToDetails(eval, "/properties"));
            return;
        }

        await next(context, ct);
    }

    private static ArmError[] ToDetails(EvaluationResults result, string? prefix)
    {
        var list = new List<ArmError>();
        CollectDetails(result, prefix, list);
        if (list.Count == 0 && !result.IsValid)
            list.Add(new ArmError("InvalidRequestContent", "The request content was invalid.", prefix ?? "/", [], null, 400));
        return list.ToArray();
    }

    private static void CollectDetails(EvaluationResults node, string? prefix, List<ArmError> list)
    {
        if (!node.IsValid && node.Errors is { Count: > 0 })
        {
            var target = node.InstanceLocation.ToString();
            if (string.IsNullOrEmpty(target))
                target = "/";
            if (prefix is not null)
                target = target.StartsWith('/') ? prefix + target : prefix + "/" + target;
            list.Add(new ArmError("InvalidRequestContent", string.Join("; ", node.Errors.Select(e => e.Value)), target, [], null, 400));
        }

        if (node.Details is null)
            return;
        foreach (var child in node.Details)
            CollectDetails(child, prefix, list);
    }
}

public sealed class DispatchMiddleware(ArmRoutingTable table) : IArmMiddleware
{
    public int Order => 1100;

    public async ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        var id = context.ResourceId ?? throw new ArmException(ArmError.InvalidResourceUri("Missing resource id."));
        var reg = table.Find(id) ?? throw new ArmException(ArmError.InvalidResourceType($"No handler for {id.Canonical}."));
        var principal = context.Principal ?? throw new ArmException(ArmError.Forbidden("No principal."));
        var sp = context.Services;
        if (!TryAuthorizeCapability(context, id, reg.Descriptor))
            return;

        var top = 50;
        if (context.Query.TryGetValue("$top", out var topRaw) && int.TryParse(topRaw, out var parsedTop))
            top = Math.Clamp(parsedTop, 1, 1000);
        context.Query.TryGetValue("$skipToken", out var skip);
        if (!string.IsNullOrEmpty(skip) && !ArmSkipTokenCodec.TryDecode(skip, out _, out _))
        {
            context.Error = ArmError.InvalidSkipToken("The $skipToken value is malformed.");
            return;
        }

        context.Query.TryGetValue("$filter", out var filterRaw);
        if (!ArmFilterParser.TryParse(filterRaw, out var filter, out var filterError))
        {
            context.Error = filterError;
            return;
        }

        var envelopeMeta = ReadEnvelopeMeta(context);
        var req = new ResourceRequest
        {
            Id = id,
            ApiVersion = context.ApiVersion ?? ArmConstants.ApiVersion,
            Principal = principal,
            RequestId = context.RequestId ?? "",
            CorrelationId = context.CorrelationId,
            IfMatch = Header(context, "If-Match"),
            IfNoneMatch = Header(context, "If-None-Match"),
            Query = context.Query,
            Tags = envelopeMeta.Tags,
            Location = envelopeMeta.Location,
            Services = sp,
        };

        var method = context.Method.ToUpperInvariant();
        if (id.Action is not null || (method == "POST" && !id.IsCollection))
        {
            JsonObject? body = ArmBody.ParseObject(context);
            var action = id.Action ?? "";
            var outcome = await reg.Handler.InvokeAsync(new ResourceActionRequest
            {
                Id = id,
                ApiVersion = req.ApiVersion,
                Principal = principal,
                RequestId = req.RequestId,
                CorrelationId = req.CorrelationId,
                IfMatch = req.IfMatch,
                IfNoneMatch = req.IfNoneMatch,
                Query = req.Query,
                Services = sp,
                Action = action,
                Body = body,
            }, ct);
            context.Result = outcome;
        }
        else if (method is "GET" or "HEAD" && id.IsCollection)
        {
            var list = await reg.Handler.ListAsync(new ResourceListRequest
            {
                Id = id,
                ApiVersion = req.ApiVersion,
                Principal = principal,
                RequestId = req.RequestId,
                CorrelationId = req.CorrelationId,
                Query = req.Query,
                Services = sp,
                Top = top,
                ContinuationToken = skip,
                Filter = filterRaw,
                ParsedFilter = filter,
            }, ct);
            context.Result = list;
            context.StatusCode = 200;
        }
        else if (method is "GET" or "HEAD")
        {
            var got = await reg.Handler.GetAsync(req, ct);
            if (got is null)
            {
                context.Error = ArmError.NotFound($"Resource '{id.Canonical}' was not found.", id.Canonical);
                return;
            }

            if (IsOperationResults(id))
            {
                await WriteOperationResultAsync(context, got, ct);
                await next(context, ct);
                return;
            }

            context.Result = got;
            context.StatusCode = 200;
            if (got is IResourceEnvelope env)
                context.ResponseHeaders["ETag"] = env.ETag;
        }
        else if (method == "PUT")
        {
            var desired = ReadDesired(context, reg);
            var outcome = await reg.Handler.PutAsync(req, desired, ct);
            context.Result = outcome;
        }
        else if (method == "PATCH")
        {
            var patch = ReadPatch(context);
            var outcome = await reg.Handler.PatchAsync(req, patch, ct);
            context.Result = outcome;
        }
        else if (method == "DELETE")
        {
            var outcome = await reg.Handler.DeleteAsync(req, ct);
            context.Result = outcome;
        }
        else
        {
            context.Error = ArmError.Validation($"Method {method} is not supported.", []);
            return;
        }

        await next(context, ct);
    }

    private static bool IsOperationResults(ResourceId id) =>
        id.TypeSegments.Length >= 2
        && id.TypeSegments[0].Equals("locations", StringComparison.OrdinalIgnoreCase)
        && id.TypeSegments[1].Equals("operationResults", StringComparison.OrdinalIgnoreCase);

    private static async ValueTask WriteOperationResultAsync(ArmRequestContext context, object got, CancellationToken ct)
    {
        var ops = context.Services.GetService(typeof(IOperationTracker)) as IOperationTracker;
        var name = context.ResourceId!.NameSegments.Length > 0 ? context.ResourceId.NameSegments[^1] : "";
        var op = ops is null ? null : await ops.GetAsync(name, ct);
        if (op is null)
        {
            context.Error = ArmError.NotFound($"Operation '{name}' was not found.", name);
            return;
        }

        switch (op.Status)
        {
            case OperationStatus.Running:
                context.StatusCode = 202;
                context.Result = got;
                context.ResponseHeaders["Retry-After"] = "1";
                break;
            case OperationStatus.Succeeded:
                if (!string.IsNullOrEmpty(op.TargetResourceId)
                    && context.Services.GetService(typeof(IResourceStore)) is IResourceStore store)
                {
                    var target = await store.GetAsync(op.TargetResourceId.ToLowerInvariant(), ct);
                    context.Result = target ?? got;
                }
                else
                {
                    context.Result = got;
                }

                context.StatusCode = 200;
                if (context.Result is IResourceEnvelope env)
                    context.ResponseHeaders["ETag"] = env.ETag;
                break;
            case OperationStatus.Failed:
                context.Error = op.Error ?? ArmError.Internal(context.RequestId ?? "unknown");
                break;
            default:
                context.Error = op.Error ?? ArmError.Conflict("Canceled", "The operation was canceled.");
                break;
        }
    }

    private static bool TryAuthorizeCapability(ArmRequestContext context, ResourceId id, ResourceTypeDescriptor descriptor)
    {
        var method = context.Method.ToUpperInvariant();
        if (id.Action is not null || (method == "POST" && !id.IsCollection))
        {
            if (!descriptor.Capabilities.HasFlag(ResourceCapabilities.Actions)
                && descriptor.Actions.Count == 0)
            {
                context.Error = ArmError.ActionNotSupported($"Actions are not supported on '{descriptor.Type}'.");
                return false;
            }

            var action = id.Action ?? "";
            if (action.Length > 0 && descriptor.Actions.Count > 0
                && !descriptor.Actions.Any(a => a.Name.Equals(action, StringComparison.OrdinalIgnoreCase)))
            {
                context.Error = ArmError.ActionNotSupported($"Action '{action}' is not supported on '{descriptor.Type}'.");
                return false;
            }

            return true;
        }

        var required = method switch
        {
            "GET" or "HEAD" when id.IsCollection && id.ResourceGroup is null => ResourceCapabilities.List | ResourceCapabilities.ListBySubscription,
            "GET" or "HEAD" when id.IsCollection => ResourceCapabilities.List | ResourceCapabilities.ListByResourceGroup | ResourceCapabilities.ListBySubscription,
            "GET" or "HEAD" => ResourceCapabilities.Get,
            "PUT" => ResourceCapabilities.Put,
            "PATCH" => ResourceCapabilities.Patch,
            "DELETE" => ResourceCapabilities.Delete,
            _ => ResourceCapabilities.Get,
        };
        if ((descriptor.Capabilities & required) == 0)
        {
            context.Error = ArmError.MethodNotAllowed($"Method {method} is not allowed on '{descriptor.Type}'.");
            return false;
        }

        return true;
    }

    private static string? Header(ArmRequestContext context, string name) =>
        context.Headers.TryGetValue(name, out var v) ? v : null;

    private static object ReadDesired(ArmRequestContext context, ArmTypeRegistration reg)
    {
        var obj = ArmBody.ParseObject(context) ?? new JsonObject();
        var props = obj["properties"] ?? obj;
        var json = props.ToJsonString();
        return JsonSerializer.Deserialize(json, reg.Descriptor.PropertiesType, JsonWire.Options)
               ?? Activator.CreateInstance(reg.Descriptor.PropertiesType)!;
    }

    private static ResourcePatch ReadPatch(ArmRequestContext context)
    {
        var obj = ArmBody.ParseObject(context) ?? new JsonObject();
        Dictionary<string, string>? tags = null;
        if (obj["tags"] is JsonObject t)
            tags = t.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<string>() ?? kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
        return new ResourcePatch(tags, obj["properties"] as JsonObject);
    }

    private static (IReadOnlyDictionary<string, string> Tags, string? Location) ReadEnvelopeMeta(ArmRequestContext context)
    {
        var obj = ArmBody.ParseObject(context);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? location = null;
        if (obj is null)
            return (tags, location);
        if (obj["tags"] is JsonObject t)
        {
            foreach (var kv in t)
                tags[kv.Key] = kv.Value?.GetValue<string>() ?? kv.Value?.ToString() ?? "";
        }

        if (obj["location"] is JsonValue loc && loc.TryGetValue<string>(out var locValue))
            location = locValue;
        return (tags, location);
    }
}

public sealed class AsyncOperationMiddleware : IArmMiddleware
{
    public int Order => 1200;

    public ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct)
    {
        Normalize(context);
        return next(context, ct);
    }

    internal static void Normalize(ArmRequestContext context)
    {
        if (context.Result is IOutcomeView view)
            view.Apply(context);

        if (context.OperationId is not null)
            ApplyAccepted(context, context.ResourceId?.Canonical ?? (context.Result as IResourceEnvelope)?.Id ?? "", context.OperationId, context.RetryAfter ?? TimeSpan.FromSeconds(1), 202);

        if (context.Result is IResourceEnvelope env)
            context.ResponseHeaders["ETag"] = env.ETag;

        var result = context.Result;
        if (result is not null)
        {
            var t = result.GetType();
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(PagedResult<>))
            {
                var value = t.GetProperty("Value")?.GetValue(result);
                var next = t.GetProperty("NextLink")?.GetValue(result) as string;
                if (value is System.Collections.IEnumerable enumerable)
                {
                    var items = new List<object>();
                    foreach (var item in enumerable)
                    {
                        if (item is not null)
                            items.Add(item);
                    }

                    WritePage(context, items, next);
                }
            }
        }
    }

    private static void WritePage(ArmRequestContext context, IReadOnlyList<object> value, string? token)
    {
        context.StatusCode = 200;
        var nextLink = token is null ? null : BuildNextLink(context, token);
        context.Result = new JsonObject
        {
            ["value"] = new JsonArray(value.Select(v => JsonSerializer.SerializeToNode(v, JsonWire.Options)).ToArray()),
            ["nextLink"] = nextLink,
        };
    }

    private static void ApplyAccepted(ArmRequestContext context, string resourceId, string operationId, TimeSpan retry, int status)
    {
        context.StatusCode = status;
        var ns = context.ResourceId?.Namespace is { Length: > 0 } n ? n : ArmConstants.ResourcesNamespace;
        var sub = context.ResourceId?.SubscriptionId ?? "";
        var loc = "local";
        var opUrl = $"/subscriptions/{sub}/providers/{ns}/locations/{loc}/operations/{operationId}?api-version={ArmConstants.ApiVersion}";
        var resultUrl = $"/subscriptions/{sub}/providers/{ns}/locations/{loc}/operationResults/{operationId}?api-version={ArmConstants.ApiVersion}";
        context.ResponseHeaders["Azure-AsyncOperation"] = opUrl;
        context.ResponseHeaders["Location"] = resultUrl;
        context.ResponseHeaders["Retry-After"] = Math.Max(1, (int)retry.TotalSeconds).ToString();
    }

    private static string BuildNextLink(ArmRequestContext context, string token)
    {
        var q = new List<string> { $"api-version={context.ApiVersion}", $"$skipToken={Uri.EscapeDataString(token)}" };
        if (context.Query.TryGetValue("$top", out var top))
            q.Add($"$top={top}");
        return $"{context.Path}?{string.Join('&', q)}";
    }
}
