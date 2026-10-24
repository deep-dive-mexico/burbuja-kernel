using System.Text;
using System.Text.Json;
using Burbuja.Compute.Abstractions;
using Burbuja.Worker;
using Shouldly;
using Xunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;

public sealed class WorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workload-tests-" + Guid.NewGuid().ToString("N"));
    public WorkerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Headroom_is_fail_closed_and_rejects_stale_and_future_samples()
    {
        var now = DateTimeOffset.UtcNow;
        var age = TimeSpan.FromSeconds(90);
        WorkloadAdmission.CanStart(null, now, age).ShouldBeFalse();
        WorkloadAdmission.CanStart(new(now, false, "pressure"), now, age).ShouldBeFalse();
        WorkloadAdmission.CanStart(new(now.AddSeconds(-91), true, "ok"), now, age).ShouldBeFalse();
        WorkloadAdmission.CanStart(new(now.AddMinutes(1), true, "ok"), now, age).ShouldBeFalse();
        WorkloadAdmission.CanStart(new(now, true, "ok"), now, age).ShouldBeTrue();
    }

    private WorkerOptions Options(string script)
    {
        var program = Path.Combine(_root, "task.sh");
        File.WriteAllText(program, script);
        var headroom = Path.Combine(_root, "headroom.json");
        File.WriteAllText(headroom, JsonSerializer.Serialize(new WorkloadHeadroom(DateTimeOffset.UtcNow, true, "ok")));
        return new WorkerOptions { HeadroomFile = headroom, ScratchDirectory = Path.Combine(_root, "scratch"),
            Workloads = new() { ["test"] = new() { Executable = "/bin/sh", Arguments = [program, "{input}", "{output}"],
                ParameterFlags = new() { ["prompt"] = "--prompt" }, TimeoutSeconds = 5 } } };
    }

    [Fact]
    public async Task Real_process_roundtrip_preserves_literal_arguments_and_cleans_input()
    {
        var options = Options("test \"$3\" = '--prompt' || exit 3\ncp \"$1\" \"$2.json\"\n");
        var runner = new FileWorkloadRunner(options);
        var payload = "{\"value\":\"synthetic\"}";
        var result = await runner.RunAsync("test", new MemoryStream(Encoding.UTF8.GetBytes(payload)),
            new Dictionary<string, string> { ["prompt"] = "$(touch /should-not-exist); ' quoted" }, TestContext.Current.CancellationToken);
        Encoding.UTF8.GetString(result).ShouldBe(payload);
        Directory.GetFileSystemEntries(options.ScratchDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unknown_options_and_oversize_audio_never_execute()
    {
        var options = Options("exit 99");
        options.Workloads["test"].MaxInputBytes = 2;
        var runner = new FileWorkloadRunner(options);
        await Should.ThrowAsync<ArgumentException>(() => runner.RunAsync("test", new MemoryStream([1]),
            new Dictionary<string, string> { ["command"] = "something" }, TestContext.Current.CancellationToken));
        await Should.ThrowAsync<InvalidDataException>(() => runner.RunAsync("test", new MemoryStream([1,2,3]),
            new Dictionary<string, string>(), TestContext.Current.CancellationToken));
        Directory.GetFileSystemEntries(options.ScratchDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_work_is_refused_and_cancelled_process_releases_slot()
    {
        var options = Options("sleep 30\n");
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runner = new FileWorkloadRunner(options);
        var first = runner.RunAsync("test", new MemoryStream([1]), new Dictionary<string,string>(), cancelled.Token);
        await Should.ThrowAsync<WorkloadBusyException>(() => runner.RunAsync("test", new MemoryStream([1]),
            new Dictionary<string,string>(), TestContext.Current.CancellationToken));
        cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => first);
        Directory.GetFileSystemEntries(options.ScratchDirectory).ShouldBeEmpty();
        File.WriteAllText(Path.Combine(_root, "task.sh"), "printf '{}' > \"$2.json\"\n");
        (await runner.RunAsync("test", new MemoryStream([1]), new Dictionary<string,string>(), TestContext.Current.CancellationToken)).Length.ShouldBe(2);
    }

    [Fact]
    public async Task Protected_service_failure_interrupts_owned_compute_and_cleans_audio()
    {
        var marker = Path.Combine(_root, "started");
        var options = Options($"touch '{marker}'\nsleep 30\n");
        options.Workloads["test"].TimeoutSeconds = 15;
        var runner = new FileWorkloadRunner(options);
        var running = runner.RunAsync("test", new MemoryStream([1]), new Dictionary<string,string>(), TestContext.Current.CancellationToken);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(3));
        while (!File.Exists(marker)) await Task.Delay(10, bound.Token);
        File.WriteAllText(options.HeadroomFile, JsonSerializer.Serialize(new WorkloadHeadroom(DateTimeOffset.UtcNow, false, "protected service unhealthy")));
        await Should.ThrowAsync<IOException>(() => running.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));
        Directory.GetFileSystemEntries(options.ScratchDirectory).ShouldBeEmpty();
    }

    [Fact]
    public async Task Real_http_worker_requires_auth_validates_options_and_enforces_headroom()
    {
        var options = Options("cp \"$1\" \"$2.json\"\n");
        options.TokenFile = Path.Combine(_root, "token");
        var token = new string('x', 48);
        File.WriteAllText(options.TokenFile, token);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<FileWorkloadRunner>();
        await using var app = builder.Build();
        app.MapWorker();
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        (await http.GetAsync("/workloads/v1/status", TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var good = await http.PostAsync("/workloads/v1/test/run", new StringContent("{\"synthetic\":true}"), TestContext.Current.CancellationToken);
        good.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await good.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("{\"synthetic\":true}");
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/workloads/v1/test/run") { Content = new StringContent("{}") };
        invalid.Headers.Add("X-Workload-Parameters", Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"command\":\"forbidden\"}")));
        (await http.SendAsync(invalid, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        File.WriteAllText(options.HeadroomFile, JsonSerializer.Serialize(new WorkloadHeadroom(DateTimeOffset.UtcNow, false, "protected service unhealthy")));
        using var busy = await http.PostAsync("/workloads/v1/test/run", new StringContent("{}"), TestContext.Current.CancellationToken);
        busy.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        busy.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(15));
        await app.StopAsync(TestContext.Current.CancellationToken);
    }
}
