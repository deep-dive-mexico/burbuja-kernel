using System.Text;
using Burbuja.Core.InMemory;
using Burbuja.Core.Redaction;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.InMemory;

public sealed class ArtifactStoreTests
{
    [Fact]
    public void Resolve_rejects_parent_traversal()
    {
        var root = NewRoot();
        try
        {
            var store = new LocalFileArtifactStore(root, new CaptureGroupRedactor());
            Should.Throw<ArgumentException>(() => store.Resolve("../secret"));
            Should.Throw<ArgumentException>(() => store.Resolve("ok/../../outside"));
            Should.Throw<ArgumentException>(() => store.Resolve("\0bad"));
            Should.Throw<ArgumentException>(() => store.Resolve(""));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Nested_key_stays_under_root()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = NewRoot();
        try
        {
            var store = new LocalFileArtifactStore(root, new CaptureGroupRedactor());
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
            await store.PutAsync("nested/a.txt", content, "text/plain", null, ct);
            (await store.ExistsAsync("nested/a.txt", ct)).ShouldBeTrue();
            var blob = await store.GetAsync("nested/a.txt", ct);
            blob.ShouldNotBeNull();
            using var reader = new StreamReader(blob!.Content);
            (await reader.ReadToEndAsync(ct)).ShouldBe("hello");
            Path.GetFullPath(store.Resolve("nested/a.txt")).StartsWith(Path.GetFullPath(root), StringComparison.Ordinal).ShouldBeTrue();
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Text_put_is_redacted()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = NewRoot();
        try
        {
            var store = new LocalFileArtifactStore(root, new CaptureGroupRedactor());
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes("token=AKIAIOSFODNN7EXAMPLE"));
            await store.PutAsync("note.txt", content, "text/plain", null, ct);
            var blob = await store.GetAsync("note.txt", ct);
            using var reader = new StreamReader(blob!.Content);
            var text = await reader.ReadToEndAsync(ct);
            text.ShouldNotContain("AKIAIOSFODNN7EXAMPLE");
            text.ShouldContain("[redacted:");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "burbuja-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
