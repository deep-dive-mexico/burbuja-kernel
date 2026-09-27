using System.Text;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Process;
using Burbuja.Core.Clock;
using Burbuja.Core.Process;
using Burbuja.Core.Redaction;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Process;

[Collection(EnvironmentCollection.Name)]
public sealed class ProcessRunnerTests
{
    public static readonly string[] PlantedSecrets =
    [
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SECRET_SESSION",
        "BURBUJA_API_KEY",
        "BURBUJA_WEBHOOK_HMAC_SECRET",
        "GH_TOKEN",
        "NPM_TOKEN",
        "MY_SECRET",
        "DB_PASSWORD",
        "OPENAI_API_KEY",
        "AZURE_API_KEY",
        "SERVICE_TOKEN",
        "APP_SECRET",
    ];

    [Fact]
    public async Task Child_env_contains_none_of_the_planted_secrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var previous = PlantedSecrets.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in PlantedSecrets)
                Environment.SetEnvironmentVariable(name, $"planted-{name}-value");

            var (runner, grants) = Create();
            var grant = grants.Mint("subject-1", "decision-1", TimeSpan.FromMinutes(1));
            var requested = PlantedSecrets.ToDictionary(k => k, k => $"requested-{k}", StringComparer.Ordinal);
            requested["SAFE_OK"] = "ok";
            var result = await runner.RunAsync(new ProcessStartRequest(
                "/usr/bin/env",
                [],
                "/",
                requested,
                TimeSpan.FromSeconds(5),
                64 * 1024), grant, ct);
            result.TimedOut.ShouldBeFalse();
            result.ExitCode.ShouldBe(0);
            foreach (var name in PlantedSecrets)
            {
                result.StandardOutput.ShouldNotContain(name);
                result.StandardOutput.ShouldNotContain($"planted-{name}-value");
                result.StandardOutput.ShouldNotContain($"requested-{name}");
            }

            result.StandardOutput.ShouldContain("GIT_TERMINAL_PROMPT=0");
            result.StandardOutput.ShouldContain("CI=1");
            result.StandardOutput.ShouldContain("NO_COLOR=1");
            result.StandardOutput.ShouldNotContain("SAFE_OK=ok");
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public async Task Grant_is_required_and_expired_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        var runner = new ProcessRunner(clock, new CaptureGroupRedactor());
        var grants = new ExecutionGrantAuthority(clock);
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await runner.RunAsync(Echo("hi"), null!, ct));
        var expired = grants.Mint("s", "d", TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(2));
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await runner.RunAsync(Echo("hi"), expired, ct));
    }

    [Fact]
    public void Path_strips_local_bin()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stripped = ProcessEnvironmentPolicy.StripLocalBin(
            $"/usr/bin{Path.PathSeparator}{Path.Combine(home, ".local", "bin")}{Path.PathSeparator}/bin",
            home);
        stripped.ShouldNotContain(".local/bin");
        stripped.ShouldContain("/usr/bin");
    }

    [Fact]
    public async Task Kill_waits_for_exit_and_caps_output_on_both_paths()
    {
        var ct = TestContext.Current.CancellationToken;
        var (runner, grants) = Create();
        var grant = grants.Mint("s", "d", TimeSpan.FromMinutes(1));
        var result = await runner.RunAsync(new ProcessStartRequest(
            "/bin/sh",
            ["-c", "while true; do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx; sleep 0.05; done"],
            "/",
            new Dictionary<string, string>(),
            TimeSpan.FromMilliseconds(200),
            64), grant, ct);
        result.TimedOut.ShouldBeTrue();
        Encoding.UTF8.GetByteCount(result.StandardOutput).ShouldBeLessThanOrEqualTo(64 + 64);
    }

    [Fact]
    public async Task Optional_redaction_applies_to_output()
    {
        var ct = TestContext.Current.CancellationToken;
        var (runner, grants) = Create();
        var grant = grants.Mint("s", "d", TimeSpan.FromMinutes(1));
        var key = "AKIA" + new string('A', 16);
        var result = await runner.RunAsync(new ProcessStartRequest(
            "/bin/echo",
            [key],
            "/",
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(5),
            4096,
            RedactOutput: true), grant, ct);
        result.StandardOutput.ShouldNotContain("AKIA");
        result.StandardOutput.ShouldContain("[redacted:aws-access-key]");
    }

    [Fact]
    public void Mint_issues_a_grant()
    {
        var clock = new FakeClock();
        var grants = new ExecutionGrantAuthority(clock);
        var g = grants.Mint("sub", "key", TimeSpan.FromMinutes(5));
        g.SubjectId.ShouldBe("sub");
        g.DecisionKey.ShouldBe("key");
        g.ExpiresAt.ShouldBe(clock.UtcNow + TimeSpan.FromMinutes(5));
    }

    private static (ProcessRunner Runner, IExecutionGrantAuthority Grants) Create()
    {
        var clock = new TimeProviderClock(TimeProvider.System);
        return (new ProcessRunner(clock, new CaptureGroupRedactor()), new ExecutionGrantAuthority(clock));
    }

    private static ProcessStartRequest Echo(string text) =>
        new("/bin/echo", [text], "/", new Dictionary<string, string>(), TimeSpan.FromSeconds(5), 1024);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
