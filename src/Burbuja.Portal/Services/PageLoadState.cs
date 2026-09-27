namespace Burbuja.Portal.Services;

/// <summary>
/// M14 R9 — where a page's own “my reads landed” claim goes, so the marker can be written on the page ROOT.
///
/// <para><b>Why the claim and the attribute are two places.</b> M14 R5e gave <c>PageHeader</c> and
/// <c>ExecutionDetail</c> a <c>Loaded</c> parameter and wrote <c>data-loaded</c> on their own header while
/// rendering. The plan asks for the opposite, twice and in the same words — “a <c>PageHeader</c>
/// <c>Loaded</c> parameter → <c>data-loaded</c> on the <b>page root</b>” (§3, Lane M) and “<c>data-loaded</c>
/// on the root, set by the page, <b>never the header</b>” (§5, layer 2) — and lane S's G43 measures it: the
/// header renders during prerender, before any read has landed, so a marker standing on it says “loaded”
/// about a page that has read nothing. The parameter stays the page's declaration; this object carries that
/// declaration up to the one element that IS the page's root, <c>main.content</c>.</para>
///
/// <para><b>Scoped, like the circuit.</b> One instance per circuit (and per prerender request), so a page
/// navigating away cannot leave its marker behind for the next one: <c>MainLayout</c> resets it on
/// <c>LocationChanged</c>, and the new page reports for itself when its own read returns. <c>false</c> is a
/// report like any other — a page that goes back to reading (Sessions during a refresh, Activity on its 3 s
/// tick) takes the marker away rather than leaving a stale claim standing.</para>
/// </summary>
public sealed class PageLoadState
{
    private bool _loaded;

    /// <summary>Whether the page currently on screen has declared its reads landed.</summary>
    public bool Loaded => _loaded;

    /// <summary>Raised when <see cref="Loaded"/> changes — the page root re-renders on it.</summary>
    public event Action? Changed;

    /// <summary>The page's claim, as it stands in this render. Idempotent: a page reporting the same state on
    /// every re-render costs one comparison and no render.</summary>
    public void Report(bool loaded)
    {
        if (_loaded == loaded)
        {
            return;
        }

        _loaded = loaded;
        Changed?.Invoke();
    }

    /// <summary>Back to “nothing is known yet” — the state a navigation starts from.</summary>
    public void Reset() => Report(false);
}
