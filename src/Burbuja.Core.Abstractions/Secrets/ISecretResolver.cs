namespace Burbuja.Core.Abstractions.Secrets;

public interface ISecretResolver
{
    ValueTask<string?> ResolveAsync(string name, CancellationToken ct);
}
