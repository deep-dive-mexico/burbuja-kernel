using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Events;
using Shouldly;
using Xunit;

namespace Burbuja.Core.UnitTests.Events;

/// <summary>
/// T4: the search rule itself. The endpoint's own facts live in `Burbuja.Host.Tests`
/// (`DataPlaneJsonPageTests`); these pin the semantics a caller has to be able to predict.
/// </summary>
public sealed class EventSearchTests
{
    [Fact]
    public void Terms_are_whitespace_separated_and_all_of_them_must_match()
    {
        var evt = Event("the supervisor denied the tool call", new JsonObject { ["tool"] = "bash", ["verdict"] = "deny" });

        EventSearch.Matches(evt, EventSearch.Parse("denied")).ShouldBeTrue();
        EventSearch.Matches(evt, EventSearch.Parse("bash deny")).ShouldBeTrue("terms are ANDed, order irrelevant");
        EventSearch.Matches(evt, EventSearch.Parse("denied  bash   deny")).ShouldBeTrue("extra whitespace is not a term");
        EventSearch.Matches(evt, EventSearch.Parse("denied allowed")).ShouldBeFalse("one missing term is not a match");
    }

    [Fact]
    public void Matching_is_case_insensitive_and_reads_the_payload_as_well_as_the_summary()
    {
        var evt = Event("Tool call settled", new JsonObject { ["tool"] = "WebSearch", ["nested"] = new JsonObject { ["note"] = "RateLimited" } });

        EventSearch.Matches(evt, EventSearch.Parse("tool call settled")).ShouldBeTrue("the summary is searched");
        EventSearch.Matches(evt, EventSearch.Parse("websearch")).ShouldBeTrue("a payload VALUE is searched");
        EventSearch.Matches(evt, EventSearch.Parse("NESTED")).ShouldBeTrue("a payload KEY is searched");
        EventSearch.Matches(evt, EventSearch.Parse("ratelimited")).ShouldBeTrue("a nested payload value is searched");
        EventSearch.Matches(evt, EventSearch.Parse("searched absent")).ShouldBeFalse();
    }

    [Fact]
    public void A_null_payload_is_still_searchable_by_its_summary()
    {
        var evt = Event("stream opened", null);
        EventSearch.Matches(evt, EventSearch.Parse("opened")).ShouldBeTrue();
        EventSearch.Matches(evt, EventSearch.Parse("anything")).ShouldBeFalse();
    }

    [Fact]
    public void An_empty_query_has_no_terms_and_so_matches_everything()
    {
        var evt = Event("anything", null);
        EventSearch.Parse(null).ShouldBeEmpty();
        EventSearch.Parse("").ShouldBeEmpty();
        EventSearch.Parse("   ").ShouldBeEmpty();
        EventSearch.Parse("\t\n ").ShouldBeEmpty();
        EventSearch.Matches(evt, EventSearch.Parse("   ")).ShouldBeTrue("no terms is not a filter");
    }

    [Fact]
    public void A_single_term_is_kept_whole_so_a_search_can_contain_spaces_in_a_value()
    {
        EventSearch.Parse("tool").ShouldBe(["tool"]);
        EventSearch.Parse("  bash  ").ShouldBe(["bash"]);
        EventSearch.Parse("Burbuja.Agents").ShouldBe(["Burbuja.Agents"], "punctuation is not a separator");
    }

    private static LedgerEvent Event(string summary, JsonNode? payload) =>
        new(
            Seq: 1,
            StreamId: "test/one",
            SubStreamId: null,
            Type: "test.event",
            Summary: summary,
            Payload: payload,
            Lanes: RenderLane.Output,
            Subject: "s",
            Value: null,
            StreamType: "test",
            At: DateTimeOffset.UnixEpoch);
}
