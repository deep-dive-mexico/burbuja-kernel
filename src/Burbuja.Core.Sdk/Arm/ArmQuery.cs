using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Core.Arm;

/// <summary>
/// Skip-token codec shared by the kernel and out-of-tree stores (N3). The format is a public
/// contract: a token minted by one store must be accepted by another.
/// </summary>
public static class ArmSkipTokenCodec
{
    public static string Encode(DateTimeOffset createdAt, string id)
    {
        var payload = createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + "\n" + id;
        return ToBase64Url(Encoding.UTF8.GetBytes(payload));
    }

    public static bool TryDecode(string token, out DateTimeOffset createdAt, out string id)
    {
        createdAt = default;
        id = "";
        try
        {
            var text = Encoding.UTF8.GetString(FromBase64Url(token));
            var idx = text.IndexOf('\n', StringComparison.Ordinal);
            if (idx <= 0)
                return false;
            if (!long.TryParse(text[..idx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                return false;
            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            id = text[(idx + 1)..];
            return id.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string token)
    {
        var s = token.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}

/// <summary>The ARM <c>$filter</c> subset the kernel supports (N3).</summary>
public static class ArmFilterParser
{
    public static bool TryParse(string? raw, out ArmListFilter? filter, out ArmError? error)
    {
        filter = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        string? nameEq = null, nameStarts = null, tagKey = null, tagEq = null, propKey = null, propEq = null;
        foreach (var clause in SplitAnd(raw))
        {
            var m = Regex.Match(clause.Trim(),
                @"^(name)\s+(eq|startswith)\s+'([^']*)'$|^(tags)/([A-Za-z0-9_-]+)\s+eq\s+'([^']*)'$|^(properties)/([A-Za-z0-9_./-]+)\s+eq\s+'([^']*)'$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!m.Success)
            {
                error = ArmError.InvalidFilter("The $filter expression is not supported.");
                return false;
            }

            if (m.Groups[1].Success)
            {
                if (m.Groups[2].Value.Equals("eq", StringComparison.OrdinalIgnoreCase))
                    nameEq = m.Groups[3].Value;
                else
                    nameStarts = m.Groups[3].Value;
            }
            else if (m.Groups[4].Success)
            {
                tagKey = m.Groups[5].Value;
                tagEq = m.Groups[6].Value;
            }
            else
            {
                propKey = m.Groups[8].Value;
                propEq = m.Groups[9].Value;
            }
        }

        filter = new ArmListFilter(nameEq, nameStarts, tagKey, tagEq, propKey, propEq);
        return true;
    }

    public static bool Matches(ResourceEnvelope<JsonObject> env, ArmListFilter filter)
    {
        if (filter.NameEq is not null && !string.Equals(env.Name, filter.NameEq, StringComparison.OrdinalIgnoreCase))
            return false;
        if (filter.NameStartsWith is not null && !env.Name.StartsWith(filter.NameStartsWith, StringComparison.OrdinalIgnoreCase))
            return false;
        if (filter.TagKey is not null)
        {
            if (!env.Tags.TryGetValue(filter.TagKey, out var tag) || !string.Equals(tag, filter.TagEq, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (filter.PropertyKey is not null)
        {
            var node = env.Properties;
            foreach (var part in filter.PropertyKey.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (node[part] is not { } next)
                    return false;
                if (next is JsonObject obj)
                    node = obj;
                else
                    return string.Equals(next.ToString(), filter.PropertyEq, StringComparison.OrdinalIgnoreCase);
            }
        }

        return true;
    }

    private static IEnumerable<string> SplitAnd(string raw)
    {
        var idx = 0;
        while (idx < raw.Length)
        {
            var and = raw.IndexOf(" and ", idx, StringComparison.OrdinalIgnoreCase);
            if (and < 0)
            {
                yield return raw[idx..];
                yield break;
            }

            yield return raw[idx..and];
            idx = and + 5;
        }
    }
}
