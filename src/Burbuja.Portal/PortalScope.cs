using System.Reflection;
using Burbuja.Portal.Components.Layout;

namespace Burbuja.Portal;

/// <summary>A portal's document, routes and policy, registered by the application that owns it.</summary>
public sealed record PortalScope(string Id, string Prefix, string Lang, string Title)
{
    public static readonly PortalScope Kernel = new("kernel", "/portal", "en", "Burbuja");

    public string Home => Prefix;
    public string SignInPath => Prefix + "/signin";
    public string SignOutPath => Prefix + "/signout";
    public string NotFoundPath => Prefix + "/not-found";
    public string Policy => Id;
    public bool IsSignIn(string? path) => string.Equals(path?.Split('?', '#')[0].TrimEnd('/'), SignInPath, StringComparison.OrdinalIgnoreCase);
    private bool IsAuthDoor(string path) => new[] { SignInPath, SignOutPath }.Any(door =>
        path.Equals(door, StringComparison.OrdinalIgnoreCase) || path.StartsWith(door + "/", StringComparison.OrdinalIgnoreCase));
    public Type Layout { get; init; } = typeof(MainLayout);
    public Assembly Assembly { get; init; } = typeof(PortalMarker).Assembly;
    public IReadOnlyList<string> Stylesheets { get; init; } = ["_content/Burbuja.Portal/portal-kernel.css"];
    public IReadOnlyList<string> Scripts { get; init; } = [];
    public string Favicon { get; init; } = "_content/Burbuja.Portal/favicon.svg";
    public string ThemeKey { get; init; } = "burbuja.theme";
    public string DefaultTheme { get; init; } = "system";
    public string? DensityKey { get; init; }
    public bool IsDefault { get; init; }
    public IReadOnlyList<PortalRedirect> Redirects { get; init; } = [];
    public string ReconnectTitle { get; init; } = "Reconnecting to the host…";
    public string ReconnectMessage { get; init; } = "The connection was interrupted. Your work is held by the kernel.";
    public string ReloadLabel { get; init; } = "Reload now";

    public bool Contains(string? path) => path is not null
        && (path.Equals(Prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Return paths must stay under this door, including after browser path normalization.</summary>
    public bool AcceptsReturn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\') || value.Any(char.IsControl) || !HasValidEscapes(value))
            return false;
        var path = value.Split('?', '#')[0];
        // Encoded controls cannot enter a Location header. Check nested path escaping as well, rather
        // than depending on how many decoding passes a proxy/router performs. Query values remain data.
        if (Uri.UnescapeDataString(value).Any(char.IsControl))
            return false;
        var decoded = path;
        while (true)
        {
            var next = Uri.UnescapeDataString(decoded);
            if (next.Contains('\\') || next.Any(char.IsControl) || next.Split('/').Any(s => s is "." or "..") || IsAuthDoor(next))
                return false;
            if (next == decoded) break;
            decoded = next;
        }
        return Contains(path);
    }

    private static bool HasValidEscapes(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') continue;
            if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
                return false;
            i += 2;
        }
        return true;
    }

    internal static bool IsLiteralPrefix(string prefix) => prefix.StartsWith('/')
        && prefix.Length > 1
        && prefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~')
        && prefix[1..].Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
}

public sealed record PortalRedirect(string Old, string New, bool Prefix = false);

/// <summary>An immutable registry per host. Unknown paths remain kernel-policy guarded.</summary>
public sealed class PortalRegistry
{
    public PortalRegistry(IEnumerable<PortalScope> definitions)
    {
        var all = definitions.ToList();
        if (!all.Any(s => s.Id.Equals(PortalScope.Kernel.Id, StringComparison.OrdinalIgnoreCase)))
            all.Insert(0, PortalScope.Kernel);
        if (all.GroupBy(s => s.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)
            || all.GroupBy(s => s.Prefix, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
            throw new InvalidOperationException("Portal ids and prefixes must be unique.");
        foreach (var scope in all)
        {
            if (string.IsNullOrWhiteSpace(scope.Id) || !PortalScope.IsLiteralPrefix(scope.Prefix)
                || !typeof(Microsoft.AspNetCore.Components.LayoutComponentBase).IsAssignableFrom(scope.Layout))
                throw new InvalidOperationException($"Invalid portal definition '{scope.Id}'.");
        }
        // Disjoint path spaces make both policies and local-return validation unambiguous. A child portal
        // cannot take over a path that its parent's sign-in would otherwise accept as a return URL.
        for (var i = 0; i < all.Count; i++)
            for (var j = i + 1; j < all.Count; j++)
                if (all[i].Contains(all[j].Prefix) || all[j].Contains(all[i].Prefix))
                    throw new InvalidOperationException($"Portal prefixes '{all[i].Prefix}' and '{all[j].Prefix}' overlap.");
        if (all.Count(s => s.IsDefault) > 1)
            throw new InvalidOperationException("Only one portal may be the default landing page.");
        All = all.AsReadOnly();
        Default = all.SingleOrDefault(s => s.IsDefault) ?? all.Single(s => s.Id.Equals(PortalScope.Kernel.Id, StringComparison.OrdinalIgnoreCase));
        AdditionalAssemblies = all.Select(s => s.Assembly).Distinct()
            .Where(a => a != typeof(PortalMarker).Assembly).ToArray();
    }

    public IReadOnlyList<PortalScope> All { get; }
    public PortalScope Default { get; }
    public Assembly[] AdditionalAssemblies { get; }
    public PortalScope? ById(string? id) => All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    public PortalScope Of(PathString path) => Of(path.Value);
    public PortalScope Of(string? path)
    {
        if (path is not null && Uri.TryCreate(path, UriKind.Absolute, out var absolute))
            path = absolute.AbsolutePath;
        path = path?.Split('?', '#')[0];
        return All.OrderByDescending(s => s.Prefix.Length).FirstOrDefault(s => s.Contains(path))
            ?? ById(PortalScope.Kernel.Id)!;
    }
    public bool SamePortal(string? left, string? right) => Of(left).Id == Of(right).Id;
}
