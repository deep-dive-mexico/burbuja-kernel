using System.Text.Json.Nodes;

namespace Burbuja.Core.Abstractions.Arm;

public sealed record ArmError(
    string Code,
    string Message,
    string? Target,
    IReadOnlyList<ArmError> Details,
    JsonObject? InnerError,
    int HttpStatus)
{
    public static ArmError NotFound(string message, string? target = null) =>
        new("ResourceNotFound", message, target, [], null, 404);

    public static ArmError InvalidApiVersion(string message, string? target = null) =>
        new("InvalidApiVersionParameter", message, target, [], null, 400);

    public static ArmError MissingApiVersion() =>
        new("MissingApiVersionParameter", "The api-version query parameter is required.", "api-version", [], null, 400);

    public static ArmError UnsupportedApiVersion(string value, IReadOnlyList<string> supported) =>
        new("UnsupportedApiVersionValue", $"The api-version '{value}' is not supported. Supported: {string.Join(", ", supported)}.", "api-version", [], null, 400);

    public static ArmError Conflict(string code, string message, string? target = null) =>
        new(code, message, target, [], null, 409);

    public static ArmError PreconditionFailed(string message, string? target = null) =>
        new("PreconditionFailed", message, target, [], null, 412);

    public static ArmError Forbidden(string message, string? target = null) =>
        new("AuthorizationFailed", message, target, [], null, 403);

    public static ArmError Throttled(string message, int retryAfterSeconds) =>
        new("TooManyRequests", message, null, [], new JsonObject { ["retryAfter"] = retryAfterSeconds }, 429);

    public static ArmError Validation(string message, IReadOnlyList<ArmError> details) =>
        new("InvalidRequestContent", message, null, details, null, 400);

    public static ArmError Internal(string requestId) =>
        new("InternalServerError", "An internal error occurred.", requestId, [], null, 500);

    public static ArmError InvalidResourceUri(string message) =>
        new("InvalidResourceUri", message, null, [], null, 400);

    public static ArmError NoRegisteredProviderFound(string message) =>
        new("NoRegisteredProviderFound", message, null, [], null, 400);

    public static ArmError InvalidResourceType(string message) =>
        new("InvalidResourceType", message, null, [], null, 400);

    public static ArmError AuthenticationFailed(string message) =>
        new("AuthenticationFailed", message, null, [], null, 401);

    public static ArmError SubscriptionNotFound(string subscriptionId) =>
        new("SubscriptionNotFound", $"Subscription '{subscriptionId}' was not found.", subscriptionId, [], null, 404);

    public static ArmError ResourceGroupNotFound(string resourceGroup) =>
        new("ResourceGroupNotFound", $"Resource group '{resourceGroup}' was not found.", resourceGroup, [], null, 404);

    public static ArmError InvalidFilter(string message) =>
        new("InvalidFilter", message, "$filter", [], null, 400);

    public static ArmError ConflictingClientRequestId(string message) =>
        new("ConflictingClientRequestId", message, "x-ms-client-request-id", [], null, 409);

    public static ArmError OperationInProgress(string message) =>
        new("OperationInProgress", message, null, [], null, 409);

    public static ArmError MethodNotAllowed(string message) =>
        new("MethodNotAllowed", message, null, [], null, 405);

    public static ArmError ActionNotSupported(string message) =>
        new("ActionNotSupported", message, null, [], null, 400);

    public static ArmError ScopeLocked(string message) =>
        new("ScopeLocked", message, null, [], null, 409);

    public static ArmError InvalidSkipToken(string message) =>
        new("InvalidSkipToken", message, "$skipToken", [], null, 400);
}
