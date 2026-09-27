namespace Burbuja.Core.Abstractions.Capacity;

public interface ICapacityGovernor
{
    int Configured { get; }
    int Current { get; }
}

public interface IThrottleObserver
{
    ValueTask RecordAsync(CancellationToken ct);
}
