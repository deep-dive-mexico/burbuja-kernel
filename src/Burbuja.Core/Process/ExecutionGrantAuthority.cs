using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Process;

namespace Burbuja.Core.Process;

/// <summary>
/// Mints <see cref="ExecutionGrant"/>s. Stays in the kernel (not the SDK) because the grant
/// constructor is internal to <c>Burbuja.Core</c> — only the kernel may mint proof of gating.
/// </summary>
public sealed class ExecutionGrantAuthority(IClock clock) : IExecutionGrantAuthority
{
    public ExecutionGrant Mint(string subjectId, string decisionKey, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decisionKey);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl));
        return new ExecutionGrant(subjectId, decisionKey, clock.UtcNow + ttl);
    }
}
