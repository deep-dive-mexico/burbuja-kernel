using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Xunit;
using static Burbuja.Hosting.Tests.WorkbenchFixture;

namespace Burbuja.Hosting.Tests;

public sealed class ExecutableSmokeTests
{
    [Fact]
    public async Task Consumer_entrypoint_runs_from_an_unrelated_working_directory_with_a_locally_installed_plugin()
    {
        await using var fixture = new WorkbenchFixture();
        var executableDirectory = Path.Combine(fixture.Root, "installed host with spaces");
        CopyDirectory(Path.Combine(AppContext.BaseDirectory, "host-fixture"), executableDirectory);
        CopyDirectory(fixture.PluginDirectory, Path.Combine(executableDirectory, "plugins", Namespace));
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(executableDirectory, "Example.Workbench.Host.dll"));
        // Let Kestrel claim the port atomically. No free-port probe or fixed-port collision.
        start.ArgumentList.Add("--urls=http://127.0.0.1:0");
        start.ArgumentList.Add("--Kernel:Seed=false");
        start.ArgumentList.Add("--Workspace:RepoRoot=" + fixture.Root);
        start.ArgumentList.Add("--Workspace:WorktreeRoot=" + Path.Combine(fixture.Root, "worktrees"));
        foreach (var key in start.Environment.Keys.Where(key =>
                     key.StartsWith("BURBUJA_", StringComparison.OrdinalIgnoreCase)
                     || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                     || key.Contains("__", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        var address = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new StringBuilder();
        void Capture(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is not { } line)
                return;
            lock (log)
                log.AppendLine(line);
            const string marker = "Now listening on: ";
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && Uri.TryCreate(line[(index + marker.Length)..].Trim(), UriKind.Absolute, out var uri))
                address.TrySetResult(uri);
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += Capture;
        process.ErrorDataReceived += Capture;
        process.Exited += (_, _) => address.TrySetException(new InvalidOperationException("Consumer exited before announcing a listener."));
        Assert.True(process.Start());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            Uri baseAddress;
            try
            {
                baseAddress = await address.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            }
            catch (Exception ex)
            {
                lock (log)
                    throw new InvalidOperationException($"Consumer did not boot: {log}", ex);
            }

            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                BaseAddress = baseAddress,
                Timeout = TimeSpan.FromSeconds(10),
            };
            client.DefaultRequestHeaders.Add("x-api-key", Owner);
            using var ready = await client.GetAsync("/health/ready", Ct);
            ready.EnsureSuccessStatusCode();
            using var landing = await client.GetAsync("/", Ct);
            Assert.Equal("/workbench", landing.Headers.Location!.OriginalString);
            using var plugin = await client.GetAsync("/providers/Example.Workbench" + Api, Ct);
            await ExpectAsync(plugin, HttpStatusCode.OK);
            using var subscription = await client.PutAsJsonAsync("/subscriptions/workbench" + Api,
                new { properties = new { displayName = "Workbench" } }, Ct);
            await ExpectAsync(subscription, HttpStatusCode.Created);
            using var group = await client.PutAsJsonAsync(Scope + Api, new { location = "local", properties = new { } }, Ct);
            await ExpectAsync(group, HttpStatusCode.Created);
            using var note = await client.PutAsJsonAsync(Notes + "/process" + Api, new { properties = new { text = "from a real executable" } }, Ct);
            await ExpectAsync(note, HttpStatusCode.Created);
            Assert.Single(await EventsAsync(client, Notes + "/process"));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            // Cleanup must reap the child even when the test's cancellation token has fired.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token);
        }
    }
}
