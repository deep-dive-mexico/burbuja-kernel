namespace Burbuja.Core.Abstractions.Clock;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
