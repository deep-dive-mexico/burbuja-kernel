using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Policy;

namespace Burbuja.Plugins.Policy.Core;

/// <summary>
/// P1.5: the browser tools, ported from nightshift's <c>classify.ts</c> rules for
/// <c>mcp__browser__*</c>.
///
/// <para><b>The distinction this encodes is reading versus acting.</b> Looking at a page is a read: it
/// changes nothing in the world and the worst case is that the agent learned something it should not
/// have, which the path rules already cover for the local filesystem. Clicking, typing and pressing keys
/// <em>act</em>: they can buy something, send a message, or change production state through a UI whose
/// controls nobody enumerated. So <c>browser_read</c> is an allow and <c>browser_act</c> is gray — the
/// supervisor looks, the owner is not interrupted.</para>
///
/// <para><b>Navigation is the interesting middle.</b> Going to a dev host or to loopback is part of
/// reading (the job's own service is usually there); going anywhere else is <c>browser_nav</c> gray,
/// because the URL is the one piece of a browser session the agent chooses freely and an exfiltration
/// is a GET with a long query string.</para>
///
/// <para><b>A batch is judged by its worst action</b>, because that is what the batch will do. Judging
/// it by its first action — or by an average — is how a screenshot followed by a <c>type</c> becomes an
/// allow.</para>
/// </summary>
public sealed class BrowserClassifier : IPolicyClassifier
{
    /// <summary>The tool prefix Claude-in-Chrome registers its tools under.</summary>
    public const string Prefix = "mcp__browser__";

