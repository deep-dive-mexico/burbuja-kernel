using Burbuja.TestKit;
using Shouldly;
using Xunit;

namespace Burbuja.Integrations.UnitTests;

/// <summary>
/// X45 (M16 M-R3) — the E2E lane's PUBLISHED HOST is not cached forever.
///
/// <para><b>The measured failure.</b> `HostPublisher.EnsurePublished` published once into `artifacts/host`
/// (or `artifacts/hostonce`) and returned early whenever `Burbuja.Host.dll` existed — no fingerprint, no
/// mtime check, no version. In lane K's M16 round, after M-R1 removed the AKOS group from the kernel rail,
/// the browser fact `G16` still read the M15 rail because the cached publish was nineteen minutes older than
/// the commit: the fact was RED AGAINST A BINARY and read exactly like a product regression.
/// `scripts/test-e2e.sh` now drops the directory per run, which fixes the lane it runs; this is the durable
/// half, so every caller (`test-compose.sh`, `test-compose-local.sh`, `test-live-local.sh` too) inherits
/// it. The facts below drive the staleness rule the publish decision is made on.</para>
/// </summary>
public sealed class HostPublisherTests
{
    /// <summary>A binary older than the source is stale: reusing it measures the tree it was built from.</summary>
    [Fact]
    public void A_publish_older_than_the_tree_is_stale()
    {
        using var tree = new TempTree();
        var artifacts = tree.Dir("artifacts/host");
        var dll = tree.Write("artifacts/host/Burbuja.Host.dll", "published");
        var source = tree.Write("src/Burbuja.Host/Program.cs", "// the tree moved");

        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow);

