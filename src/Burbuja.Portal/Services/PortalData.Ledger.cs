using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Portal.Services;

/// <summary>
/// M14 R5p — the ledger half of the portal's read model.
///
/// <para><b>What lives here.</b> The two readers that walk the kernel's event LOG — <c>EventsAsync</c> (a
/// stream resume) and <c>SearchEventsAsync</c> (the console's search, which pages by `nextFrom` and stops
/// on a repeat) — plus the day ledger Home and Pomnibot read, the spend series it is drawn from, and the
/// one parser they share. These three travel together because they answer one question from three angles:
/// what did this host DO, what did it COST, and what is the searchable record.</para>
///
/// <para>The split is by SUBJECT, and the class is one class either way, so nothing outside this file
/// changed; a lane adding a ledger-shaped reader appends to this part rather than to the primary file.</para>
/// </summary>
public partial class PortalData
{
    /// <summary>
    /// M12 R3 — today-and-the-preceding-days spend, for Home's sparkline.
    ///
    /// <para>One point per day, oldest first, ending today, so a flat line means "nothing was spent" and a
    /// missing day means zero rather than a gap. The DAYS COME FROM THE LEDGERS THAT EXIST: the kernel
    /// writes a ledger resource only for a day something was booked against, so a quiet week would
    /// otherwise draw as a line with holes in it, or worse as a line that skips the quiet days and implies
    /// continuous activity. Filling the gaps with zero is the honest shape.</para>
    ///
    /// <para>Returns the series only. The caller already has <c>SpendTodayUsd</c> from the ledger it is
    /// rendering, and a second sum here could disagree with it by a rounding step.</para>
    /// </summary>
    public async ValueTask<IReadOnlyList<double>> SpendSeriesAsync(ArmScope scope, int days, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var today = clock.GetUtcNow().UtcDateTime.Date;

        // A ledger reads as a day resource, so the points are collected by DAY rather than by list index.
        var byDay = new Dictionary<string, decimal>(StringComparer.Ordinal);
        await foreach (var item in client.ListAsync<PortalEnvelope<PortalLedgerDay>>(
                           "/providers/Burbuja.Agents/ledgers", ct: ct))
        {
            if (item.Name is { Length: > 0 } day && item.Properties is { } properties)
                byDay[day] = properties.SpentUsd;
        }

        var series = new List<double>(days);
        for (var offset = days - 1; offset >= 0; offset--)
        {
            var day = today.AddDays(-offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            series.Add((double)byDay.GetValueOrDefault(day));
        }

        return series;
    }

    /// <summary>
    /// The trajectory's data source: the events route's JSON page, read forward from
    /// <paramref name="from"/> through as many pages as the caller allows.
    /// </summary>
    public async ValueTask<IReadOnlyList<LedgerEvent>> EventsAsync(
        string path, long from, int max, CancellationToken ct)
    {
        var client = await ClientAsync(ct);
        var events = new List<LedgerEvent>();
        await foreach (var evt in client.ReadEventsAsync<LedgerEvent>(path, from, to: null, limit: options.EventPageSize, ct: ct))
        {
            events.Add(evt);
            if (events.Count >= max)
                break;
        }

        return events;
    }

    /// <summary>
    /// T4: the trajectory search, asked of the stream rather than of the page. The browser sends the
    /// words and the kernel answers with the matches plus how much of the stream it read to find them —
    /// so "no results" can say whether it looked everywhere, and a page with more matches than the
    /// caller asked for keeps an honest number instead of pretending the stream ended.
    ///
    /// <para>The read is bounded by <paramref name="max"/> matches and <paramref name="maxPages"/>
    /// pages, and <see cref="PortalEventSearch.Complete"/> says whether the stream was walked to the
    /// end. Nothing here filters: the filtering is the kernel's, and this method never holds a second
    /// model of what matches.</para>
    /// </summary>
    public async ValueTask<PortalEventSearch> SearchEventsAsync(
        string path, string query, int max, int maxPages, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new PortalEventSearch([], 0, true, []);

        var client = await ClientAsync(ct);
        var events = new List<LedgerEvent>();
        var terms = new List<string>();
        long scanned = 0;
        var cursor = 0L;
        var complete = false;
        var visited = new HashSet<long> { 0 };
        for (var page = 0; page < maxPages; page++)
        {
            var body = await client.GetAsync<JsonObject>(
                path,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["from"] = cursor.ToString(CultureInfo.InvariantCulture),
                    ["limit"] = Math.Min(max, options.EventPageSize).ToString(CultureInfo.InvariantCulture),
                    ["q"] = query,
                },
                ct);
            if (body?["value"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item?.Deserialize<LedgerEvent>(JsonWire.Options) is { } evt && events.Count < max)
                        events.Add(evt);
                }
            }

            scanned += ReadEventNumber(body?["scanned"]) ?? 0;
            terms.Clear();
            if (body?["terms"] is JsonArray echoed)
                terms.AddRange(echoed.Select(t => t?.GetValue<string>() ?? "").Where(t => t.Length > 0));

            if (ReadEventNumber(body?["nextFrom"]) is not { } next || !visited.Add(next))
            {
                complete = true;
                break;
            }

            cursor = next;
            if (events.Count >= max)
                break;
        }

        return new PortalEventSearch(events, scanned, complete, terms);
    }

    /// <summary>
    /// M10 A7 — the Agents day ledger for a given UTC day, or a zero day when nobody has booked against it.
    ///
    /// <para>A missing day is ZERO, not a failure (the resource is created by the first booking), which is why
    /// this returns a synthesized day rather than null: a page showing a budget must be able to say "$0.00 of
    /// $3.00" on a host that has not spent anything yet, and a null would render as "unknown".</para>
    /// </summary>
    public async ValueTask<PortalLedgerDay> LedgerAsync(ArmScope scope, string day, CancellationToken ct)
    {
        _ = scope;
        var client = await ClientAsync(ct);
        try
        {
            var envelope = await client.GetAsync<PortalEnvelope<PortalLedgerDay>>(
                $"/providers/Burbuja.Agents/ledgers/{day}", ct: ct);
            return envelope?.Properties ?? new PortalLedgerDay { Day = day };
        }
        catch (ArmException ex) when (ex.Error.HttpStatus == 404)
        {
            return new PortalLedgerDay { Day = day };
        }
    }

    /// <summary>The JSON number an events page carries — absent, null or not a number is no number.</summary>
    private static long? ReadEventNumber(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
}
