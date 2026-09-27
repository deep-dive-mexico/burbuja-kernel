namespace Burbuja.Core.Plugin;

public sealed class PluginBootException : Exception
{
    public const int ExitCode = 78;

    public IReadOnlyList<string> Errors { get; }

    public PluginBootException(IReadOnlyList<string> errors)
        : base("Plugin boot failed:\n" + string.Join("\n", errors))
    {
        Errors = errors;
    }
}
