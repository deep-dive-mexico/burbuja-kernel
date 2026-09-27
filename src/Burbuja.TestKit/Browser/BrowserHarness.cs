using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Burbuja.TestKit;

/// <summary>
/// Where a browser lane keeps its evidence, and what it calls itself.
///
/// <para><b>Every field is a path or a name, because that is all that differs between two lanes.</b> The
/// kernel's portal group writes <c>artifacts/e2e-logs/portal/{lane}</c> and <c>portal-browser.json</c>;
/// pomnibot's group P writes its own directory and its own summary. Nothing else about a browser lane is
/// repo-specific, which is what makes one harness enough for both.</para>
/// </summary>
/// <param name="Lane">The lane this run belongs to (<c>e2e-g</c>, <c>pomnibot-g</c>). It names the evidence,
/// so a screenshot from a failing run says which lane produced it.</param>
/// <param name="ScreenshotDir">Where screenshots go. Created on demand.</param>
/// <param name="SummaryPath">The JSON summary written on dispose — ran/skipped/attempted, the skip reason and
/// the screenshot list. <c>make proof</c> and the TRX count both read it.</param>
/// <param name="Suite">The summary's own name for what ran, so two suites in one folder can be told
/// apart.</param>
/// <param name="BrowserVariable">The environment variable that forces a skip (<c>BURBUJA_E2E_BROWSER=0</c>).
/// A lane with no Chromium must SKIP visibly, never pass quietly.</param>
public sealed record BrowserHarnessOptions(
    string Lane,
    string ScreenshotDir,
    string SummaryPath,
    string Suite,
    string BrowserVariable = "BURBUJA_E2E_BROWSER");

