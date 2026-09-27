using Burbuja.Core.Abstractions.Clock;

namespace Burbuja.Core.Clock;

public sealed class TimeProviderClock(TimeProvider time) : IClock
{
    public DateTimeOffset UtcNow => time.GetUtcNow();
}
