using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Microsoft.Extensions.Logging;

namespace Burbuja.Core.Arm;

public sealed class ArmPipeline(
    IEnumerable<IArmMiddleware> middleware,
    ILogger<ArmPipeline> log,
    IArmActivityLog? activity = null,
    IClock? clock = null)
{
    /// <summary>
    /// The status a request is recorded with when the CALLER went away. The ARM vocabulary has no code for
    /// it — `RunAsync` rethrows a cancellation rather than answering one — so it is named here, once, so
    /// that an aborted request is distinguishable from a served one in the ring.
    /// </summary>
    public const int AbortedStatus = 499;

    public const string AbortedCode = "RequestAborted";

    private readonly IArmMiddleware[] _middleware = middleware.OrderBy(m => m.Order).ToArray();

    public IReadOnlyList<IArmMiddleware> Middleware => _middleware;

    public ValueTask RunAsync(ArmRequestContext context, CancellationToken ct) =>
        RunThroughAsync(context, int.MaxValue, ct);

    public async ValueTask RunThroughAsync(ArmRequestContext context, int maxOrder, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await InvokeAt(0, context, maxOrder, ct);
            if (context.Error is not null)
                ApplyError(context, context.Error);
        }
        catch (ArmException ex)
        {
            ApplyError(context, ex.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Record(context, AbortedStatus, AbortedCode, started);
            throw;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "ARM pipeline failed request {RequestId}", context.RequestId);
            ApplyError(context, ArmError.Internal(context.RequestId ?? "unknown"));
        }

        Record(context, null, null, started);
    }

    /// <summary>
    /// One activity row, written HERE rather than by a middleware.
    ///
    /// <para><b>A middleware could not do it, and the fact that says so is in the tests.</b> A wrapping
    /// middleware's `finally` runs while the exception is still travelling, so a request whose handler threw
    /// was recorded as **200** — the default status — a moment before this method turned the throw into a
    /// 500. Anything that has to see the answer the caller actually got belongs at the end of the pipeline,
    /// which is the only place that knows it.</para>
    /// </summary>
    private void Record(ArmRequestContext context, int? status, string? code, long started)
    {
        if (activity is null)
            return;

        var finalStatus = status ?? context.Error?.HttpStatus ?? context.StatusCode;
        activity.Record(new ArmActivityRow(
            At: clock?.UtcNow ?? DateTimeOffset.UtcNow,
            DurationMs: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Method: context.Method,
            Path: context.Path,
            ResourceId: context.ResourceId?.Canonical,
            PrincipalId: context.Principal?.PrincipalId,
            PrincipalType: context.Principal?.PrincipalType,
            ApiVersion: context.ApiVersion,
            Status: finalStatus,
            ErrorCode: code ?? context.Error?.Code,
            RequestId: context.RequestId,
            CorrelationId: context.CorrelationId,
            IsDataPlane: context.IsDataPlane));
    }

    private async ValueTask InvokeAt(int index, ArmRequestContext context, int maxOrder, CancellationToken ct)
    {
        if (context.Error is not null || index >= _middleware.Length)
            return;
        if (_middleware[index].Order > maxOrder)
            return;

        await _middleware[index].InvokeAsync(
            context,
            (ctx, token) => InvokeAt(index + 1, ctx, maxOrder, token),
            ct);
    }

    private static void ApplyError(ArmRequestContext context, ArmError error)
    {
        context.Error = error;
        context.StatusCode = error.HttpStatus;
        context.ResponseHeaders["x-ms-error-code"] = error.Code;
        if (error.HttpStatus == 401)
            context.ResponseHeaders["WWW-Authenticate"] = "Bearer, ApiKey";
        if (error.HttpStatus == 429 && error.InnerError is not null
            && error.InnerError.TryGetPropertyValue("retryAfter", out var retry)
            && retry is not null)
        {
            context.ResponseHeaders["Retry-After"] = retry.ToString();
        }

        context.Result = ArmErrorEnvelope.From(error);
    }
}
