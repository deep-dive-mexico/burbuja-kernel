namespace Burbuja.Core.Abstractions.Store;

public interface IUnitOfWork
{
    ValueTask CommitAsync(CancellationToken ct);
}

public interface ICache
{
    ValueTask<T?> GetAsync<T>(string key, CancellationToken ct);
    ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken ct);
    ValueTask<bool> TryAddAsync<T>(string key, T value, TimeSpan? ttl, CancellationToken ct);
}

public interface IDecisionCache<T>
{
    ValueTask<T?> GetAsync(string streamId, string decisionKey, CancellationToken ct);
    ValueTask SetAsync(string streamId, string decisionKey, T value, CancellationToken ct);
}
