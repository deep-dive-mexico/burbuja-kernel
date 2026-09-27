namespace Burbuja.Portal.Services;

/// <summary>
/// M14 R5d (request row X15) — the sentence that says WHY a page's primary action is disabled.
///
/// <para><b>Why one helper rather than five conditions.</b> The five create forms already disabled their submit
/// correctly and told nobody: the button carried no <c>title</c> and the page rendered no reason, so a keyboard
/// user could not reach the primary action at all (a disabled button is not focusable) and a screen reader was
/// told nothing about the form it was on. Lane S's keyboard walk found it pinned per route in G43. The ORDER is
/// the part worth centralising: a reader can act on "your role cannot do this" by asking somebody, on "the day's
/// stop is reached" by waiting, and on "write a name" by typing — so the most permanent cause is named first and
/// the most local one last, and a page never shows two reasons at once.</para>
///
/// <para>Empty is a real answer: while the request is in flight the label already says <i>Working…</i> and a
/// second sentence would be noise.</para>
/// </summary>
public static class SubmitWhy
{
    /// <param name="busy">The action is running: no reason, the label carries it.</param>
    /// <param name="mayWrite">The caller's roles grant the write. When false, <paramref name="permission"/> is
    /// the sentence naming the ARM action the kernel would refuse.</param>
    /// <param name="permission">Why the role cannot perform the write, naming the action.</param>
    /// <param name="blocked">A condition the reader cannot change by typing — the day's stop, a missing
    /// workspace — or null.</param>
    /// <param name="missing">What still has to be typed, as a sentence, or null.</param>
    public static string Of(bool busy, bool mayWrite, string permission, string? blocked, string? missing)
    {
        if (busy)
            return "";
        if (!mayWrite)
            return permission;
        if (blocked is { Length: > 0 })
            return blocked;
        if (missing is { Length: > 0 })
            return missing;
        return "";
    }
}
