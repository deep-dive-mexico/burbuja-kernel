using System.Text.Json.Nodes;
using Burbuja.TestKit;
using Shouldly;
using Xunit;

namespace Burbuja.Integrations.UnitTests;

/// <summary>Exercises the real publish/spawn boundary using a tiny consumer app in a solutionless scratch tree.</summary>
public sealed class ConsumerHostTests
{
    [Fact]
    public async Task Explicit_consumer_project_and_entry_run_with_isolated_settings_environment_state_and_ports()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "consumer host " + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(root, "apps", "Consumer");
        Directory.CreateDirectory(projectDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(projectDir, "Entry.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AssemblyName>Consumer.Entry</AssemblyName>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """, ct);
            await File.WriteAllTextAsync(Path.Combine(projectDir, "Program.cs"), """
                var builder = WebApplication.CreateBuilder(args);
                var app = builder.Build();
                var state = app.Configuration["Test:StateFile"]!;
                Directory.CreateDirectory(Path.GetDirectoryName(state)!);
                File.WriteAllText(state, app.Configuration["Test:Value"]);
                app.MapGet("/health/ready", () => "ready");
                app.MapGet("/probe", () => new
                {
                    value = app.Configuration["Test:Value"],
                    environment = Environment.GetEnvironmentVariable("CONSUMER_FIXTURE_VALUE"),
                    state = File.ReadAllText(state),
                });
                app.Run();
                """, ct);

            const string entry = "Consumer.Entry.dll";
            var target = HostPublisher.EnsurePublished(directory: "artifacts/host", ct: ct,
                projectPath: "apps/Consumer/Entry.csproj", entryAssemblyName: entry, sourceRoot: root);
            target.ShouldBe(Path.Combine(root, "artifacts", "host"));
            File.Exists(Path.Combine(target, entry)).ShouldBeTrue();
            File.Exists(Path.Combine(target, "Burbuja.Host.dll")).ShouldBeFalse();
            var stampPath = Path.Combine(target, ".testkit-publish.json");
            var stampTime = File.GetLastWriteTimeUtc(stampPath);

            HostPublisher.EnsurePublished(target, ct: ct, projectPath: "apps/Consumer/Entry.csproj",
                entryAssemblyName: entry, sourceRoot: root).ShouldBe(target);
            File.GetLastWriteTimeUtc(stampPath).ShouldBe(stampTime, "an unchanged consumer publish should be reused");

            async Task<ServerProcess> Start(string name) => await ServerProcess.StartAsync(target,
                new Dictionary<string, string?>
                {
                    ["Test:Value"] = name,
                    ["Test:StateFile"] = Path.Combine(root, "state", name, "value.txt"),
                },
                new Dictionary<string, string?> { ["CONSUMER_FIXTURE_VALUE"] = "environment-" + name },
                ct: ct, entryAssemblyName: entry, repositoryRoot: root);

            await using (var first = await Start("one"))
            await using (var second = await Start("two"))
            {
                first.Port.ShouldNotBe(second.Port);
                first.LogPath.ShouldBe(Path.Combine(root, "artifacts", "e2e-logs", $"host-{first.Port}.log"));
                second.LogPath.ShouldNotBe(first.LogPath);
                using var http = new HttpClient();
                foreach (var (server, name) in new[] { (first, "one"), (second, "two") })
                {
                    var probe = JsonNode.Parse(await http.GetStringAsync(server.BaseUrl + "/probe", ct))!;
                    probe["value"]!.GetValue<string>().ShouldBe(name);
                    probe["environment"]!.GetValue<string>().ShouldBe("environment-" + name);
                    probe["state"]!.GetValue<string>().ShouldBe(name);
                }
            }

            // Sharing an explicit output directory across configurations must not silently reuse Release.
            HostPublisher.EnsurePublished(target, configuration: "Debug", ct: ct,
                projectPath: "apps/Consumer/Entry.csproj", entryAssemblyName: entry, sourceRoot: root);
            var stamp = JsonNode.Parse(await File.ReadAllTextAsync(stampPath, ct))!;
            stamp["Configuration"]!.GetValue<string>().ShouldBe("Debug");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
