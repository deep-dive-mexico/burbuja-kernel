namespace Burbuja.Plugins.Test.FakeDep;

/// <summary>Stand-in for a Microsoft.Extensions.* assembly the host does not ship.</summary>
public static class Zzz
{
    public static string Hello() => "zzz";
}