    /// <summary>Computer actions that DO something, as opposed to looking.</summary>
    private static readonly HashSet<string> ActingComputerActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "key", "left_click_drag", "right_click", "middle_click", "double_click", "triple_click",
        "scroll", "hold_key", "left_mouse_down", "left_mouse_up", "left_click",
    };

    /// <summary>Computer actions that only look.</summary>
    private static readonly HashSet<string> ReadingComputerActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "screenshot", "cursor_position", "zoom",
    };

    /// <summary>Tools that read the page.</summary>
    private static readonly HashSet<string> ReadingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "read_page", "get_page_text", "tabs_context_mcp", "find", "read_console_messages",
        "read_network_requests", "list_connected_browsers", "shortcuts_list",
    };

    /// <summary>Tools that act on the page whatever their arguments say.</summary>
    private static readonly HashSet<string> ActingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "form_input", "javascript_tool", "file_upload", "shortcuts_execute", "update_plan",
    };

    public bool Handles(string kind) =>
        kind.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        || kind.StartsWith("browser.", StringComparison.OrdinalIgnoreCase);

    public PolicyVerdict Classify(ActionPolicySubject subject)
    {
        var tool = subject.Kind.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            ? subject.Kind[Prefix.Length..]
            : subject.Kind["browser.".Length..];

        var finding = Judge(tool, subject.Input, subject.Context);
        return new PolicyVerdict(finding.Tier, finding, [finding]);
    }

    private static PolicyFinding Judge(string tool, JsonNode input, PolicyContext context)
    {
        // A batch is its worst action. Recursion is bounded by the fact that a batch's entries name
        // tools from the same set; a nested batch is judged as a batch again.
        if (tool.Equals("browser_batch", StringComparison.OrdinalIgnoreCase))
        {
            var worst = Finding(RiskTier.Allow, "browser_read", "browser_batch", "Empty batch");
            foreach (var action in input["actions"]?.AsArray() ?? [])
            {
                var name = action?["name"]?.GetValue<string>() ?? "";
                var nested = action?["input"] ?? new JsonObject();
                var candidate = Judge(name, nested, context);
                if (Rank(candidate.Tier) > Rank(worst.Tier))
                    worst = candidate;
            }

            return worst;
        }

        if (tool.Equals("navigate", StringComparison.OrdinalIgnoreCase))
        {
            var url = input["url"]?.GetValue<string>() ?? "";
            return IsLocalOrDev(url, context)
                ? Finding(RiskTier.Allow, "browser_read", tool, $"Navigate to a dev/loopback host: {url}")
                : Finding(RiskTier.Gray, "browser_nav", tool, $"Navigate off the dev hosts: {url}");
        }

        if (tool.Equals("computer", StringComparison.OrdinalIgnoreCase))
        {
            var action = input["action"]?.GetValue<string>() ?? "";
            if (ReadingComputerActions.Contains(action))
                return Finding(RiskTier.Allow, "browser_read", tool, $"Computer action reads: {action}");
            // An UNKNOWN action is gray, never an allow: the action list grows with the extension, and a
            // new one must not arrive pre-approved by our own ignorance.
            return Finding(RiskTier.Gray, "browser_act", tool, $"Computer action acts: {action}");
        }

        if (ReadingTools.Contains(tool))
            return Finding(RiskTier.Allow, "browser_read", tool, "Browser read");
        if (ActingTools.Contains(tool))
            return Finding(RiskTier.Gray, "browser_act", tool, "Browser action");

        // A browser tool nobody has classified. Gray rather than allow, for the same reason an unknown
        // computer action is: the tool list is the extension's, not ours.
        return Finding(RiskTier.Gray, "browser_act", tool, "Unclassified browser tool");
    }

    /// <summary>
    /// Loopback, the local network, and the hosts the job's own repo declares as development. The URL is
    /// matched on its HOST, never on the raw string: <c>https://evil.example/?x=dev-api</c> contains a dev
    /// host name and is not one.
    /// </summary>
    internal static bool IsLocalOrDev(string url, PolicyContext context)
    {
        if (url.Length == 0)
            return false;

        var host = Host(url);
        if (host.Length == 0)
            return false;
        if (host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0")
            return true;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var dev in context.DevHosts ?? [])
        {
            if (dev.Length == 0)
                continue;
            // A `*.example.com` entry matches any subdomain; a bare entry matches exactly.
            if (dev.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = dev[1..];
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length)
                    return true;
            }
            else if (host.Equals(dev, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Host(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute.Host;
        // Not absolute: could be a bare host, a path, or a scheme we do not parse. Anything with a dot and
        // no spaces is treated as a host; everything else has none, which routes to gray.
        var candidate = url.Split('/', '?', '#')[0];
        return candidate.Contains('.') && !candidate.Contains(' ') ? candidate : "";
    }

    private static int Rank(RiskTier tier) => tier switch
    {
        RiskTier.HardDeny => 4,
        RiskTier.Escalate => 3,
        RiskTier.Gray => 2,
        RiskTier.Allow => 1,
        _ => 0,
    };

    private static PolicyFinding Finding(RiskTier tier, string @class, string tool, string reason) =>
        new(tier, @class, tool, reason);
}

/// <summary>
/// P1.5: the mailbox, ported from nightshift's rules for its Outlook tools
/// (<c>outlook_search</c>, <c>outlook_recent</c>, <c>outlook_read</c>, <c>outlook_folders</c>,
/// <c>outlook_status</c>).
///
/// <para><b>Reading mail is an allow; writing is gray.</b> A job that can read the owner's inbox is doing
/// what it was asked to do — that is the whole point of attaching the mailbox — and it is bounded by the
/// Graph scope the token carries (<c>Mail.Read</c>), which is a stronger bound than this classifier could
/// impose. Anything that sends, replies, deletes or moves is a change to somebody else's world, and the
/// person on the other end is not in the room: gray, so the supervisor sees it, and it is never an
/// allow.</para>
///
/// <para><b>Compose is the case worth naming.</b> A draft is not a send, so <c>create_draft</c> is gray
/// rather than denied — the owner reads the draft, and the send is a separate action nightshift never
/// granted the agent at all.</para>
/// </summary>
public sealed class MailClassifier : IPolicyClassifier
{
    private static readonly HashSet<string> ReadingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "outlook_status", "outlook_search", "outlook_recent", "outlook_read", "outlook_folders",
        "outlook_attachment", "outlook_calendar", "outlook_free_busy",
    };

    private static readonly HashSet<string> WritingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "outlook_send", "outlook_reply", "outlook_reply_all", "outlook_forward", "outlook_delete",
        "outlook_move", "outlook_mark", "outlook_create_draft", "outlook_create_event",
        "outlook_accept_event",
    };

    public bool Handles(string kind) =>
        kind.StartsWith("outlook_", StringComparison.OrdinalIgnoreCase)
        || kind.StartsWith("mail.", StringComparison.OrdinalIgnoreCase);

    public PolicyVerdict Classify(ActionPolicySubject subject)
    {
        var tool = subject.Kind.StartsWith("mail.", StringComparison.OrdinalIgnoreCase)
            ? "outlook_" + subject.Kind["mail.".Length..]
            : subject.Kind;

        PolicyFinding finding;
        if (ReadingTools.Contains(tool))
            finding = new PolicyFinding(RiskTier.Allow, "mail_read", "mail", "Mailbox read");
        else if (WritingTools.Contains(tool))
            finding = new PolicyFinding(RiskTier.Gray, "mail_write", "mail", $"Mailbox change: {tool}");
        else
            // An unclassified mail tool is gray, never an allow: the tool list belongs to the server, and
            // "we have not heard of it" is not evidence that it is harmless.
            finding = new PolicyFinding(RiskTier.Gray, "mail_write", "mail", $"Unclassified mail tool: {tool}");

        return new PolicyVerdict(finding.Tier, finding, [finding]);
    }
}

