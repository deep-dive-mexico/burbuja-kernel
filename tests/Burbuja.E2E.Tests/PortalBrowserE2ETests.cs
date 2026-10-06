using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;
using static Burbuja.E2E.Tests.KernelTestHost;

namespace Burbuja.E2E.Tests;

[Trait("Category", "E2E")]
[Trait("Browser", "chromium")]
public sealed class PortalBrowserE2ETests(SharedServerFixture server)
{
    [Fact]
    public async Task Anonymous_navigation_requires_login_and_bad_credentials_keep_a_readable_form()
    {
        await using var session = await BrowserAsync();
        var page = session.Page;
        await page.GotoAsync(server.BaseUrl + "/portal/create/job");
        await Assertions.Expect(page.GetByLabel("Username")).ToBeVisibleAsync();
        await page.GetByLabel("Username").FillAsync("owner");
        await page.GetByLabel("Password").FillAsync("wrong");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("Password")).ToBeVisibleAsync();
        Assert.Contains("/portal/signin", page.Url);
        Assert.DoesNotContain("stack trace", (await page.Locator("body").InnerTextAsync()).ToLowerInvariant());
    }

    [Fact]
    public async Task Owner_creates_a_workspace_through_the_form_and_the_public_resource_exists()
    {
        await using var session = await BrowserAsync("owner");
        var page = session.Page;
        var name = "browser-" + Guid.NewGuid().ToString("N")[..8];
        await page.GotoAsync(server.BaseUrl + "/portal/create/workspace");
        await ConfirmInteractiveAsync(page);
        await page.GetByRole(AriaRole.Textbox, new() { Name = "name", Exact = true }).FillAsync(name);
        await page.GetByLabel("Display name").FillAsync("Browser workspace");
        await page.Locator("#init").UncheckAsync();
        await page.Locator("[data-action=create-workspace]").ClickAsync();
        await page.WaitForURLAsync(new Regex("/portal/workspaces/" + name));
        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        client.DefaultRequestHeaders.Add("x-api-key", Owner);
        var resource = await GetAsync(client, AgentRoot + "/workspaces/" + name);
        Assert.Equal("Browser workspace", resource["properties"]!["displayName"]!.GetValue<string>());
        await ScreenshotAsync(page, "workspace-created");
    }

    [Fact]
    public async Task Owner_creates_a_paused_job_accepts_it_and_reads_the_completed_job()
    {
        await using var session = await BrowserAsync("owner");
        var page = session.Page;
        var name = "browser-job-" + Guid.NewGuid().ToString("N")[..8];
        await page.GotoAsync(server.BaseUrl + "/portal/create/job");
        await ConfirmInteractiveAsync(page);
        await page.GetByRole(AriaRole.Combobox, new() { Name = "workspace", Exact = true }).SelectOptionAsync("work");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "name", Exact = true }).FillAsync(name);
        await page.GetByRole(AriaRole.Textbox, new() { Name = "title", Exact = true }).FillAsync("Browser job");
        await page.GetByRole(AriaRole.Textbox, new() { Name = "brief", Exact = true }).FillAsync("Write the scripted note.");
        await page.Locator("#auto").UncheckAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "scenario", Exact = true }).FillAsync("happy-path");
        await page.Locator("[data-action=create-job]").ClickAsync();
        await page.WaitForURLAsync(new Regex("/portal/jobs/work/" + name));
        await Assertions.Expect(page.Locator("[data-action=accept-job]")).ToBeVisibleAsync();
        await page.Locator("[data-action=accept-job]").ClickAsync();
        using var client = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };
        client.DefaultRequestHeaders.Add("x-api-key", Owner);
        await WaitAsync(client, Workspace + "/jobs/" + name,
            body => body["properties"]?["status"]?.GetValue<string>() == "succeeded");
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("[data-action=accept-job]")).ToHaveCountAsync(0);
        Assert.Contains("succeeded", (await page.Locator("body").InnerTextAsync()).ToLowerInvariant());
        await ScreenshotAsync(page, "job-completed");
    }

    [Fact]
    public async Task A_reader_can_read_the_console_but_cannot_submit_a_workspace()
    {
        await using var session = await BrowserAsync("reader");
        await session.Page.GotoAsync(server.BaseUrl + "/portal/create/workspace");
        await Assertions.Expect(session.Page.Locator("[data-action=create-workspace]")).ToBeDisabledAsync();
        Assert.Contains("roles do not grant", await session.Page.Locator("body").InnerTextAsync());
    }

    [Theory]
    [InlineData("/portal")]
    [InlineData("/portal/workspaces")]
    [InlineData("/portal/sessions")]
    [InlineData("/portal/integrations")]
    [InlineData("/portal/approvals")]
    [InlineData("/portal/plugins")]
    [InlineData("/portal/explore")]
    [InlineData("/portal/search")]
    [InlineData("/portal/activity")]
    [InlineData("/portal/create/workspace")]
    [InlineData("/portal/create/job")]
    [InlineData("/portal/create/agent")]
    public async Task Every_generic_console_route_loads_without_a_server_error_or_browser_exception(string route)
    {
        await using var session = await BrowserAsync("owner");
        var exceptions = new List<string>();
        session.Page.PageError += (_, error) => exceptions.Add(error);
        var response = await session.Page.GotoAsync(server.BaseUrl + route);
        Assert.NotNull(response);
        Assert.True(response!.Status < 400, $"Route {route} returned {response.Status}");
        await Assertions.Expect(session.Page.Locator("h1")).ToBeVisibleAsync();
        await Assertions.Expect(session.Page.Locator("main[data-loaded=true]")).ToBeVisibleAsync();
        var text = await session.Page.Locator("body").InnerTextAsync();
        Assert.DoesNotContain("Unhandled exception", text);
        Assert.DoesNotContain("InternalServerError", text);
        Assert.Empty(exceptions);
    }

    [Theory]
    [InlineData(1440, 900, false)]
    [InlineData(390, 844, false)]
    [InlineData(390, 844, true)]
    public async Task The_signed_in_console_is_readable_at_desktop_and_mobile_in_both_appearances(int width, int height, bool dark)
    {
        await using var session = await BrowserAsync("owner", width, height);
        await session.Page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light });
        await session.Page.GotoAsync(server.BaseUrl + "/portal");
        await Assertions.Expect(session.Page.Locator("h1")).ToBeVisibleAsync();
        Assert.DoesNotContain("NaN", await session.Page.Locator("body").InnerTextAsync());
        Assert.True(await session.Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth <= innerWidth + 2"), "The console must not overflow horizontally.");
        await ScreenshotAsync(session.Page, $"console-{width}-{(dark ? "dark" : "light")}");
    }

    [Fact]
    public async Task Logout_invalidates_a_session_and_back_navigation_cannot_restore_write_authority()
    {
        await using var session = await BrowserAsync("owner");
        await session.Page.GotoAsync(server.BaseUrl + "/portal/create/workspace");
        await session.Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await session.Page.GotoAsync(server.BaseUrl + "/portal/create/workspace");
        await Assertions.Expect(session.Page.GetByLabel("Username")).ToBeVisibleAsync();
        await Assertions.Expect(session.Page.Locator("[data-action=create-workspace]")).ToHaveCountAsync(0);
    }

    private async Task<BrowserSession> BrowserAsync(string? username = null, int width = 1440, int height = 900)
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync(new()
        { ViewportSize = new() { Width = width, Height = height }, ReducedMotion = ReducedMotion.Reduce });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        var session = new BrowserSession(playwright, browser, context, page);
        if (username is not null)
        {
            try
            {
            await page.GotoAsync(server.BaseUrl + "/portal/signin");
            await page.GetByLabel("Username").FillAsync(username);
            await page.GetByLabel("Password").FillAsync("owner-dev-password");
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
            await page.WaitForURLAsync(url => !url.Contains("/signin", StringComparison.Ordinal));
            await Assertions.Expect(page.Locator("h1")).ToBeVisibleAsync();
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }
        return session;
    }

    private static async Task ScreenshotAsync(IPage page, string name)
    {
        var directory = Path.Combine(Repository, "artifacts", "e2e-browser");
        Directory.CreateDirectory(directory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name + ".png"), FullPage = true });
    }

    private static async Task ConfirmInteractiveAsync(IPage page)
    {
        // Exercise a real circuit round trip; a server-rendered form can otherwise be replaced during hydration.
        var create = page.Locator("button[aria-haspopup=menu]");
        var menu = page.GetByRole(AriaRole.Menu);
        for (var attempt = 0; attempt < 10 && !await menu.IsVisibleAsync(); attempt++)
        {
            await create.ClickAsync();
            try { await Assertions.Expect(menu).ToBeVisibleAsync(new() { Timeout = 1_000 }); }
            catch (PlaywrightException) when (attempt < 9) { }
        }
        await Assertions.Expect(menu).ToBeVisibleAsync();
        await create.ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Menu)).ToBeHiddenAsync();
    }

    private sealed record BrowserSession(IPlaywright Playwright, IBrowser Browser, IBrowserContext Context, IPage Page) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Browser.DisposeAsync();
            Playwright.Dispose();
        }
    }
}
