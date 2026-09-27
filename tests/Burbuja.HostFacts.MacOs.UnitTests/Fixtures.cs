namespace Burbuja.HostFactsMacOs.UnitTests;

/// <summary>Reads a synthetic command output from <c>fixtures/</c> (copied next to the test assembly).
/// See <c>fixtures/PROVENANCE.md</c> for the authored examples and their parser cases.</summary>
internal static class Fixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
}