/// <summary>
/// I3: the Notion workspace, and the rules its tools are judged by.
///
/// <para><b>The same shape as the mailbox and the browser, and that is the point of the track.</b> Reading a
/// page or a database is an allow — it changes nothing and it is what attaching a workspace is FOR — while
/// creating a page, appending blocks, changing properties or uploading a file are gray, because the other
/// people in that workspace are not in the room. An unclassified <c>notion_*</c> tool is gray rather than an
/// allow, for the reason the other two give: the tool list belongs to the server, and "we have not heard of
/// it" is not evidence that it is harmless.</para>
///
/// <para><b>The sink is a write, and it is the case nightshift built this for.</b> One page per finished job,
/// host-side, with the summary and the pending items — a report a human reads. That it is publication rather
/// than a private note is exactly why it is gray: a write into a shared workspace is visible to whoever else
/// is in it, which is a fact about somebody else's world and not only about this host's files.</para>
/// </summary>
public sealed class NotionClassifier : IPolicyClassifier
{
    /// <summary>Tools that only read. The workspace's own vocabulary, not a renaming of it.</summary>
    private static readonly HashSet<string> ReadingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "notion_search", "notion_read_page", "notion_read_database", "notion_read_block",
        "notion_list_users", "notion_status",
    };

    /// <summary>Tools that change the workspace: pages, blocks, properties, uploads, comments.</summary>
    private static readonly HashSet<string> WritingTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "notion_create_page", "notion_update_page", "notion_append_blocks", "notion_delete_block",
        "notion_update_block", "notion_upload_file", "notion_create_comment", "notion_archive_page",
    };

    public bool Handles(string kind) =>
        kind.StartsWith("notion_", StringComparison.OrdinalIgnoreCase)
        || kind.StartsWith("notion.", StringComparison.OrdinalIgnoreCase);

    public PolicyVerdict Classify(ActionPolicySubject subject)
    {
        var tool = subject.Kind.StartsWith("notion.", StringComparison.OrdinalIgnoreCase)
            ? "notion_" + subject.Kind["notion.".Length..]
            : subject.Kind;

        PolicyFinding finding;
        if (ReadingTools.Contains(tool))
            finding = new PolicyFinding(RiskTier.Allow, "notion_read", "notion", "Workspace read");
        else if (WritingTools.Contains(tool))
            finding = new PolicyFinding(RiskTier.Gray, "notion_write", "notion", $"Workspace change: {tool}");
        else
            finding = new PolicyFinding(RiskTier.Gray, "notion_write", "notion", $"Unclassified Notion tool: {tool}");

        return new PolicyVerdict(finding.Tier, finding, [finding]);
    }
}