/// <summary>
/// One headless Chromium per test assembly, one context per fact, screenshots and a JSON summary on
/// dispose — extracted from the kernel's portal group so pomnibot's browser facts drive the same machine.
///
/// <para><b>Why a library and not a second copy.</b> The kernel's group G learned three things the hard way
/// and all three are encoded here: a browser per FACT makes a suite slower than the thing it tests; a page
/// that is not disposed leaks a live SSE stream and a SignalR circuit for the rest of the run (8 established
/// connections at the start of the group, 24 by the end, with later facts starved behind them); and
/// "no Chromium here" must be a VISIBLE skip with a reason, because Chromium is a ~130 MB download that some
/// lanes will never have.</para>
///
/// <para><b>It deliberately does not depend on xunit.</b> The TestKit is a packable library that both repos
/// consume, and <c>Assert.Skip</c>, collections and traits belong to the test project. So this exposes plain
/// <c>Task</c>s and counters, and each repo's fixture forwards to them — the kernel's fixture keeps its
/// collection and its <c>Assert.SkipWhen</c>, and pomnibot's keeps its own.</para>
///
/// <para><b>Scope is one assembly's fact group</b>: start it as a class fixture (or a collection fixture, if
/// the group needs its own thread budget — browser facts burn 8-20x an HTTP fact and will starve a shared
/// host if they run in the general pool).</para>
/// </summary>
public sealed class BrowserHarness(BrowserHarnessOptions options)
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly List<string> _screenshots = [];
    private int _attempted;
    private int _skipped;

    public BrowserHarnessOptions Options { get; } = options;

    /// <summary>Non-null when the browser is usable; null means every fact must skip.</summary>
    public IBrowser? Browser => _browser;

    /// <summary>Why <see cref="Browser"/> is null. Empty while the browser works.</summary>
    public string SkipReason { get; private set; } = "";

    public IReadOnlyList<string> Screenshots => _screenshots;

    /// <summary>Facts that actually executed (attempted minus skipped), which is what "ran" means.</summary>
    public int Ran => _attempted - _skipped;

    public int Skipped => _skipped;

    public int Attempted => _attempted;

    public async Task StartAsync()
    {
        if (Environment.GetEnvironmentVariable(Options.BrowserVariable) == "0")
        {
            SkipReason = $"{Options.BrowserVariable}=0";
            return;
        }

        try
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--disable-dev-shm-usage"],
            });
        }
        catch (Exception ex)
        {
            // PlaywrightException "Executable doesn't exist … playwright install" is the normal case on a
            // machine that never ran the browser install script. Anything else is a real problem and is
            // reported in the reason rather than swallowed.
            SkipReason = ex.Message.Split('\n')[0];
            if (_playwright is not null)
            {
                _playwright.Dispose();
                _playwright = null;
            }

            _browser = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
            await _browser.DisposeAsync();
        if (_playwright is not null)
            _playwright.Dispose();

        var json = new JsonObject
        {
            ["suite"] = Options.Suite,
            ["lane"] = Options.Lane,
            ["ran"] = Ran,
            ["skipped"] = _skipped,
            ["attempted"] = _attempted,
            ["skippedReason"] = _browser is null ? SkipReason : null,
            ["screenshots"] = _screenshots.Count,
            ["files"] = new JsonArray(_screenshots.Select(s => (JsonNode?)JsonValue.Create(Path.GetFileName(s))).ToArray()),
            ["at"] = DateTimeOffset.UtcNow.ToString("O"),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Options.SummaryPath)!);
        await File.WriteAllTextAsync(
            Options.SummaryPath,
            json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// A fresh context at a fixed desktop viewport. Fixed on purpose: a layout is responsive, and a screenshot
    /// whose width depends on the machine is not evidence of anything.
    ///
    /// <para><c>BaseURL</c> is what makes <c>page.GotoAsync("/portal")</c> work — Playwright refuses a
    /// relative URL without it.</para>
    ///
    /// <para>The returned page OWNS its context and disposes it: a portal page holds an open SSE stream and a
    /// SignalR circuit, so a fact that walks away without closing them leaves a live connection for the rest
    /// of the run. Use it with <c>await using var page = await Fx.NewPageAsync(...)</c>.</para>
    ///
    /// <para><paramref name="colorScheme"/> is for the facts that have to know what the OS said: it is null
    /// (the browser's own default) unless a fact asks, and a fact about a light/dark decision that left it
    /// to the environment would be measuring the machine rather than the code. Each call gets a fresh
    /// context, so a fact can hold a page in one scheme and open another in the other.</para>
    /// </summary>
    public async Task<BrowserPage> NewPageAsync(string baseUrl, ColorScheme? colorScheme = null)
    {
        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUrl,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            ColorScheme = colorScheme,
        });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        return new BrowserPage(context, page);
    }

    /// <summary>Called once by every fact that reaches the fixture, before any skip decision.</summary>
    public void CountAttempted() => _attempted++;

    /// <summary>
    /// Called when a fact decides to skip. `ran` is derived, not counted separately: an earlier version
    /// incremented both at the gate and reported "ran=10, skipped=2" for an 8-fact group, which is the kind of
    /// number that makes a proof summary untrustworthy.
    /// </summary>
    public void CountSkipped() => _skipped++;

    /// <summary>Saves a screenshot and remembers it for the summary and <c>make proof</c>.</summary>
    public async Task<string> ShotAsync(IPage page, string slug)
    {
        Directory.CreateDirectory(Options.ScreenshotDir);
        var path = Path.Combine(Options.ScreenshotDir, slug + ".png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
        _screenshots.Add(path);
        return path;
    }

    /// <summary>
    /// Wait until Blazor's client runtime is up, because a circuit is a websocket rendezvous rather than an
    /// instant and a fact that clicks before it is up is a flake.
    ///
    /// <para>Returns false on timeout; the CALLER decides whether that is a skip or a failure, since that
    /// choice depends on what the lane is asserting. (In the kernel's portal lane it skips: the script was
    /// once genuinely not served, and the gate now means a real regression rather than a missing toolchain —
    /// see <c>docs/07-testing.md</c>.)</para>
    /// </summary>
    public static async Task<bool> WaitForBlazorAsync(IPage page, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await page.EvaluateAsync<bool>("() => !!window.Blazor"))
                return true;
            await page.WaitForTimeoutAsync(500);
        }

        return false;
    }
}

/// <summary>
/// A page plus the context it lives in, so a fact cannot leak either one. <see cref="IPage"/> rather than
/// inheritance: the facts use GotoAsync/ClickAsync/Locator and nothing else, and a wrapper keeps them from
/// accidentally reaching past the disposal.
/// </summary>
public sealed class BrowserPage(IBrowserContext context, IPage page) : IAsyncDisposable
{
    public IPage Page { get; } = page;

    // The handful of members the facts use, forwarded verbatim. Kept explicit so nothing reaches past
    // DisposeAsync through a cast.
    public string Url => Page.Url;

    public IBrowserContext Context => context;

    public Task GotoAsync(string url) => Page.GotoAsync(url);

    public Task FillAsync(string selector, string value) => Page.FillAsync(selector, value);

    public Task ClickAsync(string selector) => Page.ClickAsync(selector);

    public Task WaitForLoadStateAsync(LoadState state = LoadState.Load) => Page.WaitForLoadStateAsync(state);

    public Task WaitForSelectorAsync(string selector, PageWaitForSelectorOptions? options = null) =>
        Page.WaitForSelectorAsync(selector, options);

    public Task WaitForTimeoutAsync(float ms) => Page.WaitForTimeoutAsync(ms);

    public ILocator Locator(string selector, PageLocatorOptions? options = null) => Page.Locator(selector, options);

    public ValueTask DisposeAsync() => context.DisposeAsync();
}
