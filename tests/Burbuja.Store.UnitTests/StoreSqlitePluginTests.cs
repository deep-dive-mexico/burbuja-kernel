using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Arm;
using Burbuja.Core.InMemory;
using Burbuja.Core.Redaction;
using Burbuja.Plugins.Store.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Burbuja.Store.UnitTests;

/// <summary>
/// The plugin has to swap the kernel's in-memory stores for durable ones inside an already
/// configured container — that replacement is the whole point of P6.
/// </summary>
public sealed class StoreSqlitePluginTests
{
    [Fact]
    public void ConfigureServices_replaces_every_kernel_store()
    {
        var root = Directory.CreateTempSubdirectory("pomnibot-store-plugin-").FullName;
        try
        {
            var plugin = new StoreSqlitePlugin();
            plugin.Manifest.EntryType.ShouldBe(typeof(StoreSqlitePlugin).FullName);
            plugin.Manifest.Load.ShouldBe(PluginLoadKind.Dynamic);

            var clock = new TestClock();
            var redactor = new CaptureGroupRedactor();
            var services = new ServiceCollection();
            services.AddSingleton<IClock>(clock);
            services.AddSingleton<IRedactor>(redactor);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Sqlite:Path"] = Path.Combine(root, "pomnibot.db"),
                    ["Sqlite:ArtifactRoot"] = Path.Combine(root, "artifacts"),
                })
                .Build());

            var outbox = new InMemoryOutbox();
            services.AddSingleton<IResourceStore>(new InMemoryResourceStore(clock));
            services.AddSingleton<IEventLog>(new InMemoryEventLog(redactor, clock));
            services.AddSingleton<ILeaseStore>(new InMemoryLeaseStore(clock));
            services.AddSingleton<IOutbox>(outbox);
            services.AddSingleton<IOutboxStore>(outbox);
            services.AddSingleton(typeof(IDecisionCache<>), typeof(InMemoryDecisionCache<>));
            services.AddSingleton<IArtifactStore>(new LocalFileArtifactStore(Path.Combine(root, "memory-artifacts"), redactor));

            plugin.ConfigureServices(services);

            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<IResourceStore>().ShouldBeOfType<SqliteResourceStore>();
            provider.GetRequiredService<IEventLog>().ShouldBeOfType<SqliteEventLog>();
            provider.GetRequiredService<ILeaseStore>().ShouldBeOfType<SqliteLeaseStore>();
            provider.GetRequiredService<IOutboxStore>().ShouldBeOfType<SqliteOutbox>();
            provider.GetRequiredService<IOutbox>().ShouldBeOfType<SqliteOutbox>();
            provider.GetRequiredService<IDecisionCache<SampleDecision>>().ShouldBeOfType<SqliteDecisionCache<SampleDecision>>();
            provider.GetRequiredService<IArtifactStore>().ShouldBeOfType<FileArtifactStore>();

            // Both outbox interfaces must resolve to the same instance, or the relay would
            // claim messages through a different queue than the one writers enqueue into.
            provider.GetRequiredService<IOutbox>().ShouldBeSameAs(provider.GetRequiredService<IOutboxStore>());
            provider.GetRequiredService<SqliteDatabase>().Path.ShouldBe(Path.Combine(root, "pomnibot.db"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Plugin_json_describes_the_same_plugin_as_the_manifest()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "plugin.json");
        File.Exists(path).ShouldBeTrue("plugin.json must be copied next to the assembly");

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var manifest = new StoreSqlitePlugin().Manifest;
        json["id"]!.GetValue<string>().ShouldBe(manifest.Id);
        json["assembly"]!.GetValue<string>().ShouldBe(manifest.Assembly);
        json["entry_type"]!.GetValue<string>().ShouldBe(manifest.EntryType);
        json["load"]!.GetValue<string>().ShouldBe("dynamic");
        json["trusted"]!.GetValue<bool>().ShouldBe(manifest.Trusted);
        json["load_order"]!.GetValue<int>().ShouldBe(manifest.LoadOrder);
    }
}
