using Burbuja.Core.Abstractions.Policy;
using Burbuja.Core.Sdk.Policy;

namespace Burbuja.Core.Policy;

public sealed record ShellParseResult(
    IReadOnlyList<ParsedShellSegment> Segments,
    IReadOnlyList<string> Unparseable,
    IReadOnlyList<string> ArgSubstitutions,
    IReadOnlyList<(string Verb, string Body)> ScriptBodies,
    IReadOnlyList<DetailedParsedSegment> Detailed);

public sealed record DetailedParsedSegment(
    IReadOnlyList<string> Argv,
    IReadOnlyList<string> Redirects,
    IReadOnlyDictionary<string, string> EnvAssigns,
    string Raw);

/// <summary>
/// Faithful port of nightshift <c>src/policy/bash.ts</c> (+ path helpers from <c>paths.ts</c>).
/// Order: heredocs → tokenizer → substitutions → segments/redirects/env assigns → argv normalisation.
/// </summary>
public sealed class ShellCommandParser : IShellCommandParser
{
    public IReadOnlyList<ParsedShellSegment> Parse(string command) => ParseDetailed(command).Segments;

    public ShellParseResult ParseDetailed(string command)
    {
        var parsed = ShellSegmenter.SplitCommandLine(command);
        var lineUnparseable = parsed.Unparseable.Count > 0;
        var detailed = parsed.Segments.Select(s => new DetailedParsedSegment(
            s.Argv, s.Redirects, s.EnvAssigns, s.Raw)).ToList();
        var segs = parsed.Segments.Select(s => new ParsedShellSegment(
            s.Argv,
            s.Argv.Count > 0 ? s.Argv[0] : null,
            s.Redirects,
            lineUnparseable)).ToList();
        return new ShellParseResult(segs, parsed.Unparseable, parsed.ArgSubstitutions, parsed.ScriptBodies, detailed);
    }
}
