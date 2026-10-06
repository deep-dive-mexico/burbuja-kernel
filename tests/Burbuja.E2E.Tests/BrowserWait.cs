// Adapted from burbuja-server 97940d9; the fixture here is the independent generic kernel host.
namespace Burbuja.E2E.Tests;

/// <summary>
/// How long a browser fact waits for the UI, in one place.
///
/// <para><b>Why this exists.</b> The compose lane runs 65 facts against ONE container, and a 20-second bound
/// expires there on <em>rendering</em> rather than on behaviour: the failing set changed from run to run
/// (D5 + G14 + G9, then D5 + G10 + G14 + G9), every failure was a wait — page text that still read the portal
/// shell, an element that had not appeared — and each one read as a product defect while the same fact passed
/// in the self-hosted lane. A wait is a bound on patience, not a claim about speed, so it lives here with the
/// reason attached; the assertions keep their own precision.</para>
/// </summary>
internal static class BrowserWait
{
    /// <summary>Sixty seconds: a Blazor round trip plus a re-render on a container running the whole lane.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>The same bound for <c>WaitForSelectorAsync</c>.</summary>
    public static Microsoft.Playwright.PageWaitForSelectorOptions Selector => new() { Timeout = 60_000 };
}
