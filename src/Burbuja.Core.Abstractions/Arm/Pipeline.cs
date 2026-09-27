namespace Burbuja.Core.Abstractions.Arm;

public sealed class ArmRequestContext
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public Stream? Body { get; init; }
    public ReadOnlyMemory<byte> BufferedBody { get; set; }
    public required IServiceProvider Services { get; init; }

    public string? RequestId { get; set; }
    public string? ClientRequestId { get; set; }
    public string? CorrelationId { get; set; }
    public ArmPrincipal? Principal { get; set; }
    public ResourceId? ResourceId { get; set; }
    public string? ApiVersion { get; set; }
    public bool IsDataPlane { get; set; }

    /// <summary>
    /// M11 P6 — false when the action this request invokes declared <c>Idempotent: false</c>, in which case
    /// the idempotency seam is skipped entirely for it. Set by <c>ResourceIdParseMiddleware</c> (Order 300),
    /// the first middleware that can resolve the declaration, and read by <c>IdempotencyMiddleware</c>
    /// (Order 800). Defaults to true so every request to a verb that did not opt out keeps the replay it had.
    /// </summary>
    public bool ActionIdempotent { get; set; } = true;

    public int StatusCode { get; set; } = 200;
    public Dictionary<string, string> ResponseHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public object? Result { get; set; }
    public ArmError? Error { get; set; }
    public string? OperationId { get; set; }
    public TimeSpan? RetryAfter { get; set; }
}

public interface IArmMiddleware
{
    int Order { get; }
    ValueTask InvokeAsync(ArmRequestContext context, Func<ArmRequestContext, CancellationToken, ValueTask> next, CancellationToken ct);
}
