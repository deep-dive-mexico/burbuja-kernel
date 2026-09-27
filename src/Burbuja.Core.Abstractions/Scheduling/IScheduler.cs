namespace Burbuja.Core.Abstractions.Scheduling;

public interface IScheduler
{
    void Schedule(string id, DateTimeOffset due, Func<CancellationToken, ValueTask> work, TimeSpan? every = null);
    void Cancel(string id);
}
