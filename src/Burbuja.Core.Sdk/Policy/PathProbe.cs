using System.Runtime.CompilerServices;
using Burbuja.Core.Abstractions.Policy;

namespace Burbuja.Core.Sdk.Policy;

internal static class SdkModuleInit
{
#pragma warning disable CA2255 // library boot: timeout must exist before compiled Regex fields run
    [ModuleInitializer]
    internal static void Init() =>
        AppContext.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromMilliseconds(250));
#pragma warning restore CA2255
}

public sealed class RealPathProbe : IPathProbe
{
    public static RealPathProbe Shared { get; } = new();

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public string RealPath(string path) => PathClassifier.LexicalRealPath(path);

    public bool Contains(string parent, string child)
    {
        var r = PathClassifier.FoldForCompare(PathClassifier.LexicalRealPath(parent));
        var t = PathClassifier.FoldForCompare(PathClassifier.LexicalRealPath(child));
        var sep = Path.DirectorySeparatorChar;
        return t == r || t.StartsWith(r + sep, StringComparison.Ordinal);
    }
}

public sealed class InMemoryPathProbe : IPathProbe
{
    private readonly List<string> _existing = [];

    public InMemoryPathProbe(params string[] existing) => _existing.AddRange(existing.Select(Normalize));

    public void Add(string path) => _existing.Add(Normalize(path));

    public bool Exists(string path)
    {
        var n = Normalize(path);
        return _existing.Any(e => e == n || e.StartsWith(n + "/", StringComparison.Ordinal) || n.StartsWith(e + "/", StringComparison.Ordinal));
    }

    public string RealPath(string path) => Normalize(path);

    public bool Contains(string parent, string child)
    {
        var r = Normalize(parent).TrimEnd('/');
        var t = Normalize(child).TrimEnd('/');
        return t == r || t.StartsWith(r + "/", StringComparison.Ordinal);
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
