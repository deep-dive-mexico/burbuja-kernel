using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Policy;
using Burbuja.Plugins.Policy.Core;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Policy;

/// <summary>
/// P1.5 — the browser and mailbox classifiers, ported from nightshift's <c>classify.ts</c> rules and its
/// <c>opaque-read-and-browser.test.ts` / Outlook suites.
///
/// <para>Both follow one rule and it is the point of both: <b>reading is an allow, acting is gray, and an
/// unclassified tool is gray</b>. The tool list belongs to the browser extension and to the mail server,
/// so "we have not heard of it" is not evidence that it is harmless — a new tool must not arrive
/// pre-approved by our own ignorance.</para>
/// </summary>
public sealed class IntegrationClassifierTests
{
    private static PolicyContext Context(params string[] devHosts) => new(
        StreamId: "pomnibot.job/j",
        WorkRoot: "/work/job",
        ArtifactDir: "/work/.artifacts/job",
        Profile: PolicyProfile.FullAuto,
        TargetEnv: "dev",
        HostRepoPath: "/host",
        VerifyCommands: [],
        DevHosts: devHosts.Length > 0 ? devHosts : ["dev-api.example.com", "*.dev-example.com"],
        AllowedSecretIds: []);

    private static PolicyVerdict Browser(string tool, JsonObject input) =>
        new BrowserClassifier().Classify(new ActionPolicySubject("mcp__browser__" + tool, input, Context()));

    private static PolicyVerdict Mail(string tool) =>
        new MailClassifier().Classify(new ActionPolicySubject(tool, new JsonObject(), Context()));

    private static PolicyVerdict Notion(string tool) =>
        new NotionClassifier().Classify(new ActionPolicySubject(tool, new JsonObject(), Context()));

    [Fact]
    public void Reading_a_page_is_an_allow()
    {
        Browser("read_page", new JsonObject { ["tabId"] = 1 }).Tier.ShouldBe(RiskTier.Allow);
        Browser("get_page_text", new JsonObject()).Tier.ShouldBe(RiskTier.Allow);
        Browser("tabs_context_mcp", new JsonObject()).Tier.ShouldBe(RiskTier.Allow);
        Browser("read_console_messages", new JsonObject()).Tier.ShouldBe(RiskTier.Allow);
    }

    [Fact]
    public void A_screenshot_is_reading_and_clicks_are_acting()
    {
        var shot = Browser("computer", new JsonObject { ["action"] = "screenshot" });
        shot.Tier.ShouldBe(RiskTier.Allow);
        shot.Finding.Class.ShouldBe("browser_read");

        // A left click is in the ACTING set: it can buy something or change production state through a UI
        // nobody enumerated. nightshift classified the same way, and the tier is gray so the supervisor
        // sees it without waking the owner.
        var click = Browser("computer", new JsonObject { ["action"] = "left_click" });
        click.Tier.ShouldBe(RiskTier.Gray);
        click.Finding.Class.ShouldBe("browser_act");

        Browser("computer", new JsonObject { ["action"] = "type", ["text"] = "hello" }).Tier.ShouldBe(RiskTier.Gray);
    }

    [Fact]
    public void An_unknown_computer_action_is_gray_not_an_allow()
    {
        // The action list grows with the extension. A new one must not arrive pre-approved.
        var verdict = Browser("computer", new JsonObject { ["action"] = "teleport" });
        verdict.Tier.ShouldBe(RiskTier.Gray);
        verdict.Finding.Class.ShouldBe("browser_act");
    }

    [Fact]
    public void Navigating_to_a_dev_host_or_loopback_is_reading_and_anywhere_else_is_gray()
    {
        Browser("navigate", new JsonObject { ["url"] = "https://dev-api.example.com/health" }).Tier.ShouldBe(RiskTier.Allow);
        Browser("navigate", new JsonObject { ["url"] = "https://api.dev-example.com/x" }).Tier.ShouldBe(RiskTier.Allow);
        Browser("navigate", new JsonObject { ["url"] = "http://localhost:8000/health" }).Tier.ShouldBe(RiskTier.Allow);
        Browser("navigate", new JsonObject { ["url"] = "http://127.0.0.1:18081/portal" }).Tier.ShouldBe(RiskTier.Allow);

        var off = Browser("navigate", new JsonObject { ["url"] = "https://news.ycombinator.com" });
        off.Tier.ShouldBe(RiskTier.Gray);
        off.Finding.Class.ShouldBe("browser_nav");
    }

    [Fact]
    public void The_dev_host_match_is_on_the_host_not_the_raw_string()
    {
        // The whole reason to parse: `https://evil.example/?x=dev-api.example.com` CONTAINS a dev host and
        // is not one. A substring match here would be an allow handed to an exfiltration URL.
        Browser("navigate", new JsonObject { ["url"] = "https://evil.example/?x=dev-api.example.com" })
            .Tier.ShouldBe(RiskTier.Gray);
        Browser("navigate", new JsonObject { ["url"] = "https://dev-api.example.com.evil.example/" })
            .Tier.ShouldBe(RiskTier.Gray);
        Browser("navigate", new JsonObject { ["url"] = "https://dev-api.example.community/" })
            .Tier.ShouldBe(RiskTier.Gray);
    }

