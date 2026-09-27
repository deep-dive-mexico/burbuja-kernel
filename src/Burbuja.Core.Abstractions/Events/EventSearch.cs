namespace Burbuja.Core.Abstractions.Events;

/// <summary>
/// The server-side search behind <c>GET …/events?q=</c>.
///
/// <para><b>Why it is here and not in the client.</b> The trajectory view filters rows a user can see;
/// doing that in the browser means the page must first hold the events it is filtering, so the answer
/// to "where is the tool call that failed" depends on how much of the stream the page happened to
/// page in. A stream is the one place that has all of it. The portal asks the stream, and what the
/// user sees is a bounded page of real matches rather than a filter over a window.</para>
///
/// <para><b>Terms, not patterns.</b> Space-separated terms, all of which must appear
/// (<c>AND</c>), case-insensitively, in the event's <c>summary</c> or its payload text. There is no
/// regex and no prefix syntax on purpose: a query language is a thing to get wrong and the search this
/// replaces is a text box. <c>type</c> and <c>subject</c> are deliberately <em>not</em> searched — the
/// summary is what the catalogue puts on the row, and searching a field the user cannot see is how a
/// result set starts disagreeing with its own rows.</para>
/// </summary>
public static class EventSearch
{
    /// <summary>
    /// How many events one search page reads before it stops and says it stopped. The bound is
    /// reported (<c>scanned</c>) and resumable (<c>nextFrom</c>) rather than hidden, so a search over a
    /// long stream is a sequence of short honest pages and never a request that appears to hang.
    /// </summary>
    public const int ScanLimit = 20_000;

    /// <summary>The terms of a query: whitespace-separated, empties dropped, original casing kept.</summary>
    public static IReadOnlyList<string> Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];
        return query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Every term appears in the event's searchable text. No terms is every event.</summary>
    public static bool Matches(LedgerEvent evt, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
            return true;
        var text = SearchableText(evt);
        for (var i = 0; i < terms.Count; i++)
        {
            if (!text.Contains(terms[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The text a search reads: the event's summary, then its payload in its own JSON form. The payload
    /// is stringified rather than walked so that a nested field, a key and a number are all findable by
    /// the same rule — "find it wherever it is".
    /// </summary>
    public static string SearchableText(LedgerEvent evt) =>
        string.Concat(evt.Summary, "\n", evt.Payload?.ToJsonString() ?? string.Empty);
}