        HostPublisher.IsStale(artifacts, tree.Root).ShouldBeTrue(
            "the binary predates the source it was built from; using it would measure yesterday's tree");
    }

    /// <summary>A binary newer than every input is the case the cache exists for: the early return is right.</summary>
    [Fact]
    public void A_publish_newer_than_the_tree_is_reused()
    {
        using var tree = new TempTree();
        var artifacts = tree.Dir("artifacts/host");
        var dll = tree.Write("artifacts/host/Burbuja.Host.dll", "published");
        var source = tree.Write("src/Burbuja.Host/Program.cs", "// unchanged");

        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow);

        HostPublisher.IsStale(artifacts, tree.Root).ShouldBeFalse(
            "a publish newer than every input is what a per-run cache is for");
    }

    /// <summary>No publish at all is always stale — the first run's case, and the one the old code got right.</summary>
    [Fact]
    public void A_missing_publish_is_always_stale()
    {
        using var tree = new TempTree();
        var artifacts = tree.Dir("artifacts/host");
        tree.Write("src/Burbuja.Host/Program.cs", "// no publish yet");

        HostPublisher.IsStale(artifacts, tree.Root).ShouldBeTrue(
            "there is nothing to measure, so the publish has to run");
    }

    /// <summary>
    /// The build's own output is not the publish's input: a fresh `obj/` or `bin/` write must not make a
    /// fresh publish look stale, or every round after a `dotnet build` would republish for no reason.
    /// </summary>
    [Fact]
    public void Build_output_does_not_make_a_fresh_publish_stale()
    {
        using var tree = new TempTree();
        var artifacts = tree.Dir("artifacts/host");
        var dll = tree.Write("artifacts/host/Burbuja.Host.dll", "published");
        var source = tree.Write("src/Burbuja.Host/Program.cs", "// source");
        var obj = tree.Write("src/Burbuja.Host/obj/Debug/net10.0/Host.generated.cs", "// generated");
        var bin = tree.Write("src/Burbuja.Host/bin/Release/net10.0/Burbuja.Host.dll", "built");

        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(obj, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(bin, DateTime.UtcNow);

        HostPublisher.IsStale(artifacts, tree.Root).ShouldBeFalse(
            "bin/ and obj/ are the build's output, not the publish's input");
    }

    /// <summary>The repository's own build files count: a package pin can change what the Host publishes.</summary>
    [Fact]
    public void A_changed_package_pin_makes_the_publish_stale()
    {
        using var tree = new TempTree();
        var artifacts = tree.Dir("artifacts/host");
        var dll = tree.Write("artifacts/host/Burbuja.Host.dll", "published");
        var pins = tree.Write("Directory.Packages.props", "<Project />");

        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(pins, DateTime.UtcNow);

        HostPublisher.IsStale(artifacts, tree.Root).ShouldBeTrue(
            "Directory.Packages.props is an input to the publish, like every other caller of the lock file");
    }

    [Fact]
    public void A_consumer_assembly_is_checked_instead_of_a_neighboring_kernel_host()
    {
        using var tree = new TempTree();
        var artifacts = tree.Dir("artifacts/host");
        var kernel = tree.Write("artifacts/host/Burbuja.Host.dll", "kernel");
        File.SetLastWriteTimeUtc(kernel, DateTime.UtcNow.AddMinutes(1));
        HostPublisher.IsStale(artifacts, tree.Root, "Consumer.Host.dll").ShouldBeTrue();

        var consumer = tree.Write("artifacts/host/Consumer.Host.dll", "consumer");
        var source = tree.Write("src/Consumer.Host/Program.cs", "// source");
        File.SetLastWriteTimeUtc(consumer, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow);
        HostPublisher.IsStale(artifacts, tree.Root, "Consumer.Host.dll").ShouldBeTrue();

        File.SetLastWriteTimeUtc(consumer, DateTime.UtcNow.AddMinutes(1));
        HostPublisher.IsStale(artifacts, tree.Root, "Consumer.Host.dll").ShouldBeFalse();
    }

    [Fact]
    public void Consumer_plugin_source_is_a_publish_input_but_its_build_output_is_not()
    {
        using var tree = new TempTree();
        var artifacts = tree.Dir("artifacts/host");
        var dll = tree.Write("artifacts/host/Consumer.Host.dll", "published");
        var plugin = tree.Write("plugins/Fixture/Response.cs", "// changed");
        var built = tree.Write("plugins/Fixture/bin/Debug/net10.0/Fixture.dll", "built");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(-1));
        File.SetLastWriteTimeUtc(plugin, DateTime.UtcNow);
        HostPublisher.IsStale(artifacts, tree.Root, "Consumer.Host.dll").ShouldBeTrue();

        File.SetLastWriteTimeUtc(plugin, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(built, DateTime.UtcNow);
        HostPublisher.IsStale(artifacts, tree.Root, "Consumer.Host.dll").ShouldBeFalse();
    }

    [Fact]
    public void An_explicit_project_outside_src_tracks_its_inputs_and_excludes_its_publish_directory()
    {
        using var tree = new TempTree();
        var artifacts = tree.Dir("apps/Consumer/output");
        tree.Write("apps/Consumer/Entry.csproj", "<Project />");
        var source = tree.Write("apps/Consumer/Program.cs", "// changed");
        var dll = tree.Write("apps/Consumer/output/Consumer.Host.dll", "published");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(-5));
        HostPublisher.IsStale(artifacts, tree.Root, "Consumer.Host.dll", "apps/Consumer/Entry.csproj").ShouldBeTrue();

        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(1));
        var output = tree.Write("apps/Consumer/output/other.dll", "dependency");
        File.SetLastWriteTimeUtc(output, DateTime.UtcNow.AddMinutes(2));
        HostPublisher.IsStale(artifacts, tree.Root, "Consumer.Host.dll", "apps/Consumer/Entry.csproj").ShouldBeFalse();
    }

    [Fact]
    public void Root_discovery_accepts_an_arbitrary_solution_or_an_explicit_solutionless_root()
    {
        using var tree = new TempTree();
        var nested = tree.Dir("tests/Consumer.Tests/bin/Debug/net10.0");
        RepoLayout.FindRoot(root: tree.Root).ShouldBe(tree.Root);
        tree.Write("Consumer.slnx", "<Solution />");
        RepoLayout.FindRoot(startDirectory: nested).ShouldBe(tree.Root);
        RepoLayout.FindRoot(startDirectory: nested, solutionFileName: "Consumer.slnx").ShouldBe(tree.Root);
    }

    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "burbuja-host-publish-" + Guid.NewGuid().ToString("N"));

        public TempTree() => System.IO.Directory.CreateDirectory(Root);

        public string Dir(string relative)
        {
            var path = Path.Combine(Root, relative);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        public string Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