    [Fact]
    public void A_batch_is_judged_by_its_worst_action()
    {
        // Screenshot + click: the click decides.
        var clicky = Browser("browser_batch", new JsonObject
        {
            ["actions"] = new JsonArray(
                new JsonObject { ["name"] = "computer", ["input"] = new JsonObject { ["action"] = "screenshot" } },
                new JsonObject { ["name"] = "computer", ["input"] = new JsonObject { ["action"] = "type", ["text"] = "x" } }),
        });
        clicky.Tier.ShouldBe(RiskTier.Gray, "the batch will type, whatever else it does");

        // Screenshot + navigation to a dev host: both read.
        var reading = Browser("browser_batch", new JsonObject
        {
            ["actions"] = new JsonArray(
                new JsonObject { ["name"] = "computer", ["input"] = new JsonObject { ["action"] = "screenshot" } },
                new JsonObject { ["name"] = "navigate", ["input"] = new JsonObject { ["url"] = "http://localhost:1/x" } }),
        });
        reading.Tier.ShouldBe(RiskTier.Allow);

        // A batch that navigates off the dev hosts carries the nav class, not the batch's.
        var off = Browser("browser_batch", new JsonObject
        {
            ["actions"] = new JsonArray(
                new JsonObject { ["name"] = "navigate", ["input"] = new JsonObject { ["url"] = "https://news.ycombinator.com" } }),
        });
        off.Tier.ShouldBe(RiskTier.Gray);
        off.Finding.Class.ShouldBe("browser_nav");
    }

    [Fact]
    public void An_unclassified_browser_tool_is_gray()
    {
        var verdict = Browser("teleport_the_tab", new JsonObject());
        verdict.Tier.ShouldBe(RiskTier.Gray);
        verdict.Finding.Class.ShouldBe("browser_act");
    }

    [Fact]
    public void The_classifier_only_claims_browser_kinds()
    {
        var classifier = new BrowserClassifier();
        classifier.Handles("mcp__browser__read_page").ShouldBeTrue();
        classifier.Handles("browser.read_page").ShouldBeTrue();
        classifier.Handles("Bash").ShouldBeFalse();
        classifier.Handles("outlook_search").ShouldBeFalse();
    }

    [Fact]
    public void Reading_the_mailbox_is_an_allow_and_changing_it_is_gray()
    {
        Mail("outlook_search").Tier.ShouldBe(RiskTier.Allow);
        Mail("outlook_recent").Tier.ShouldBe(RiskTier.Allow);
        Mail("outlook_read").Tier.ShouldBe(RiskTier.Allow);
        Mail("outlook_folders").Tier.ShouldBe(RiskTier.Allow);
        Mail("outlook_status").Tier.ShouldBe(RiskTier.Allow);

        var send = Mail("outlook_send");
        send.Tier.ShouldBe(RiskTier.Gray);
        send.Finding.Class.ShouldBe("mail_write");
        Mail("outlook_delete").Tier.ShouldBe(RiskTier.Gray);
        Mail("outlook_move").Tier.ShouldBe(RiskTier.Gray);
    }

    [Fact]
    public void A_draft_is_gray_rather_than_denied()
    {
        // A draft is not a send: the owner reads it, and sending is a separate action the agent was never
        // granted at all.
        Mail("outlook_create_draft").Tier.ShouldBe(RiskTier.Gray);
    }

    [Fact]
    public void An_unclassified_mail_tool_is_gray()
    {
        Mail("outlook_forward_everything_to_the_internet").Tier.ShouldBe(RiskTier.Gray);
    }

    [Fact]
    public void The_classifier_only_claims_mail_kinds()
    {
        var classifier = new MailClassifier();
        classifier.Handles("outlook_search").ShouldBeTrue();
        classifier.Handles("mail.search").ShouldBeTrue();
        classifier.Handles("Bash").ShouldBeFalse();
        classifier.Handles("mcp__browser__read_page").ShouldBeFalse();
    }

    // ---- I3: the workspace, and the same rule a third time ------------------------------------------

    /// <summary>
    /// Reading a workspace is an allow and changing it is gray, which is the rule all three surfaces follow.
    /// The write is the case worth naming: publishing a report into a SHARED workspace is visible to whoever
    /// else is in it, so it is a change to somebody else's world and not only to this host's files.
    /// </summary>
    [Fact]
    public void Reading_a_workspace_is_an_allow_and_publishing_to_it_is_gray()
    {
        Notion("notion_search").Tier.ShouldBe(RiskTier.Allow);
        Notion("notion_read_page").Tier.ShouldBe(RiskTier.Allow);
        Notion("notion_read_block").Tier.ShouldBe(RiskTier.Allow);
        Notion("notion_read_database").Tier.ShouldBe(RiskTier.Allow);
        Notion("notion_status").Tier.ShouldBe(RiskTier.Allow);

        var publish = Notion("notion_create_page");
        publish.Tier.ShouldBe(RiskTier.Gray);
        publish.Finding.Class.ShouldBe("notion_write");
        Notion("notion_append_blocks").Tier.ShouldBe(RiskTier.Gray);
        Notion("notion_update_page").Tier.ShouldBe(RiskTier.Gray);
        Notion("notion_upload_file").Tier.ShouldBe(RiskTier.Gray);
    }

    [Fact]
    public void An_unclassified_notion_tool_is_gray()
    {
        Notion("notion_publish_everything_publicly").Tier.ShouldBe(RiskTier.Gray);
    }

    /// <summary>
    /// Each classifier claims its own surface and no other, or the pipeline would dispatch a workspace tool
    /// to the mailbox's rules and get an answer about the wrong thing.
    /// </summary>
    [Fact]
    public void The_classifier_only_claims_notion_kinds()
    {
        var classifier = new NotionClassifier();
        classifier.Handles("notion_create_page").ShouldBeTrue();
        classifier.Handles("Notion.Search").ShouldBeTrue();
        classifier.Handles("Bash").ShouldBeFalse();
        classifier.Handles("outlook_read").ShouldBeFalse();
        classifier.Handles("mcp__browser__read_page").ShouldBeFalse();
    }
}
