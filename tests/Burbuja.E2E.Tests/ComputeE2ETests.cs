// Adapted from burbuja-server 97940d9; the fixture here is the independent generic kernel host.
using System.Net;
using System.Text.Json.Nodes;
using Burbuja.TestKit;
using Shouldly;
using Xunit;

namespace Burbuja.E2E.Tests;

/// <summary>Group B: the Burbuja.Compute workspace lifecycle through the published Host.</summary>
[Trait("Category", "E2E")]
public sealed class ComputeE2ETests(SharedServerFixture server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Workspace_provisions_runs_a_command_and_deallocates()
    {
        using var client = server.Client();
        var name = server.NewName("ws");
        var path = WorkspacePath(name);

        using var put = await client.PutAsync(path, Spec(), Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await WaitForStateAsync(client, path, "running");

        using var exec = await client.PostAsync(path + "/exec", """{"command":"echo hello-from-compute"}""", Ct);
        exec.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await ArmTestClient.ReadJsonAsync(exec, Ct);
        body!["stdout"]!.GetValue<string>().ShouldContain("hello-from-compute");

        using var deallocate = await client.PostAsync(path + "/deallocate", "{}", Ct);
        deallocate.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitForStateAsync(client, path, "deallocated");
    }

    [Fact]
    public async Task Hard_denied_exec_is_403_and_never_runs()
    {
        using var client = server.Client();
        var name = server.NewName("ws");
        var path = WorkspacePath(name);

        using var put = await client.PutAsync(path, Spec(), Ct);
        put.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitForStateAsync(client, path, "running");

        // A sentinel inside the compute root must survive a denied destructive command.
        var sentinel = Path.Combine(server.ComputeRoot ?? Path.GetTempPath(), "sentinel.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        await File.WriteAllTextAsync(sentinel, "keep", Ct);

        using var exec = await client.PostAsync(path + "/exec", """{"command":"rm -rf /"}""", Ct);
        exec.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await ArmTestClient.ReadJsonAsync(exec, Ct);
        body!["error"]!["code"]!.GetValue<string>().ShouldBe("PolicyDenied");
        File.Exists(sentinel).ShouldBeTrue("a denied command must not run");
        (await File.ReadAllTextAsync(sentinel, Ct)).ShouldBe("keep");
    }

    [Fact]
    public async Task Catalogue_lists_backends_images_and_sizes()
    {
        using var client = server.Client();
        using var backends = await client.GetAsync($"/subscriptions/local/resourceGroups/dev/providers/Burbuja.Compute/backends", Ct);
        backends.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ArmTestClient.ReadJsonAsync(backends, Ct);
        var kinds = json!["value"]!.AsArray().Select(v => v!["name"]!.GetValue<string>()).ToList();
        kinds.ShouldContain("local-directory");

        using var images = await client.GetAsync($"/subscriptions/local/resourceGroups/dev/providers/Burbuja.Compute/images", Ct);
        images.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static string WorkspacePath(string name) =>
        $"/subscriptions/local/resourceGroups/dev/providers/Burbuja.Compute/workspaces/{name}";

    private static string Spec() =>
        """{"properties":{"displayName":"e2e","backend":"local-directory","image":"ubuntu-24.04","size":"small"}}""";

    private static async Task WaitForStateAsync(ArmTestClient client, string path, string expected)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        string? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync(path, Ct);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await ArmTestClient.ReadJsonAsync(response, Ct);
                last = body!["properties"]!["state"]!.GetValue<string>();
                if (string.Equals(last, expected, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            await Task.Delay(300, Ct);
        }

        throw new TimeoutException($"workspace never reached '{expected}' (last '{last}')");
    }
}
