namespace Burbuja.Core.Abstractions.Leases;

public sealed record LeaseAcquisition(string Resource, string Owner, string FencingToken, DateTimeOffset ExpiresAt);

public interface ILeaseStore
{
    ValueTask<LeaseAcquisition?> AcquireAsync(string resource, string owner, TimeSpan ttl, CancellationToken ct);
    ValueTask<bool> RenewAsync(string resource, string owner, string fencingToken, TimeSpan ttl, CancellationToken ct);
    ValueTask ReleaseAsync(string resource, string owner, string fencingToken, CancellationToken ct);
    ValueTask<LeaseAcquisition?> GetAsync(string resource, CancellationToken ct);
    ValueTask<IReadOnlyList<LeaseAcquisition>> ListExpiredAsync(CancellationToken ct);
}
